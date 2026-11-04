using KaraokeDJ.Audio;
using NAudio.Wave;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --clicktest</c>: quanto è precisa la griglia, misurata su brani finti dove i colpi
/// stanno a istanti noti al campione. Sulla libreria vera (<c>--gridtest</c>) non si può misurare l'errore
/// in millisecondi, perché il battito "vero" non lo conosce nessuno: si sa solo quanti colpi cadono vicino
/// alla griglia. Qui invece la verità è scritta da noi, quindi si può dire se la griglia è *in ritardo o in
/// anticipo* e di quanto — che è esattamente ciò che fa entrare un passaggio fuori tempo.
///
/// Due numeri, e contano per motivi diversi:
/// - **scarto medio** (segnato): tutta la griglia spostata sempre dalla stessa parte. È il difetto peggiore,
///   perché non si vede guardando la forma d'onda e sposta ogni passaggio della stessa quantità.
/// - **sparpaglio** (90° percentile dell'errore assoluto): quanto balla da un battito all'altro.
/// </summary>
public static class ClickTest
{
    /// <summary>Oltre questo scarto medio la griglia è spostata in modo da sentirsi: 5 ms è già percepibile sui bassi.</summary>
    private const double MaxBiasMs = 5;
    /// <summary>Oltre questo sparpaglio i singoli battiti ballano (mezzo frame d'analisi sono 5,8 ms).</summary>
    private const double MaxSpreadMs = 8;

    public static string Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mixfonia-clicktest");
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        bool ok = true;

        foreach (double bpm in new[] { 100.0, 120.0, 128.0, 174.0 })
        {
            var (path, truth) = WriteSteady(dir, bpm);
            var r = Measure(path, truth);
            ok &= r.Pass;
            lines.Add($"  {bpm,5:0.0} BPM fisso   {r}");
        }

        // un pezzo che "respira" come quelli suonati a mano: da 120 a 124 BPM lungo il brano
        {
            var (path, truth) = WriteDrifting(dir, 120, 124);
            var r = Measure(path, truth);
            ok &= r.Pass;
            lines.Add($"  120→124 BPM    {r}");
        }

        return $"click: griglia {(ok ? "precisa" : "DA CORREGGERE")} (scarto medio ≤ {MaxBiasMs:0} ms, sparpaglio ≤ {MaxSpreadMs:0} ms)\n"
               + string.Join("\n", lines);
    }

    private readonly record struct Result(double BiasMs, double SpreadMs, double Bpm, int Beats, bool Fluid)
    {
        public bool Pass => Beats > 8 && Math.Abs(BiasMs) <= MaxBiasMs && SpreadMs <= MaxSpreadMs;

        public override string ToString()
        {
            if (Beats <= 8) return "nessuna griglia (l'analisi non ha trovato i battiti)";
            string verso = BiasMs < 0 ? "in anticipo" : "in ritardo";
            return $"scarto medio {Math.Abs(BiasMs),5:0.0} ms {verso,-11}  sparpaglio {SpreadMs,4:0.0} ms  "
                   + $"({Bpm,6:0.00} BPM, {Beats} battiti, griglia {(Fluid ? "fluida" : "fissa")})"
                   + (Pass ? "" : "   ⚠");
        }
    }

    /// <summary>Confronta la griglia dell'analisi con gli istanti veri: per ogni colpo, il battito più vicino.</summary>
    private static Result Measure(string path, double[] truth)
    {
        var a = AudioAnalyzer.Analyze(path);
        double[] beats = a.Beats ?? Array.Empty<double>();
        bool fluid = beats.Length > 8;
        if (!fluid)
        {
            // niente griglia fluida: si ricostruisce quella fissa (BPM + aggancio), che è ciò che userebbe l'app
            if (a.Bpm <= 0) return new Result(0, 0, a.Bpm, 0, false);
            double off = a.BeatOffsetSec >= 0 ? a.BeatOffsetSec : 0;
            double period = 60.0 / a.Bpm;
            int n = (int)((truth[^1] + period) / period) + 1;
            beats = Enumerable.Range(0, n).Select(i => off + i * period).ToArray();
        }

        var errs = new List<double>();
        foreach (double t in truth)
        {
            double best = double.MaxValue;
            foreach (double b in beats)
            {
                double d = b - t;
                if (Math.Abs(d) < Math.Abs(best)) best = d;
            }
            // oltre mezzo battito non è più "lo stesso battito": quel colpo è stato perso, non misurato male
            if (Math.Abs(best) < 0.2) errs.Add(best * 1000);
        }
        if (errs.Count < 8) return new Result(0, 0, a.Bpm, 0, fluid);

        errs.Sort();
        double bias = errs[errs.Count / 2];                       // mediana: un paio di battiti persi non la spostano
        var abs = errs.Select(Math.Abs).OrderBy(v => v).ToArray();
        double spread = abs[(int)(abs.Length * 0.9)];
        return new Result(bias, spread, a.Bpm, errs.Count, fluid);
    }

    // ------------------------------------------------------------------ i brani finti

    private static (string Path, double[] Truth) WriteSteady(string dir, double bpm)
    {
        // aggancio non rotondo: così un difetto che "arrotonda all'inizio del brano" si vede
        const double anchor = 0.37;
        double period = 60.0 / bpm;
        var truth = new List<double>();
        for (double t = anchor; t < 90; t += period) truth.Add(t);
        var path = Path.Combine(dir, $"click-{bpm:0}.wav");
        Render(path, truth, 91);
        return (path, truth.ToArray());
    }

    private static (string Path, double[] Truth) WriteDrifting(string dir, double from, double to)
    {
        var truth = new List<double>();
        double t = 0.37;
        const double dur = 90;
        while (t < dur)
        {
            truth.Add(t);
            double bpm = from + (to - from) * (t / dur);
            t += 60.0 / bpm;
        }
        var path = Path.Combine(dir, $"click-{from:0}-{to:0}.wav");
        Render(path, truth, dur + 1);
        return (path, truth.ToArray());
    }

    /// <summary>
    /// Cassa + colpo secco agli istanti dati, su un tappeto appena sussurrato. Il colpo è a banda larga
    /// perché l'analisi lavora sul flusso spettrale: una sinusoide sola non le fa vedere nessun attacco.
    /// </summary>
    private static void Render(string path, List<double> beats, double seconds)
    {
        int sr = SourceFactory.SampleRate;
        int frames = (int)(seconds * sr);
        var mono = new float[frames];
        var rnd = new Random(1234);                 // sempre uguale: la prova deve dare lo stesso numero ogni volta

        foreach (double bt in beats)
        {
            int at = (int)Math.Round(bt * sr);      // l'istante vero, al campione
            // transiente a banda larga, 3 ms
            int tr = (int)(0.003 * sr);
            for (int i = 0; i < tr && at + i < frames; i++)
                mono[at + i] += (float)((rnd.NextDouble() * 2 - 1) * 0.5 * (1 - (double)i / tr));
            // corpo della cassa: 60 Hz che si spegne in 120 ms
            int body = (int)(0.12 * sr);
            for (int i = 0; i < body && at + i < frames; i++)
                mono[at + i] += (float)(Math.Sin(2 * Math.PI * 60 * i / sr) * 0.55 * Math.Exp(-i / (0.035 * sr)));
        }
        // un filo di rumore di fondo: senza, lo scarto quadratico del flusso è quasi zero e il tracker si insospettisce
        for (int i = 0; i < frames; i++) mono[i] += (float)((rnd.NextDouble() * 2 - 1) * 0.0015);

        var buf = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            float v = Math.Clamp(mono[i], -1f, 1f);
            buf[i * 2] = v; buf[i * 2 + 1] = v;
        }
        using var w = new WaveFileWriter(path, SourceFactory.Format);
        w.WriteSamples(buf, 0, buf.Length);
    }
}
