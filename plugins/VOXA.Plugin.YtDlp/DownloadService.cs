using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VOXA.Plugin.YtDlp;

/// <summary>Percorsi usati dal plugin (impostati dal plugin all'avvio tramite l'host).</summary>
internal static class AppPaths
{
    public static string ToolsDir { get; set; } = "";
    public static string Root { get; set; } = "";
    public static string DownloadsDir { get; set; } = "";
    public static void EnsureDirs() { Directory.CreateDirectory(ToolsDir); Directory.CreateDirectory(Root); Directory.CreateDirectory(DownloadsDir); }
}

public sealed class DownloadStatus
{
    public string Message { get; init; } = "";
    public double Percent { get; init; } = -1;
}

/// <summary>
/// Scarica brani da YouTube tramite yt-dlp (+ ffmpeg). I link Spotify vengono
/// risolti in "artista titolo" e cercati su YouTube (stesso approccio di spotdl).
/// </summary>
public sealed class DownloadService
{
    private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static DownloadService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) KaraokeDJ/1.0");
    }

    public string YtDlpPath => Path.Combine(AppPaths.ToolsDir, "yt-dlp.exe");
    public string FfmpegPath => Path.Combine(AppPaths.ToolsDir, "ffmpeg.exe");
    public string DenoPath => Path.Combine(AppPaths.ToolsDir, "deno.exe");
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    public bool ToolsReady => File.Exists(YtDlpPath) && File.Exists(FfmpegPath);

    /// <summary>Archivio yt-dlp degli ID già scaricati: evita di riscaricare lo stesso video.</summary>
    public string ArchivePath => Path.Combine(AppPaths.Root, "download-archive.txt");

    /// <summary>Callback (artista, titolo) → true se il brano è già in libreria. Usato per saltare i duplicati da Spotify.</summary>
    public Func<string, string, bool>? TrackExists { get; set; }

    public static string NormalizeForCompare(string s)
    {
        s = s.ToLowerInvariant().Replace("&", " and ");
        s = Regex.Replace(s, @"\(.*?\)|\[.*?\]", " ");            // (Remastered), [Official Video]
        s = Regex.Replace(s, @"\b(official|audio|video|remaster(ed)?|lyrics?|hd|hq|feat\.?|ft\.?)\b", " ");
        s = Regex.Replace(s, @"[^\p{L}\p{N}]+", "");
        return s;
    }

    public static bool IsSpotifyUrl(string url) => url.Contains("open.spotify.com/", StringComparison.OrdinalIgnoreCase);
    public static bool IsYouTubeUrl(string url) =>
        url.Contains("youtube.com/", StringComparison.OrdinalIgnoreCase) || url.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase);

    public async Task EnsureToolsAsync(IProgress<DownloadStatus>? progress, CancellationToken ct)
    {
        AppPaths.EnsureDirs();
        if (!File.Exists(YtDlpPath))
        {
            progress?.Report(new DownloadStatus { Message = "Scarico yt-dlp…" });
            await DownloadFileAsync(YtDlpUrl, YtDlpPath, progress, ct);
        }
        if (!File.Exists(FfmpegPath))
        {
            progress?.Report(new DownloadStatus { Message = "Scarico ffmpeg (~90 MB, solo la prima volta)…" });
            var zipPath = Path.Combine(AppPaths.ToolsDir, "ffmpeg.zip");
            await DownloadFileAsync(FfmpegUrl, zipPath, progress, ct);
            progress?.Report(new DownloadStatus { Message = "Estraggo ffmpeg…" });
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var name in new[] { "ffmpeg.exe", "ffprobe.exe" })
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (entry != null) entry.ExtractToFile(Path.Combine(AppPaths.ToolsDir, name), true);
                }
            }
            File.Delete(zipPath);
        }
        if (!File.Exists(DenoPath))
        {
            // yt-dlp usa un runtime JavaScript per risolvere i formati YouTube; senza, alcuni formati mancano.
            try
            {
                progress?.Report(new DownloadStatus { Message = "Scarico Deno (runtime JS per YouTube, ~45 MB, solo la prima volta)…" });
                var zipPath = Path.Combine(AppPaths.ToolsDir, "deno.zip");
                await DownloadFileAsync(DenoUrl, zipPath, progress, ct);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase));
                    entry?.ExtractToFile(DenoPath, true);
                }
                File.Delete(zipPath);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* facoltativo: senza Deno i download funzionano comunque (per ora) */ }
        }
    }

    public async Task UpdateYtDlpAsync(IProgress<DownloadStatus>? progress, CancellationToken ct)
    {
        if (!File.Exists(YtDlpPath)) { await EnsureToolsAsync(progress, ct); return; }
        progress?.Report(new DownloadStatus { Message = "Aggiorno yt-dlp…" });
        await RunProcessAsync(YtDlpPath, "-U", null, ct);
    }

    /// <summary>Risolve un link Spotify in una query di ricerca "artista titolo".</summary>
    public async Task<string> ResolveSpotifyAsync(string url, CancellationToken ct)
    {
        try
        {
            var html = await Http.GetStringAsync(url, ct);
            var m = Regex.Match(html, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success)
            {
                var title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                // "Titolo - song and lyrics by Artista | Spotify"
                var m2 = Regex.Match(title, @"^(.*?)\s+-\s+(?:song|brano|canzone)[^|]*?\b(?:by|di)\s+(.*?)\s*\|\s*Spotify", RegexOptions.IgnoreCase);
                if (m2.Success) return $"{m2.Groups[2].Value} {m2.Groups[1].Value}";
                title = Regex.Replace(title, @"\s*\|\s*Spotify\s*$", "");
                if (!string.IsNullOrWhiteSpace(title)) return title;
            }
        }
        catch { }

        // Fallback: oEmbed pubblico (solo titolo)
        var oembed = await Http.GetStringAsync("https://open.spotify.com/oembed?url=" + Uri.EscapeDataString(url), ct);
        var t = Regex.Match(oembed, "\"title\"\\s*:\\s*\"(.*?)\"");
        if (t.Success) return Regex.Unescape(t.Groups[1].Value);
        throw new InvalidOperationException("Impossibile leggere il brano da Spotify.");
    }

    public sealed record SpotifyTrack(string Artist, string Title);

    public static bool IsYouTubePlaylist(string url) =>
        IsYouTubeUrl(url) && Regex.IsMatch(url, @"[?&]list=([A-Za-z0-9_-]+)");

    public static bool IsSpotifyCollection(string url) =>
        IsSpotifyUrl(url) && Regex.IsMatch(url, @"open\.spotify\.com/(?:intl-[a-z]+/)?(playlist|album)/");

    /// <summary>
    /// Scarica un brano o un'intera playlist/album (YouTube o Spotify). Ritorna i file scaricati.
    /// </summary>
    public async Task<List<string>> DownloadAsync(string input, bool video, IProgress<DownloadStatus>? progress, CancellationToken ct)
    {
        await EnsureToolsAsync(progress, ct);
        Directory.CreateDirectory(AppPaths.DownloadsDir);

        if (IsSpotifyCollection(input))
            return await DownloadSpotifyCollectionAsync(input, video, progress, ct);

        string target;
        bool playlist = false;
        if (IsSpotifyUrl(input))
        {
            progress?.Report(new DownloadStatus { Message = "Leggo il brano da Spotify…" });
            var query = await ResolveSpotifyAsync(input, ct);
            progress?.Report(new DownloadStatus { Message = $"Cerco su YouTube: {query}" });
            target = "ytsearch1:" + query;
        }
        else if (input.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            target = input;
            playlist = IsYouTubePlaylist(input);
        }
        else
        {
            target = "ytsearch1:" + input; // testo libero → ricerca
        }

        var files = await RunYtDlpAsync(target, video, playlist, playlist ? "%(playlist_title)s" : null, null, 0, 1, progress, ct);
        if (files.Count == 0) throw new InvalidOperationException("Nessun file scaricato.");
        progress?.Report(new DownloadStatus { Message = files.Count == 1 ? "Completato" : $"Completati {files.Count} brani", Percent = 100 });
        return files;
    }

    // ------------------------------------------------------------------ Spotify playlist / album

    /// <summary>Legge nome e tracce di una playlist/album pubblici dalla pagina embed di Spotify.</summary>
    public async Task<(string Name, List<SpotifyTrack> Tracks)> ResolveSpotifyCollectionAsync(string url, CancellationToken ct)
    {
        var m = Regex.Match(url, @"open\.spotify\.com/(?:intl-[a-z]+/)?(playlist|album)/([A-Za-z0-9]+)");
        if (!m.Success) throw new InvalidOperationException("Link Spotify non riconosciuto.");
        var kind = m.Groups[1].Value;
        var id = m.Groups[2].Value;

        var html = await Http.GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}", ct);
        var js = Regex.Match(html, @"<script id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline);
        if (!js.Success) throw new InvalidOperationException("Pagina Spotify non leggibile (playlist privata?).");

        using var doc = JsonDocument.Parse(js.Groups[1].Value);
        JsonElement? entity = FindProperty(doc.RootElement, "trackList", out var parent) ? parent : null;
        if (entity == null) throw new InvalidOperationException("Nessuna traccia trovata (playlist privata o vuota?).");

        string name = kind == "album" ? "Album" : "Playlist";
        foreach (var key in new[] { "name", "title" })
            if (entity.Value.TryGetProperty(key, out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
            { name = n.GetString()!; break; }

        var tracks = new List<SpotifyTrack>();
        foreach (var item in entity.Value.GetProperty("trackList").EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var artist = item.TryGetProperty("subtitle", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title)) continue;
            // negli album il "subtitle" contiene gli artisti; nelle playlist pure. Prendiamo il primo artista.
            var firstArtist = artist.Split(',')[0].Trim();
            tracks.Add(new SpotifyTrack(firstArtist, title.Trim()));
        }
        if (tracks.Count == 0) throw new InvalidOperationException("Nessuna traccia trovata (playlist privata o vuota?).");
        return (name, tracks);
    }

    private static bool FindProperty(JsonElement el, string name, out JsonElement parent)
    {
        parent = default;
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (p.Name == name && p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0) { parent = el; return true; }
                if (FindProperty(p.Value, name, out parent)) return true;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                if (FindProperty(item, name, out parent)) return true;
        }
        return false;
    }

    private async Task<List<string>> DownloadSpotifyCollectionAsync(string url, bool video, IProgress<DownloadStatus>? progress, CancellationToken ct)
    {
        progress?.Report(new DownloadStatus { Message = "Leggo la playlist da Spotify…" });
        var (name, tracks) = await ResolveSpotifyCollectionAsync(url, ct);
        var folder = SanitizeFileName(name);

        var files = new List<string>();
        int failed = 0, skipped = 0;
        for (int i = 0; i < tracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tr = tracks[i];
            var label = $"Brano {i + 1}/{tracks.Count}: {tr.Artist} - {tr.Title}";
            if (TrackExists?.Invoke(tr.Artist, tr.Title) == true)
            {
                skipped++;
                progress?.Report(new DownloadStatus { Message = label + " — già in libreria, salto", Percent = 100.0 * (i + 1) / tracks.Count });
                continue;
            }
            try
            {
                var fileName = SanitizeFileName($"{tr.Artist} - {tr.Title}");
                var got = await RunYtDlpAsync("ytsearch1:" + tr.Artist + " " + tr.Title, video, false, folder, label, i, tracks.Count, progress, ct, fileName);
                foreach (var f in got) WriteTags(f, tr.Artist, tr.Title, name);
                files.AddRange(got);
                if (got.Count == 0) failed++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }
        }
        progress?.Report(new DownloadStatus
        {
            Message = $"Playlist \"{name}\": {files.Count} scaricati" + (skipped > 0 ? $", {skipped} già presenti" : "") + (failed > 0 ? $", {failed} non trovati" : ""),
            Percent = 100,
        });
        if (files.Count == 0 && skipped == 0) throw new InvalidOperationException("Nessun brano scaricato.");
        return files;
    }

    // ------------------------------------------------------------------ yt-dlp

    /// <param name="subfolder">Sottocartella (template yt-dlp o nome fisso) sotto la cartella download.</param>
    /// <param name="statusPrefix">Testo mostrato prima dell'avanzamento (es. "Brano 3/20: …").</param>
    /// <param name="itemIndex">Indice del brano corrente e <paramref name="itemCount"/> totale: per calcolare la percentuale complessiva quando si scaricano più brani uno alla volta.</param>
    private async Task<List<string>> RunYtDlpAsync(string target, bool video, bool playlist, string? subfolder, string? statusPrefix,
        int itemIndex, int itemCount, IProgress<DownloadStatus>? progress, CancellationToken ct, string? fileNameNoExt = null)
    {
        var outDir = subfolder == null ? AppPaths.DownloadsDir : Path.Combine(AppPaths.DownloadsDir, subfolder);
        var outTemplate = Path.Combine(outDir, (fileNameNoExt ?? "%(artist,uploader)s - %(track,title)s") + ".%(ext)s");
        var args = new List<string>
        {
            playlist ? "--yes-playlist" : "--no-playlist",
            "--newline", "--progress", "--windows-filenames", "--no-mtime", "--ignore-errors",
            "--embed-metadata",
            "--download-archive", Quote(ArchivePath),
            "--ffmpeg-location", Quote(AppPaths.ToolsDir),
            "-o", Quote(outTemplate),
        };
        if (File.Exists(DenoPath)) { args.Add("--js-runtimes"); args.Add(Quote("deno:" + DenoPath)); }
        args.Add("--print"); args.Add("after_move:filepath");
        if (video)
        {
            args.Add("-f"); args.Add(Quote("bv*[height<=1080][ext=mp4]+ba[ext=m4a]/bv*[height<=1080]+ba/b"));
            args.Add("--merge-output-format"); args.Add("mp4");
        }
        else
        {
            args.Add("-x"); args.Add("--audio-format"); args.Add("mp3"); args.Add("--audio-quality"); args.Add("0");
        }
        args.Add(Quote(target));

        var files = new List<string>();
        var lines = new List<string>();
        var before = Directory.Exists(outDir)
            ? new HashSet<string>(Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int plIndex = 0, plCount = 0;
        string prefix = statusPrefix ?? "";

        double Overall(double pct)
        {
            if (playlist && plCount > 0) return ((plIndex - 1) + pct / 100.0) / plCount * 100.0;
            if (itemCount > 1) return (itemIndex + pct / 100.0) / itemCount * 100.0;
            return pct;
        }

        int code = await RunProcessAsync(YtDlpPath, string.Join(' ', args), line =>
        {
            lines.Add(line);
            var item = Regex.Match(line, @"\[download\] Downloading item (\d+) of (\d+)");
            if (item.Success)
            {
                plIndex = int.Parse(item.Groups[1].Value);
                plCount = int.Parse(item.Groups[2].Value);
                prefix = $"Brano {plIndex}/{plCount}";
                progress?.Report(new DownloadStatus { Message = prefix + "…", Percent = Overall(0) });
                return;
            }
            var m = Regex.Match(line, @"\[download\]\s+([\d\.]+)%");
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                progress?.Report(new DownloadStatus
                {
                    Message = (prefix.Length > 0 ? prefix + " — " : "") + line.Replace("[download]", "").Trim(),
                    Percent = Overall(pct),
                });
            else if (line.StartsWith("[ExtractAudio]") || line.StartsWith("[Merger]"))
                progress?.Report(new DownloadStatus { Message = (prefix.Length > 0 ? prefix + " — " : "") + "conversione…", Percent = Overall(100) });
            else if (line.Length > 3 && File.Exists(line.Trim()))
                files.Add(line.Trim());
        }, ct);

        AppendLog(target, args, lines, files);

        if (files.Count == 0 && Directory.Exists(outDir))
        {
            // Fallback: yt-dlp potrebbe aver stampato i percorsi con un encoding diverso → prendiamo i file nuovi
            files = new DirectoryInfo(outDir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !before.Contains(f.FullName) && !f.Name.EndsWith(".part") && !f.Name.EndsWith(".ytdl"))
                .OrderBy(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToList();
        }

        if (files.Count == 0 && lines.Any(l => l.Contains("already been recorded in the archive")))
            throw new InvalidOperationException("Già scaricato in precedenza (presente nell'archivio download). Se l'hai cancellato, rimuovilo da " + ArchivePath);

        if (files.Count == 0)
        {
            var err = string.Join('\n', lines.Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).TakeLast(3));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"yt-dlp terminato con codice {code}" : err);
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Log dell'ultima esecuzione di yt-dlp, utile per diagnosticare i download.</summary>
    private static void AppendLog(string target, List<string> args, List<string> lines, List<string> files)
    {
        try
        {
            var log = Path.Combine(AppPaths.Root, "ytdlp.log");
            if (File.Exists(log) && new FileInfo(log).Length > 2_000_000) File.Delete(log);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine().AppendLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {target}");
            sb.AppendLine("args: " + string.Join(' ', args));
            foreach (var l in lines) sb.AppendLine(l);
            sb.AppendLine($"--> file riconosciuti: {files.Count}");
            File.AppendAllText(log, sb.ToString());
        }
        catch { }
    }

    private static void WriteTags(string path, string artist, string title, string album)
    {
        try
        {
            using var tf = TagLib.File.Create(path);
            tf.Tag.Title = title;
            tf.Tag.Performers = new[] { artist };
            tf.Tag.Album = album;
            tf.Save();
        }
        catch { }
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "Playlist" : name;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    private static async Task<int> RunProcessAsync(string exe, string args, Action<string>? onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine?.Invoke(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { p.Kill(true); } catch { } });
        await p.WaitForExitAsync(ct);
        return p.ExitCode;
    }

    private static async Task DownloadFileAsync(string url, string dest, IProgress<DownloadStatus>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? -1;
        var tmp = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0)
                    progress?.Report(new DownloadStatus { Message = $"Scarico {Path.GetFileName(dest)}… {read / 1048576} / {total / 1048576} MB", Percent = 100.0 * read / total });
            }
        }
        File.Move(tmp, dest, true);
    }
}
