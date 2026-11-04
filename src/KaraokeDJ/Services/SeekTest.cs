using KaraokeDJ.Audio;
using NAudio.Wave;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --seektest</c>: quando si salta a un punto, l'audio che esce è davvero quello di quel punto?
/// Il caso che in serata manda tutto fuori tempo è l'mp3 scaricato a 48 kHz: MediaFoundation cerca per
/// fotogramma e non per campione, e sbagliava fino a mezzo secondo — un battito intero. La prova si costruisce
/// il caso da sola (un mp3 a 48 kHz con i colpi dove dice lei) e confronta le due strade del deck:
/// il lettore continuo (quello dei primi istanti dopo il caricamento) e la copia in memoria.
/// </summary>
public static class SeekTest
{
    /// <summary>Oltre questo errore il salto sposta il brano di una frazione di battito che si sente.</summary>
    private const double MaxErrMs = 5;

    public static string Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mixfonia-seektest");
        Directory.CreateDirectory(dir);
        string mp3 = Path.Combine(dir, "clicks-48k.mp3");
        try { MakeMp3(dir, mp3); }
        catch (Exception ex) { return "seek: non si può costruire l'mp3 di prova su questo PC (" + ex.Message + ")"; }

        // la verità: dove stanno i colpi leggendo il brano dall'inizio alla fine, senza nessun salto
        var mem = MemoryTrack.Decode(mp3, CancellationToken.None);
        if (mem == null) return "seek: FAIL la copia in memoria non si è fatta";
        var truth = ClickPositions(mem);
        if (truth.Count < 4) return $"seek: FAIL trovati solo {truth.Count} colpi nel brano di prova";

        var lines = new List<string>();
        var file = Measure(truth, SourceFactory.Open(mp3), "lettore continuo (MediaFoundation)");
        var ram = Measure(truth, (mem, mem), "copia in memoria");
        lines.Add("  " + file);
        lines.Add("  " + ram);

        // conta solo la strada che il deck usa per suonare: la copia in memoria
        bool ok = ram.MaxErrMs <= MaxErrMs;
        return $"seek: salti {(ok ? "esatti" : "DA CORREGGERE")} (errore ≤ {MaxErrMs:0} ms sulla strada buona)\n"
               + string.Join("\n", lines)
               + $"\n  → quanto guadagna la copia in memoria: da {file.MaxErrMs:0} ms a {ram.MaxErrMs:0} ms"
               + $"\n  → finché non è pronta (primi istanti dopo il caricamento) il deck salta con l'errore del lettore continuo";
    }

    private readonly record struct Report(string Which, double MaxErrMs, double MedErrMs, int Count)
    {
        public override string ToString() =>
            Count == 0 ? $"{Which}: nessun colpo ritrovato"
                       : $"{Which,-38} errore tipico {MedErrMs,6:0.0} ms, peggiore {MaxErrMs,7:0.0} ms  ({Count} salti)";
    }

    /// <summary>Per ogni colpo: si salta un quarto di secondo prima e si guarda se il colpo arriva quando deve.</summary>
    private static Report Measure(List<double> truth, (WaveStream reader, ISampleProvider provider) src, string which)
    {
        const double pre = 0.25;
        int sr = SourceFactory.SampleRate;
        var errs = new List<double>();
        using (src.reader)
        {
            foreach (double t in truth)
            {
                if (t < pre + 0.05) continue;
                try { src.reader.CurrentTime = TimeSpan.FromSeconds(t - pre); }
                catch { continue; }
                var buf = new float[(int)(0.6 * sr) * 2];
                int n = 0;
                while (n < buf.Length)
                {
                    int got = src.provider.Read(buf, n, buf.Length - n);
                    if (got == 0) break;
                    n += got;
                }
                int at = FirstClick(buf, n);
                if (at < 0) { errs.Add(600); continue; }    // il colpo non è nemmeno nella finestra: errore enorme
                errs.Add((at / (double)sr - pre) * 1000);
            }
        }
        if (errs.Count == 0) return new Report(which, 0, 0, 0);
        var abs = errs.Select(Math.Abs).OrderBy(v => v).ToArray();
        return new Report(which, abs[^1], abs[abs.Length / 2], errs.Count);
    }

    /// <summary>Primo campione sopra soglia: i colpi sono secchi su quasi silenzio, non c'è da interpretare.</summary>
    private static int FirstClick(float[] interleaved, int count)
    {
        for (int i = 0; i + 1 < count; i += 2)
            if (Math.Abs(interleaved[i]) > 0.2f) return i / 2;
        return -1;
    }

    private static List<double> ClickPositions(MemoryTrack mem)
    {
        var pos = new List<double>();
        int sr = SourceFactory.SampleRate;
        mem.Position = 0;
        var buf = new float[sr * 2];
        long frame = 0;
        int n;
        double last = -1;
        while ((n = mem.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i + 1 < n; i += 2)
                if (Math.Abs(buf[i]) > 0.2f)
                {
                    double t = (frame + i / 2) / (double)sr;
                    if (t - last > 0.5) { pos.Add(t); last = t; }
                }
            frame += n / 2;
        }
        mem.Position = 0;
        return pos;
    }

    /// <summary>Un mp3 a 48 kHz con un colpo secco ogni 12 secondi: il caso che sbagliava di mezzo secondo.</summary>
    private static void MakeMp3(string dir, string mp3)
    {
        string wav = Path.Combine(dir, "clicks-48k.wav");
        const int sr = 48000;                      // 48 kHz: è questo che manda fuori il salto, non l'mp3 in sé
        const double dur = 150;
        int frames = (int)(sr * dur);
        // 16 bit PCM: è il formato che l'encoder mp3 di Windows accetta senza discutere
        var buf = new short[frames * 2];
        var rnd = new Random(77);
        for (int i = 0; i < frames; i++)
        {
            short v = (short)((rnd.NextDouble() * 2 - 1) * 32);        // quasi silenzio
            buf[i * 2] = v; buf[i * 2 + 1] = v;
        }
        for (double t = 6; t < dur - 2; t += 12)
        {
            int at = (int)(t * sr);
            int len = (int)(0.004 * sr);
            for (int i = 0; i < len; i++)
            {
                short v = (short)(0.8 * 32767 * (1 - (double)i / len));
                buf[(at + i) * 2] = v; buf[(at + i) * 2 + 1] = v;
            }
        }
        using (var w = new WaveFileWriter(wav, new WaveFormat(sr, 16, 2)))
            w.WriteSamples(buf, 0, buf.Length);
        if (File.Exists(mp3)) File.Delete(mp3);
        using (var r = new WaveFileReader(wav))
            MediaFoundationEncoder.EncodeToMp3(r, mp3, 256000);
    }
}
