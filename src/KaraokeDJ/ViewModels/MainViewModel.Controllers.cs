using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaraokeDJ.Services;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Console plug &amp; play: ogni 3 s si guardano le porte MIDI; se ne compare una con un preset di fabbrica
/// (Assets/Controllers) viene aperta e mappata da sola. Le mappature imparate dall'utente stanno sopra al preset.
/// </summary>
public partial class MainViewModel
{
    private DispatcherTimer? _controllerTimer;
    [ObservableProperty] private string _controllerStatus = "Nessuna console collegata";
    [ObservableProperty] private bool _controllerConnected;
    public ControllerPreset? ActivePreset { get; private set; }
    private bool _midiDisabledByUser;

    private void StartControllerWatch()
    {
        Midi.MessageReceived += OnMidiMessageForHints;
        _midiDisabledByUser = Settings.MidiDeviceName == "-";
        CheckControllers(announce: true);
        _controllerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _controllerTimer.Tick += (_, _) => CheckControllers(announce: true);
        _controllerTimer.Start();
    }

    /// <summary>
    /// Mixer a schermo con le manopole (trim, EQ, filtro, pan, cuffia, mic). Con una console che ha il suo mixer
    /// si nascondono, come fa Serato: quello spazio va alla libreria. Il tasto 🎚 le riporta quando servono.
    /// </summary>
    [ObservableProperty] private bool _mixerKnobsVisible = true;
    [ObservableProperty] private double _mixerMeterHeight = 190;
    [ObservableProperty] private double _masterMeterHeight = 146;
    partial void OnMixerKnobsVisibleChanged(bool value)
    {
        MixerMeterHeight = value ? 190 : 110;
        MasterMeterHeight = value ? 146 : 86;
    }

    /// <summary>La console ha EQ e fader di canale suoi? Allora il mixer a schermo è un doppione.</summary>
    private static bool HasHardwareMixer(ControllerPreset? p) =>
        p != null && p.Mappings.Any(m => m.Action == "a.eqlow") && p.Mappings.Any(m => m.Action == "a.fader");

    /// <summary>LED della console (pad hot cue, play): vedi <see cref="MidiFeedback"/>.</summary>
    public MidiFeedback Leds { get; } = new();
    private string _ledState = "";

    /// <summary>
    /// Aggiorna i LED: pad degli hot cue accesi dove c'è un punto salvato (blu sul deck A, rosso sul B),
    /// tasto play acceso mentre il deck suona. Chiamata dal timer: si manda qualcosa solo se è cambiato davvero.
    /// </summary>
    private void TickLeds()
    {
        if (!Leds.IsOpen) return;
        TickVu();
        var sb = new System.Text.StringBuilder();
        foreach (var d in new[] { DeckA, DeckB })
        {
            sb.Append(d.IsPlaying ? '1' : '0').Append(d.HasTrack ? '1' : '0');
            for (int i = 0; i < 8; i++) sb.Append(d.Track?.HotCue(i) >= 0 ? '1' : '0');
        }
        // jingle: pad acceso se ha un suono (FilePath e non HasFile: niente File.Exists 25 volte al secondo)
        foreach (var p in Pads) sb.Append(p.IsPlaying ? '2' : string.IsNullOrEmpty(p.FilePath) ? '0' : '1');
        var now = sb.ToString();
        if (now == _ledState) return;
        _ledState = now;
        var pal = ActivePreset?.Leds ?? new LedPalette();
        foreach (var (d, colour) in new[] { (DeckA, pal.A), (DeckB, pal.B) })
        {
            string p = d == DeckA ? "a." : "b.";
            int on = pal.Button ?? colour;
            Leds.Set(Midi.KeyFor(p + "play"), d.IsPlaying ? on : MidiFeedback.Off);
            // CUE acceso a deck carico e fermo: si vede subito quale deck è pronto a partire
            Leds.Set(Midi.KeyFor(p + "cue"), d.HasTrack && !d.IsPlaying ? on : MidiFeedback.Off);
            for (int i = 0; i < 8; i++)
                Leds.Set(Midi.KeyFor($"{p}hotcue{i + 1}"), d.Track?.HotCue(i) >= 0 ? colour : MidiFeedback.Off);
        }
        for (int i = 0; i < Pads.Count; i++)
        {
            var pad = Pads[i];
            Leds.Set(Midi.KeyFor($"pad{i + 1}"), pad.IsPlaying ? pal.B : string.IsNullOrEmpty(pad.FilePath) ? MidiFeedback.Off : pal.A);
        }
    }

    /// <summary>
    /// VU meter della console seguono quelli dello schermo: in serata si guardano i livelli senza girarsi verso il PC.
    /// 25 volte al secondo, ma MidiFeedback manda solo i valori cambiati.
    /// </summary>
    private void TickVu()
    {
        if (ActivePreset?.Leds?.Vu is not { Count: > 0 } vus) return;
        foreach (var v in vus)
        {
            double level = v.Source switch
            {
                "a" => Math.Max(DeckA.LevelL, DeckA.LevelR),
                "b" => Math.Max(DeckB.LevelL, DeckB.LevelR),
                "masterL" => MasterL,
                "masterR" => MasterR,
                _ => 0,
            };
            Leds.SetCc(v.Channel, v.Number, (int)Math.Round(Math.Clamp(level, 0, 1) * v.Max));
        }
    }

    private readonly HashSet<string> _unmappedSeen = new();
    private DateTime _lastUnmappedHint;

    /// <summary>
    /// Se la console manda un comando che nessun preset conosce, lo dice invece di ignorarlo in silenzio.
    /// Il MIDI non ha modo di presentarsi: la console manda numeri e basta, e nessun documento e mai
    /// completo per tutti i modelli. Cosi i buchi si scoprono usando la console, senza leggere log.
    /// </summary>
    private void OnMidiMessageForHints(MidiKey key, int value)
    {
        if (value == 0 || Midi.ActionFor(key) != null) return;
        // manopole a 14 bit (Inpulse e molte altre): il CC n+32 è la metà fine del CC n, già mappato. Non è un comando nuovo.
        if (key.Type == "cc" && key.Number is >= 32 and < 64 && Midi.ActionFor(key with { Number = key.Number - 32 }) != null) return;
        if (!_unmappedSeen.Add(key.ToString())) return;                       // una volta sola per controllo
        if ((DateTime.UtcNow - _lastUnmappedHint).TotalSeconds < 6) return;   // e senza inondare la barra
        _lastUnmappedHint = DateTime.UtcNow;
        StatusText = $"La console manda un comando che non conosco ({key}): assegnalo in Impostazioni → MIDI e tastiera";
    }

    private void CheckControllers(bool announce)
    {
        List<string> devices;
        try { devices = MidiService.ListDevices(); } catch { return; }
        if (Midi.IsOpen)
        {
            if (Midi.DeviceName != null && !devices.Contains(Midi.DeviceName))
            {
                var gone = Midi.DeviceName;
                Midi.Close(); ActivePreset = null; ControllerConnected = false;
                MixerKnobsVisible = true;   // senza console il mixer a schermo è l'unico che c'è
                ControllerStatus = "Console scollegata: " + gone;
                StatusText = ControllerStatus;
            }
            return;
        }
        if (_midiDisabledByUser) return;
        // prima la console scelta a mano (se c'è), poi la prima con un preset di fabbrica
        string? name = !string.IsNullOrEmpty(Settings.MidiDeviceName) && devices.Contains(Settings.MidiDeviceName) ? Settings.MidiDeviceName
                     : devices.FirstOrDefault(d => ControllerPresets.Find(d) != null);
        if (name != null) OpenController(name, announce);
    }

    /// <summary>Apre la porta MIDI e carica preset di fabbrica + personalizzazioni dell'utente.</summary>
    public bool OpenController(string name, bool announce)
    {
        if (!Midi.Open(name)) { ControllerStatus = "Impossibile aprire " + name; return false; }
        // la console scelta a mano vince sul riconoscimento dal nome della porta (nomi uguali, cloni, porte generiche)
        var preset = ControllerPresets.All.FirstOrDefault(p => p.Id == Settings.ControllerPresetId) ?? ControllerPresets.Find(name);
        ActivePreset = preset;
        Midi.LoadMappings(Enumerable.Empty<MidiMapping>());
        if (preset != null) Midi.AddMappings(preset.Mappings);
        Midi.AddMappings(Settings.MidiMappings);
        Settings.MidiDeviceName = name;
        ControllerConnected = true;
        MixerKnobsVisible = !HasHardwareMixer(preset);
        // stessa console anche in uscita, per i LED dei pad (se non c'è o non risponde, pazienza)
        if (Settings.ControllerLeds) { Leds.Open(name); _ledState = ""; } else Leds.Close();
        ControllerStatus = preset != null
            ? $"{preset.Name} — pronta, {Midi.Count} controlli mappati ({preset.Provenance})" + (preset.Incomplete ? " ⚠ senza play/cue: assegnali a mano" : "")
            : $"{name} — nessun preset: scegli la console qui sotto o usa \"Impara\"";
        if (announce) StatusText = preset != null ? $"Console riconosciuta: {preset.Name}. Plug & play: play, cue, jog, fader, EQ, filtro, hot cue, loop, browse." : "MIDI collegato: " + name;
        return true;
    }

    /// <summary>Sceglie a mano il preset da usare con la console collegata (o null = torna al riconoscimento automatico).</summary>
    public void ApplyPreset(string? presetId)
    {
        Settings.ControllerPresetId = presetId;
        _unmappedSeen.Clear();
        if (Midi.IsOpen && Midi.DeviceName is { } dev) OpenController(dev, announce: true);
        SaveSettings();
    }

    /// <summary>Scelta manuale dalle impostazioni ("(nessuno)" = MIDI spento anche per le console riconosciute).</summary>
    public void ApplyMidiDevice(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            _midiDisabledByUser = true; Settings.MidiDeviceName = "-";
            Midi.Close(); ActivePreset = null; ControllerConnected = false;
            ControllerStatus = "MIDI disattivato"; StatusText = ControllerStatus; return;
        }
        _midiDisabledByUser = false;
        OpenController(name, announce: true);
    }

    /// <summary>Le mappature da salvare: solo quelle diverse dal preset di fabbrica attivo.</summary>
    private List<MidiMapping> UserMidiMappings()
    {
        var all = Midi.ExportMappings();
        if (ActivePreset == null) return all;
        return all.Where(m => !ActivePreset.Mappings.Any(p => p.Key == m.Key && p.Action == m.Action && p.Invert == m.Invert && p.Relative == m.Relative)).ToList();
    }

    /// <summary>Importa una mappatura (JSON Mixfonia, XML Mixxx, djay) senza ancora salvarla: la finestra chiede nome e porta.</summary>
    public (ControllerPreset Preset, string Report) ImportControllerFile(string path) => ControllerPresets.Import(path);

    /// <summary>Salva il preset nella cartella dell'utente; se la porta collegata combacia, lo applica subito.</summary>
    public string SaveUserPreset(ControllerPreset preset)
    {
        var file = ControllerPresets.SaveUser(preset);
        if (Midi.DeviceName is { } n && ControllerPresets.Find(n)?.Id == preset.Id) { Settings.MidiMappings.Clear(); OpenController(n, announce: true); }
        else StatusText = $"Mappatura \"{preset.Name}\" salvata: si attiva quando colleghi una porta che contiene \"{string.Join("\" o \"", preset.Match)}\"";
        return file;
    }

    /// <summary>La mappatura in uso (preset + personalizzazioni) come file da condividere.</summary>
    public ControllerPreset ExportCurrentMapping()
    {
        if (Midi.DeviceName == null) throw new InvalidOperationException("Nessuna console collegata");
        return ControllerPresets.Export(Midi.DeviceName, ActivePreset, Midi.ExportMappings());
    }

    /// <summary>Torna alla mappatura di fabbrica della console collegata.</summary>
    [RelayCommand]
    private void ResetControllerMapping()
    {
        Settings.MidiMappings.Clear();
        if (Midi.DeviceName is { } n) OpenController(n, announce: false);
        StatusText = "Mappatura di fabbrica ripristinata";
    }

    // ---------------------------------------------------------------- diario MIDI (per capire cosa manda davvero una console)

    private System.IO.StreamWriter? _midiLog;
    public bool MidiLogging => _midiLog != null;
    public static string MidiLogFile => Path.Combine(AppPaths.Root, "midi-log.txt");

    /// <summary>
    /// Registra su file ogni messaggio che arriva dalla console, con l'azione a cui è mappato.
    /// Serve quando un controllo non fa quello che dovrebbe: si registra mezzo minuto muovendolo e si legge cosa manda.
    /// </summary>
    [RelayCommand]
    public void ToggleMidiLog()
    {
        if (_midiLog != null) { StopMidiLog(); return; }
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            _midiLog = new StreamWriter(MidiLogFile, append: true) { AutoFlush = true };
            _midiLog.WriteLine($"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} · console: {Midi.DeviceName ?? "nessuna"} · preset: {ActivePreset?.Name ?? "nessuno"} ---");
            Midi.MessageReceived += OnMidiLogMessage;
            StatusText = "Registrazione MIDI avviata: muovi i controlli, poi premi di nuovo per fermare (" + MidiLogFile + ")";
        }
        catch (Exception ex) { StatusText = "Non riesco a registrare il MIDI: " + ex.Message; }
        OnPropertyChanged(nameof(MidiLogging));
    }

    private void OnMidiLogMessage(MidiKey key, int value)
    {
        var action = Midi.ActionFor(key);
        _midiLog?.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {(Midi.ShiftHeld ? "SHIFT+" : "      ")}{key}  = {value,3}  → {action ?? "(non mappato)"}");
    }

    public void StopMidiLog()
    {
        if (_midiLog == null) return;
        Midi.MessageReceived -= OnMidiLogMessage;
        _midiLog.WriteLine("--- fine ---");
        _midiLog.Dispose(); _midiLog = null;
        StatusText = "Registrazione MIDI finita: " + MidiLogFile;
        OnPropertyChanged(nameof(MidiLogging));
    }

    /// <summary>Elenco delle console con preset (per le impostazioni).</summary>
    public static string SupportedControllers => string.Join(", ", ControllerPresets.All.Select(p => p.Name + (p.IsUser ? " (tua)" : "")));
}
