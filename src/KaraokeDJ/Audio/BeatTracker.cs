namespace KaraokeDJ.Audio;

/// <summary>
/// Griglia "fluida": invece di un BPM fisso con un aggancio, segue i battiti uno per uno lungo tutto il brano.
/// Serve per i pezzi suonati senza click (i classici italiani, il rock anni '60–'70, le registrazioni dal vivo),
/// dove il tempo respira e una griglia rigida si scolla dopo pochi secondi.
///
/// Metodo: programmazione dinamica sull'inviluppo degli attacchi (Ellis, "Beat Tracking by Dynamic Programming"):
/// ogni battito preferisce cadere su un attacco forte e a una distanza vicina al periodo atteso; lo scostamento si paga.
/// </summary>
public static class BeatTracker
{
    /// <summary>Quanto costa allontanarsi dal periodo atteso: più alto = griglia più rigida.</summary>
    private const double Tightness = 8.0;

    /// <summary>Istanti dei battiti (secondi). Vuoto se non c'è abbastanza materiale.</summary>
    public static double[] Track(double[] flux, double hopSec, double bpm)
    {
        int n = flux.Length;
        if (n < 32 || bpm <= 0 || hopSec <= 0) return Array.Empty<double>();

        // inviluppo normalizzato: media zero, scarto 1, solo le salite
        var on = new double[n];
        double mean = flux.Average();
        double sd = Math.Sqrt(flux.Sum(v => (v - mean) * (v - mean)) / n);
        if (sd < 1e-9) return Array.Empty<double>();
        for (int i = 0; i < n; i++) on[i] = Math.Max(0, (flux[i] - mean) / sd);

        double period = 60.0 / bpm / hopSec;            // periodo atteso, in frame
        if (period < 2 || period > n / 4.0) return Array.Empty<double>();
        int lo = (int)Math.Round(period * 0.55), hi = (int)Math.Round(period * 1.75);

        var score = new double[n];
        var back = new int[n];
        for (int t = 0; t < n; t++)
        {
            double best = double.NegativeInfinity; int bestIdx = -1;
            for (int d = lo; d <= hi; d++)
            {
                int prev = t - d;
                if (prev < 0) break;
                double dev = Math.Log(d / period);
                double sc = score[prev] - Tightness * dev * dev;
                if (sc > best) { best = sc; bestIdx = prev; }
            }
            if (bestIdx < 0) { score[t] = on[t]; back[t] = -1; }
            else { score[t] = on[t] + best; back[t] = bestIdx; }
        }

        // si riparte dal punteggio migliore nell'ultimo tratto e si torna indietro lungo la catena
        int start = -1; double bestEnd = double.NegativeInfinity;
        for (int t = Math.Max(0, n - (int)(period * 4)); t < n; t++)
            if (score[t] > bestEnd) { bestEnd = score[t]; start = t; }
        if (start < 0) return Array.Empty<double>();

        var beats = new List<double>();
        for (int t = start; t >= 0; t = back[t])
        {
            beats.Add(t * hopSec);
            if (back[t] < 0) break;
        }
        beats.Reverse();
        if (beats.Count < 8) return Array.Empty<double>();

        // prolunga all'inizio e alla fine con l'ultimo intervallo noto: i bordi non devono restare senza griglia
        double firstGap = beats[1] - beats[0];
        for (double t = beats[0] - firstGap; t > 0.05; t -= firstGap) beats.Insert(0, t);
        return beats.ToArray();
    }

    /// <summary>BPM medio della griglia (utile come etichetta) e quanto "respira" il tempo.</summary>
    public static (double Bpm, double DriftPercent) Summary(double[] beats)
    {
        if (beats.Length < 8) return (0, 0);
        var gaps = new double[beats.Length - 1];
        for (int i = 1; i < beats.Length; i++) gaps[i - 1] = beats[i] - beats[i - 1];
        var sorted = gaps.OrderBy(g => g).ToArray();
        double med = sorted[sorted.Length / 2];
        if (med <= 0) return (0, 0);
        double p10 = sorted[(int)(sorted.Length * 0.1)], p90 = sorted[(int)(sorted.Length * 0.9)];
        return (60.0 / med, (p90 - p10) / med * 100);
    }
}
