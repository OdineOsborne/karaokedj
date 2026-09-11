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

    public MidiKey Key => new(Type, Channel, Number);
}

/// <summary>Azioni controllabili via MIDI. Il valore è 0..127 per i CC, velocity per le note.</summary>
public static class MidiActions
{
    public static readonly (string Id, string Label, bool IsContinuous)[] All =
    {
        ("crossfader", "Crossfader", true),
        ("master", "Volume master", true),
        ("a.play", "Deck A – Play/Pausa", false),
        ("a.stop", "Deck A – Stop", false),
        ("a.volume", "Deck A – Gain", true),
        ("a.tempo", "Deck A – Tempo", true),
        ("a.keyup", "Deck A – Tonalità +", false),
        ("a.keydown", "Deck A – Tonalità −", false),
        ("a.keyreset", "Deck A – Tonalità 0", false),
        ("a.keylock", "Deck A – Key lock on/off", false),
        ("b.play", "Deck B – Play/Pausa", false),
        ("b.stop", "Deck B – Stop", false),
        ("b.volume", "Deck B – Gain", true),
        ("b.tempo", "Deck B – Tempo", true),
        ("b.keyup", "Deck B – Tonalità +", false),
        ("b.keydown", "Deck B – Tonalità −", false),
        ("b.keyreset", "Deck B – Tonalità 0", false),
        ("b.keylock", "Deck B – Key lock on/off", false),
        ("next", "Prossimo in coda", false),
        ("fadeA", "Sfuma verso A", false),
        ("fadeB", "Sfuma verso B", false),
        ("projector", "Proiettore on/off", false),
        ("padstop", "Stop tutti i pad", false),
        ("pad1", "Pad 1 (F1)", false), ("pad2", "Pad 2 (F2)", false), ("pad3", "Pad 3 (F3)", false), ("pad4", "Pad 4 (F4)", false),
        ("pad5", "Pad 5 (F5)", false), ("pad6", "Pad 6 (F6)", false), ("pad7", "Pad 7 (F7)", false), ("pad8", "Pad 8 (F8)", false),
        ("pad9", "Pad 9 (F9)", false), ("pad10", "Pad 10 (F10)", false), ("pad11", "Pad 11 (F11)", false), ("pad12", "Pad 12 (F12)", false),
    };
}

/// <summary>Ingresso MIDI (NAudio) con mappatura "learn" e dispatch delle azioni sul thread UI.</summary>
public sealed class MidiService : IDisposable
{
    private MidiIn? _in;
    private readonly Dictionary<MidiKey, string> _map = new();
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
        _map.Clear();
        foreach (var m in mappings) _map[m.Key] = m.Action;
    }

    public List<MidiMapping> ExportMappings() =>
        _map.Select(kv => new MidiMapping { Action = kv.Value, Type = kv.Key.Type, Channel = kv.Key.Channel, Number = kv.Key.Number }).ToList();

    public MidiKey? KeyFor(string action) => _map.FirstOrDefault(kv => kv.Value == action).Key;

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
                var cb = _learnCallback;
                _learnCallback = null;
                cb(key);
                return;
            }
            if (_map.TryGetValue(key, out var action))
                ActionTriggered?.Invoke(action, value, continuous);
        });
    }

    public void Dispose() => Close();
}
