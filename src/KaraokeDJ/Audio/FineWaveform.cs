using KaraokeDJ.Services;

namespace KaraokeDJ.Audio;

/// <summary>
/// Forma d'onda "fine" per la vista di mixaggio: <see cref="ColumnsPerSec"/> colonne al secondo,
/// 2 byte per colonna (picco, energia dei bassi). Calcolata in background al caricamento sul deck e messa in cache.
/// Accanto, nello stesso passaggio di lettura, le tre bande per l'onda colorata (file .bande, vedi <see cref="LoadBands"/>):
/// stanno in un file a parte perché il formato di .fine è letto da mezza applicazione (griglia, studio, vista di mix)
/// e cambiargli il passo avrebbe voluto dire toccare tutto per una questione di colore.
/// </summary>
public static class FineWaveform
{
    public const int ColumnsPerSec = 50;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string PathFor(string trackId) => Path.Combine(WaveformStore.Dir, trackId + ".fine");
    public static string BandsPathFor(string trackId) => Path.Combine(WaveformStore.Dir, trackId + ".bande");

    public static byte[]? Load(string trackId)
    {
        try { var p = PathFor(trackId); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch { return null; }
    }

    /// <summary>Tre byte per colonna (bassi, medi, alti): il colore dell'onda. Null = non ancora calcolate.</summary>
    public static byte[]? LoadBands(string trackId)
    {
        try { var p = BandsPathFor(trackId); return File.Exists(p) ? File.ReadAllBytes(p) : null; }
        catch { return null; }
    }

    /// <summary>Carica dalla cache o calcola (decodifica completa del file, qualche secondo) e salva.</summary>
    public static async Task<byte[]?> GetOrComputeAsync(string trackId, string audioPath, CancellationToken ct)
    {
        var cached = Load(trackId);
        if (cached != null) return cached;
        return (await ComputeAndStoreAsync(trackId, audioPath, ct)).Fine;
    }

    /// <summary>
    /// Come sopra ma per le bande. Sta a parte apposta: chi vuole solo l'onda (lo studio, la stima della griglia)
    /// non deve ridecodificare il file solo perché mancano i colori, che a lui non servono.
    /// I brani già in cache hanno .fine e non .bande, e li ricalcola solo chi li mette davvero su un deck.
    /// </summary>
    public static async Task<byte[]?> GetOrComputeBandsAsync(string trackId, string audioPath, CancellationToken ct)
    {
        var cached = LoadBands(trackId);
        if (cached != null) return cached;
        return (await ComputeAndStoreAsync(trackId, audioPath, ct)).Bands;
    }

    private static async Task<(byte[]? Fine, byte[]? Bands)> ComputeAndStoreAsync(string trackId, string audioPath, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var res = Compute(audioPath, ct);
                if (res.Fine != null)
                {
                    try
                    {
                        Directory.CreateDirectory(WaveformStore.Dir);
                        File.WriteAllBytes(PathFor(trackId), res.Fine);
                        if (res.Bands != null) File.WriteAllBytes(BandsPathFor(trackId), res.Bands);
                    }
                    catch { }
                }
                return res;
            }, ct);
        }
        finally { Gate.Release(); }
    }

    private static (byte[]? Fine, byte[]? Bands) Compute(string audioPath, CancellationToken ct)
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
                var mids = new List<float>(8192);
                var highs = new List<float>(8192);
                var lows = new List<float>(8192);
                float pk = 0; double bassSq = 0, midSq = 0, hiSq = 0; int inCol = 0;
                // passa-basso a un polo (~150 Hz) per l'energia dei bassi (mono)
                double lp = 0, a = 1 - Math.Exp(-2 * Math.PI * 150 / sr);
                // secondo passa-basso a 2,5 kHz: sotto c'è il corpo di voci e strumenti, sopra piatti e sibilanti.
                // Medi = quello che sta fra i due filtri, alti = quello che resta: tre bande con due filtri.
                double lp2 = 0, a2 = 1 - Math.Exp(-2 * Math.PI * 2500 / sr);
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
                        lp2 += a2 * (m - lp2);
                        bassSq += lp * lp;
                        double md = lp2 - lp, hi = m - lp2;
                        midSq += md * md; hiSq += hi * hi;
                        if (++inCol >= colLen)
                        {
                            peaks.Add((byte)Math.Clamp(pk * 255, 0, 255));
                            double rms = Math.Sqrt(bassSq / colLen);
                            bass.Add((byte)Math.Clamp(rms * 3 * 255, 0, 255));
                            // grezzi: la scala la si decide alla fine, quando si sa quanto suona forte QUESTO brano
                            lows.Add((float)rms);
                            mids.Add((float)Math.Sqrt(midSq / colLen));
                            highs.Add((float)Math.Sqrt(hiSq / colLen));
                            pk = 0; bassSq = 0; midSq = 0; hiSq = 0; inCol = 0;
                        }
                    }
                }
                var outp = new byte[peaks.Count * 2];
                for (int i = 0; i < peaks.Count; i++) { outp[i * 2] = peaks[i]; outp[i * 2 + 1] = bass[i]; }
                return (outp, PackBands(lows, mids, highs, peaks.Count));
            }
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// Porta le tre bande a 0..255 con UNA sola scala per tutte e tre, scelta sul brano.
    /// Una scala fissa non va: un brano registrato piano resterebbe un filo in mezzo alla corsia e un brano
    /// moderno sarebbe tutto schiacciato in alto. Si prende il 98° percentile del massimo fra le bande — non il
    /// massimo, che è un colpo solo e schiaccerebbe tutto il resto — e lo si porta quasi a fondo scala.
    /// La scala è la stessa per le tre bande, altrimenti il colore direbbe una bugia: le proporzioni fra bassi,
    /// medi e alti devono restare quelle vere, è il loro rapporto che fa riconoscere lo stacco dal ritornello.
    /// </summary>
    private static byte[] PackBands(List<float> lows, List<float> mids, List<float> highs, int cols)
    {
        var colMax = new float[cols];
        for (int i = 0; i < cols; i++) colMax[i] = Math.Max(lows[i], Math.Max(mids[i], highs[i]));
        var sorted = (float[])colMax.Clone();
        Array.Sort(sorted);
        float p98 = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.98)] : 0;
        // brano quasi muto o silenzio: meglio lasciare piatto che moltiplicare il rumore di fondo per mille
        double scale = p98 > 1e-4 ? 245.0 / p98 : 0;
        var bands = new byte[cols * 3];
        for (int i = 0; i < cols; i++)
        {
            bands[i * 3] = (byte)Math.Clamp(lows[i] * scale, 0, 255);
            bands[i * 3 + 1] = (byte)Math.Clamp(mids[i] * scale, 0, 255);
            bands[i * 3 + 2] = (byte)Math.Clamp(highs[i] * scale, 0, 255);
        }
        return bands;
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
