using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using NAudio.Midi;

namespace KaraokeDJ.Services;

/// <summary>Una sillaba/parola del testo karaoke con il suo istante.</summary>
public sealed record LyricEvent(double Sec, string Text, bool NewLine);

/// <summary>
/// File MIDI / KAR: vengono resi audio con FluidSynth (installato automaticamente con il soundfont MuseScore General, MIT)
/// e messi in cache; il testo (eventi Lyric/Text) viene estratto con la mappa dei tempi per il proiettore.
/// </summary>
public sealed class MidiRenderService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const string FluidZip = "https://github.com/FluidSynth/fluidsynth/releases/download/v2.6.0/fluidsynth-v2.6.0-win10-x64-cpp11.zip";
    private const string SoundFontUrl = "https://ftp.osuosl.org/pub/musescore/soundfont/MuseScore_General/MuseScore_General.sf3";

    public static readonly string[] Extensions = { ".mid", ".midi", ".kar" };
    public string ToolDir => Path.Combine(AppPaths.ToolsDir, "fluidsynth");
    public string FluidExe => Path.Combine(ToolDir, "fluidsynth.exe");
    public string SoundFont => Path.Combine(AppPaths.ToolsDir, "MuseScore_General.sf3");
    public static string CacheDir => Path.Combine(AppPaths.Root, "midi");
    public bool IsReady => File.Exists(FluidExe) && File.Exists(SoundFont);

    public event Action<string, double>? Progress;
    private void Report(string msg, double pct = -1) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Progress?.Invoke(msg, pct));

    public static bool IsMidi(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static string RenderedPathFor(string trackId) => Path.Combine(CacheDir, trackId + ".mp3");
    public static string? Rendered(string trackId)
    {
        var p = RenderedPathFor(trackId);
        if (File.Exists(p)) return p;
        var w = Path.ChangeExtension(p, ".wav");
        return File.Exists(w) ? w : null;
    }

    public async Task EnsureToolsAsync(CancellationToken ct)
    {
        if (IsReady) return;
        Directory.CreateDirectory(AppPaths.ToolsDir);
        if (!File.Exists(FluidExe))
        {
            Report("Scarico FluidSynth (sintetizzatore MIDI, ~3 MB)…");
            var zip = Path.Combine(AppPaths.ToolsDir, "fluidsynth.zip");
            await DownloadAsync(FluidZip, zip, ct);
            Directory.CreateDirectory(ToolDir);
            using (var z = ZipFile.OpenRead(zip))
                foreach (var e in z.Entries.Where(e => e.FullName.Contains("/bin/") && e.Name.Length > 0))
                    e.ExtractToFile(Path.Combine(ToolDir, e.Name), true);
            File.Delete(zip);
        }
        if (!File.Exists(SoundFont))
        {
            Report("Scarico il soundfont General MIDI (MuseScore General, ~38 MB)…");
            await DownloadAsync(SoundFontUrl, SoundFont, ct);
        }
        Report("Motore MIDI pronto", 100);
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken ct)
    {
        var tmp = dest + ".part";
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) Report($"Scarico {Path.GetFileName(dest)}: {100.0 * done / total:0}%", 100.0 * done / total);
            }
        }
        File.Move(tmp, dest, true);
    }

    /// <summary>Rende il MIDI in audio (cache per id). Ritorna il percorso del file audio.</summary>
    public async Task<string> RenderAsync(string trackId, string midiPath, CancellationToken ct)
    {
        var cached = Rendered(trackId);
        if (cached != null) return cached;
        await EnsureToolsAsync(ct);
        await Gate.WaitAsync(ct);
        try
        {
            cached = Rendered(trackId);
            if (cached != null) return cached;
            Directory.CreateDirectory(CacheDir);
            var wav = Path.Combine(CacheDir, trackId + ".wav");
            Report("Rendering MIDI → audio…");
            var psi = new ProcessStartInfo(FluidExe, $"-ni -g 0.7 -r 44100 -F \"{wav}\" \"{SoundFont}\" \"{midiPath}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("FluidSynth non avviato");
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 || !File.Exists(wav)) throw new InvalidOperationException("FluidSynth: " + (await err).Trim());
            // in mp3 per risparmiare spazio (Media Foundation); se non riesce resta il wav
            var mp3 = RenderedPathFor(trackId);
            try
            {
                NAudio.MediaFoundation.MediaFoundationApi.Startup();
                using (var r = new NAudio.Wave.WaveFileReader(wav))
                    NAudio.Wave.MediaFoundationEncoder.EncodeToMp3(r, mp3, 192000);
                File.Delete(wav);
                Report("MIDI pronto", 100);
                return mp3;
            }
            catch { Report("MIDI pronto (wav)", 100); return wav; }
        }
        finally { Gate.Release(); }
    }

    /// <summary>Durata (s) e BPM iniziali dalla mappa dei tempi.</summary>
    public static (double durationSec, double bpm) Info(string path)
    {
        try
        {
            var mf = new MidiFile(path, false);
            var (map, lastTick) = TempoMap(mf);
            double bpm = 0;
            foreach (var t in mf.Events[0]) if (t is TempoEvent te) { bpm = 60_000_000.0 / te.MicrosecondsPerQuarterNote; break; }
            return (TickToSec(map, lastTick, mf.DeltaTicksPerQuarterNote), Math.Round(bpm, 1));
        }
        catch { return (0, 0); }
    }

    /// <summary>Testo karaoke con tempi: convenzioni KAR ("/" e "\" = nuova riga, "@…" = intestazioni ignorate).</summary>
    public static List<LyricEvent> ExtractLyrics(string path)
    {
        var list = new List<LyricEvent>();
        try
        {
            var mf = new MidiFile(path, false);
            var (map, _) = TempoMap(mf);
            int ppq = mf.DeltaTicksPerQuarterNote;
            // preferisce gli eventi Lyric; se non ci sono usa i Text (molti .kar)
            var lyric = new List<(long tick, string text)>(); var text = new List<(long tick, string text)>();
            for (int tr = 0; tr < mf.Tracks; tr++)
                foreach (var e in mf.Events[tr])
                {
                    if (e is not TextEvent te) continue;
                    var s = te.Text;
                    if (string.IsNullOrEmpty(s) || s.StartsWith("@") || s.StartsWith("%")) continue;
                    if (te.MetaEventType == MetaEventType.Lyric) lyric.Add((e.AbsoluteTime, s));
                    else if (te.MetaEventType == MetaEventType.TextEvent) text.Add((e.AbsoluteTime, s));
                }
            var src = lyric.Count >= 4 ? lyric : text;
            foreach (var (tick, s) in src.OrderBy(x => x.tick))
            {
                var t = s;
                bool nl = false;
                if (t.StartsWith("/") || t.StartsWith("\\")) { nl = true; t = t[1..]; }
                t = t.Replace("\r", "").Replace("\n", " ");
                if (t.Length == 0 && !nl) continue;
                list.Add(new LyricEvent(TickToSec(map, tick, ppq), t, nl));
            }
        }
        catch { }
        return list;
    }

    private static (List<(long tick, double sec, int usPerQ)> map, long lastTick) TempoMap(MidiFile mf)
    {
        var tempos = new List<(long tick, int us)>();
        long last = 0;
        for (int tr = 0; tr < mf.Tracks; tr++)
            foreach (var e in mf.Events[tr])
            {
                if (e is TempoEvent te) tempos.Add((e.AbsoluteTime, te.MicrosecondsPerQuarterNote));
                if (e.AbsoluteTime > last) last = e.AbsoluteTime;
            }
        tempos.Sort((a, b) => a.tick.CompareTo(b.tick));
        var map = new List<(long, double, int)>();
        double sec = 0; long prevTick = 0; int cur = 500000;
        map.Add((0, 0, cur));
        foreach (var (tick, us) in tempos)
        {
            sec += (tick - prevTick) * cur / 1e6 / mf.DeltaTicksPerQuarterNote;
            prevTick = tick; cur = us;
            map.Add((tick, sec, cur));
        }
        return (map, last);
    }

    private static double TickToSec(List<(long tick, double sec, int usPerQ)> map, long tick, int ppq)
    {
        var seg = map[0];
        foreach (var m in map) { if (m.tick <= tick) seg = m; else break; }
        return seg.sec + (tick - seg.tick) * seg.usPerQ / 1e6 / ppq;
    }
}
