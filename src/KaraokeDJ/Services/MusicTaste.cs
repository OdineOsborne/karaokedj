using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>
/// Quanto due brani stanno bene uno dopo l'altro *musicalmente* (BPM e tonalità li valuta <see cref="SearchUtil.Compatibility"/>).
/// Nasce da un caso reale: dopo "Hells Bells" degli AC/DC venivano proposti brani di Battisti, perché i file scaricati
/// da YouTube hanno come genere la categoria del video ("Music", "People &amp; Blogs") e quindi "stesso genere" era sempre vero.
/// Qui i generi finti vengono ignorati e, quando manca il genere vero, si usano artista, epoca e il carattere del suono
/// (energia e brillantezza, misurate dall'analisi).
/// </summary>
public static class MusicTaste
{
    /// <summary>Categorie di YouTube e simili: non sono generi musicali, non dicono niente sullo stile.</summary>
    private static readonly HashSet<string> Junk = new(StringComparer.OrdinalIgnoreCase)
    {
        "music", "musica", "people & blogs", "people and blogs", "entertainment", "intrattenimento", "film & animation",
        "film e animazione", "education", "istruzione", "comedy", "commedia", "news & politics", "notizie e politica",
        "sports", "sport", "gaming", "howto & style", "guide e stile", "travel & events", "viaggi ed eventi",
        "science & technology", "scienza e tecnologia", "nonprofits & activism", "pets & animals", "animali",
        "autos & vehicles", "auto e veicoli", "trailer", "video", "audio", "other", "altro", "unknown", "sconosciuto",
        "soundtrack", "various", "varie", "vari", "misc", "miscellaneous", "karaoke", "base musicale", "backing track",
    };

    /// <summary>true se il genere dice qualcosa sullo stile (non è una categoria di YouTube o un riempitivo).</summary>
    public static bool IsUseful(string? genre) =>
        !string.IsNullOrWhiteSpace(genre) && !Junk.Contains(genre.Trim());

    /// <summary>I soli generi utili di un brano (può non restarne nessuno).</summary>
    public static List<string> UsefulGenres(Track t) => t.Genres.Where(IsUseful).ToList();

    /// <summary>Ripulisce la stringa dei generi letta dai tag: toglie le categorie finte.</summary>
    public static string Clean(string? genre)
    {
        if (string.IsNullOrWhiteSpace(genre)) return "";
        return string.Join("; ", Track.SplitGenres(genre).Where(IsUseful));
    }

    /// <summary>
    /// Fattore 0…1 da moltiplicare alla compatibilità BPM/tonalità, con il motivo da mostrare al DJ.
    /// Sotto 0,35 significa "sono due mondi diversi": il brano sparisce dai suggeriti.
    /// </summary>
    public static (double Factor, string Why) Affinity(Track r, Track t)
    {
        // stesso artista: la scelta più sicura che esista
        if (SameArtist(r, t)) return (1.0, "stesso artista");

        var reasons = new List<string>();
        double era = EraFactor(r, t, reasons);

        var ga = UsefulGenres(r); var gb = UsefulGenres(t);
        if (ga.Count > 0 && gb.Count > 0)
        {
            double g = GenreFactor(r, t, ga, gb, reasons);
            return (Clamp(g * era), string.Join(", ", reasons));
        }

        // niente generi nei file: proviamo a riconoscere gli artisti (tabella interna)
        var fa = ArtistGenres.FamilyOf(r.Artist); var fb = ArtistGenres.FamilyOf(t.Artist);
        if (fa != null && fb != null)
        {
            double g = ArtistGenres.Similarity(fa, fb);
            reasons.Insert(0, fa == fb ? fa : $"{fa} → {fb}");
            // il suono conferma o smentisce un po' (±0,1), ma il genere comanda
            double sound = r.Energy > 0 && t.Energy > 0 ? SoundSimilarity(r, t) : 0.5;
            return (Clamp(g * era * (0.9 + 0.2 * sound)), string.Join(", ", reasons));
        }

        // artisti sconosciuti alla tabella: meglio essere prudenti che proporre accostamenti a caso
        reasons.Insert(0, fa == null && fb == null ? "artisti non riconosciuti" : "uno dei due artisti non riconosciuto");
        double s2 = r.Energy > 0 && t.Energy > 0 ? SoundSimilarity(r, t) : 0.4;
        return (Clamp(0.45 * era * (0.6 + 0.8 * s2)), string.Join(", ", reasons));
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1);

    public static bool SameArtist(Track a, Track b)
    {
        var x = SearchUtil.NormalizeForCompare(a.Artist);
        var y = SearchUtil.NormalizeForCompare(b.Artist);
        return x.Length > 2 && y.Length > 2 && (x == y || x.Contains(y) || y.Contains(x));
    }

    private static double GenreFactor(Track r, Track t, List<string> ga, List<string> gb, List<string> why)
    {
        if (ga.Any(x => gb.Any(y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase))))
        { why.Add("genere " + ga.First(x => gb.Any(y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)))); return 1.0; }
        var wa = SearchUtil.Words(string.Join(" ", ga));
        var wb = SearchUtil.Words(string.Join(" ", gb));
        if (wa.Intersect(wb).Any()) { why.Add("generi affini"); return 0.85; }
        why.Add("generi diversi");
        return 0.25;
    }

    /// <summary>Stessa epoca: un pezzo del 1970 dopo uno del 2013 di solito spezza la serata.</summary>
    private static double EraFactor(Track r, Track t, List<string> why)
    {
        if (r.Year <= 0 || t.Year <= 0) return 0.85;   // anno ignoto: non è colpa del brano, ma nemmeno un punto a favore
        int d = Math.Abs(r.Year - t.Year);
        if (d <= 7) { why.Add("stessa epoca"); return 1.0; }
        if (d <= 15) return 0.85;
        if (d <= 25) { why.Add("epoche lontane"); return 0.6; }
        why.Add("epoche molto lontane");
        return 0.4;
    }

    /// <summary>
    /// Carattere del suono dall'analisi: energia (quanto spinge) e brillantezza (chitarre/piatti contro voci e archi).
    /// Se l'analisi è vecchia e non li ha, restituisce un valore neutro basso: meglio pochi suggerimenti che suggerimenti a caso.
    /// </summary>
    private static double SoundSimilarity(Track r, Track t)
    {
        double de = Math.Abs(r.Energy - t.Energy);
        double db = Math.Abs(r.Brightness - t.Brightness);
        return 1 - Math.Clamp(0.6 * de + 0.4 * db, 0, 1);
    }

    /// <summary>Etichetta per il DJ: com'è messa la libreria riguardo ai generi (per capire perché i suggerimenti sono deboli).</summary>
    public static string LibraryGenreHealth(IEnumerable<Track> tracks)
    {
        var list = tracks.Where(t => !t.IsKaraoke).ToList();
        if (list.Count == 0) return "";
        int ok = list.Count(t => UsefulGenres(t).Count > 0 || ArtistGenres.FamilyOf(t.Artist) != null);
        if (ok * 100 / Math.Max(1, list.Count) >= 70) return "";
        return $"⚠ {list.Count - ok} brani su {list.Count} senza genere e con artista non riconosciuto — Impostazioni → Generi con AI";
    }
}
