using KaraokeDJ.ViewModels;
using NAudio.CoreAudioApi;

namespace KaraokeDJ.Services;

/// <summary>Esito di un controllo: verde (a posto), giallo (si può fare meglio), rosso (da sistemare prima di partire).</summary>
public enum CheckLevel { Ok, Warn, Fail }

public sealed record CheckResult(CheckLevel Level, string What, string Detail, string Advice = "")
{
    public string Icon => Level switch { CheckLevel.Ok => "✓", CheckLevel.Warn => "!", _ => "✗" };
}

/// <summary>
/// Controllo pre-serata: mezz'ora prima di cominciare si preme un tasto e si vede in un colpo d'occhio
/// se manca qualcosa. Non prova a riparare niente e non fa suonare niente: guarda e riferisce.
///
/// L'ordine è quello dei guai veri: prima l'audio (senza quello non c'è serata), poi schermo, licenza,
/// disco, libreria, console, e infine le cose che possono far partire la musica da sole.
/// </summary>
public static class Preflight
{
    public static List<CheckResult> Run(MainViewModel vm)
    {
        var list = new List<CheckResult>();
        var s = vm.Settings;

        // ---- uscita audio
        var outDesc = vm.Engine.OutputDescription;
        if (string.IsNullOrEmpty(outDesc))
            list.Add(new(CheckLevel.Fail, "Uscita audio", "nessuna uscita attiva", "Impostazioni → Audio: scegli la scheda della serata"));
        else if (vm.Engine.OutputRestarts > 0)
            list.Add(new(CheckLevel.Warn, "Uscita audio", $"{outDesc} · già riavviata {vm.Engine.OutputRestarts} volte", "Se succede spesso, cambia porta USB o scheda"));
        else
            list.Add(new(CheckLevel.Ok, "Uscita audio", outDesc));

        // ---- cuffia (pre-ascolto): non obbligatoria, ma senza non si prepara il brano dopo
        list.Add(vm.Engine.CueRunning
            ? new(CheckLevel.Ok, "Cuffia (pre-ascolto)", vm.Engine.CueDescription)
            : new CheckResult(CheckLevel.Warn, "Cuffia (pre-ascolto)", "non configurata",
                "Impostazioni → Audio → Cuffia: seconda scheda, o canali 3-4 se la console ce li ha"));

        // ---- microfono
        if (vm.Engine.Mic.IsOn)
            list.Add(new(CheckLevel.Ok, "Microfono", "acceso e funzionante"));
        else if (!string.IsNullOrEmpty(s.MicDeviceId) && DeviceExists(s.MicDeviceId, DataFlow.Capture))
            list.Add(new(CheckLevel.Ok, "Microfono", "ingresso scelto, spento (si accende col tasto MIC)"));
        else
            list.Add(new(CheckLevel.Warn, "Microfono", "nessun ingresso scelto", "Impostazioni → Audio → Microfono"));

        // ---- proiettore
        int screens = 0;
        try { screens = System.Windows.Forms.Screen.AllScreens.Length; } catch { }
        list.Add(screens > 1
            ? new(CheckLevel.Ok, "Proiettore", $"{screens} schermi collegati")
            : new CheckResult(CheckLevel.Warn, "Proiettore", "un solo schermo",
                "Collega proiettore o TV PRIMA di aprire Mixfonia, e controlla che Windows non sposti l'audio sull'HDMI"));

        // ---- licenza
        if (vm.IsLicensed) list.Add(new(CheckLevel.Ok, "Licenza", "attiva"));
        else if (vm.IsTrial) list.Add(new(CheckLevel.Warn, "Licenza", $"prova, {vm.TrialDaysLeft} giorni rimasti", "Attivala prima della serata: alla scadenza compare la scritta sul proiettore"));
        else list.Add(new(CheckLevel.Fail, "Licenza", "scaduta: scritta \"versione dimostrativa\" sul proiettore", "Attiva la licenza"));

        // ---- spazio su disco (serve alla registrazione e ai file temporanei)
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(AppPaths.Root))!);
            double gb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            list.Add(gb < 3 ? new(CheckLevel.Fail, "Spazio su disco", $"{gb:0.0} GB liberi", "Libera spazio: la registrazione prende circa 0,6 GB l'ora")
                   : gb < 10 ? new(CheckLevel.Warn, "Spazio su disco", $"{gb:0.0} GB liberi", "Bastano per ~15 ore di registrazione, ma tienilo d'occhio")
                   : new CheckResult(CheckLevel.Ok, "Spazio su disco", $"{gb:0.0} GB liberi"));
        }
        catch { }

        // ---- libreria
        int tot = vm.Tracks.Count;
        int analyzed = vm.Tracks.Count(t => t.Analyzed && t.Bpm > 0);
        int missing = vm.Tracks.Count(t => !string.IsNullOrEmpty(t.FilePath) && !File.Exists(t.FilePath));
        if (tot == 0) list.Add(new(CheckLevel.Fail, "Libreria", "vuota", "Aggiungi le cartelle della musica"));
        else
        {
            list.Add(analyzed >= tot * 0.9
                ? new(CheckLevel.Ok, "Libreria", $"{tot} brani, {analyzed} analizzati")
                : new CheckResult(CheckLevel.Warn, "Libreria", $"{tot} brani, solo {analyzed} analizzati",
                    "Senza analisi niente BPM, tonalità, griglia e passaggi automatici: lancia l'analisi ORA, non in serata"));
            if (missing > 0)
                list.Add(new(CheckLevel.Fail, "File mancanti", $"{missing} brani in libreria puntano a file che non ci sono",
                    "Disco esterno staccato? Ricollegalo e rifai la scansione"));
        }

        // ---- console
        list.Add(vm.ControllerConnected
            ? new(CheckLevel.Ok, "Console", vm.ControllerStatus)
            : new CheckResult(CheckLevel.Warn, "Console", "nessuna collegata", "Se la usi in serata, collegala adesso e prova play, fader e piatti"));

        // ---- diario errori
        list.Add(CrashLog.Count == 0
            ? new(CheckLevel.Ok, "Errori registrati", "nessuno da quando è aperta l'app")
            : new CheckResult(CheckLevel.Warn, "Errori registrati", $"{CrashLog.Count} in questa sessione", "Guarda crash.log e mandamelo"));

        // ---- cose che possono far partire la musica da sole
        var auto = new List<string>();
        if (vm.AutoMix) auto.Add("auto-mix acceso");
        if (vm.FillMusicOn) auto.Add("riempimento acceso");
        list.Add(auto.Count == 0
            ? new(CheckLevel.Ok, "Partenze automatiche", "niente parte da solo")
            : new CheckResult(CheckLevel.Warn, "Partenze automatiche", string.Join(", ", auto),
                "Va bene se lo vuoi: ricorda che FERMA TUTTO (Esc) li spegne insieme alla musica"));

        // ---- coda pronta
        list.Add(vm.Queue.Count > 0
            ? new(CheckLevel.Ok, "Coda", vm.Queue.Count == 1 ? "1 brano pronto" : $"{vm.Queue.Count} brani pronti")
            : new CheckResult(CheckLevel.Warn, "Coda", "vuota", "Preparane qualcuno: il primo quarto d'ora è quello in cui si corre di più"));

        return list;
    }

    private static bool DeviceExists(string id, DataFlow flow)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            return en.EnumerateAudioEndPoints(flow, DeviceState.Active).Any(d => d.ID == id);
        }
        catch { return false; }
    }

    /// <summary>Riga di riepilogo: "pronto" solo se non c'è niente di rosso.</summary>
    public static string Summary(List<CheckResult> checks)
    {
        int fail = checks.Count(c => c.Level == CheckLevel.Fail);
        int warn = checks.Count(c => c.Level == CheckLevel.Warn);
        return fail > 0 ? $"{fail} cose da sistemare prima di partire" + (warn > 0 ? $", {warn} da guardare" : "")
             : warn > 0 ? $"Si può partire · {warn} cose da guardare"
             : "Tutto a posto: si può partire";
    }

    /// <summary>Versione testuale (per <c>--preflight</c> e per incollarmela).</summary>
    public static string AsText(List<CheckResult> checks) =>
        Summary(checks) + "\n" + string.Join("\n", checks.Select(c =>
            $"  {c.Icon} {c.What}: {c.Detail}" + (c.Level != CheckLevel.Ok && c.Advice.Length > 0 ? "\n      → " + c.Advice : "")));
}
