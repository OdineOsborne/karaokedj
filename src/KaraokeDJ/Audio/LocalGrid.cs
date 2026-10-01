namespace KaraokeDJ.Audio;

/// <summary>
/// Griglia dei battiti misurata sul posto, nei secondi intorno al punto del passaggio, sull'audio in memoria.
/// La griglia dell'analisi è un BPM medio con un aggancio a inizio brano: basta uno 0,5 % di errore e dopo tre
/// minuti è fuori di uno o due battiti, proprio dove si mixa. Qui si cercano insieme BPM (vicino a quello
/// dell'analisi, anche metà/doppio) e fase che fanno cadere più colpi sulla griglia, al millisecondo.
/// </summary>
public static class LocalGrid
{
    public readonly record struct Result(double Bpm, double AnchorSec, double Confidence)
    {
        /// <summary>Sopra questa soglia i colpi cadono davvero sulla griglia (picco netto contro la media).</summary>
        public bool Reliable => Bpm > 0 && Confidence >= 1.45;
    }

    /// <param name="mono">audio mono a 44,1 kHz a partire da <paramref name="startSec"/> (secondi del file)</param>
    public static Result Measure(float[] mono, double startSec, double bpmGuess)
    {
        if (bpmGuess <= 0 || mono.Length < SourceFactory.SampleRate * 4) return default;
        const int Sr = SourceFactory.SampleRate;
        // inviluppo a ~1 kHz (finestre da 44 campioni = 0,9977 ms: il tempo si converte col valore esatto, non con
        // "1 ms", altrimenti tutti i BPM escono sbagliati dello 0,23 %): bassi (cassa) e resto, poi solo le salite
        const int per = 44;
        const double binSec = per / (double)Sr;
        int ms = mono.Length / per;
        var low = new double[ms]; var high = new double[ms];
        double lp = 0, a = 1 - Math.Exp(-2 * Math.PI * 150.0 / Sr);
        for (int i = 0; i < ms; i++)
        {
            double sl = 0, sh = 0;
            for (int j = 0; j < per; j++)
            {
                double x = mono[i * per + j];
                lp += a * (x - lp);
                sl += Math.Abs(lp); sh += Math.Abs(x - lp);
            }
            low[i] = sl / per; high[i] = sh / per;
        }
        var onset = new double[ms];
        var lowOn = new double[ms]; var highOn = new double[ms];
        for (int i = 4; i < ms; i++)
        {
            lowOn[i] = Math.Max(0, low[i] - low[i - 4]);
            highOn[i] = Math.Max(0, high[i] - high[i - 4]);
            onset[i] = lowOn[i] * 2 + highOn[i];
        }
        // un po' di larghezza (±2 ms) così un colpo vale anche se la griglia è a un millisecondo
        var on = new double[ms];
        for (int i = 2; i < ms - 2; i++) on[i] = Math.Max(Math.Max(onset[i - 2], onset[i - 1]), Math.Max(onset[i], Math.Max(onset[i + 1], onset[i + 2])));

        double bestScore = -1, bestBpm = 0, bestPhase = 0, bestMean = 1;
        // la famiglia del BPM dell'analisi: lo stesso, metà o doppio, purché in una fascia ballabile
        var centers = new[] { bpmGuess, bpmGuess * 2, bpmGuess / 2 }.Where(b => b >= 60 && b <= 200).Distinct();
        foreach (var c in centers)
            for (double bpm = c * 0.97; bpm <= c * 1.03; bpm += 0.05)
            {
                double period = 60.0 / bpm / binSec;
                int beats = (int)((ms - 3) / period);
                if (beats < 6) continue;
                double sum = 0, best = -1, bestP = 0;
                int nph = (int)period;
                for (int ph = 0; ph < nph; ph++)
                {
                    double s = 0;
                    for (int k = 0; k < beats; k++) { int idx = (int)(ph + k * period); if (idx < ms) s += on[idx]; }
                    sum += s;
                    if (s > best) { best = s; bestP = ph; }
                }
                double mean = sum / Math.Max(1, nph);
                // a parità di colpi conta di più un BPM vicino a quello dell'analisi (non inseguire il doppio a caso)
                double score = best * (c == bpmGuess ? 1.0 : 0.93);
                if (score > bestScore) { bestScore = score; bestBpm = bpm; bestPhase = bestP; bestMean = mean; }
            }
        if (bestBpm <= 0) return default;
        // il "1" della battuta: su 1 e 3 la cassa senza rullante, su 2 e 4 il rullante (alti). Fra 1 e 3 vince la
        // cassa più forte (spesso c'è anche il piatto). È una stima: serve a far partire i passaggi sulle frasi.
        {
            double period = 60.0 / bestBpm / binSec;
            var L = new double[4]; var H = new double[4];
            for (int k = 0; ; k++)
            {
                int idx = (int)(bestPhase + k * period);
                if (idx + 2 >= ms) break;
                double l = 0, h = 0;
                for (int d = -2; d <= 2; d++) { if (idx + d < 0) continue; l = Math.Max(l, lowOn[idx + d]); h = Math.Max(h, highOn[idx + d]); }
                L[k % 4] += l; H[k % 4] += h;
            }
            int bestQ = 0; double bestV = double.MinValue;
            for (int q = 0; q < 4; q++)
            {
                double v = (L[q] - H[q]) + (L[(q + 2) % 4] - H[(q + 2) % 4]) + 0.25 * L[q];
                if (v > bestV) { bestV = v; bestQ = q; }
            }
            bestPhase += bestQ * period;
        }
        // il BPM vero dei brani prodotti al computer è quasi sempre intero (o mezzo): se è lì vicino, si usa quello
        double rounded = Math.Round(bestBpm * 2) / 2;
        if (Math.Abs(rounded - bestBpm) < 0.06) bestBpm = rounded;
        return new Result(bestBpm, startSec + bestPhase * binSec, bestScore / Math.Max(1e-9, bestMean));
    }
}
