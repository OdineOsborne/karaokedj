using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>Un giudizio del DJ su un suggerimento: "dopo X, Y non c'entra" (−1) o "Y va benissimo" (+1).</summary>
public sealed class FeedbackEntry
{
    public string From { get; set; } = "";     // id del brano di riferimento (vuoto = giudizio sul brano in sé)
    public string To { get; set; } = "";       // id del brano suggerito
    public int Score { get; set; }             // +1 / −1
    public string FromGenre { get; set; } = "";
    public string ToGenre { get; set; } = "";
    public DateTime Utc { get; set; }
}

/// <summary>
/// Memoria del suggeritore: i pollici su/giù del DJ pesano sui punteggi futuri (coppia esatta, brano in generale,
/// passaggio fra generi) e i brani bocciati spariscono subito dai suggeriti della serata.
/// </summary>
public sealed class SuggestionFeedback
{
    private sealed class FileData { public List<FeedbackEntry> Entries { get; set; } = new(); public List<string> ExternalRejected { get; set; } = new(); }
    private readonly FileData _data;
    private readonly HashSet<string> _rejectedThisSession = new();

    public SuggestionFeedback() { _data = JsonStore.Load<FileData>(Path.Combine(AppPaths.Root, "feedback.json")); }
    private void Save() { try { JsonStore.Save(Path.Combine(AppPaths.Root, "feedback.json"), _data); } catch { } }

    public int Count => _data.Entries.Count;

    public void Rate(Track? from, Track to, int score)
    {
        _data.Entries.Add(new FeedbackEntry
        {
            From = from?.Id ?? "", To = to.Id, Score = Math.Sign(score),
            FromGenre = from?.Genre ?? "", ToGenre = to.Genre, Utc = DateTime.UtcNow,
        });
        if (score < 0) _rejectedThisSession.Add(to.Id);
        Save();
    }

    /// <summary>Un brano bocciato stasera non viene più proposto (in questa sessione).</summary>
    public bool IsRejectedNow(Track t) => _rejectedThisSession.Contains(t.Id);

    /// <summary>Fattore moltiplicativo per il punteggio di "to" dopo "from": 1 = neutro.</summary>
    public double Factor(Track from, Track to)
    {
        double f = 1;
        int pair = 0, track = 0, genre = 0;
        foreach (var e in _data.Entries)
        {
            if (e.To == to.Id) { track += e.Score; if (e.From == from.Id) pair += e.Score; }
            if (e.FromGenre.Length > 0 && e.ToGenre.Length > 0 && GenreEq(e.FromGenre, from.Genre) && GenreEq(e.ToGenre, to.Genre) && !GenreEq(from.Genre, to.Genre))
                genre += e.Score;
        }
        if (pair < 0) f *= 0.15;                       // "dopo questo, quello no": quasi escluso
        else if (pair > 0) f *= 1.4;
        f *= Math.Pow(0.85, Math.Max(0, -track));      // bocciato più volte in generale: scende
        f *= Math.Pow(1.05, Math.Clamp(track, 0, 5));  // apprezzato: sale un po'
        if (genre < 0) f *= Math.Pow(0.8, Math.Min(4, -genre));   // quel passaggio di genere non piace
        return f;
    }

    private static bool GenreEq(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Proposte "fuori libreria" bocciate: non vanno riproposte all'AI.</summary>
    public IReadOnlyList<string> ExternalRejected => _data.ExternalRejected;
    public void RejectExternal(string display)
    {
        if (!_data.ExternalRejected.Contains(display, StringComparer.OrdinalIgnoreCase)) { _data.ExternalRejected.Add(display); Save(); }
    }

    public void ResetSession() => _rejectedThisSession.Clear();

    /// <summary>Scarta un brano solo per stasera (es. tolto dalla coda dopo che l'automix l'aveva scelto), senza memorizzare un giudizio.</summary>
    public void RejectForSession(Track t) => _rejectedThisSession.Add(t.Id);
}
