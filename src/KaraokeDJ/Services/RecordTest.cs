using KaraokeDJ.Audio;
using KaraokeDJ.ViewModels;
using NAudio.Wave;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --rectest</c>: prova che la registrazione della serata contenga davvero quello che è stato suonato.
/// Suona un tono di prova (100 Hz + 6 kHz) con il volume master a zero — quindi in silenzio, si può lanciare
/// anche con le casse accese — e poi riapre il file registrato per misurare che le due frequenze ci siano
/// e che la durata sia quella giusta.
/// </summary>
public static class RecordTest
{
    public static async Task<string> RunAsync(MainViewModel vm)
    {
        var errors = new List<string>();
        var lines = new List<string>();
        string tone = Path.Combine(Path.GetTempPath(), "mixfonia-rec-tone.wav");
        WriteTone(tone, seconds: 6);

        float volWas = vm.Engine.MasterVolume;
        double crossWas = vm.Crossfader;
        vm.Engine.MasterVolume = 0;          // niente in sala: il registratore prende il mix prima del volume master
        vm.Crossfader = -1;                  // tutto sul deck A
        string? path = null;
        try
        {
            vm.Engine.DeckA.Load(new Models.Track { FilePath = tone, Title = "tono di prova" }, tone);
            vm.ToggleRecording();
            if (!vm.Engine.Recorder.IsRecording) return "rec: FAIL (la registrazione non è partita)";
            path = vm.Engine.Recorder.FilePath;
            vm.Engine.DeckA.Play();
            await Task.Delay(3000);
            vm.Engine.DeckA.Pause();
            await Task.Delay(300);
            vm.ToggleRecording();
        }
        finally
        {
            vm.Engine.DeckA.Eject();
            vm.Engine.MasterVolume = volWas;
            vm.Crossfader = crossWas;
        }

        if (path == null || !File.Exists(path)) return "rec: FAIL (file non creato)";
        var info = new FileInfo(path);
        using (var r = new AudioFileReader(path))
        {
            double secs = r.TotalTime.TotalSeconds;
            lines.Add($"file: {Path.GetFileName(path)} · {secs:0.0}s · {info.Length / 1024} KB · {r.WaveFormat}");
            if (secs < 2.5 || secs > 5) errors.Add($"durata {secs:0.0}s (attesi ~3,3s)");
            // misura le due frequenze del tono nel pezzo centrale della registrazione
            var buf = new float[(int)(SourceFactory.SampleRate * 1.0) * 2];
            r.Position = (long)(r.WaveFormat.AverageBytesPerSecond * 1.2);
            int n = r.Read(buf, 0, buf.Length);
            double e100 = Goertzel(buf, n, 100), e6k = Goertzel(buf, n, 6000), rms = Rms(buf, n);
            lines.Add($"contenuto: 100Hz={e100:0.000} 6kHz={e6k:0.000} rms={rms:0.000}");
            if (rms < 0.01) errors.Add("il file è muto");
            if (e100 < 0.05 || e6k < 0.05) errors.Add("nel file non ci sono le due frequenze del tono");
        }
        if (vm.Engine.Recorder.Dropped > 0) errors.Add($"{vm.Engine.Recorder.Dropped} campioni persi (disco lento)");
        try { File.Delete(path); } catch { }

        return "rec: " + (errors.Count == 0
            ? "OK (il file contiene il mix, durata e contenuto giusti, niente campioni persi)"
            : "ERRORI → " + string.Join("; ", errors)) + "\n  " + string.Join("\n  ", lines);
    }

    private static void WriteTone(string path, int seconds)
    {
        int fs = SourceFactory.SampleRate;
        using var w = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(fs, 2));
        var buf = new float[fs * 2];
        for (int s = 0; s < seconds; s++)
        {
            for (int i = 0; i < fs; i++)
            {
                double t = (s * (double)fs + i) / fs;
                float v = (float)(0.35 * Math.Sin(2 * Math.PI * 100 * t) + 0.35 * Math.Sin(2 * Math.PI * 6000 * t));
                buf[2 * i] = v; buf[2 * i + 1] = v;
            }
            w.WriteSamples(buf, 0, buf.Length);
        }
    }

    private static double Rms(float[] b, int n)
    {
        double s = 0;
        for (int i = 0; i < n; i++) s += b[i] * (double)b[i];
        return n > 0 ? Math.Sqrt(s / n) : 0;
    }

    /// <summary>Energia a una frequenza (Goertzel) sul canale sinistro.</summary>
    private static double Goertzel(float[] b, int n, double freq)
    {
        int fs = SourceFactory.SampleRate;
        double w = 2 * Math.Cos(2 * Math.PI * freq / fs);
        double s1 = 0, s2 = 0; int count = 0;
        for (int i = 0; i + 1 < n; i += 2)
        {
            double s0 = b[i] + w * s1 - s2;
            s2 = s1; s1 = s0; count++;
        }
        if (count == 0) return 0;
        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - w * s1 * s2)) / count * 2;
    }
}
