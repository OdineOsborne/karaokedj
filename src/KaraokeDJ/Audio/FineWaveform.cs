using KaraokeDJ.Services;

namespace KaraokeDJ.Audio;

/// <summary>
/// Forma d'onda "fine" per la vista di mixaggio: <see cref="ColumnsPerSec"/> colonne al secondo,
/// 2 byte per colonna (picco, energia dei bassi). Calcolata in background al caricamento sul deck e messa in cache.
/// </summary>
public static class FineWaveform
{
    public const int ColumnsPerSec = 50;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string PathFor(string trackId) => Path.Combine(WaveformStore.Dir, trackId + ".fine");

    public static byte[]? Load(string trackId)
    {
        try { var p = PathFor(trackId); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch { return null; }
    }

    /// <summary>Carica dalla cache o calcola (decodifica completa del file, qualche secondo) e salva.</summary>
    public static async Task<byte[]?> GetOrComputeAsync(string trackId, string audioPath, CancellationToken ct)
    {
        var cached = Load(trackId);
        if (cached != null) return cached;
        await Gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var data = Compute(audioPath, ct);
                if (data != null)
                {
                    try { Directory.CreateDirectory(WaveformStore.Dir); File.WriteAllBytes(PathFor(trackId), data); } catch { }
                }
                return data;
            }, ct);
        }
        finally { Gate.Release(); }
    }

    private static byte[]? Compute(string audioPath, CancellationToken ct)
    {
        try
        {
            var (reader, provider) = SourceFactory.Open(audioPath);
            using (reader)
            {
                int sr = SourceFactory.SampleRate;
                int colLen = sr / ColumnsPerSec;             // frame per colonna
                var buf = new float[colLen * 2 * 8];
                var peaks = new List<byte>(8192);
                var bass = new List<byte>(8192);
                float pk = 0; double bassSq = 0; int inCol = 0;
                // passa-basso a un polo (~150 Hz) per l'energia dei bassi (mono)
                double lp = 0, a = 1 - Math.Exp(-2 * Math.PI * 150 / sr);
                int n;
                while ((n = provider.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    for (int i = 0; i + 1 < n; i += 2)
                    {
                        float m = (buf[i] + buf[i + 1]) * 0.5f;
                        float am = Math.Abs(m);
                        if (am > pk) pk = am;
                        lp += a * (m - lp);
                        bassSq += lp * lp;
                        if (++inCol >= colLen)
                        {
                            peaks.Add((byte)Math.Clamp(pk * 255, 0, 255));
                            double rms = Math.Sqrt(bassSq / colLen);
                            bass.Add((byte)Math.Clamp(rms * 3 * 255, 0, 255));
                            pk = 0; bassSq = 0; inCol = 0;
                        }
                    }
                }
                var outp = new byte[peaks.Count * 2];
                for (int i = 0; i < peaks.Count; i++) { outp[i * 2] = peaks[i]; outp[i * 2 + 1] = bass[i]; }
                return outp;
            }
        }
        catch { return null; }
    }
}

/// <summary>Stima della fase della griglia dei battiti dalla forma d'onda fine (energia dei bassi).</summary>
public static class BeatGrid
{
    /// <summary>
    /// Trova l'offset (0 ≤ off &lt; battito) che massimizza l'energia dei bassi sui battiti, poi sceglie fra i 4 battiti
    /// della battuta quello più forte come "1". Ritorna secondi dall'inizio del file; -1 se impossibile.
    /// </summary>
    public static double EstimateOffset(byte[] fine, double bpm)
    {
        if (fine == null || fine.Length < 4 || bpm <= 0) return -1;
        int cols = fine.Length / 2;
        double colSec = 1.0 / FineWaveform.ColumnsPerSec;
        double beat = 60.0 / bpm;
        int stepsPerBeat = Math.Max(4, (int)Math.Round(beat / colSec));   // risoluzione della ricerca: una colonna
        int nSteps = Math.Min(stepsPerBeat, 200);
        double bestOff = 0, bestScore = -1;
        // usa i primi ~90 s (o tutto il brano): abbastanza per la fase, robusto ai cambi di tempo
        int limitCols = Math.Min(cols, 90 * FineWaveform.ColumnsPerSec);
        for (int s = 0; s < nSteps; s++)
        {
            double off = beat * s / nSteps;
            double score = 0; int n = 0;
            for (double t = off; t * FineWaveform.ColumnsPerSec < limitCols; t += beat)
            {
                int c = (int)(t * FineWaveform.ColumnsPerSec);
                if (c >= cols) break;
                // finestra di ±1 colonna attorno al battito
                int b = fine[c * 2 + 1];
                if (c > 0) b = Math.Max(b, fine[(c - 1) * 2 + 1]);
                if (c + 1 < cols) b = Math.Max(b, fine[(c + 1) * 2 + 1]);
                score += b; n++;
            }
            if (n > 0) { score /= n; if (score > bestScore) { bestScore = score; bestOff = off; } }
        }
        // scelta del "1": fra i 4 battiti consecutivi quello con più energia media
        double bestBar = bestOff, barScore = -1;
        for (int k = 0; k < 4; k++)
        {
            double off = bestOff + beat * k;
            double score = 0; int n = 0;
            for (double t = off; t * FineWaveform.ColumnsPerSec < limitCols; t += beat * 4)
            {
                int c = (int)(t * FineWaveform.ColumnsPerSec);
                if (c >= cols) break;
                score += fine[c * 2 + 1] + fine[c * 2] * 0.5; n++;
            }
            if (n > 0) { score /= n; if (score > barScore) { barScore = score; bestBar = off; } }
        }
        return bestBar;
    }
}
