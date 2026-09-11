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
                ActionTriggered?.Invoke(action, value, continuous);
        });
    }

    public void Dispose() => Close();
}
