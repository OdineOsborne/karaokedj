using NAudio.Midi;

namespace KaraokeDJ.Services;

/// <summary>
/// Accende i LED della console (pad degli hot cue, tasto play).
///
/// Come si sa quale LED accendere: sulle console il LED di un tasto risponde allo stesso numero di nota
/// che il tasto manda quando lo premi — il documento Hercules della P8 lo dice esplicitamente (tabella
/// "Midi Output" con gli stessi nomi di controllo). Quindi non serve una seconda tabella: si riusa la mappatura
/// dei comandi. Se una console non funziona così, non si accende niente e non si rompe niente.
///
/// Regola: i LED non devono mai poter disturbare l'audio. Tutto qui dentro è "prova e lascia perdere".
/// </summary>
public sealed class MidiFeedback : IDisposable
{
    private MidiOut? _out;
    private readonly Dictionary<int, int> _sent = new();   // ultimo valore mandato per nota: si scrive solo se cambia

    public bool IsOpen => _out != null;
    public string? DeviceName { get; private set; }

    /// <summary>Colori del documento P8 (le altre console accendono comunque: per loro qualsiasi valore &gt; 0 è "acceso").</summary>
    public const int Off = 0x00, Blue = 0x7D, Red = 0x7E, Purple = 0x7F;

    public static List<string> ListDevices()
    {
        var list = new List<string>();
        try { for (int i = 0; i < MidiOut.NumberOfDevices; i++) list.Add(MidiOut.DeviceInfo(i).ProductName); }
        catch { }
        return list;
    }

    /// <summary>Apre l'uscita MIDI con lo stesso nome della console collegata in ingresso.</summary>
    public bool Open(string? deviceName)
    {
        Close();
        if (string.IsNullOrEmpty(deviceName)) return false;
        try
        {
            for (int i = 0; i < MidiOut.NumberOfDevices; i++)
            {
                if (MidiOut.DeviceInfo(i).ProductName != deviceName) continue;
                _out = new MidiOut(i);
                DeviceName = deviceName;
                return true;
            }
        }
        catch { _out = null; }
        return false;
    }

    public void Close()
    {
        if (_out == null) return;
        try { AllOff(); _out.Dispose(); } catch { }
        _out = null; DeviceName = null; _sent.Clear();
    }

    /// <summary>Accende/spegne il LED di un tasto (note): solo se il valore è cambiato, per non intasare la porta.</summary>
    public void Set(MidiKey? key, int value)
    {
        if (_out == null || key is not { Type: "note" }) return;
        int id = key.Channel * 1000 + key.Number;
        if (_sent.TryGetValue(id, out var prev) && prev == value) return;
        _sent[id] = value;
        try { _out.Send(MidiMessage.StartNote(key.Number, value, Math.Clamp(key.Channel, 1, 16)).RawData); }
        catch { }
    }

    public void AllOff()
    {
        if (_out == null) return;
        foreach (var id in _sent.Keys.ToList())
        {
            try { _out.Send(MidiMessage.StartNote(id % 1000, 0, Math.Clamp(id / 1000, 1, 16)).RawData); } catch { }
        }
        _sent.Clear();
    }

    public void Dispose() => Close();
}
