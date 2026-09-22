using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>Cosa vuole fare il DJ col prossimo brano.</summary>
public enum FlowIntent { Auto, Keep, Up, Down }

/// <summary>Stato della serata che conta per la scelta del prossimo brano.</summary>
public sealed record FlowContext(
    Track Reference,
    FlowIntent Intent,
    IReadOnlyList<Track> PlayedTonight,
    bool KaraokeNight);

/// <summary>
/// Motore di associazione: sceglie il brano dopo con un senso *logico* (si può mixare, stesso mondo musicale)
/// e *emotivo* (l'energia sale o scende come vuole il DJ, l'umore non fa salti assurdi, la lingua resta coerente).
/// Ogni voce porta con sé il motivo, così il DJ capisce perché gliel'abbiamo proposta e può dissentire.
/// </summary>
public static class SetFlow
{
    /// <summary>Punteggio 0…1 e motivo. 0 = da non proporre.</summary>
    public static (double Score, string Why) Rank(Track c, FlowContext ctx)
    {
        var r = ctx.Reference;
        double mix = SearchUtil.Compatibility(r, c);
        if (mix <= 0) return (0, "");

        var (style, styleWhy) = MusicTaste.Affinity(r, c);
        if (style < 0.32) return (0, styleWhy);      // due mondi diversi: nessun BPM lo giustifica

        var reasons = new List<string>();
        if (styleWhy.Length > 0) reasons.Add(styleWhy);

        double flow = EnergyFlow(r, c, ctx.Intent, reasons);
        double mood = Mood(r, c, reasons);
        double lang = Language(r, c, ctx.KaraokeNight, reasons);
        double repeat = NotTooMuchOfTheSame(c, ctx, reasons);

        // Lo stile non è una voce fra le altre: è il filtro. Un pezzo di un altro mondo non diventa buono
        // perché ha lo stesso BPM — era esattamente l'errore "Battisti dopo gli AC/DC".
        double mechanics = 0.45 * mix + 0.25 * flow + 0.15 * mood + 0.15 * lang;
        double score = mechanics * style;
        score *= repeat;
        // quello che si è imparato dalle serate vere (se l'utente ha acconsentito a condividerle: vedi UsageStats)
        double learned = UsageStats.Bonus(r, c);
        if (learned > 1.02) reasons.Add("funziona nelle serate");
        else if (learned < 0.98) reasons.Add("di solito scartato");
        score *= learned;
        return (Math.Clamp(score, 0, 1), string.Join(", ", reasons));
    }

    /// <summary>Energia: la si tiene, la si alza o si calma la sala. Senza analisi non possiamo dire niente.</summary>
    private static double EnergyFlow(Track r, Track c, FlowIntent intent, List<string> why)
    {
        if (r.Energy <= 0 || c.Energy <= 0) return 0.5;
        double delta = intent switch
        {
            FlowIntent.Up => 0.12,
            FlowIntent.Down => -0.15,
            FlowIntent.Keep => 0,
            _ => r.Energy < 0.45 ? 0.08 : r.Energy > 0.85 ? -0.03 : 0.03,   // automatico: si sale piano, non si scende dal picco
        };
        double target = Math.Clamp(r.Energy + delta, 0, 1);
        double d = Math.Abs(c.Energy - target);
        if (intent == FlowIntent.Up && c.Energy > r.Energy + 0.05) why.Add("alza l'energia");
        else if (intent == FlowIntent.Down && c.Energy < r.Energy - 0.05) why.Add("calma la sala");
        else if (d < 0.08) why.Add("stessa energia");
        return Math.Clamp(1 - d * 2.2, 0, 1);
    }

    /// <summary>
    /// Umore: maggiore/minore e velocità. Un salto da un pezzo malinconico a uno euforico (o viceversa) si sente,
    /// quindi vale solo se il DJ lo ha chiesto alzando o calmando.
    /// </summary>
    private static double Mood(Track r, Track c, List<string> why)
    {
        double va = Valence(r), vb = Valence(c);
        if (va < 0 || vb < 0) return 0.5;
        double d = Math.Abs(va - vb);
        if (d < 0.15 && IsMinor(r.Key) == IsMinor(c.Key)) why.Add(IsMinor(c.Key) ? "stesso umore (minore)" : "stesso umore");
        return Math.Clamp(1 - d * 1.6, 0, 1);
    }

    /// <summary>Umore del brano 0 (malinconico) … 1 (euforico): modo, velocità ed energia. -1 se non sappiamo abbastanza.</summary>
    public static double Valence(Track t)
    {
        if (string.IsNullOrEmpty(t.Key) && t.Bpm <= 0) return -1;
        double v = 0.5;
        if (!string.IsNullOrEmpty(t.Key)) v += IsMinor(t.Key) ? -0.18 : 0.18;
        if (t.Bpm > 0) v += Math.Clamp((t.Bpm - 105) / 250.0, -0.15, 0.15);
        if (t.Energy > 0) v += (t.Energy - 0.55) * 0.35;
        return Math.Clamp(v, 0, 1);
    }

    public static bool IsMinor(string? key) => !string.IsNullOrEmpty(key) && key.TrimEnd().EndsWith("m", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lingua: in una serata italiana si canta di più se non si salta a ogni brano fra inglese e italiano.
    /// La deduciamo dalla famiglia dell'artista (pop italiano, cantautorato, liscio…) quando la conosciamo.
    /// </summary>
    private static double Language(Track r, Track c, bool karaoke, List<string> why)
    {
        bool? a = IsItalian(r), b = IsItalian(c);
        if (a == null || b == null) return 0.5;
        if (a == b) { if (karaoke) why.Add(a == true ? "italiano come il precedente" : "stessa lingua"); return 1.0; }
        return karaoke ? 0.45 : 0.7;      // col karaoke il cambio di lingua pesa di più
    }

    public static bool? IsItalian(Track t)
    {
        var fam = ArtistGenres.FamilyOf(t.Artist);
        if (fam == null) return null;
        return fam.Contains("italiano") || fam is "cantautorato" or "liscio" or "neomelodico";
    }

    /// <summary>Tre brani di fila dello stesso artista o della stessa famiglia stancano: si abbassa il punteggio.</summary>
    private static double NotTooMuchOfTheSame(Track c, FlowContext ctx, List<string> why)
    {
        var last = ctx.PlayedTonight.TakeLast(3).ToList();
        if (last.Count == 0) return 1;
        int sameArtist = last.Count(p => MusicTaste.SameArtist(p, c));
        if (sameArtist >= 2) { why.Add("già molto di questo artista"); return 0.55; }
        var fam = ArtistGenres.FamilyOf(c.Artist);
        if (fam != null && last.Count >= 3 && last.All(p => ArtistGenres.FamilyOf(p.Artist) == fam)) { why.Add("cambia un po' genere"); return 0.8; }
        return 1;
    }
}
