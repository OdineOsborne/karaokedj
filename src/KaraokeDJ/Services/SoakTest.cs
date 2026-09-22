using System.Diagnostics;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Services;

/// <summary>
/// Prova di resistenza: <c>KaraokeDJ.exe --soak &lt;minuti&gt;</c>. Fa da DJ impazzito sulla libreria vera per N minuti
/// (carica brani a caso, hot cue, loop, salti, tonalità, tempo, EQ, filtro, effetti, dissolvenze, automix, pad, cuffia, mic se configurato,
/// proiettore aperto) e ogni 30 s annota memoria, brani suonati, errori del thread audio e riavvii dell'uscita.
/// Esce 0 con "SOAK OK" se nessun errore non gestito e memoria stabile; 2 altrimenti. Log: %TEMP%\mixfonia-soak.log.
/// </summary>
public static class SoakTest
{
    public static async Task RunAsync(MainViewModel vm, int minutes)
    {
        var log = Path.Combine(Path.GetTempPath(), "mixfonia-soak.log");
        var sb = new System.Text.StringBuilder();
        void L(string s) { var line = $"{DateTime.Now:HH:mm:ss} {s}"; sb.AppendLine(line); Console.Error.WriteLine(line); try { File.WriteAllText(log, sb.ToString()); } catch { } }

        var rnd = new Random(12345);
        var proc = Process.GetCurrentProcess();
        var samplesMb = new List<double>();
        int loads = 0, loadFails = 0, actions = 0;
        var t0 = DateTime.UtcNow;
        var end = t0.AddMinutes(minutes);
        var lastReport = t0;

        await Task.Delay(2500);
        var pool = vm.Tracks.Where(t => !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath)).ToList();
        L($"SOAK start: {minutes} min, {pool.Count} brani disponibili, uscita: {vm.Engine.OutputDescription}");
        if (pool.Count == 0) { L("SOAK FAIL: libreria vuota"); Environment.Exit(2); }
        vm.IsProjectorOpen = true;
        vm.MasterVolume = 0.6;

        var decks = new[] { vm.DeckA, vm.DeckB };
        void Load(DeckViewModel d)
        {
            var t = pool[rnd.Next(pool.Count)];
            try
            {
                if (vm.LoadToDeck(d, t, singer: rnd.Next(3) == 0 ? "Soak" : "", keyShift: rnd.Next(-2, 3), confirmIfPlaying: false)) { loads++; d.Play(); }
                else loadFails++;
            }
            catch (Exception ex) { loadFails++; CrashLog.Write("soak load " + t.FilePath, ex); }
            // partenza da un punto casuale, così si esercitano tutte le zone del brano (non solo gli intro)
            if (d.HasTrack && d.DurationSec > 40) d.Deck.Seek(rnd.NextDouble() * d.DurationSec * 0.7);
        }
        Load(vm.DeckA); Load(vm.DeckB);

        while (DateTime.UtcNow < end)
        {
            await Task.Delay(rnd.Next(300, 1500));
            var d = decks[rnd.Next(2)];
            try
            {
                actions++;
                switch (rnd.Next(24))
                {
                    case 0: case 1: Load(d); break;
                    case 2: if (d.IsPlaying) d.Pause(); else d.Play(); break;
                    case 3: d.HotCue((1 + rnd.Next(8)).ToString()); break;
                    case 4: d.BeatJump(rnd.Next(2) == 0 ? "4" : "-4"); break;
                    case 5: d.LoopBeatsCommand.Execute(new[] { "1", "2", "4", "8" }[rnd.Next(4)]); break;
                    case 6: d.LoopExitCommand.Execute(null); break;
                    case 7: d.KeyShift = rnd.Next(-4, 5); break;
                    case 8: d.TempoPercent = rnd.Next(-8, 9); break;
                    case 9: d.EqLowKill = !d.EqLowKill; break;
                    case 10: d.FilterValue = rnd.NextDouble() * 2 - 1; break;
                    case 11: d.EchoOn = !d.EchoOn; break;
                    case 12: d.ReverbOn = !d.ReverbOn; break;
                    case 13: vm.Crossfader = rnd.NextDouble() * 2 - 1; break;
                    case 14: vm.AutoMix = !vm.AutoMix; break;
                    case 15: d.CueOn = !d.CueOn; break;
                    case 16: d.Fader = 0.3 + rnd.NextDouble() * 0.7; break;
                    case 17: vm.TriggerPadByIndex(rnd.Next(8)); break;
                    case 18: d.Deck.Seek(rnd.NextDouble() * Math.Max(1, d.DurationSec)); break;
                    case 19: d.VocalRemove = !d.VocalRemove; break;
                    case 20: if (!string.IsNullOrEmpty(vm.Settings.MicDeviceId)) vm.MicOn = !vm.MicOn; break;
                    case 21: d.Quantize = !d.Quantize; break;
                    case 22: d.BrakeCommand.Execute(null); break;
                    default: d.EqLowKill = d.EqMidKill = d.EqHighKill = false; d.EchoOn = d.ReverbOn = false; d.FilterValue = 0; d.KeyShift = 0; d.TempoPercent = 0; break;
                }
                // se un deck è vuoto o fermo da un pezzo, ricarichiamo: il test deve suonare quasi sempre
                foreach (var x in decks) if (!x.HasTrack || (!x.IsPlaying && rnd.Next(6) == 0)) Load(x);
            }
            catch (Exception ex) { CrashLog.Write("soak action", ex); }

            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 30)
            {
                lastReport = DateTime.UtcNow;
                proc.Refresh();
                double mb = proc.PrivateMemorySize64 / 1048576.0;
                samplesMb.Add(mb);
                L($"{(DateTime.UtcNow - t0).TotalMinutes:0.0} min · mem {mb:0} MB · ws {proc.WorkingSet64 / 1048576} MB · gc {GC.GetTotalMemory(false) / 1048576} MB · brani {loads} (falliti {loadFails}) · azioni {actions} · " +
                  $"deck errori A{vm.DeckA.Deck.Faults}/B{vm.DeckB.Deck.Faults} · riavvii uscita {vm.Engine.OutputRestarts} · errori log {CrashLog.Count} · A: {vm.DeckA.Title} ({(vm.DeckA.IsPlaying ? "play" : "stop")}) B: {vm.DeckB.Title} ({(vm.DeckB.IsPlaying ? "play" : "stop")})");
            }
        }

        // verdetto: nessun errore non gestito; memoria che non cresce senza freno (ultimo quarto vs primo quarto, tolleranza 250 MB)
        double first = samplesMb.Take(Math.Max(1, samplesMb.Count / 4)).Average();
        double last = samplesMb.Skip(samplesMb.Count - Math.Max(1, samplesMb.Count / 4)).Average();
        bool memOk = last - first < 250;
        int audioFaults = vm.DeckA.Deck.Faults + vm.DeckB.Deck.Faults;
        bool ok = CrashLog.Count == 0 && memOk && vm.Engine.OutputRestarts == 0 && audioFaults == 0;
        L($"riepilogo: brani {loads}, falliti {loadFails}, azioni {actions}, mem {first:0}→{last:0} MB ({(memOk ? "stabile" : "CRESCE")}), errori audio {audioFaults}, riavvii uscita {vm.Engine.OutputRestarts}, errori registrati {CrashLog.Count}" +
          (vm.DeckA.Deck.LastError != null ? " · A: " + vm.DeckA.Deck.LastError : "") + (vm.DeckB.Deck.LastError != null ? " · B: " + vm.DeckB.Deck.LastError : "") + (vm.Engine.LastOutputError != null ? " · uscita: " + vm.Engine.LastOutputError : ""));
        L(ok ? "SOAK OK" : "SOAK FAIL");
        Environment.Exit(ok ? 0 : 2);
    }
}
