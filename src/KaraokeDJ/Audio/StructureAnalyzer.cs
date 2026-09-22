using NAudio.Dsp;

namespace KaraokeDJ.Audio;

/// <summary>Un pezzo del brano: da dove comincia, che tipo è e quanto "spinge" (0…1).</summary>
public sealed record TrackSection(double Start, string Kind, double Energy);

/// <summary>
/// Struttura del brano: dove cambia davvero la musica (intro, strofa, ritornello, finale).
///
/// Metodo classico (Foote): si descrive ogni battuta con un vettore — accordi (chroma) e timbro (bande) —
/// si confronta ogni battuta con ogni altra, e si cerca dove la matrice di somiglianza fa "l'angolo":
/// prima tutto simile, dopo tutto diverso. Lì c'è un confine.
///
/// Serve al passaggio automatico: mixare sul cambio di sezione suona come lo fa un DJ, mixare a metà ritornello no.
/// </summary>
public static class StructureAnalyzer
{
    private const int Fft = 4096, Hop = 2048;       // ~46 ms di passo: basta e avanza per la struttura
    private const int KernelBars = 8;               // quanto guarda avanti e indietro per decidere se è un confine
    private const int MinBars = 8;                  // sezione più corta accettata (sotto è dettaglio, non struttura)
    private const double MaxSeconds = 720;

    /// <summary>Sezioni + quanto valgono i confini (scarti tipo sopra la novità media: 0 = a caso, sopra 1 = cambia davvero).</summary>
    public static (List<TrackSection> Sections, double Score) Analyze(string path, double[]? beats, CancellationToken ct = default)
    {
        var empty = (new List<TrackSection>(), 0.0);
        var (reader, provider) = SourceFactory.Open(path);
        float[] x;
        int got = 0;
        int fs = SourceFactory.SampleRate;
        using (reader)
        {
            int want = (int)(Math.Min(reader.TotalTime.TotalSeconds + 1, MaxSeconds) * fs);
            if (want < fs * 30) return empty;                 // sotto i 30 s non c'è struttura da trovare
            x = new float[want];
            var buf = new float[8192 * 2];
            while (got < want)
            {
                ct.ThrowIfCancellationRequested();
                int n = provider.Read(buf, 0, buf.Length);
                if (n == 0) break;
                for (int i = 0; i + 1 < n && got < want; i += 2) x[got++] = 0.5f * (buf[i] + buf[i + 1]);
            }
        }
        if (got < fs * 30) return empty;
        double duration = got / (double)fs;

        // ---- confini delle battute: dai battiti veri se ci sono, altrimenti blocchi da 2 s
        var barStarts = BarStarts(beats, duration);
        if (barStarts.Count < MinBars * 3) return empty;

        // ---- un vettore per battuta: 12 note + 6 bande di timbro
        var feats = BarFeatures(x, got, fs, barStarts, ct);
        int nb = feats.Count;
        if (nb < MinBars * 3) return empty;

        // ---- matrice di somiglianza e novità "a scacchiera"
        var novelty = Novelty(feats, ct);

        // ---- picchi: i confini. Distanza minima MinBars, soglia sulla media + scarto
        double mean = novelty.Average();
        double sd = Math.Sqrt(novelty.Sum(v => (v - mean) * (v - mean)) / novelty.Length);
        var bounds = new List<int> { 0 };
        for (int i = KernelBars; i < nb - KernelBars; i++)
        {
            if (novelty[i] < mean + 0.6 * sd) continue;
            if (novelty[i] < novelty[i - 1] || novelty[i] < novelty[i + 1]) continue;
            if (i - bounds[^1] < MinBars) { if (novelty[i] > novelty[bounds[^1]] && bounds.Count > 1) bounds[^1] = i; continue; }
            bounds.Add(i);
        }

        // ---- quanto vale questa divisione rispetto a confini a caso (stesso numero, stessa distanza minima)
        double score = Score(novelty, bounds, mean, sd);

        // ---- energia di ogni sezione (RMS) e raggruppamento delle sezioni che si somigliano
        var sections = Label(feats, bounds, barStarts, x, got, fs, duration);
        return (sections, score);
    }

    /// <summary>Inizio di ogni battuta (4 battiti). Senza griglia: blocchi regolari da 2 secondi.</summary>
    private static List<double> BarStarts(double[]? beats, double duration)
    {
        var list = new List<double>();
        if (beats is { Length: > 16 })
        {
            for (int i = 0; i + 4 < beats.Length; i += 4) list.Add(beats[i]);
            if (list.Count > 8) { list.Add(beats[^1]); return list; }
            list.Clear();
        }
        for (double t = 0; t < duration - 2; t += 2) list.Add(t);
        list.Add(duration);
        return list;
    }

    /// <summary>Per ogni battuta: 12 note (chroma) + 6 bande di timbro, vettore normalizzato.</summary>
    private static List<double[]> BarFeatures(float[] x, int len, int fs, List<double> bars, CancellationToken ct)
    {
        var window = new double[Fft];
        for (int i = 0; i < Fft; i++) window[i] = FastFourierTransform.HannWindow(i, Fft);
        var cplx = new Complex[Fft];
        int m = (int)Math.Log2(Fft);
        // bin → nota e bin → banda, calcolati una volta sola
        var pitch = new int[Fft / 2];
        var band = new int[Fft / 2];
        for (int k = 1; k < Fft / 2; k++)
        {
            double f = (double)k * fs / Fft;
            pitch[k] = f is >= 80 and <= 2000 ? ((((int)Math.Round(69 + 12 * Math.Log2(f / 440.0)) % 12) + 12) % 12) : -1;
            band[k] = f < 120 ? 0 : f < 300 ? 1 : f < 800 ? 2 : f < 2000 ? 3 : f < 5000 ? 4 : 5;
        }

        var feats = new List<double[]>();
        for (int b = 0; b + 1 < bars.Count; b++)
        {
            ct.ThrowIfCancellationRequested();
            int from = (int)(bars[b] * fs), to = (int)(bars[b + 1] * fs);
            if (to > len) to = len;
            if (to - from < Fft) { feats.Add(new double[18]); continue; }
            var v = new double[18];
            int frames = 0;
            for (int off = from; off + Fft <= to; off += Hop)
            {
                for (int i = 0; i < Fft; i++) { cplx[i].X = (float)(x[off + i] * window[i]); cplx[i].Y = 0; }
                FastFourierTransform.FFT(true, m, cplx);
                for (int k = 1; k < Fft / 2; k++)
                {
                    double mag = Math.Sqrt(cplx[k].X * cplx[k].X + cplx[k].Y * cplx[k].Y);
                    if (pitch[k] >= 0) v[pitch[k]] += mag;
                    v[12 + band[k]] += mag;
                }
                frames++;
            }
            if (frames == 0) { feats.Add(new double[18]); continue; }
            // note e timbro normalizzati a parte: contano tutti e due, nessuno dei due deve schiacciare l'altro
            Normalize(v, 0, 12); Normalize(v, 12, 6);
            feats.Add(v);
        }
        return feats;
    }

    private static void Normalize(double[] v, int from, int n)
    {
        double s = 0;
        for (int i = from; i < from + n; i++) s += v[i] * v[i];
        s = Math.Sqrt(s);
        if (s < 1e-9) return;
        for (int i = from; i < from + n; i++) v[i] /= s;
    }

    private static double Cos(double[] a, double[] b)
    {
        double d = 0;
        for (int i = 0; i < a.Length; i++) d += a[i] * b[i];
        return d / 2;   // due blocchi normalizzati a parte: il massimo è 2
    }

    /// <summary>
    /// Novità di Foote: sul confine giusto, le battute prima si somigliano fra loro, quelle dopo pure,
    /// ma prima e dopo no. Il "kernel a scacchiera" misura esattamente questo.
    /// </summary>
    private static double[] Novelty(List<double[]> feats, CancellationToken ct)
    {
        int n = feats.Count, L = KernelBars;
        var nov = new double[n];
        // pesi gaussiani: le battute vicine al confine contano più di quelle lontane
        var w = new double[2 * L];
        for (int i = 0; i < 2 * L; i++)
        {
            double d = (i - L + 0.5) / L;
            w[i] = Math.Exp(-4 * d * d);
        }
        for (int c = L; c < n - L; c++)
        {
            if ((c & 15) == 0) ct.ThrowIfCancellationRequested();
            double sum = 0, wsum = 0;
            for (int i = 0; i < 2 * L; i++)
                for (int j = 0; j < 2 * L; j++)
                {
                    int a = c - L + i, b = c - L + j;
                    double sign = (i < L) == (j < L) ? 1 : -1;     // stessa metà = +, metà opposte = −
                    double weight = w[i] * w[j];
                    sum += sign * weight * Cos(feats[a], feats[b]);
                    wsum += weight;
                }
            nov[c] = wsum > 0 ? sum / wsum : 0;    // alto = prima tutto simile, dopo tutto simile, ma fra i due no: e un confine
        }
        return nov;
    }

    /// <summary>
    /// Quanto sono "buoni" i confini scelti: di quanti scarti tipo la novità media nei confini sta sopra
    /// la novità media del brano. Un confine messo a caso vale 0; sopra 1 vuol dire che lì cambia davvero qualcosa.
    /// Misura stabile: non divide per un numero che tende a zero, come farebbe un rapporto col caso.
    /// </summary>
    private static double Score(double[] novelty, List<int> bounds, double mean, double sd)
    {
        var chosen = bounds.Where(b => b > 0 && b < novelty.Length).Select(b => novelty[b]).ToList();
        if (chosen.Count == 0 || sd < 1e-12) return 0;
        return (chosen.Average() - mean) / sd;
    }

    /// <summary>Dà un nome alle sezioni: intro, ritornello (la più ripetuta e piena), strofa, ponte, finale.</summary>
    private static List<TrackSection> Label(List<double[]> feats, List<int> bounds, List<double> bars, float[] x, int len, int fs, double duration)
    {
        int ns = bounds.Count;
        var start = new double[ns];
        var mid = new double[ns][];
        var rms = new double[ns];
        for (int s = 0; s < ns; s++)
        {
            int from = bounds[s], to = s + 1 < ns ? bounds[s + 1] : feats.Count;
            start[s] = bars[Math.Min(from, bars.Count - 1)];
            double endSec = to < bars.Count ? bars[to] : duration;
            var v = new double[18];
            for (int b = from; b < to && b < feats.Count; b++)
                for (int i = 0; i < 18; i++) v[i] += feats[b][i] / Math.Max(1, to - from);
            mid[s] = v;
            int i0 = (int)(start[s] * fs), i1 = (int)Math.Min(endSec * fs, len);
            double sq = 0; int n = 0;
            for (int i = i0; i < i1; i += 7) { sq += x[i] * (double)x[i]; n++; }      // uno su sette: basta per il livello
            rms[s] = n > 0 ? Math.Sqrt(sq / n) : 0;
        }
        double maxRms = Math.Max(1e-9, rms.Max());

        // Raggruppa le sezioni che si somigliano. La soglia è relativa al brano: in un pezzo rock tutto si somiglia
        // (stessa chitarra, stessa batteria), in una ballata molto meno. Una soglia fissa direbbe "ritornello" a tutto.
        var sims = new List<double>();
        for (int i = 0; i < ns; i++)
            for (int j = i + 1; j < ns; j++) sims.Add(Cos(mid[i], mid[j]));
        sims.Sort();
        double thr = sims.Count > 0 ? Math.Max(0.90, sims[(int)(sims.Count * 0.75)]) : 0.93;

        var cluster = new int[ns];
        for (int i = 0; i < ns; i++) cluster[i] = -1;
        int next = 0;
        for (int i = 0; i < ns; i++)
        {
            if (cluster[i] >= 0) continue;
            cluster[i] = next;
            for (int j = i + 1; j < ns; j++)
                if (cluster[j] < 0 && Cos(mid[i], mid[j]) > thr) cluster[j] = next;
            next++;
        }
        // "Ritornello" si dice solo quando c'è una prova: una parte che torna più volte, piena, e che NON è
        // mezzo brano. Se il gruppo più grosso copre tutto, vuol dire che non abbiamo distinto niente:
        // meglio dire quanto spinge quel pezzo che inventarsi un nome.
        int bestCluster = -1; double bestScore = 0;
        for (int c = 0; c < next; c++)
        {
            var idx = Enumerable.Range(0, ns).Where(i => cluster[i] == c).ToList();
            if (idx.Count < 2 || idx.Count > ns * 0.5) continue;
            // conta soprattutto che quella parte TORNI; il livello serve solo a scegliere fra due gruppi pari
            double meanRms = idx.Average(i => rms[i]);
            double score = idx.Count + meanRms / maxRms;
            if (score > bestScore) { bestScore = score; bestCluster = c; }
        }

        var list = new List<TrackSection>();
        for (int s = 0; s < ns; s++)
        {
            double e = rms[s] / maxRms;
            string kind =
                s == 0 && e < 0.75 ? "intro" :
                s == ns - 1 && e < 0.9 ? "finale" :
                bestCluster >= 0 && cluster[s] == bestCluster ? "ritornello" :
                e >= 0.85 ? "pieno" : e >= 0.6 ? "medio" : "calma";
            list.Add(new TrackSection(Math.Round(start[s], 2), kind, Math.Round(e, 3)));
        }
        return list;
    }
}
