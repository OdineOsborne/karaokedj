using NAudio.Midi;

namespace KaraokeDJ.Services;

/// <summary>Un messaggio MIDI semplificato: CC o nota.</summary>
public sealed record MidiKey(string Type, int Channel, int Number)
{
    public override string ToString() => $"{Type} ch{Channel} #{Number}";
}

public sealed class MidiMapping
{
    public string Action { get; set; } = "";
    public string Type { get; set; } = "cc";   // cc | note
    public int Channel { get; set; }
    public int Number { get; set; }
    /// <summary>Fader montato al contrario sulla console: il valore va invertito.</summary>
    public bool Invert { get; set; }
    /// <summary>Encoder relativo (jog, browse): il valore è un delta, non una posizione.</summary>
    public bool Relative { get; set; }

    public MidiKey Key => new(Type, Channel, Number);
}

/// <summary>Compatibilità: il catalogo delle azioni è in <see cref="AppActions"/>.</summary>
public static class MidiActions
{
    public static IEnumerable<(string Id, string Label, bool IsContinuous)> All => AppActions.All.Select(a => (a.Id, a.Label, a.IsContinuous));
}

/// <summary>Ingresso MIDI (NAudio) con mappatura "learn" e dispatch delle azioni sul thread UI.</summary>
public sealed class MidiService : IDisposable
{
    private MidiIn? _in;
    private readonly Dictionary<MidiKey, string> _map = new();
    private readonly HashSet<MidiKey> _invert = new();
    /// <summary>Encoder relativi mappati su azioni assolute (es. SHIFT+jog → tempo): teniamo noi la posizione 0..127.</summary>
    private readonly HashSet<MidiKey> _relative = new();
    private readonly Dictionary<MidiKey, int> _relPos = new();
    private Action<MidiKey>? _learnCallback;

    /// <summary>(azione, valore 0..127, è un CC continuo). Chiamato sul thread UI.</summary>
    public event Action<string, int, bool>? ActionTriggered;
    /// <summary>Ultimo messaggio ricevuto (per mostrare "attività MIDI" nella UI).</summary>
    public event Action<MidiKey, int>? MessageReceived;

    public string? DeviceName { get; private set; }
    public bool IsOpen => _in != null;

    public static List<string> ListDevices()
    {
        var list = new List<string>();
        try
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++) list.Add(MidiIn.DeviceInfo(i).ProductName);
        }
        catch { }
        return list;
    }

    public void LoadMappings(IEnumerable<MidiMapping> mappings)
    {
        _map.Clear(); _invert.Clear(); _relative.Clear(); _relPos.Clear();
        foreach (var m in mappings) Put(m);
    }

    public List<MidiMapping> ExportMappings() =>
        _map.Select(kv => new MidiMapping { Action = kv.Value, Type = kv.Key.Type, Channel = kv.Key.Channel, Number = kv.Key.Number, Invert = _invert.Contains(kv.Key), Relative = _relative.Contains(kv.Key) }).ToList();

    /// <summary>Aggiunge le mappature senza cancellare le altre (preset di fabbrica + personalizzazioni).</summary>
    public void AddMappings(IEnumerable<MidiMapping> mappings)
    {
        foreach (var m in mappings) Put(m);
    }

    private void Put(MidiMapping m)
    {
        _map[m.Key] = m.Action;
        if (m.Invert) _invert.Add(m.Key); else _invert.Remove(m.Key);
        if (m.Relative) _relative.Add(m.Key); else _relative.Remove(m.Key);
    }

    /// <summary>Azioni che leggono già un delta (1..63 avanti, 65..127 indietro): per loro il flag Relative non cambia niente.</summary>
    private static bool TakesDelta(string action) => action == "browse" || action.EndsWith(".jog");
    public int Count => _map.Count;

    public MidiKey? KeyFor(string action) => _map.FirstOrDefault(kv => kv.Value == action).Key;
    public string? ActionFor(MidiKey key) => _map.TryGetValue(key, out var a) ? a : null;
    public bool IsInverted(string action) => KeyFor(action) is { } k && _invert.Contains(k);
    public bool IsRelative(string action) => KeyFor(action) is { } k && _relative.Contains(k);
    /// <summary>Inverti / relativo sul controllo già mappato a questa azione (correzioni fai-da-te: fader al contrario, encoder).</summary>
    public void SetFlags(string action, bool invert, bool relative)
    {
        if (KeyFor(action) is not { } k) return;
        if (invert) _invert.Add(k); else _invert.Remove(k);
        if (relative) _relative.Add(k); else { _relative.Remove(k); _relPos.Remove(k); }
    }

    public void SetMapping(string action, MidiKey key)
    {
        foreach (var k in _map.Where(kv => kv.Value == action).Select(kv => kv.Key).ToList()) _map.Remove(k);
        _map[key] = action;
    }

    public void ClearMapping(string action)
    {
        foreach (var k in _map.Where(kv => kv.Value == action).Select(kv => kv.Key).ToList()) _map.Remove(k);
    }

    /// <summary>Il prossimo messaggio ricevuto viene passato al callback invece di eseguire l'azione.</summary>
    public void BeginLearn(Action<MidiKey> callback) => _learnCallback = callback;
    public void CancelLearn() => _learnCallback = null;

    public bool Open(string? deviceName)
    {
        Close();
        if (string.IsNullOrEmpty(deviceName)) return false;
        try
        {
            for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            {
                if (MidiIn.DeviceInfo(i).ProductName != deviceName) continue;
                _in = new MidiIn(i);
                _in.MessageReceived += OnMessage;
                _in.ErrorReceived += (_, _) => { };
                _in.Start();
                DeviceName = deviceName;
                return true;
            }
        }
        catch { Close(); }
        return false;
    }

    public void Close()
    {
        if (_in == null) return;
        try { _in.Stop(); _in.Dispose(); } catch { }
        _in = null;
        DeviceName = null;
    }

    private void OnMessage(object? sender, MidiInMessageEventArgs e)
    {
        MidiKey? key = null;
        int value = 0;
        bool continuous = false;
        switch (e.MidiEvent)
        {
            case ControlChangeEvent cc:
                key = new MidiKey("cc", cc.Channel, (int)cc.Controller);
                value = cc.ControllerValue;
                continuous = true;
                break;
            case NoteOnEvent on when on.Velocity > 0:
                key = new MidiKey("note", on.Channel, on.NoteNumber);
                value = on.Velocity;
                break;
            case NoteEvent off when off.CommandCode == MidiCommandCode.NoteOff || (off is NoteOnEvent on0 && on0.Velocity == 0):
                key = new MidiKey("note", off.Channel, off.NoteNumber);
                value = 0; // rilascio (per le azioni "tieni premuto")
                break;
            default:
                return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(() =>
        {
            MessageReceived?.Invoke(key, value);
            if (_learnCallback != null)
            {
                if (value == 0 && !continuous) return; // il rilascio del tasto non è un controllo da imparare
                var cb = _learnCallback;
                _learnCallback = null;
                cb(key);
                return;
            }
            if (_map.TryGetValue(key, out var action))
            {
                int v = value;
                if (continuous && _relative.Contains(key) && !TakesDelta(action))
                {
                    // encoder relativo su un controllo assoluto: accumuliamo la posizione partendo dal centro
                    int delta = v == 0 ? 0 : v < 64 ? v : v - 128;
                    if (delta == 0) return;
                    v = Math.Clamp(_relPos.GetValueOrDefault(key, 64) + delta, 0, 127);
                    _relPos[key] = v;
                }
                ActionTriggered?.Invoke(action, continuous && _invert.Contains(key) ? 127 - v : v, continuous);
            }
        });
    }

    public void Dispose() => Close();
}
