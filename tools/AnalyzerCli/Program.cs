using KaraokeDJ.Audio;

// Uso: AnalyzerCli <file>...      oppure   AnalyzerCli --synth
if (args.Length > 0 && args[0] == "--lyrics") { LyricsTest.Run(); return; }
if (args.Length > 0 && args[0] == "--clean") { CleanTest.Run(); return; }
if (args.Length > 0 && args[0] == "--fx") { FxTest.Run(); return; }
if (args.Length > 0 && args[0] == "--drums") { DrumTest.Run(); return; }
if (args.Length > 1 && args[0] == "--jog") { JogTest.Run(args[1]); return; }
if (args.Length > 1 && args[0] == "--midi") { MidiTest.Run(args[1]); return; }
if (args.Length > 1 && args[0] == "--lrc") { foreach (var e in KaraokeDJ.Services.LrcParser.Parse(args[1])) Console.WriteLine($"{e.Sec:0.00} {(e.NewLine ? "|" : " ")} [{e.Text}]"); return; }
if (args.Length > 0 && args[0] == "--synth")
{
    // Segnali sintetici: accordo noto + click a tempo noto
    var tests = new (string name, double bpm, int[] notes)[]
    {
        ("C major 120", 120, new[] { 60, 64, 67, 72 }),
        ("E major 96", 96, new[] { 52, 56, 59, 64 }),
        ("A minor 128", 128, new[] { 57, 60, 64, 69 }),
        ("F# major 140", 140, new[] { 54, 58, 61, 66 }),
    };
    foreach (var (name, bpm, notes) in tests)
    {
        var path = Path.Combine(Path.GetTempPath(), "synth_" + name.Replace(' ', '_') + ".wav");
        Synth(path, bpm, notes, 60);
        var r = AudioAnalyzer.Analyze(path);
        Console.WriteLine($"{name,-16} → {r.Bpm,6:0.0} BPM  key {r.Key} ({r.Camelot}) conf {r.KeyConfidence:0.00}");
    }
    return;
}

foreach (var f in args)
{
    try
    {
        var r = AudioAnalyzer.Analyze(f);
        Console.WriteLine($"{Path.GetFileName(f)}\n   {r.Bpm:0.0} BPM  key {r.Key} ({r.Camelot}) conf {r.KeyConfidence:0.00}  intro {r.IntroEndSec}s  outro {r.OutroStartSec}s");
    }
    catch (Exception ex) { Console.WriteLine($"{f}: {ex.Message}"); }
}

static void Synth(string path, double bpm, int[] midiNotes, double seconds)
{
    int sr = 44100;
    int n = (int)(sr * seconds);
    var data = new short[n * 2];
    double beat = 60.0 / bpm;
    var rnd = new Random(1);
    for (int i = 0; i < n; i++)
    {
        double t = (double)i / sr;
        double v = 0;
        // accordo: cambia voicing ogni 4 battute per simulare un brano
        int bar = (int)(t / (beat * 4));
        foreach (var m in midiNotes)
        {
            double f = 440 * Math.Pow(2, (m + (bar % 2 == 0 ? 0 : 12) - 69) / 12.0);
            v += 0.12 * Math.Sin(2 * Math.PI * f * t) + 0.04 * Math.Sin(2 * Math.PI * 2 * f * t);
        }
        // kick/click ogni beat, snare ogni 2
        double tb = t % beat;
        if (tb < 0.05) v += 0.8 * Math.Exp(-tb * 60) * Math.Sin(2 * Math.PI * 80 * tb);
        double t2 = t % (beat * 2);
        if (t2 >= beat && t2 < beat + 0.08) v += 0.4 * Math.Exp(-(t2 - beat) * 40) * (rnd.NextDouble() * 2 - 1);
        short s = (short)Math.Clamp(v * 20000, short.MinValue, short.MaxValue);
        data[i * 2] = s; data[i * 2 + 1] = s;
    }
    using var w = new NAudio.Wave.WaveFileWriter(path, new NAudio.Wave.WaveFormat(sr, 16, 2));
    var bytes = new byte[data.Length * 2];
    Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
    w.Write(bytes, 0, bytes.Length);
}
