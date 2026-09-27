using KaraokeDJ.Audio;
using KaraokeDJ.Audio.Studio;
using KaraokeDJ.Models.Studio;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace KaraokeDJ.Services.Studio;

/// <summary>Esporta il progetto in un file unico (wav 16 bit, e mp3 320 kbit/s) più la scaletta con i minutaggi.</summary>
public static class StudioExport
{
    /// <returns>il percorso del file principale (mp3 se richiesto, altrimenti wav)</returns>
    public static string Export(StudioProject project, Func<StudioClip, string?> pathOf, string wavPath, bool mp3,
        IProgress<double>? progress, CancellationToken ct)
    {
        var snap = project.Clone();
        var r = new StudioRenderer(snap, pathOf, sync: true);
        double total = snap.EndSec + 4;                          // code di echo e riverbero
        long frames = (long)(total * SourceFactory.SampleRate);
        Directory.CreateDirectory(Path.GetDirectoryName(wavPath)!);
        var buf = new float[SourceFactory.SampleRate / 5 * 2];
        using (var w = new WaveFileWriter(wavPath, new WaveFormat(SourceFactory.SampleRate, 16, 2)))
        {
            long done = 0;
            while (done < frames)
            {
                ct.ThrowIfCancellationRequested();
                int n = (int)Math.Min(buf.Length / 2, frames - done);
                r.Read(buf, 0, n * 2);
                // sfumatura degli ultimi 2 secondi di coda, così il file non finisce con un taglio
                for (int i = 0; i < n; i++)
                {
                    double left = (frames - done - i) / (double)SourceFactory.SampleRate;
                    if (left < 2) { float f = (float)(left / 2); buf[i * 2] *= f; buf[i * 2 + 1] *= f; }
                }
                w.WriteSamples(buf, 0, n * 2);
                done += n;
                progress?.Report(done / (double)frames * (mp3 ? 0.85 : 1));
            }
        }
        File.WriteAllText(Path.ChangeExtension(wavPath, ".scaletta.txt"), StudioDj.Setlist(snap));
        if (!mp3) return wavPath;

        var mp3Path = Path.ChangeExtension(wavPath, ".mp3");
        MediaFoundationApi.Startup();
        using (var rd = new WaveFileReader(wavPath))
            MediaFoundationEncoder.EncodeToMp3(rd, mp3Path, 320000);
        progress?.Report(1);
        return mp3Path;
    }
}
