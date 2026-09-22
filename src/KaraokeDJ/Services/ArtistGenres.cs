using System.Text.Json;

namespace KaraokeDJ.Services;

/// <summary>
/// Tabella "artista → famiglia musicale" (Assets/artist-genres.json) più la distanza fra famiglie.
/// Serve quando i file non portano un genere vero: senza questa conoscenza l'app non ha modo di sapere
/// che gli AC/DC e Battisti sono due serate diverse, e proporrebbe accostamenti che nessun DJ farebbe.
/// L'utente può ampliarla: %AppData%\KaraokeDJ\artist-genres.json (stesso formato) si somma a quella dell'app.
/// </summary>
public static class ArtistGenres
{
    private sealed class Family { public string Gruppo { get; set; } = ""; public double Energia { get; set; } }
    private sealed class Data
    {
        public Dictionary<string, Family> Famiglie { get; set; } = new();
        public Dictionary<string, string> Artisti { get; set; } = new();
    }

    private static Dictionary<string, Family>? _families;
    private static Dictionary<string, string>? _artists;

    public static string AppFile => Path.Combine(AppContext.BaseDirectory, "Assets", "artist-genres.json");
    public static string UserFile => Path.Combine(AppPaths.Root, "artist-genres.json");

    private static void Load()
    {
        if (_artists != null) return;
        _families = new(StringComparer.OrdinalIgnoreCase);
        _artists = new(StringComparer.OrdinalIgnoreCase);
        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        foreach (var f in new[] { AppFile, UserFile })
        {
            try
            {
                if (!File.Exists(f)) continue;
                var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(f), opt);
                if (d == null) continue;
                foreach (var kv in d.Famiglie) _families[kv.Key] = kv.Value;
                foreach (var kv in d.Artisti) _artists[SearchUtil.NormalizeForCompare(kv.Key)] = kv.Value;
            }
            catch { }
        }
    }

    public static void Reload() { _artists = null; Load(); }
    public static int Count { get { Load(); return _artists!.Count; } }

    /// <summary>Famiglia musicale dell'artista ("hard rock", "cantautorato"…) o null se non lo conosciamo.</summary>
    public static string? FamilyOf(string? artist)
    {
        Load();
        if (string.IsNullOrWhiteSpace(artist)) return null;
        var n = SearchUtil.NormalizeForCompare(artist);
        if (n.Length < 3) return null;
        if (_artists!.TryGetValue(n, out var fam)) return fam;
        // "AC/DC feat. qualcuno", "Queen & David Bowie": proviamo il primo nome della lista
        foreach (var sep in new[] { " feat ", " ft ", " con ", " e ", " & ", " x " })
        {
            int i = n.IndexOf(sep, StringComparison.Ordinal);
            if (i > 2 && _artists.TryGetValue(n[..i].Trim(), out var f2)) return f2;
        }
        // contenimento: "the beatles" dentro "the beatles remastered"
        foreach (var kv in _artists)
            if (kv.Key.Length >= 5 && n.Contains(kv.Key, StringComparison.Ordinal)) return kv.Value;
        return null;
    }

    /// <summary>
    /// Quanto due famiglie stanno bene di fila: 1 = stessa famiglia, ~0,8 stesso gruppo e stessa spinta,
    /// ~0,3 gruppi diversi ma energia simile (rock → dance), ~0,1 mondi lontani (metal → cantautorato).
    /// </summary>
    public static double Similarity(string a, string b)
    {
        Load();
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (!_families!.TryGetValue(a, out var fa) || !_families.TryGetValue(b, out var fb)) return 0.4;
        double de = Math.Abs(fa.Energia - fb.Energia);
        double baseSim = string.Equals(fa.Gruppo, fb.Gruppo, StringComparison.OrdinalIgnoreCase) ? 0.9 : 0.4;
        return Math.Clamp(baseSim * (1 - 0.9 * de), 0.08, 1);
    }
}
