using System.Net.Http;
using System.Text.Json;
using VOXA.Plugins;

namespace KaraokeDJ.Services;

/// <summary>
/// Sorgenti integrate, lecite e gratuite: Audius (brani pubblicati dagli artisti, scaricabili quando lo consentono),
/// Jamendo (Creative Commons, serve un client_id gratuito), Internet Archive (audio con licenza libera / pubblico dominio).
/// </summary>
public static class LegalSources
{
    public static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    static LegalSources() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("VOXA/1.6 (+https://voxa-cloud.vercel.app)"); }

    public static IImportSource[] All { get; } = { new AudiusSource(), new JamendoSource(), new ArchiveSource() };

    /// <summary>Nome file sicuro "Artista - Titolo.ext" nella cartella, senza sovrascrivere.</summary>
    internal static string DestPath(string folder, string artist, string title, string ext)
    {
        var name = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        if (name.Length > 120) name = name[..120];
        Directory.CreateDirectory(folder);
        var p = Path.Combine(folder, name + ext);
        int i = 2;
        while (File.Exists(p)) p = Path.Combine(folder, $"{name} ({i++}){ext}");
        return p;
    }

    /// <summary>Scarica in <paramref name="dest"/>; se il server dichiara un formato diverso (es. WAV) l'estensione viene corretta. Ritorna il percorso finale.</summary>
    internal static async Task<string> DownloadAsync(string url, string dest, IProgress<ImportProgress>? progress, string label, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var ctype = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
        var ext = ctype switch
        {
            "audio/wave" or "audio/wav" or "audio/x-wav" => ".wav",
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/flac" or "audio/x-flac" => ".flac",
            "audio/mp4" or "audio/x-m4a" or "audio/aac" => ".m4a",
            "audio/ogg" => ".ogg",
            _ => null,
        };
        var cdName = resp.Content.Headers.ContentDisposition?.FileNameStar ?? resp.Content.Headers.ContentDisposition?.FileName;
        if (ext == null && !string.IsNullOrEmpty(cdName)) { var e = Path.GetExtension(cdName.Trim('"')).ToLowerInvariant(); if (e.Length is > 1 and < 6) ext = e; }
        if (ext != null && !dest.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) dest = Path.ChangeExtension(dest, ext);
        long total = resp.Content.Headers.ContentLength ?? -1;
        var tmp = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress?.Report(new ImportProgress($"{label}: {done / 1048576.0:0.0} MB" + (total > 0 ? $" / {total / 1048576.0:0.0} MB" : ""), total > 0 ? 100.0 * done / total : -1));
            }
        }
        File.Move(tmp, dest, true);
        return dest;
    }

    internal static double Similarity(string query, string candidate)
    {
        var q = SearchUtil.Words(query); var c = SearchUtil.Words(candidate);
        if (q.Length == 0 || c.Length == 0) return 0;
        int hit = q.Count(w => c.Contains(w));
        return (double)hit / q.Length;
    }

    internal static void Tag(string path, string artist, string title, string album)
    {
        try
        {
            using var tf = TagLib.File.Create(path);
            tf.Tag.Title = title; tf.Tag.Performers = new[] { artist }; tf.Tag.Album = album;
            tf.Save();
        }
        catch { }
    }
}

/// <summary>Audius: rete musicale aperta; API pubblica senza chiave. Scarica solo i brani che l'artista ha reso scaricabili.</summary>
public sealed class AudiusSource : IImportSource
{
    public string Id => "audius";
    public string Name => "Audius (gratuito, brani degli artisti)";
    public string Description => "Rete musicale aperta: scarica solo i brani che l'artista ha marcato come scaricabili (remix, edit, produzioni indipendenti).";
    public string InputHint => "titolo o artista da cercare su Audius";
    public bool SupportsVideo => false;
    public bool CanHandle(string input) => input.Trim().Length > 0;

    private static async Task<string> HostAsync(CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await LegalSources.Http.GetStringAsync("https://api.audius.co", ct));
            var arr = doc.RootElement.GetProperty("data");
            if (arr.GetArrayLength() > 0) return arr[0].GetString()!.TrimEnd('/');
        }
        catch { }
        return "https://api.audius.co";
    }

    public async Task<IReadOnlyList<string>> ImportAsync(string input, bool video, string destFolder, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ImportProgress("Cerco su Audius…"));
        var host = await HostAsync(ct);
        var json = await LegalSources.Http.GetStringAsync($"{host}/v1/tracks/search?query={Uri.EscapeDataString(input)}&app_name=VOXA", ct);
        using var doc = JsonDocument.Parse(json);
        var best = new List<(string id, string title, string artist, bool dl, double score)>();
        foreach (var t in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var title = t.GetProperty("title").GetString() ?? "";
            var artist = t.TryGetProperty("user", out var u) && u.TryGetProperty("name", out var un) ? un.GetString() ?? "" : "";
            bool dl = t.TryGetProperty("is_downloadable", out var d) && d.GetBoolean();
            var id = t.GetProperty("id").GetString() ?? "";
            best.Add((id, title, artist, dl, LegalSources.Similarity(input, artist + " " + title)));
        }
        if (best.Count == 0) throw new InvalidOperationException("Nessun risultato su Audius");
        var pick = best.Where(b => b.dl).OrderByDescending(b => b.score).FirstOrDefault();
        if (pick.id == null)
        {
            var top = best.OrderByDescending(b => b.score).First();
            throw new InvalidOperationException($"Trovato \"{top.artist} - {top.title}\" ma l'artista non ne consente il download (solo streaming). Prova un altro titolo.");
        }
        var dest = LegalSources.DestPath(destFolder, pick.artist, pick.title, ".mp3");
        dest = await LegalSources.DownloadAsync(host + "/v1/tracks/" + pick.id + "/download?app_name=VOXA", dest, progress, "Audius · " + pick.artist + " - " + pick.title, ct);
        LegalSources.Tag(dest, pick.artist, pick.title, "Audius");
        progress?.Report(new ImportProgress($"Scaricato da Audius: {pick.artist} - {pick.title}", 100));
        return new[] { dest };
    }
}

/// <summary>Jamendo: catalogo Creative Commons; serve un client_id gratuito (developer.jamendo.com) impostato nelle Impostazioni.</summary>
public sealed class JamendoSource : IImportSource
{
    public static string? ClientId { get; set; }
    public string Id => "jamendo";
    public string Name => "Jamendo (Creative Commons)";
    public string Description => "Musica con licenza Creative Commons scaricabile in MP3. Serve un client_id gratuito (developer.jamendo.com) in Impostazioni → Plugin e fonti. Per uso commerciale verifica la licenza del singolo brano.";
    public string InputHint => "titolo o artista da cercare su Jamendo";
    public bool SupportsVideo => false;
    public bool CanHandle(string input) => input.Trim().Length > 0;

    public async Task<IReadOnlyList<string>> ImportAsync(string input, bool video, string destFolder, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("Jamendo: inserisci il client_id in Impostazioni → Plugin e fonti (gratuito su developer.jamendo.com)");
        progress?.Report(new ImportProgress("Cerco su Jamendo…"));
        var url = $"https://api.jamendo.com/v3.0/tracks/?client_id={Uri.EscapeDataString(ClientId)}&format=json&limit=10&audiodownload_allowed=true&audioformat=mp32&search={Uri.EscapeDataString(input)}";
        using var doc = JsonDocument.Parse(await LegalSources.Http.GetStringAsync(url, ct));
        var results = doc.RootElement.GetProperty("results");
        (string dl, string title, string artist, string lic, double score) pick = default;
        foreach (var t in results.EnumerateArray())
        {
            var title = t.GetProperty("name").GetString() ?? ""; var artist = t.GetProperty("artist_name").GetString() ?? "";
            var dl = t.TryGetProperty("audiodownload", out var a) ? a.GetString() ?? "" : "";
            var lic = t.TryGetProperty("license_ccurl", out var l) ? l.GetString() ?? "" : "";
            if (dl.Length == 0) continue;
            var s = LegalSources.Similarity(input, artist + " " + title);
            if (pick.dl == null || s > pick.score) pick = (dl, title, artist, lic, s);
        }
        if (pick.dl == null) throw new InvalidOperationException("Nessun brano scaricabile su Jamendo per questa ricerca");
        var dest = LegalSources.DestPath(destFolder, pick.artist, pick.title, ".mp3");
        dest = await LegalSources.DownloadAsync(pick.dl, dest, progress, "Jamendo · " + pick.artist + " - " + pick.title, ct);
        LegalSources.Tag(dest, pick.artist, pick.title, "Jamendo · " + pick.lic);
        progress?.Report(new ImportProgress($"Scaricato da Jamendo: {pick.artist} - {pick.title} ({pick.lic})", 100));
        return new[] { dest };
    }
}

/// <summary>Internet Archive: audio con licenza dichiarata (Creative Commons, pubblico dominio, netlabel).</summary>
public sealed class ArchiveSource : IImportSource
{
    public string Id => "archive";
    public string Name => "Internet Archive (licenza libera)";
    public string Description => "Registrazioni con licenza libera o di pubblico dominio (netlabel, live autorizzati, storico). Nessuna chiave.";
    public string InputHint => "titolo o artista da cercare su archive.org";
    public bool SupportsVideo => false;
    public bool CanHandle(string input) => input.Trim().Length > 0;

    public async Task<IReadOnlyList<string>> ImportAsync(string input, bool video, string destFolder, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ImportProgress("Cerco su archive.org…"));
        var q = Uri.EscapeDataString($"({input}) AND mediatype:audio AND (licenseurl:* OR collection:netlabels OR collection:opensource_audio)");
        var json = await LegalSources.Http.GetStringAsync($"https://archive.org/advancedsearch.php?q={q}&fl[]=identifier&fl[]=title&fl[]=creator&rows=8&output=json", ct);
        if (json.TrimStart().StartsWith("<")) throw new InvalidOperationException("archive.org non risponde in questo momento (offline o in manutenzione)");
        using var doc = JsonDocument.Parse(json);
        var docs = doc.RootElement.GetProperty("response").GetProperty("docs");
        (string id, string title, string creator, double score) pick = default;
        foreach (var d in docs.EnumerateArray())
        {
            var id = d.GetProperty("identifier").GetString() ?? "";
            var title = d.TryGetProperty("title", out var t) ? (t.ValueKind == JsonValueKind.Array ? t[0].GetString() : t.GetString()) ?? "" : "";
            var creator = d.TryGetProperty("creator", out var c) ? (c.ValueKind == JsonValueKind.Array ? c[0].GetString() : c.GetString()) ?? "" : "";
            var s = LegalSources.Similarity(input, creator + " " + title);
            if (pick.id == null || s > pick.score) pick = (id, title, creator, s);
        }
        if (pick.id == null) throw new InvalidOperationException("Nessun risultato con licenza libera su archive.org");
        // file: preferisci mp3
        using var meta = JsonDocument.Parse(await LegalSources.Http.GetStringAsync($"https://archive.org/metadata/{pick.id}", ct));
        string? file = null;
        foreach (var f in meta.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = f.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) { file = name; break; }
        }
        if (file == null) throw new InvalidOperationException($"\"{pick.title}\" su archive.org non ha un MP3");
        var dest = LegalSources.DestPath(destFolder, pick.creator, pick.title, ".mp3");
        dest = await LegalSources.DownloadAsync("https://archive.org/download/" + pick.id + "/" + Uri.EscapeDataString(file), dest, progress, "archive.org · " + pick.title, ct);
        LegalSources.Tag(dest, pick.creator, pick.title, "Internet Archive");
        progress?.Report(new ImportProgress($"Scaricato da archive.org: {pick.creator} - {pick.title}", 100));
        return new[] { dest };
    }
}
