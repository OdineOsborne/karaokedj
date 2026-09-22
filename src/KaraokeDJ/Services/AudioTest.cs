using KaraokeDJ.Audio;
using KaraokeDJ.ViewModels;
using NAudio.Wave;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --audiotest</c>: verifica che muovere manopole e fader cambi davvero il suono.
/// Genera un brano di prova (100 Hz + 6 kHz), lo carica in un deck vero e misura l'energia delle due frequenze
/// dopo la catena audio, esattamente come la sente il pubblico. Serve perché "il controllo si muove" non vuol dire
/// "il suono cambia": qui si misura l'uscita, non la proprietà.
/// </summary>
public static class AudioTest
{
    private const int Fs = 44100;

    public static string Run()
    {
        var wav = Path.Combine(Path.GetTempPath(), "mixfonia-test-tone.wav");
        WriteTone(wav);
        var deck = new Deck("T");
        var vm = new DeckViewModel(deck);
        var errors = new List<string>();
        var lines = new List<string>();

        try { deck.Load(new Models.Track { FilePath = wav, Title = "tono di prova" }, wav); }
        catch (Exception ex) { return "audio: FAIL (il brano di prova non si carica: " + ex.Message + ")"; }
        deck.Play();
        var (b100, b6k, brms) = Measure(deck);
        lines.Add($"base 100Hz={b100:0.000} 6kHz={b6k:0.000}");
        if (brms < 0.01) return "audio: FAIL (il deck non produce suono: rms " + brms.ToString("0.0000") + ")";

        void Check(string what, Action setup, Action undo, Func<double, double, double, bool> ok, string expected)
        {
            setup();
            var (l, h, rms) = Measure(deck);
            lines.Add($"{what}: 100Hz={l:0.000} 6kHz={h:0.000} rms={rms:0.000}");
            if (!ok(l, h, rms)) errors.Add($"{what} non cambia il suono ({expected})");
            undo();
            Measure(deck); // lascia stabilizzare i filtri
        }

        // ogni prova passa dalla proprietà del ViewModel: è la stessa strada che fanno mouse e MIDI
        Check("EQ bassi -12", () => vm.EqLow = -12, () => vm.EqLow = 0, (l, h, _) => l < b100 * 0.5, "i bassi devono scendere");
        Check("EQ bassi +12", () => vm.EqLow = 12, () => vm.EqLow = 0, (l, h, _) => l > b100 * 1.5, "i bassi devono salire");
        Check("EQ alti -12", () => vm.EqHigh = -12, () => vm.EqHigh = 0, (l, h, _) => h < b6k * 0.5, "gli alti devono scendere");
        Check("kill bassi", () => vm.EqLowKill = true, () => vm.EqLowKill = false, (l, h, _) => l < b100 * 0.2, "i bassi devono sparire");
        Check("filtro low-pass", () => { vm.FilterOn = true; vm.FilterValue = -0.8; }, () => vm.FilterValue = 0, (l, h, _) => h < b6k * 0.5, "gli alti devono scendere");
        Check("filtro high-pass", () => vm.FilterValue = 0.8, () => vm.FilterValue = 0, (l, h, _) => l < b100 * 0.5, "i bassi devono scendere");
        Check("fader a zero", () => vm.Fader = 0, () => vm.Fader = 1, (_, _, rms) => rms < brms * 0.05, "deve fare silenzio");
        Check("fader a metà", () => vm.Fader = 0.5, () => vm.Fader = 1, (_, _, rms) => rms < brms * 0.75 && rms > brms * 0.2, "il volume deve dimezzarsi");
        Check("trim -12 dB", () => vm.GainDb = -12, () => vm.GainDb = 0, (_, _, rms) => rms < brms * 0.5, "il volume deve scendere");
        Check("pan tutto a sinistra", () => vm.Pan = -1, () => vm.Pan = 0, (_, _, _) => Channels(deck).R < Channels(deck).L * 0.3, "deve uscire solo a sinistra");

        // jog: la traccia deve poter andare anche INDIETRO (scratch), non solo avanti
        vm.Fader = 1; vm.GainDb = 0; deck.Seek(3);
        Measure(deck);
        var jogLines = new List<string>();
        deck.JogStart();
        double start = deck.PositionSec;
        var back = new float[Fs / 2];
        for (int i = 0; i < 8; i++) { deck.JogRate(-1.5, 0.01); deck.Read(back, 0, back.Length); }
        double afterBack = deck.PositionSec;
        deck.JogRate(1.5, 0.01);
        for (int i = 0; i < 8; i++) deck.Read(back, 0, back.Length);
        double afterFwd = deck.PositionSec;
        deck.JogEnd(0.01);
        jogLines.Add($"jog: partenza {start:0.00}s → indietro {afterBack:0.00}s → avanti {afterFwd:0.00}s");
        if (afterBack >= start - 0.05) errors.Add("il jog non va indietro (scratch)");
        if (afterFwd <= afterBack + 0.05) errors.Add("il jog non torna avanti");
        lines.AddRange(jogLines);

        deck.Eject();
        try { File.Delete(wav); } catch { }
        return "audio: " + (errors.Count == 0 ? "OK (EQ, kill, filtro, fader, trim, pan agiscono sul suono)" : "ERRORI → " + string.Join("; ", errors))
             + "\n  " + string.Join("\n  ", lines);
    }

    /// <summary>Due secondi di 100 Hz + 6 kHz, stereo 16 bit.</summary>
    private static void WriteTone(string path)
    {
        using var w = new WaveFileWriter(path, new WaveFormat(Fs, 16, 2));
        var buf = new short[Fs * 2 * 2];
        for (int i = 0; i < Fs * 2; i++)
        {
            double t = (double)i / Fs;
            double s = 0.4 * Math.Sin(2 * Math.PI * 100 * t) + 0.4 * Math.Sin(2 * Math.PI * 6000 * t);
            short v = (short)(Math.Clamp(s, -1, 1) * 32000);
            buf[2 * i] = v; buf[2 * i + 1] = v;
        }
        var bytes = new byte[buf.Length * 2];
        Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
        w.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Legge mezzo secondo dal deck e misura l'ampiezza a 100 Hz e 6 kHz (Goertzel) più il livello generale.</summary>
    private static (double Low, double High, double Rms) Measure(Deck deck)
    {
        int frames = Fs / 2;
        var buf = new float[frames * 2];
        Prime(deck);                                  // dall'inizio del tono, con un quarto di secondo per far assestare i filtri
        int n = deck.Read(buf, 0, buf.Length);
        var mono = new float[n / 2];
        double sum = 0;
        for (int i = 0; i < mono.Length; i++) { mono[i] = 0.5f * (buf[2 * i] + buf[2 * i + 1]); sum += mono[i] * mono[i]; }
        return (Goertzel(mono, 100), Goertzel(mono, 6000), Math.Sqrt(sum / Math.Max(1, mono.Length)));
    }

    private static (double L, double R) Channels(Deck deck)
    {
        int frames = Fs / 4;
        var buf = new float[frames * 2];
        Prime(deck);
        int n = deck.Read(buf, 0, buf.Length);
        double l = 0, r = 0;
        for (int i = 0; i + 1 < n; i += 2) { l += buf[i] * buf[i]; r += buf[i + 1] * buf[i + 1]; }
        return (Math.Sqrt(l / frames), Math.Sqrt(r / frames));
    }

    /// <summary>Riparte dall'inizio del tono e scarta un quarto di secondo (SoundTouch e filtri devono riempirsi).</summary>
    private static void Prime(Deck deck)
    {
        deck.Seek(0);
        var warm = new float[Fs / 4 * 2];
        deck.Read(warm, 0, warm.Length);
    }

    private static double Goertzel(float[] x, double freq)
    {
        double w = 2 * Math.PI * freq / Fs, cw = Math.Cos(w), coeff = 2 * cw;
        double s0 = 0, s1 = 0, s2 = 0;
        foreach (var v in x) { s0 = v + coeff * s1 - s2; s2 = s1; s1 = s0; }
        double re = s1 - s2 * cw, im = s2 * Math.Sin(w);
        return 2 * Math.Sqrt(re * re + im * im) / x.Length;
    }
}
