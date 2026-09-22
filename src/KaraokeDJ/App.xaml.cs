using System.Windows;
using KaraokeDJ.ViewModels;
using KaraokeDJ.Views;

namespace KaraokeDJ;

public partial class App : Application
{
    public static MainViewModel? Vm { get; private set; }
    private static readonly DateTime _startedUtc = DateTime.UtcNow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool selfTest = e.Args.Contains("--selftest");
        int soakIdx = Array.IndexOf(e.Args, "--soak");
        bool automated = selfTest || soakIdx >= 0;
        bool recovered = e.Args.Contains("--recovered");

        // ---- rete di sicurezza per la serata: nessun errore deve fermare lo show ----
        // 1) errori sul thread UI: registrati in crash.log e mostrati nella barra di stato, niente finestra modale che blocca la regia
        DispatcherUnhandledException += (_, args) =>
        {
            KaraokeDJ.Services.CrashLog.Write("UI", args.Exception);
            if (selfTest)
            {
                Console.Error.WriteLine("SELFTEST FAIL: " + args.Exception);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-selftest.log"), args.Exception.ToString()); } catch { }
                Environment.Exit(2);
            }
            if (Vm != null) Vm.StatusText = "Errore: " + args.Exception.Message + " (registrato in crash.log)";
            else MessageBox.Show(args.Exception.ToString(), "Errore imprevisto", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        // 2) task dimenticati che falliscono: solo diario
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) => { KaraokeDJ.Services.CrashLog.Write("task", args.Exception); args.SetObserved(); };
        // 3) crash vero (thread audio/plugin): salviamo coda e impostazioni e ripartiamo da soli con --recovered
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            KaraokeDJ.Services.CrashLog.Write("FATALE", args.ExceptionObject as Exception);
            try { Vm?.EmergencySave(); } catch { }
            // ripartiamo solo se l'app era in piedi da almeno un minuto: un errore all'avvio non deve fare un ciclo infinito
            if (!automated && (DateTime.UtcNow - _startedUtc).TotalSeconds > 60)
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--recovered") { UseShellExecute = false }); } catch { }
            }
        };

        try { LibVLCSharp.Shared.Core.Initialize(); }
        catch (Exception ex)
        {
            MessageBox.Show("LibVLC non inizializzato (i video karaoke non funzioneranno):\n" + ex.Message, "Avviso", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (Environment.GetEnvironmentVariable("MIXFONIA_SOFTWARE") == "1") System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        Vm = new MainViewModel();
        var win = new MainWindow { DataContext = Vm };
        MainWindow = win;
        win.Show();
        Vm.Start();

        if (selfTest) RunSelfTest(win);
        // --audiotest: misura che EQ, filtro, fader e trim cambino davvero il suono, poi esce
        if (e.Args.Contains("--audiotest")) RunAudioTest();
        // --soak <minuti>: prova di resistenza sulla libreria vera (vedi Services/SoakTest)
        if (soakIdx >= 0) _ = KaraokeDJ.Services.SoakTest.RunAsync(Vm, soakIdx + 1 < e.Args.Length && int.TryParse(e.Args[soakIdx + 1], out var m) ? m : 30);
        if (recovered) Vm.StatusText = "Ripristinato dopo un errore imprevisto: coda e impostazioni conservate (dettagli in crash.log)";
        // --shot <file.png>: rende la finestra principale su file (software rendering) ed esce: per verifiche automatiche
        int shotIdx = Array.IndexOf(e.Args, "--shot");
        if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length) RunShot(win, e.Args[shotIdx + 1]);
    }

    private async void RunShot(MainWindow win, string path)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(4000);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)win.ActualWidth, (int)win.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(win);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = File.Create(path)) enc.Save(fs);
            Console.Error.WriteLine("SHOT OK " + path);
        }
        catch (Exception ex) { try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { } }
        Shutdown(0);
    }

    /// <summary>--selftest: apre tutte le finestre (errori XAML/risorse vengono fuori qui), poi esce con codice 0.</summary>
    private async void RunSelfTest(MainWindow win)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(1500);
            var wins = new Window[]
            {
                new Views.RhythmWindow(Vm!.Rhythm) { Owner = win },
                new Views.DuplicatesWindow(Vm) { Owner = win },
                new Views.SettingsWindow(Vm) { Owner = win },
                new Views.AnimationWindow(Vm) { Owner = win },
                new Views.SupportWindow(Vm) { Owner = win },
                new Views.BorderoWindow(Vm) { Owner = win },
                new Views.RemoteWindow(Vm) { Owner = win },
                new Views.ShortcutPopup(Vm, "a.play") { Owner = win },
            };
            foreach (var w in wins) { w.Show(); await System.Threading.Tasks.Task.Delay(300); }
            Vm.IsProjectorOpen = true;
            await System.Threading.Tasks.Task.Delay(800);
            Vm.DeckA.FxVisible = true;
            await System.Threading.Tasks.Task.Delay(500);
            var plugins = string.Join("; ", Vm.Plugins.Plugins.Select(p => p.Name + (p.Ok ? "" : " ERR: " + p.Error)));
            var sources = string.Join(", ", Vm.ImportSources.Select(s => s.Id));
            Console.Error.WriteLine("SELFTEST OK");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-selftest.log"), "OK\nplugins: " + plugins + "\nsources: " + sources + "\ncontrollers: " + KaraokeDJ.Services.ControllerPresets.All.Count + " preset, midi: " + (Vm.Midi.DeviceName ?? "nessuno")
                + "\nmappings: " + KaraokeDJ.Services.ControllerPresets.All.Sum(p => p.Mappings.Count) + ", sample: " + string.Join(",", KaraokeDJ.Services.ControllerPresets.All.Take(2).Select(p => p.Id + "=" + p.Mappings[0].Action)) + ImportTest() + MidiRangeTest()); } catch { }
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SELFTEST FAIL: " + ex);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-selftest.log"), ex.ToString()); } catch { }
            Environment.Exit(2);
        }
    }

    /// <summary>--audiotest: prova che i comandi cambino davvero il suono (vedi Services/AudioTest).</summary>
    private async void RunAudioTest()
    {
        await System.Threading.Tasks.Task.Delay(1500);
        string res;
        try { res = KaraokeDJ.Services.AudioTest.Run(); }
        catch (Exception ex) { res = "audio: FAIL " + ex; }
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-audiotest.log"), res); } catch { }
        Shutdown(res.StartsWith("audio: OK") ? 0 : 2);
    }

    /// <summary>
    /// Selftest: fader e manopole devono coprire TUTTA la corsa MIDI (0…127), non solo la metà alta.
    /// (Regressione del 22/9: i valori sotto 64 venivano scambiati per il rilascio di un tasto e scartati.)
    /// </summary>
    private static string MidiRangeTest()
    {
        var vm = Vm!;
        var errors = new List<string>();
        void Check(string what, string action, int midi, double expected, Func<double> read)
        {
            vm.SimulateMidi(action, midi);
            double got = read();
            if (Math.Abs(got - expected) > 0.01) errors.Add($"{what}@{midi}={got:0.##} (atteso {expected:0.##})");
        }
        Check("fader A", "a.fader", 0, 0, () => vm.DeckA.Fader);
        Check("fader A", "a.fader", 127, 1, () => vm.DeckA.Fader);
        Check("EQ bassi A", "a.eqlow", 0, -12, () => vm.DeckA.EqLow);
        Check("EQ bassi A", "a.eqlow", 127, 12, () => vm.DeckA.EqLow);
        Check("trim A", "a.volume", 0, -12, () => vm.DeckA.GainDb);
        Check("filtro A", "a.filtervalue", 0, -1, () => vm.DeckA.FilterValue);
        Check("master", "master", 0, 0, () => vm.MasterVolume);
        Check("crossfader", "crossfader", 0, -1, () => vm.Crossfader);
        Check("volume cuffia", "cuevolume", 0, 0, () => vm.CueVolume);
        // rimettiamo tutto a posto (il selftest non deve lasciare l'app con i fader a zero)
        vm.SimulateMidi("a.fader", 127); vm.SimulateMidi("a.eqlow", 64); vm.SimulateMidi("a.volume", 64);
        vm.SimulateMidi("a.filtervalue", 64); vm.SimulateMidi("master", 106); vm.SimulateMidi("crossfader", 64); vm.SimulateMidi("cuevolume", 85);
        return "\nmidi range: " + (errors.Count == 0 ? "OK (0…127 su fader, EQ, trim, filtro, master, crossfader, cuffia)" : "ERRORI → " + string.Join("; ", errors));
    }

    /// <summary>Selftest: se MIXFONIA_IMPORT_TEST punta a un file (Mixxx XML / djay / JSON), prova l'importazione e riporta il risultato.</summary>
    private static string ImportTest()
    {
        var f = Environment.GetEnvironmentVariable("MIXFONIA_IMPORT_TEST");
        if (string.IsNullOrEmpty(f)) return "";
        try { var (p, rep) = KaraokeDJ.Services.ControllerPresets.Import(f); return $"\nimport: {p.Name} [{string.Join("|", p.Match)}] {rep}; play={p.Mappings.FirstOrDefault(m => m.Action == "a.play")?.Key}"; }
        catch (Exception ex) { return "\nimport: ERRORE " + ex.Message; }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Vm?.Shutdown();
        base.OnExit(e);
    }
}
