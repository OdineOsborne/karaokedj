using System.Diagnostics;
using System.Text.RegularExpressions;

namespace KaraokeDJ.Services;

/// <summary>
/// Separazione vocale con Demucs (htdemucs) in un venv Python dedicato.
/// Installazione automatica: Python (winget) → venv → torch CPU + demucs. Elaborazione ~1–3 min/brano su CPU.
/// </summary>
public sealed class StemService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string VenvDir => Path.Combine(AppPaths.ToolsDir, "venv");
    public string VenvPython => Path.Combine(VenvDir, "Scripts", "python.exe");
    public string StemsDir => Path.Combine(AppPaths.Root, "stems");

    public bool IsReady => File.Exists(VenvPython) && File.Exists(Path.Combine(VenvDir, "Lib", "site-packages", "demucs", "__init__.py"))
        && Directory.Exists(Path.Combine(VenvDir, "Lib", "site-packages", "numpy"));

    public event Action<string, double>? Progress; // messaggio, percentuale (-1 = indeterminato)

    private void Report(string msg, double pct = -1) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Progress?.Invoke(msg, pct));

    // ------------------------------------------------------------------ installazione

    /// <summary>Trova un Python 3.9–3.12 "vero" (non l'alias dello Store).</summary>
    public static async Task<string?> FindPythonAsync(CancellationToken ct)
    {
        var candidates = new List<(string exe, string args)>
        {
            ("py", "-3.11"), ("py", "-3.12"), ("py", "-3.10"), ("py", "-3"),
            ("python3.11", ""), ("python", ""),
        };
        foreach (var (exe, args) in candidates)
        {
            try
            {
                var (code, output) = await RunAsync(exe, (args + " -c \"import sys;print(sys.executable);print(sys.version_info[:2])\"").Trim(), null, ct, 15000);
                if (code != 0) continue;
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length < 2) continue;
                var m = Regex.Match(lines[^1], @"\((\d+), (\d+)\)");
                if (!m.Success) continue;
                int major = int.Parse(m.Groups[1].Value), minor = int.Parse(m.Groups[2].Value);
                if (major == 3 && minor is >= 9 and <= 12 && File.Exists(lines[^2])) return lines[^2];
            }
            catch { }
        }
        return null;
    }

    public async Task EnsureInstalledAsync(CancellationToken ct)
    {
        if (IsReady) return;
        await Gate.WaitAsync(ct);
        try
        {
            if (IsReady) return;
            Report("Cerco Python…");
            var python = await FindPythonAsync(ct);
            if (python == null)
            {
                Report("Installo Python 3.11 (winget)…");
                var (code, output) = await RunAsync("winget", "install --id Python.Python.3.11 -e --silent --accept-source-agreements --accept-package-agreements", null, ct, 600000);
                python = await FindPythonAsync(ct);
                if (python == null) throw new InvalidOperationException("Python non trovato dopo l'installazione. Installa Python 3.11 da python.org e riprova.\n" + output.Split('\n').LastOrDefault());
            }
            if (!File.Exists(VenvPython))
            {
                Report("Creo l'ambiente Python…");
                var (code, output) = await RunAsync(python, $"-m venv \"{VenvDir}\"", null, ct, 300000);
                if (code != 0) throw new InvalidOperationException("Creazione venv fallita: " + output);
            }
            Report("Scarico PyTorch CPU (~200 MB, solo la prima volta)…");
            var (c1, o1) = await RunAsync(VenvPython, "-m pip install --upgrade pip", null, ct, 300000);
            var (c2, o2) = await RunAsync(VenvPython, "-m pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu", line => ReportPip(line), ct, 1800000);
            if (c2 != 0) throw new InvalidOperationException("Installazione torch fallita: " + Tail(o2));
            Report("Installo Demucs…");
            var (c3, o3) = await RunAsync(VenvPython, "-m pip install demucs \"numpy<2.3\"", line => ReportPip(line), ct, 1800000);
            if (c3 != 0) throw new InvalidOperationException("Installazione demucs fallita: " + Tail(o3));
            Report("Motore AI pronto", 100);
        }
        finally { Gate.Release(); }
    }

    private void ReportPip(string line)
    {
        if (line.Contains("Downloading") || line.Contains("Installing") || line.Contains("Collecting"))
            Report(line.Trim().Length > 90 ? line.Trim()[..90] : line.Trim());
    }

    // ------------------------------------------------------------------ separazione

    /// <summary>Separa voce e base. Ritorna (base senza voce, sola voce) in mp3.</summary>
    /// <summary>Cartella degli stem di un brano (vocals/drums/bass/other.mp3), o null se non ancora separati.</summary>
    public string? StemsDirFor(string trackId)
    {
        var d = Path.Combine(StemsDir, trackId);
        return Audio.StemMixReader.HasAll(d) ? d : null;
    }

    /// <summary>
    /// Separa il brano nei 4 stem (voce, batteria, basso, altro) e genera anche la base senza voce (somma degli altri 3).
    /// Ritorna (base senza voce, voce, cartella stem).
    /// </summary>
    public async Task<(string instrumental, string vocals, string stemsDir)> SeparateAsync(string trackId, string audioPath, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var outDir = Path.Combine(StemsDir, trackId);
        var inst = Path.Combine(outDir, "no_vocals.mp3");
        var voc = Path.Combine(outDir, "vocals.mp3");
        if (File.Exists(inst) && File.Exists(voc) && Audio.StemMixReader.HasAll(outDir)) return (inst, voc, outDir);

        await Gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(outDir);
            var work = Path.Combine(outDir, "work");
            Directory.CreateDirectory(work);
            Report("Separazione voce con Demucs (può richiedere qualche minuto)…", 0);
            var args = $"-m demucs -n htdemucs --mp3 --mp3-bitrate 192 -j 2 -o \"{work}\" \"{audioPath}\"";
            var (code, output) = await RunAsync(VenvPython, args, line =>
            {
                var m = Regex.Match(line, @"(\d{1,3})%\|");
                if (m.Success) Report("Demucs: " + m.Groups[1].Value + "%", double.Parse(m.Groups[1].Value));
                else if (line.Contains("Downloading", StringComparison.OrdinalIgnoreCase)) Report("Scarico il modello htdemucs (~80 MB)…");
            }, ct, 3600000);
            if (code != 0) throw new InvalidOperationException("Demucs fallito: " + Tail(output));

            // work\htdemucs\<nome>\{vocals,drums,bass,other}.mp3
            foreach (var name in Audio.StemMixReader.Names)
            {
                var produced = Directory.EnumerateFiles(work, name + ".mp3", SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidOperationException($"Demucs non ha prodotto {name}.mp3.\n" + Tail(output));
                File.Move(produced, Path.Combine(outDir, name + ".mp3"), true);
            }
            try { Directory.Delete(work, true); } catch { }
            Report("Creo la base senza voce…", 95);
            await Task.Run(() => RenderNoVocals(outDir, inst), ct);
            Report("Separazione completata", 100);
            return (inst, voc, outDir);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Somma batteria+basso+altro in no_vocals.mp3 (AAC/MP3 via Media Foundation; fallback WAV rinominato).</summary>
    private static void RenderNoVocals(string stemsDir, string outPath)
    {
        using var mix = new Audio.StemMixReader(stemsDir);
        mix.SetGain(0, 0f); // voce muta
        var wav = Path.ChangeExtension(outPath, ".wav");
        using (var w = new NAudio.Wave.WaveFileWriter(wav, mix.WaveFormat))
        {
            var buf = new float[Audio.SourceFactory.SampleRate * 2];
            int n;
            while ((n = mix.Read(buf, 0, buf.Length)) > 0) w.WriteSamples(buf, 0, n);
        }
        try
        {
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
            using var r = new NAudio.Wave.WaveFileReader(wav);
            NAudio.Wave.MediaFoundationEncoder.EncodeToMp3(r, outPath, 192000);
            File.Delete(wav);
        }
        catch
        {
            // nessun encoder MP3 disponibile: teniamo il WAV col nome atteso
            File.Move(wav, outPath, true);
        }
    }

    private static string Tail(string s) => string.Join('\n', s.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(4));

    private static async Task<(int code, string output)> RunAsync(string exe, string args, Action<string>? onLine, CancellationToken ct, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        var sb = new System.Text.StringBuilder();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Handle(string? data)
        {
            if (data == null) return;
            // tqdm usa \r: spezziamo
            foreach (var part in data.Split('\r'))
            {
                if (part.Length == 0) continue;
                lock (sb) sb.AppendLine(part);
                onLine?.Invoke(part);
            }
        }
        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived += (_, e) => Handle(e.Data);
        try { p.Start(); }
        catch (Exception ex) { return (-1, ex.Message); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        using var reg = cts.Token.Register(() => { try { p.Kill(true); } catch { } });
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; return (-2, "timeout"); }
        string output; lock (sb) output = sb.ToString();
        return (p.ExitCode, output);
    }
}
