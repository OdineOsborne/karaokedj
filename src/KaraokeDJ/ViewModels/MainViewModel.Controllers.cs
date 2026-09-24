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
        _midiDisabledByUser = Settings.MidiDeviceName == "-";
        CheckControllers(announce: true);
        _controllerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _controllerTimer.Tick += (_, _) => CheckControllers(announce: true);
        _controllerTimer.Start();
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
        var preset = ControllerPresets.Find(name);
        ActivePreset = preset;
        Midi.LoadMappings(Enumerable.Empty<MidiMapping>());
        if (preset != null) Midi.AddMappings(preset.Mappings);
        Midi.AddMappings(Settings.MidiMappings);
        Settings.MidiDeviceName = name;
        ControllerConnected = true;
        ControllerStatus = preset != null ? $"{preset.Name} — pronta, {Midi.Count} controlli mappati" : $"{name} — nessun preset: usa \"Impara\" (tasto destro sui comandi)";
        if (announce) StatusText = preset != null ? $"Console riconosciuta: {preset.Name}. Plug & play: play, cue, jog, fader, EQ, filtro, hot cue, loop, browse." : "MIDI collegato: " + name;
        return true;
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
