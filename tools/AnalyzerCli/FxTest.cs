using KaraokeDJ.Audio;

public static class FxTest
{
    public static void Run()
    {
        const int fs = 44100; int n = fs * 3;
        // "voce" 1 kHz al centro, "strumento" 220 Hz solo a sinistra, basso 60 Hz al centro
        var buf = new float[n * 2];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / fs;
            float voice = (float)(0.3 * Math.Sin(2 * Math.PI * 1000 * t));
            float inst = (float)(0.3 * Math.Sin(2 * Math.PI * 220 * t));
            float bass = (float)(0.3 * Math.Sin(2 * Math.PI * 60 * t));
            buf[2 * i] = voice + inst + bass; buf[2 * i + 1] = voice + bass;
        }
        double E(float[] b, double f) { double re = 0, im = 0; for (int i = 0; i < n; i++) { double t = (double)i / fs; double m = 0.5 * (b[2 * i] + b[2 * i + 1]); re += m * Math.Cos(2 * Math.PI * f * t); im += m * Math.Sin(2 * Math.PI * f * t); } return Math.Sqrt(re * re + im * im) / n; }
        var fx = new FxChain { VocalRemove = true, VocalStrength = 1f };
        var outb = (float[])buf.Clone();
        fx.Process(outb, 0, outb.Length);
        Console.WriteLine($"VOCE OFF: 1kHz {E(buf, 1000):0.000} → {E(outb, 1000):0.000}   220Hz(L) {E(buf, 220):0.000} → {E(outb, 220):0.000}   basso 60Hz {E(buf, 60):0.000} → {E(outb, 60):0.000}");

        // stabilità: tutti gli effetti accesi su rumore, controlliamo che non esploda
        var rnd = new Random(3); var noise = new float[n * 2]; for (int i = 0; i < noise.Length; i++) noise[i] = (float)(rnd.NextDouble() - 0.5);
        var all = new FxChain { EchoOn = true, EchoFeedback = 0.9f, EchoMix = 1f, ReverbOn = true, ReverbSize = 1f, ReverbMix = 1f, FlangerOn = true, Filter = -0.5f, VocalRemove = true };
        float pk = 0; bool nan = false;
        for (int k = 0; k < 4; k++) { var nb = (float[])noise.Clone(); all.Process(nb, 0, nb.Length); pk = Math.Max(pk, nb.Max(Math.Abs)); nan |= nb.Any(float.IsNaN); }
        Console.WriteLine($"Tutti gli FX su rumore, picco dopo 12 s: {pk:0.00} (input 0.5)  NaN: {nan}");

        // echo: impulso → ripetizioni a EchoTimeSec
        var imp = new float[n * 2]; imp[0] = imp[1] = 1f;
        var e = new FxChain { EchoOn = true, EchoTimeSec = 0.25f, EchoFeedback = 0.5f, EchoMix = 1f };
        e.Process(imp, 0, imp.Length);
        var peaks = new List<string>();
        for (int i = 1; i < n; i++) if (Math.Abs(imp[2 * i]) > 0.05 && Math.Abs(imp[2 * i]) >= Math.Abs(imp[2 * (i - 1)]) && Math.Abs(imp[2 * i]) > Math.Abs(imp[2 * (i + 1)])) peaks.Add($"{i / (double)fs:0.000}s={Math.Abs(imp[2 * i]):0.00}");
        Console.WriteLine("ECHO impulso, ripetizioni: " + string.Join(" ", peaks.Take(5)));

        // echo out: dry a 0 → l'uscita è solo coda
        var eo = new FxChain { EchoOn = true, EchoTimeSec = 0.2f, EchoFeedback = 0.7f, EchoMix = 1f };
        var sig = new float[fs * 2]; for (int i = 0; i < fs; i++) sig[2 * i] = sig[2 * i + 1] = (float)Math.Sin(2 * Math.PI * 440 * i / (double)fs) * 0.5f;
        eo.Process(sig, 0, sig.Length);
        eo.Dry = 0f;
        var tail = new float[fs * 2]; for (int i = 0; i < fs; i++) tail[2 * i] = tail[2 * i + 1] = 0.5f;
        eo.Process(tail, 0, tail.Length);
        Console.WriteLine($"ECHO OUT: rms primo 0.2s {Rms(tail, 0, (int)(0.2 * fs)):0.000}, 0.2–0.4 {Rms(tail, (int)(0.2 * fs), (int)(0.2 * fs)):0.000}, 0.8–1.0 {Rms(tail, (int)(0.8 * fs), (int)(0.2 * fs)):0.000}");
    }
    static double Rms(float[] b, int from, int len) { double s = 0; for (int i = from; i < from + len; i++) s += b[2 * i] * b[2 * i]; return Math.Sqrt(s / len); }
}
