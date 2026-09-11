using KaraokeDJ.Audio;
using NAudio.Wave;

/// <summary>Renderizza 2 battute di house a 124 BPM in test-media/drums.wav e stampa i picchi per suono.</summary>
public static class DrumTest
{
    public static void Run()
    {
        foreach (var s in Enum.GetValues<DrumSound>())
        {
            if (s == DrumSound.Sample) continue;
            var b = DrumSynth.Render(s);
            float peak = 0; foreach (var v in b) peak = Math.Max(peak, Math.Abs(v));
            Console.WriteLine($"{s,-10} {b.Length / 2 / 44100.0:0.000}s  peak {peak:0.00}");
        }
        var eng = new RhythmEngine { Bpm = 124, Volume = 1f };
        void Add(string n, DrumSound s, string pat, float vol)
        {
            var t = eng.AddTrack(n, s); t.Volume = vol;
            for (int i = 0; i < 16; i++) t.Pattern[i] = pat[i] == 'x';
        }
        Add("Kick", DrumSound.Kick, "x...x...x...x...", 0.9f);
        Add("Clap", DrumSound.Clap, "....x.......x...", 0.7f);
        Add("Hat", DrumSound.HatClosed, "x.x.x.x.x.x.x.x.", 0.45f);
        Add("OHat", DrumSound.HatOpen, "..x...x...x...x.", 0.4f);
        eng.Start();
        int frames = (int)(44100 * 60.0 / 124 * 8); // 2 battute
        var buf = new float[frames * 2];
        int off = 0;
        while (off < buf.Length) off += eng.Read(buf, off, Math.Min(4096, buf.Length - off));
        Directory.CreateDirectory("test-media");
        using var w = new WaveFileWriter("test-media/drums.wav", SourceFactory.Format);
        w.WriteSamples(buf, 0, buf.Length);
        float pk = 0; int nz = 0; foreach (var v in buf) { pk = Math.Max(pk, Math.Abs(v)); if (Math.Abs(v) > 0.01f) nz++; }
        Console.WriteLine($"mix: {frames / 44100.0:0.00}s peak {pk:0.00} campioni attivi {100.0 * nz / buf.Length:0}%  step finale {eng.CurrentStep}");
    }
}
