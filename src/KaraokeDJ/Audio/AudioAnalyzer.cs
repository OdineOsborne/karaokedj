using NAudio.Dsp;

namespace KaraokeDJ.Audio;

/// <param name="Waveform">Per colonna: [picco 0..255, RMS 0..255], <see cref="AudioAnalyzer.WaveColumns"/> colonne.</param>
public sealed record AnalysisResult(double Bpm, string Key, string Camelot, double KeyConfidence, double IntroEndSec, double OutroStartSec, byte[] Waveform,
    double Energy = 0, double Brightness = 0, double BeatOffsetSec = -1);

/// <summary>
/// Stima BPM (flusso spettrale + autocorrelazione) e tonalità (chroma + profili di Krumhansl)
/// da un estratto del brano. Precisione da "DJ pratico": BPM ±1, tonalità indicativa.
/// </summary>
public static class AudioAnalyzer
{
    private const int Fft = 2048;
    private const int Hop = 512;
    private const double ExcerptSeconds = 75;
    public const int WaveColumns = 800;
    private const int EnvFrame = 4410; // 100 ms

    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    // Profili Krumhansl-Kessler
    private static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
    private static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
    // Maggiore "blues/mixolidio" (settima minore, terza minore di passaggio): tipico di rock e blues, riportato come maggiore
    private static readonly double[] MixoProfile = { 6.35, 2.23, 3.48, 3.20, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 4.20, 2.00 };

    // Camelot: indice per tonica (0 = C) → numero per maggiore; il relativo minore ha lo stesso numero con "A"
    private static readonly double[] HarmonicWeight = { 1.0, 0.5, 0.33, 0.25 };

    private static readonly int[] CamelotMajor = { 8, 3, 10, 5, 12, 7, 2, 9, 4, 11, 6, 1 };

    /// <summary>
    /// Istanti dei colpi (onset) in secondi, dal flusso spettrale a piena risoluzione (~12 ms).
    /// Serve a verificare la griglia dei battiti: l'onda "fine" (50 colonne al secondo) è troppo grossa per quello.
    /// </summary>
    public static List<double> OnsetTimes(string path, double maxSeconds = 90, CancellationToken ct = default)
    {
        var (reader, provider) = SourceFactory.Open(path);
        using (reader)
        {
            int fs = SourceFactory.SampleRate;
            int want = (int)(maxSeconds * fs);
            var x = new float[want];
            int got = 0;
            var buf = new float[8192 * 2];
            while (got < want)
            {
                int n = provider.Read(buf, 0, buf.Length);
                if (n == 0) break;
                for (int i = 0; i + 1 < n && got < want; i += 2) x[got++] = 0.5f * (buf[i] + buf[i + 1]);
            }
            int frames = (got - Fft) / Hop;
            if (frames < 50) return new List<double>();

            var flux = new double[frames];
            var prevMag = new double[Fft / 2];
            var window = new double[Fft];
            for (int i = 0; i < Fft; i++) window[i] = FastFourierTransform.HannWindow(i, Fft);
            var cplx = new Complex[Fft];
            int m = (int)Math.Log2(Fft);
            for (int fr = 0; fr < frames; fr++)
            {
                ct.ThrowIfCancellationRequested();
                int off = fr * Hop;
                for (int i = 0; i < Fft; i++) { cplx[i].X = (float)(x[off + i] * window[i]); cplx[i].Y = 0; }
                FastFourierTransform.FFT(true, m, cplx);
                double fl = 0;
                for (int k = 1; k < Fft / 2; k++)
                {
                    double mag = Math.Sqrt(cplx[k].X * cplx[k].X + cplx[k].Y * cplx[k].Y);
                    double d = mag - prevMag[k];
                    if (d > 0) fl += d;
                    prevMag[k] = mag;
                }
                flux[fr] = fl;
            }

            // picchi: massimi locali sopra la media mobile di ±0,5 s
            double frameSec = (double)Hop / fs;
            int w = Math.Max(3, (int)(0.5 / frameSec));
            var onsets = new List<double>();
            double lastT = -1;
            for (int i = 1; i < frames - 1; i++)
            {
                if (flux[i] <= flux[i - 1] || flux[i] < flux[i + 1]) continue;
                double sum = 0; int n2 = 0;
                for (int j = Math.Max(0, i - w); j < Math.Min(frames, i + w); j++) { sum += flux[j]; n2++; }
                double local = sum / Math.Max(1, n2);
                if (flux[i] < local * 1.6) continue;
                double t = i * frameSec;
                if (lastT >= 0 && t - lastT < 0.09) continue;
                onsets.Add(t); lastT = t;
            }
            return onsets;
        }
    }

    public static AnalysisResult Analyze(string path, CancellationToken ct = default)
    {
        var (reader, provider) = SourceFactory.Open(path);
        using (reader)
        {
            int fs = SourceFactory.SampleRate;
            double total = reader.TotalTime.TotalSeconds;
            long totalFrames = Math.Max(1, (long)(total * fs));
            double colLen = (double)totalFrames / WaveColumns;

            // estratto centrale per BPM / tonalità
            double exStart = total > ExcerptSeconds + 20 ? Math.Max(0, total * 0.35 - ExcerptSeconds / 2) : 0;
            long exStartFrame = (long)(exStart * fs);
            int exWant = (int)(Math.Min(ExcerptSeconds, Math.Max(10, total - exStart)) * fs);
            var excerpt = new float[exWant];
            int exGot = 0;

            var colPeak = new float[WaveColumns];
            var colSq = new double[WaveColumns];
            var colN = new int[WaveColumns];
            var env = new List<double>();
            double envSq = 0; int envN = 0;

            var buf = new float[8192 * 2];
            long pos = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int n = provider.Read(buf, 0, buf.Length);
                if (n == 0) break;
                for (int i = 0; i + 1 < n; i += 2, pos++)
                {
                    float s = 0.5f * (buf[i] + buf[i + 1]);
                    int col = (int)Math.Min(WaveColumns - 1, pos / colLen);
                    float a = Math.Abs(s);
                    if (a > colPeak[col]) colPeak[col] = a;
                    colSq[col] += s * s; colN[col]++;
                    envSq += s * s;
                    if (++envN == EnvFrame) { env.Add(Math.Sqrt(envSq / envN)); envSq = 0; envN = 0; }
                    if (pos >= exStartFrame && exGot < exWant) excerpt[exGot++] = s;
                }
            }
            if (envN > 0) env.Add(Math.Sqrt(envSq / envN));
            if (pos < fs * 5) throw new InvalidOperationException("Brano troppo corto per l'analisi.");

            // forma d'onda normalizzata
            float maxPeak = Math.Max(1e-6f, colPeak.Max());
            var wave = new byte[WaveColumns * 2];
            for (int c = 0; c < WaveColumns; c++)
            {
                double rms = colN[c] > 0 ? Math.Sqrt(colSq[c] / colN[c]) : 0;
                wave[c * 2] = (byte)Math.Clamp(colPeak[c] / maxPeak * 255, 0, 255);
                wave[c * 2 + 1] = (byte)Math.Clamp(rms / maxPeak * 255, 0, 255);
            }

            var (introEnd, outroStart) = DetectIntroOutro(env, 0.1, pos / (double)fs);
            var core = AnalyzeSamples(excerpt, exGot, fs, ct, exStart);
            return core with { IntroEndSec = introEnd, OutroStartSec = outroStart, Waveform = wave };
        }
    }

    /// <summary>
    /// Intro = dall'inizio fino a quando il brano raggiunge stabilmente il suo livello "pieno";
    /// outro = dall'ultimo momento a livello pieno fino alla fine. Soglia: -6 dB dal 75° percentile.
    /// </summary>
    private static (double introEnd, double outroStart) DetectIntroOutro(List<double> env, double frameSec, double duration)
    {
        int n = env.Count;
        if (n < 50) return (0, duration);
        var db = env.Select(v => 20 * Math.Log10(Math.Max(v, 1e-6))).ToArray();
        // media mobile 2 s
        int w = (int)Math.Round(2.0 / frameSec);
        var sm = new double[n];
        double acc = 0; int cnt = 0;
        var q = new Queue<double>();
        for (int i = 0; i < n; i++)
        {
            q.Enqueue(db[i]); acc += db[i]; cnt++;
            if (q.Count > w) { acc -= q.Dequeue(); cnt--; }
            sm[i] = acc / cnt;
        }
        var sorted = sm.OrderBy(v => v).ToArray();
        double p75 = sorted[(int)(sorted.Length * 0.75)];
        double thr = p75 - 6;
        int sustain = (int)Math.Round(4.0 / frameSec);

        int introIdx = 0;
        for (int i = 0; i < n; i++)
        {
            if (sm[i] < thr) continue;
            int ok = 0;
            for (int j = i; j < Math.Min(n, i + sustain); j++) if (sm[j] >= thr - 2) ok++;
            if (ok >= sustain * 0.8) { introIdx = i; break; }
        }
        int outroIdx = n - 1;
        for (int i = n - 1; i >= 0; i--)
        {
            if (sm[i] < thr) continue;
            int ok = 0;
            for (int j = i; j > Math.Max(-1, i - sustain); j--) if (sm[j] >= thr - 2) ok++;
            if (ok >= sustain * 0.8) { outroIdx = i; break; }
        }
        double introEnd = introIdx * frameSec;
        double outroStart = Math.Min(duration, (outroIdx + 1) * frameSec);
        if (introEnd < 3) introEnd = 0;
        if (duration - outroStart < 3) outroStart = duration;
        return (Math.Round(introEnd, 1), Math.Round(outroStart, 1));
    }

    private static AnalysisResult AnalyzeSamples(float[] x, int len, int fs, CancellationToken ct, double startSec = 0)
    {
        int frames = (len - Fft) / Hop;
        if (frames < 50) throw new InvalidOperationException("Brano troppo corto per l'analisi.");

        var flux = new double[frames];
        double centroidSum = 0; int centroidN = 0;
        var prevMag = new double[Fft / 2];
        var window = new double[Fft];
        for (int i = 0; i < Fft; i++) window[i] = FastFourierTransform.HannWindow(i, Fft);
        var cplx = new Complex[Fft];
        int m = (int)Math.Log2(Fft);

        for (int fr = 0; fr < frames; fr++)
        {
            if ((fr & 63) == 0) ct.ThrowIfCancellationRequested();
            int off = fr * Hop;
            for (int i = 0; i < Fft; i++)
            {
                cplx[i].X = (float)(x[off + i] * window[i]);
                cplx[i].Y = 0;
            }
            FastFourierTransform.FFT(true, m, cplx);

            double fl = 0, magSum = 0, magFreq = 0;
            for (int k = 1; k < Fft / 2; k++)
            {
                double mag = Math.Sqrt(cplx[k].X * cplx[k].X + cplx[k].Y * cplx[k].Y);
                double d = mag - prevMag[k];
                if (d > 0) fl += d;
                prevMag[k] = mag;
                magSum += mag; magFreq += mag * ((double)k * fs / Fft);   // per il centro di gravità dello spettro
            }
            flux[fr] = fl;
            if (magSum > 1e-9) { centroidSum += magFreq / magSum; centroidN++; }
        }

        double bpm = EstimateBpm(flux, fs);
        var segs = ComputeChromaSegments(x, len, fs, ct);
        if (Environment.GetEnvironmentVariable("KDJ_DEBUG_CHROMA") == "1")
        {
            var g = new double[12]; foreach (var s in segs) for (int i = 0; i < 12; i++) g[i] += s[i];
            double mx = g.Max(); Console.Error.WriteLine("chroma: " + string.Join(" ", NoteNames.Select((n, i) => $"{n}={g[i] / mx:0.00}")));
        }
        var (key, camelot, conf) = EstimateKeyVoting(segs);
        // carattere del suono: quanto spinge (livello medio) e quanto è brillante (centro di gravità dello spettro)
        double sq = 0; float peak = 0;
        for (int i = 0; i < len; i++) { sq += (double)x[i] * x[i]; float ab = Math.Abs(x[i]); if (ab > peak) peak = ab; }
        double rms = Math.Sqrt(sq / Math.Max(1, len));
        double rmsDb = 20 * Math.Log10(Math.Max(rms, 1e-6));
        double energy = Math.Clamp((rmsDb + 26) / 16, 0.02, 1);
        double centroid = centroidN > 0 ? centroidSum / centroidN : 0;
        double brightness = Math.Clamp((centroid - 700) / 2800, 0.02, 1);
        // aggancio della griglia: qui abbiamo il flusso spettrale a ~12 ms, molto più preciso dell'onda a 20 ms
        double anchor = bpm > 0 ? EstimateAnchor(flux, (double)Hop / fs, startSec, bpm) : -1;
        return new AnalysisResult(bpm, key, camelot, conf, 0, 0, Array.Empty<byte>(), energy, brightness, anchor);
    }

    /// <summary>
    /// Fase della griglia: l'istante (dall'inizio del brano, meno di una battuta) su cui cadono i colpi.
    /// Si prova ogni fase possibile e si tiene quella su cui si accumula più "attacco"; poi si sceglie,
    /// fra i quattro battiti, quello che fa da "1" della battuta.
    /// </summary>
    private static double EstimateAnchor(double[] flux, double hopSec, double startSec, double bpm)
    {
        double beat = 60.0 / bpm;
        const int Steps = 96;
        const double Tol = 0.045;
        double Score(double off, double period)
        {
            double sum = 0;
            for (int i = 0; i < flux.Length; i++)
            {
                double t = startSec + i * hopSec;
                double ph = ((t - off) / period) % 1.0; if (ph < 0) ph += 1;
                double d = Math.Min(ph, 1 - ph) * period;
                if (d <= Tol) sum += flux[i];
            }
            return sum;
        }
        double best = 0, bestScore = -1;
        for (int s = 0; s < Steps; s++)
        {
            double off = beat * s / Steps;
            double sc = Score(off, beat);
            if (sc > bestScore) { bestScore = sc; best = off; }
        }
        // il "1": fra i quattro battiti della battuta, quello con più attacco
        double bestBar = best, barScore = -1;
        for (int k = 0; k < 4; k++)
        {
            double off = best + beat * k;
            double sc = Score(off, beat * 4);
            if (sc > barScore) { barScore = sc; bestBar = off; }
        }
        return Math.Round(bestBar % (beat * 4), 3);
    }

    /// <summary>Chroma a 12 classi con FFT lunga (risoluzione 5 Hz) così anche le note basse cadono nel bin giusto.</summary>
    private static List<double[]> ComputeChromaSegments(float[] x, int len, int fs, CancellationToken ct)
    {
        const int N = 8192, H = 4096, SegFrames = 40;
        var segments = new List<double[]>();
        var chroma = new double[12];
        int frames = (len - N) / H;
        if (frames <= 0) return segments;
        var window = new double[N];
        for (int i = 0; i < N; i++) window[i] = FastFourierTransform.HannWindow(i, N);
        var cplx = new Complex[N];
        var mags = new double[N / 2];
        int m = (int)Math.Log2(N);
        var binPitch = new int[N / 2];
        for (int k = 1; k < N / 2; k++)
        {
            double f = (double)k * fs / N;
            if (f < 80 || f > 2000) { binPitch[k] = -1; continue; }
            double midi = 69 + 12 * Math.Log2(f / 440.0);
            // accetta solo i bin vicini al centro della nota (±35 cent) per ridurre lo spargimento
            if (Math.Abs(midi - Math.Round(midi)) > 0.35) { binPitch[k] = -1; continue; }
            binPitch[k] = (((int)Math.Round(midi) % 12) + 12) % 12;
        }
        for (int fr = 0; fr < frames; fr++)
        {
            if ((fr & 15) == 0) ct.ThrowIfCancellationRequested();
            if (fr > 0 && fr % SegFrames == 0) { segments.Add(chroma); chroma = new double[12]; }
            int off = fr * H;
            for (int i = 0; i < N; i++) { cplx[i].X = (float)(x[off + i] * window[i]); cplx[i].Y = 0; }
            FastFourierTransform.FFT(true, m, cplx);
            // magnitudini + soglia relativa: contano solo i picchi spettrali (note), non il rumore di fondo
            double maxMag = 0;
            for (int k = 1; k < N / 2; k++)
            {
                double mg = Math.Sqrt(cplx[k].X * cplx[k].X + cplx[k].Y * cplx[k].Y);
                mags[k] = mg;
                if (mg > maxMag) maxMag = mg;
            }
            double thr = maxMag * 0.05;
            for (int k = 2; k < N / 2 - 1; k++)
            {
                double mg = mags[k];
                if (mg < thr || mg < mags[k - 1] || mg < mags[k + 1]) continue; // solo massimi locali
                double f0 = (double)k * fs / N;
                if (f0 < 60 || f0 > 4000) continue;
                double val = Math.Log(1 + mg / thr);
                // somma sub-armonica: il picco vota per la propria nota e per le fondamentali di cui potrebbe essere armonico
                for (int h = 1; h <= 4; h++)
                {
                    double f = f0 / h;
                    if (f < 55) break;
                    double midi = 69 + 12 * Math.Log2(f / 440.0);
                    if (Math.Abs(midi - Math.Round(midi)) > 0.35) continue;
                    int pc = (((int)Math.Round(midi) % 12) + 12) % 12;
                    chroma[pc] += val * HarmonicWeight[h - 1];
                }
            }
        }
        segments.Add(chroma);
        return segments;
    }

    private static double EstimateBpm(double[] flux, int fs)
    {
        int n = flux.Length;
        // normalizzazione locale: togliamo la media mobile per enfatizzare gli attacchi
        double mean = flux.Average();
        var o = new double[n];
        for (int i = 0; i < n; i++) o[i] = Math.Max(0, flux[i] - mean);

        double framesPerSec = (double)fs / Hop;
        int minLag = (int)Math.Floor(framesPerSec * 60.0 / 200.0); // 200 BPM
        int maxLag = (int)Math.Ceiling(framesPerSec * 60.0 / 60.0); // 60 BPM
        int acfMax = maxLag * 3 + 2; // serve anche per i multipli (2L, 3L)
        var acf = new double[acfMax + 1];
        for (int lag = minLag; lag <= acfMax; lag++)
        {
            double s = 0;
            for (int i = lag; i < n; i++) s += o[i] * o[i - lag];
            acf[lag] = s / (n - lag);
        }

        // punteggio con prior log-normale centrato su 120 BPM (stile librosa)
        double best = -1; int bestLag = minLag;
        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double b = 60.0 * framesPerSec / lag;
            double prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(b / 120.0) / 1.0, 2));
            // rinforzo armonico: un tempo vero ha picchi anche a 2L e 3L (battute e mezze battute)
            double score = (acf[lag] + 0.5 * acf[2 * lag] + 0.25 * acf[3 * lag]) * prior;
            if (score > best) { best = score; bestLag = lag; }
        }

        // interpolazione parabolica per precisione sub-frame
        double lagF = bestLag;
        if (bestLag > minLag && bestLag < maxLag)
        {
            double a = acf[bestLag - 1], b0 = acf[bestLag], c = acf[bestLag + 1];
            double denom = a - 2 * b0 + c;
            if (Math.Abs(denom) > 1e-12) lagF = bestLag + 0.5 * (a - c) / denom;
        }
        double bpm = 60.0 * framesPerSec / lagF;

        // porta nel range "da ballo" 70–170 raddoppiando/dimezzando
        while (bpm < 70) bpm *= 2;
        while (bpm > 170) bpm /= 2;
        return Math.Round(bpm, 1);
    }

    /// <summary>Voto a maggioranza tra segmenti: più robusto di un unico chroma globale (modulazioni, assoli).</summary>
    private static (string key, string camelot, double confidence) EstimateKeyVoting(List<double[]> segments)
    {
        var votes = new Dictionary<string, double>();
        var camelotOf = new Dictionary<string, string>();
        // anche il chroma globale partecipa con peso pari a 1/3 dei segmenti
        var global = new double[12];
        foreach (var s in segments) for (int i = 0; i < 12; i++) global[i] += s[i];
        var all = new List<(double[] c, double w)>();
        foreach (var s in segments) all.Add((s, 1.0));
        all.Add((global, Math.Max(1, segments.Count / 3.0)));
        foreach (var (c, w) in all)
        {
            var (k, cam, conf) = EstimateKey(c);
            if (k == "?") continue;
            votes[k] = votes.GetValueOrDefault(k) + w * (0.5 + conf);
            camelotOf[k] = cam;
        }
        if (votes.Count == 0) return ("?", "", 0);
        var ordered = votes.OrderByDescending(v => v.Value).ToList();
        double total = votes.Values.Sum();
        double confidence = ordered[0].Value / total;
        return (ordered[0].Key, camelotOf[ordered[0].Key], Math.Round(confidence, 2));
    }

    private static (string key, string camelot, double confidence) EstimateKey(double[] chroma)
    {
        double sum = chroma.Sum();
        if (sum <= 0) return ("?", "", 0);
        var c = chroma.Select(v => v / sum).ToArray();

        double best = double.MinValue, second = double.MinValue;
        int bestTonic = 0; bool bestMajor = true;
        for (int tonic = 0; tonic < 12; tonic++)
        {
            double rMaj = Correlate(c, MajorProfile, tonic);
            double rMin = Correlate(c, MinorProfile, tonic);
            foreach (var (r, isMaj) in new[] { (rMaj, true), (rMin, false) })
            {
                if (r > best) { second = best; best = r; bestTonic = tonic; bestMajor = isMaj; }
                else if (r > second) second = r;
            }
        }
        string key = NoteNames[bestTonic] + (bestMajor ? "" : "m");
        string camelot = CamelotOf(bestTonic, bestMajor);
        double conf = Math.Clamp((best - second) * 5, 0, 1);
        return (key, camelot, conf);
    }

    private static double Correlate(double[] c, double[] profile, int shift)
    {
        double mc = c.Average(), mp = profile.Average();
        double num = 0, dc = 0, dp = 0;
        for (int i = 0; i < 12; i++)
        {
            double a = c[(i + shift) % 12] - mc;
            double b = profile[i] - mp;
            num += a * b; dc += a * a; dp += b * b;
        }
        return num / Math.Sqrt(dc * dp + 1e-12);
    }

    public static string CamelotOf(int tonic, bool major)
    {
        if (major) return CamelotMajor[tonic] + "B";
        // il relativo maggiore è 3 semitoni sopra
        return CamelotMajor[(tonic + 3) % 12] + "A";
    }

    /// <summary>Traspone una tonalità ("Am", "F#") di n semitoni.</summary>
    public static string Transpose(string key, int semitones)
    {
        if (string.IsNullOrEmpty(key) || key == "?") return key;
        bool minor = key.EndsWith('m');
        var root = minor ? key[..^1] : key;
        int idx = Array.IndexOf(NoteNames, root);
        if (idx < 0) return key;
        int t = ((idx + semitones) % 12 + 12) % 12;
        return NoteNames[t] + (minor ? "m" : "");
    }

    public static string CamelotOfKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key == "?") return "";
        bool minor = key.EndsWith('m');
        int idx = Array.IndexOf(NoteNames, minor ? key[..^1] : key);
        return idx < 0 ? "" : CamelotOf(idx, !minor);
    }
}
