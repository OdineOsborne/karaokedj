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
        // --layouttest: prova il layout alle misure dei portatili (vedi Services/LayoutTest)
        if (e.Args.Contains("--layouttest")) RunLayoutTest(win);
        // --analyze: analizza in blocco la libreria (BPM, tonalità, intro/outro, energia, brillantezza) e esce
        if (e.Args.Contains("--analyze")) RunAnalyze(e.Args.Contains("force"));
        // --gridtest [quanti]: quanto la griglia dei battiti sta davvero sui colpi del brano
        int gridIdx = Array.IndexOf(e.Args, "--gridtest");
        if (gridIdx >= 0) RunGridTest(gridIdx + 1 < e.Args.Length && int.TryParse(e.Args[gridIdx + 1], out var gn) ? gn : 20);
        // --suggesttest "<pezzo del titolo>": cosa verrebbe proposto dopo quel brano, e perché
        int sugIdx = Array.IndexOf(e.Args, "--suggesttest");
        if (sugIdx >= 0 && sugIdx + 1 < e.Args.Length) RunSuggestTest(e.Args[sugIdx + 1]);
        // --soak <minuti>: prova di resistenza sulla libreria vera (vedi Services/SoakTest)
        if (soakIdx >= 0) _ = KaraokeDJ.Services.SoakTest.RunAsync(Vm, soakIdx + 1 < e.Args.Length && int.TryParse(e.Args[soakIdx + 1], out var m) ? m : 30);
        if (recovered) Vm.StatusText = "Ripristinato dopo un errore imprevisto: coda e impostazioni conservate (dettagli in crash.log)";
        // --midiwatch <secondi>: ascolta la console senza eseguire niente e scrive che cosa manda (per capire i comandi impazziti)
        int watchIdx = Array.IndexOf(e.Args, "--midiwatch");
        if (watchIdx >= 0) RunMidiWatch(watchIdx + 1 < e.Args.Length && int.TryParse(e.Args[watchIdx + 1], out var ws) ? ws : 10);
        // --jogtest: il piatto va avanti e indietro? (silenzioso: master a zero)
        if (e.Args.Contains("--jogtest")) RunJogTest();
        // --preflight: stampa il controllo pre-serata ed esce
        if (e.Args.Contains("--preflight")) RunPreflight();
        // --sections [force]: calcola la struttura ai brani che non ce l'hanno (silenzioso)
        if (e.Args.Contains("--sections")) RunSections(e.Args.Contains("force"));
        // --structtest [quanti]: la struttura trovata nei brani e quanto e affidabile
        int structIdx = Array.IndexOf(e.Args, "--structtest");
        if (structIdx >= 0) RunStructTest(structIdx + 1 < e.Args.Length && int.TryParse(e.Args[structIdx + 1], out var sn) ? sn : 12);
        // --rectest: prova la registrazione della serata (in silenzio: master a zero)
        if (e.Args.Contains("--rectest")) RunRecTest();
        // --showtest <file.png>: fotografa il proiettore con striscia messaggi e applausometro
        int showIdx = Array.IndexOf(e.Args, "--showtest");
        if (showIdx >= 0 && showIdx + 1 < e.Args.Length) RunShowTest(e.Args[showIdx + 1]);
        // --shot <file.png>: rende la finestra principale su file (software rendering) ed esce: per verifiche automatiche
        int shotIdx = Array.IndexOf(e.Args, "--shot");
        if (shotIdx >= 0 && shotIdx + 1 < e.Args.Length) RunShot(win, e.Args[shotIdx + 1]);
    }

    private async void RunShot(MainWindow win, string path)
    {
        try
        {
            // --size LARGHEZZAxALTEZZA insieme a --shot: per vedere l'app come su un portatile piccolo
            // MIXFONIA_SHOT_TRACK="pezzo del titolo": carica quel brano sul deck A prima della foto (verifiche visive)
            var want = Environment.GetEnvironmentVariable("MIXFONIA_SHOT_TRACK");
            if (!string.IsNullOrWhiteSpace(want))
            {
                var t = Vm!.Tracks.FirstOrDefault(x => x.Display.Contains(want, StringComparison.OrdinalIgnoreCase));
                if (t != null) Vm.LoadToDeck(Vm.DeckA, t, confirmIfPlaying: false);
            }
            var size = Environment.GetEnvironmentVariable("MIXFONIA_SIZE");
            if (size != null && size.Split('x') is [var sw, var sh] && double.TryParse(sw, out var pw) && double.TryParse(sh, out var ph))
            { win.Width = pw; win.Height = ph; win.UpdateLayout(); }
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
                new Views.PreflightWindow(Vm) { Owner = win },
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
                + "\nmappings: " + KaraokeDJ.Services.ControllerPresets.All.Sum(p => p.Mappings.Count) + ", sample: " + string.Join(",", KaraokeDJ.Services.ControllerPresets.All.Take(2).Select(p => p.Id + "=" + p.Mappings[0].Action)) + ImportTest() + MidiRangeTest() + DropTest() + StopTest()); } catch { }
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SELFTEST FAIL: " + ex);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-selftest.log"), ex.ToString()); } catch { }
            Environment.Exit(2);
        }
    }

    private async void RunLayoutTest(MainWindow win)
    {
        await System.Threading.Tasks.Task.Delay(2500);
        string res;
        try { res = KaraokeDJ.Services.LayoutTest.Run(win); }
        catch (Exception ex) { res = "layout: FAIL " + ex; }
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-layouttest.log"), res); } catch { }
        Shutdown(res.StartsWith("layout: OK") ? 0 : 2);
    }

    /// <summary>
    /// Selftest del trascinamento: carica un brano sul deck (come il rilascio dalla libreria), carica un file per percorso
    /// (come il rilascio da Esplora risorse) e accoda nel punto giusto. La coda dell'utente viene rimessa com'era.
    /// </summary>
    private static string DropTest()
    {
        var vm = Vm!;
        if (vm.Tracks.Count == 0) return "\ndrop: saltato (libreria vuota)";
        var errors = new List<string>();
        var queueBackup = vm.Queue.ToList();
        try
        {
            var t = vm.Tracks[0];
            if (!vm.LoadToDeck(vm.DeckB, t, confirmIfPlaying: false) || !vm.DeckB.HasTrack) errors.Add("brano non caricato sul deck");
            vm.DeckB.Eject();
            if (!string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath))
            {
                if (!vm.LoadFileToDeck(vm.DeckB, t.FilePath) || !vm.DeckB.HasTrack) errors.Add("file per percorso non caricato");
                vm.DeckB.Eject();
            }
            if (vm.LoadFileToDeck(vm.DeckB, Path.Combine(Path.GetTempPath(), "non-esiste.xyz"))) errors.Add("un file inesistente non dovrebbe caricarsi");
            vm.Queue.Clear();
            vm.AddTrackToQueue(t, null);
            var first = vm.Queue[0];
            vm.AddTrackToQueue(vm.Tracks.Count > 1 ? vm.Tracks[1] : t, first);      // rilasciato sopra il primo → va prima
            if (vm.Queue.Count != 2 || vm.Queue[1] != first) errors.Add("ordine in coda sbagliato");
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        finally
        {
            vm.Queue.Clear();
            foreach (var q in queueBackup) vm.Queue.Add(q);
        }
        return "\ndrop: " + (errors.Count == 0 ? "OK (deck da libreria, deck da file, coda nel punto giusto)" : "ERRORI → " + string.Join("; ", errors));
    }

    /// <summary>
    /// Selftest della regola più importante della serata: la musica parte SOLO se lo decide il DJ, e un tasto la ferma sempre.
    /// Prova che il piatto (anche impazzito) non avvia un deck fermo, che i "tieni premuto" non lo avviano,
    /// e che FERMA TUTTO riporta il silenzio spegnendo quello che potrebbe rifar partire la musica.
    /// </summary>
    private static string StopTest()
    {
        var vm = Vm!;
        var t = vm.Tracks.FirstOrDefault(x => !string.IsNullOrEmpty(x.FilePath) && File.Exists(x.FilePath));
        if (t == null) return "\nstop: saltato (libreria vuota)";
        var errors = new List<string>();
        var queueBackup = vm.Queue.ToList();
        bool autoMixWas = vm.AutoMix, endlessWas = vm.AutoMixEndless, fillWas = vm.FillMusicOn;
        // il selftest deve poter girare mentre le casse sono accese: durante la prova l'uscita resta a zero
        float volWas = vm.Engine.MasterVolume;
        vm.Engine.MasterVolume = 0;
        try
        {
            // all'avvio non deve esserci niente in grado di far partire la musica da solo
            if (vm.AutoMix || vm.FillMusicOn) errors.Add("all'avvio auto-mix o riempimento sono già accesi");
            if (vm.DeckA.Deck.IsPlaying || vm.DeckB.Deck.IsPlaying) errors.Add("all'avvio un deck sta già suonando");
            vm.LoadToDeck(vm.DeckA, t, confirmIfPlaying: false);
            // 1) deck fermo + piatto che manda: non deve partire niente
            for (int i = 0; i < 20; i++) vm.SimulateMidi("a.jogscratch", i % 2 == 0 ? 3 : 124);
            if (vm.DeckA.Deck.IsPlaying) errors.Add("il piatto ha fatto partire un deck fermo");
            // 2) "tieni premuto" su un deck fermo: idem
            vm.SimulateMidi("a.fwd", 127, continuous: false);
            vm.SimulateMidi("a.rev", 127, continuous: false);
            if (vm.DeckA.Deck.IsPlaying) errors.Add("un comando \"tieni premuto\" ha fatto partire un deck fermo");
            // 3) con il deck in marcia il piatto deve invece lavorare
            vm.DeckA.Deck.Play();
            vm.SimulateMidi("a.jogscratch", 3);
            if (!vm.DeckA.IsJogging) errors.Add("con il deck in marcia il piatto non aggancia lo scratch");
            // 4) FERMA TUTTO: silenzio e niente che possa rifar partire la musica
            vm.AutoMix = true; vm.AutoMixEndless = true; vm.FillMusicOn = true;
            vm.Panic();
            if (vm.DeckA.Deck.IsPlaying || vm.DeckB.Deck.IsPlaying) errors.Add("dopo FERMA TUTTO un deck suona ancora");
            if (vm.AutoMix || vm.AutoMixEndless || vm.FillMusicOn) errors.Add("dopo FERMA TUTTO qualcosa può ancora far partire la musica da solo");
            // 5) e dopo il panico il piatto continua a non poter riavviare
            for (int i = 0; i < 20; i++) vm.SimulateMidi("a.jogscratch", i % 2 == 0 ? 3 : 124);
            if (vm.DeckA.Deck.IsPlaying) errors.Add("dopo FERMA TUTTO il piatto riavvia il deck");
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        finally
        {
            try
            {
                vm.Engine.MasterVolume = volWas;
                vm.DeckA.Eject();
                vm.AutoMix = autoMixWas; vm.AutoMixEndless = endlessWas; vm.FillMusicOn = fillWas;
                vm.Queue.Clear();
                foreach (var q in queueBackup) vm.Queue.Add(q);
            }
            catch { }
        }
        return "\nstop: " + (errors.Count == 0 ? "OK (niente parte da solo, FERMA TUTTO zittisce tutto)" : "ERRORI → " + string.Join("; ", errors));
    }

    private async void RunSuggestTest(string query)
    {
        await System.Threading.Tasks.Task.Delay(2500);
        string res;
        try { res = await KaraokeDJ.Services.SuggestTest.RunAsync(Vm!, query); }
        catch (Exception ex) { res = "suggest: FAIL " + ex.Message; }
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-suggest.log"), res); } catch { }
        Shutdown(0);
    }

    private async void RunGridTest(int n)
    {
        await System.Threading.Tasks.Task.Delay(2000);
        string res;
        try { res = await KaraokeDJ.Services.GridTest.RunAsync(Vm!, n); }
        catch (Exception ex) { res = "grid: FAIL " + ex.Message; }
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-grid.log"), res); } catch { }
        Shutdown(0);
    }

    /// <summary>--analyze: analisi in blocco della libreria dalla riga di comando, con avanzamento a schermo.</summary>
    private async void RunAnalyze(bool force = false)
    {
        var vm = Vm!;
        await System.Threading.Tasks.Task.Delay(1500);
        // "force": rifà tutto (serve quando cambia il modo di calcolare qualcosa, es. l'aggancio della griglia)
        if (force) foreach (var t in vm.Tracks) t.Analyzed = false;
        int todo = vm.Tracks.Count(t => !t.Analyzed || t.Energy <= 0);
        Console.Error.WriteLine($"ANALISI: {todo} brani da fare su {vm.Tracks.Count}");
        var t0 = DateTime.UtcNow;
        string last = "";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { if (vm.AnalyzeStatus != last) { last = vm.AnalyzeStatus; Console.Error.WriteLine("  " + last); } };
        timer.Start();
        try { await vm.AnalyzeMissingCommand.ExecuteAsync(null); }
        catch (Exception ex) { Console.Error.WriteLine("ANALISI FALLITA: " + ex.Message); }
        timer.Stop();
        int withEnergy = vm.Tracks.Count(t => t.Energy > 0);
        var msg = $"ANALISI FINITA in {(DateTime.UtcNow - t0).TotalMinutes:0.0} min · con energia: {withEnergy}/{vm.Tracks.Count}";
        Console.Error.WriteLine(msg);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-analisi.log"), msg); } catch { }
        Shutdown(0);
    }

    /// <summary>--midiwatch: registra quello che manda la console SENZA eseguire le azioni. Serve a scoprire i messaggi spontanei.</summary>
    private async void RunMidiWatch(int seconds)
    {
        var vm = Vm!;
        vm.Midi.Muted = true;   // in ascolto non si tocca niente
        var counts = new Dictionary<string, (int N, int Min, int Max, int Last)>();
        void OnMsg(KaraokeDJ.Services.MidiKey k, int v)
        {
            string key = k.ToString() + "  → " + (vm.Midi.ActionFor(k) ?? "(non mappato)");
            if (counts.TryGetValue(key, out var c)) counts[key] = (c.N + 1, Math.Min(c.Min, v), Math.Max(c.Max, v), v);
            else counts[key] = (1, v, v, v);
        }
        vm.Midi.MessageReceived += OnMsg;
        Console.Error.WriteLine($"midiwatch: ascolto \"{vm.Midi.DeviceName ?? "nessuna console"}\" per {seconds}s senza eseguire niente…");
        await System.Threading.Tasks.Task.Delay(seconds * 1000);
        vm.Midi.MessageReceived -= OnMsg;
        var lines = counts.OrderByDescending(kv => kv.Value.N)
                          .Select(kv => $"  {kv.Value.N,6} msg  {kv.Key,-44} valori {kv.Value.Min}…{kv.Value.Max} (ultimo {kv.Value.Last})");
        string res = counts.Count == 0
            ? "midiwatch: la console non ha mandato NIENTE (nessun messaggio spontaneo)"
            : $"midiwatch: {counts.Values.Sum(c => c.N)} messaggi spontanei in {seconds}s" + Environment.NewLine + string.Join(Environment.NewLine, lines);
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-midiwatch.log"), res); } catch { }
        Shutdown(0);
    }

    /// <summary>--showtest &lt;file.png&gt;: mette a schermo quello che vede il pubblico (striscia messaggi + applausometro) e lo fotografa.</summary>
    private async void RunShowTest(string path)
    {
        var vm = Vm!;
        try
        {
            vm.TickerText = "Benvenuti alla serata karaoke!\nPrenota la tua canzone al DJ\nStasera pizza e birra 10 €";
            vm.TickerOn = true;
            vm.IsProjectorOpen = true;
            await System.Threading.Tasks.Task.Delay(3500);
            ShotOf(Windows.OfType<Views.ProjectorWindow>().FirstOrDefault(), path.Replace(".png", "-striscia.png"));
            vm.StartApplause();
            await System.Threading.Tasks.Task.Delay(1200);
            // valori finti solo per la foto: dal vivo li porta il microfono
            vm.ApplauseValue = 78; vm.ApplauseScore = 91; vm.ApplauseCaption = "DA PELLE D'OCA!";
            await System.Threading.Tasks.Task.Delay(150);
            if (!ShotOf(Windows.OfType<Views.ProjectorWindow>().FirstOrDefault(), path)) { Shutdown(2); return; }
            Console.Error.WriteLine($"showtest: OK {path} (striscia \"{vm.TickerLine.Trim()}\", applausometro {vm.ApplauseScore})");
        }
        catch (Exception ex) { Console.Error.WriteLine("showtest: FAIL " + ex.Message); Shutdown(2); return; }
        Shutdown(0);
    }

    /// <summary>
    /// --sections: calcola la struttura ai brani già analizzati che non ce l'hanno (BPM, tonalità e griglia restano
    /// com'erano). Non fa rumore e non tocca l'audio: si può lanciare a PC libero.
    /// </summary>
    private async void RunSections(bool force)
    {
        var vm = Vm!;
        await System.Threading.Tasks.Task.Delay(1500);
        var todo = vm.Tracks.Where(t => t.Analyzed && !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath)
                                        && (force || t.Sections == null)).ToList();
        Console.Error.WriteLine($"STRUTTURA: {todo.Count} brani da fare su {vm.Tracks.Count}");
        int done = 0, found = 0, good = 0;
        var t0 = DateTime.UtcNow;
        foreach (var t in todo)
        {
            try
            {
                var beats = t.Beats?.Select(b => (double)b).ToArray();
                var (sections, score) = await System.Threading.Tasks.Task.Run(() => KaraokeDJ.Audio.StructureAnalyzer.Analyze(t.FilePath, beats));
                if (sections.Count > 1)
                {
                    t.Sections = sections; t.SectionsScore = Math.Round(score, 2);
                    found++; if (score >= 1.0) good++;
                }
                vm.Library.Save(t);
            }
            catch (Exception ex) { Console.Error.WriteLine($"  {t.Display}: {ex.Message}"); }
            if (++done % 10 == 0) Console.Error.WriteLine($"  {done}/{todo.Count} · {(DateTime.UtcNow - t0).TotalMinutes:0.0} min");
        }
        var msg = $"STRUTTURA FINITA in {(DateTime.UtcNow - t0).TotalMinutes:0.0} min · trovata su {found}/{todo.Count}, affidabile su {good}";
        Console.Error.WriteLine(msg);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-sezioni.log"), msg); } catch { }
        Shutdown(0);
    }

    /// <summary>--jogtest: prova il piatto avanti/indietro con i numeri veri della console (vedi Services/JogTest).</summary>
    private async void RunJogTest()
    {
        await System.Threading.Tasks.Task.Delay(1500);
        string res;
        try { res = await KaraokeDJ.Services.JogTest.RunAsync(Vm!); }
        catch (Exception ex) { res = "jog: FAIL " + ex.Message; }
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-jogtest.log"), res); } catch { }
        Shutdown(res.StartsWith("jog: OK") || res.StartsWith("jog: saltato") ? 0 : 2);
    }

    /// <summary>--preflight: il controllo pre-serata a riga di comando (vedi Services/Preflight).</summary>
    private async void RunPreflight()
    {
        await System.Threading.Tasks.Task.Delay(2500);
        string res;
        try { res = KaraokeDJ.Services.Preflight.AsText(KaraokeDJ.Services.Preflight.Run(Vm!)); }
        catch (Exception ex) { res = "pre-serata: FAIL " + ex.Message; }
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-preserata.log"), res); } catch { }
        Shutdown(0);
    }

    /// <summary>--structtest: struttura dei brani e quanto i confini valgono rispetto al caso (vedi Services/StructTest).</summary>
    private async void RunStructTest(int n)
    {
        await System.Threading.Tasks.Task.Delay(2000);
        string res;
        try { res = await KaraokeDJ.Services.StructTest.RunAsync(Vm!, n); }
        catch (Exception ex) { res = "struttura: FAIL " + ex.Message; }
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-struct.log"), res); } catch { }
        Shutdown(0);
    }

    /// <summary>--rectest: la registrazione della serata contiene davvero il mix (vedi Services/RecordTest).</summary>
    private async void RunRecTest()
    {
        await System.Threading.Tasks.Task.Delay(1500);
        string res;
        try { res = await KaraokeDJ.Services.RecordTest.RunAsync(Vm!); }
        catch (Exception ex) { res = "rec: FAIL " + ex.Message; }
        Console.Error.WriteLine(res);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mixfonia-rectest.log"), res); } catch { }
        Shutdown(res.StartsWith("rec: OK") ? 0 : 2);
    }

    /// <summary>Fotografa una finestra su PNG (per le verifiche visive).</summary>
    private static bool ShotOf(Window? w, string path)
    {
        if (w == null) { Console.Error.WriteLine("showtest: finestra non aperta"); return false; }
        w.UpdateLayout();
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        return true;
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
