using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeDJ.Audio;

/// <summary>
/// Canale microfono: cattura WASAPI → stereo 44.1k → gain → effetti voce (eco, riverbero) → mixer principale.
/// Il "talk-over" abbassa i deck: a mano (tasto TALK) o automatico quando la voce supera la soglia,
/// con attacco rapido e rilascio lento come su un mixer da DJ. Il livello serve anche all'applausometro.
/// </summary>
public sealed class MicInput : ISampleProvider, IDisposable
{
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _src;
    private readonly object _gate = new();
    private float _gain = 1f, _lastGain = 1f;
    private double _duckEnv = 1;
    private volatile bool _on;

    public FxChain Fx { get; } = new();
    public WaveFormat WaveFormat => SourceFactory.Format;
    public volatile float Peak;
    /// <summary>Picco "lento" (decade in ~1 s): per l'applausometro.</summary>
    public volatile float SlowPeak;

    public bool IsOn => _on;
    public string? DeviceId { get; private set; }
    public string Status { get; private set; } = "";

    /// <summary>Gain lineare (0 … 8).</summary>
    public float Gain { get => _gain; set => _gain = Math.Clamp(value, 0f, 8f); }
    /// <summary>Talk-over manuale (tasto premuto).</summary>
    public volatile bool TalkOver;
    /// <summary>Talk-over automatico: quando il mic supera la soglia i deck si abbassano.</summary>
    public volatile bool AutoDuck;
    /// <summary>Soglia lineare del talk-over automatico (0.05 ≈ −26 dB).</summary>
    public float DuckThreshold = 0.05f;
    /// <summary>Quanto si abbassano i deck (lineare: 0.25 ≈ −12 dB).</summary>
    public float DuckGain = 0.25f;
    /// <summary>Fattore corrente di attenuazione da applicare ai deck (1 = niente).</summary>
    public float DuckFactor => (float)_duckEnv;

    public static List<AudioDevice> ListInputDevices()
    {
        var list = new List<AudioDevice> { new("", "Microfono predefinito di Windows") };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    public void Start(string? deviceId)
    {
        Stop();
        try
        {
            MMDevice? dev = null;
            using (var en = new MMDeviceEnumerator())
                dev = string.IsNullOrEmpty(deviceId) ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : en.GetDevice(deviceId);
            var cap = new WasapiCapture(dev, true, 30);
            var buf = new BufferedWaveProvider(cap.WaveFormat) { DiscardOnBufferOverflow = true, ReadFully = true, BufferDuration = TimeSpan.FromSeconds(2) };
            ISampleProvider s = buf.ToSampleProvider();
            if (s.WaveFormat.Channels == 1) s = new MonoToStereoSampleProvider(s);
            else if (s.WaveFormat.Channels > 2) s = new MultiplexingSampleProvider(new[] { s }, 2);
            if (s.WaveFormat.SampleRate != SourceFactory.SampleRate) s = new WdlResamplingSampleProvider(s, SourceFactory.SampleRate);
            cap.DataAvailable += (_, e) => buf.AddSamples(e.Buffer, 0, e.BytesRecorded);
            cap.RecordingStopped += (_, e) =>
            {
                if (e.Exception == null) return;
                bool mine; lock (_gate) mine = _capture == cap;
                if (!mine) return; // fermata voluta
                Status = "Microfono: " + e.Exception.Message;
                // il mic si è staccato (USB, driver): riproviamo una volta dopo mezzo secondo, sullo stesso ingresso
                _ = System.Threading.Tasks.Task.Run(() => { Thread.Sleep(500); try { Start(deviceId); } catch { } });
            };
            cap.StartRecording();
            lock (_gate) { _capture = cap; _buffer = buf; _src = s; }
            DeviceId = deviceId; _on = true;
            Status = "Microfono: " + dev.FriendlyName;
        }
        catch (Exception ex) { Status = "Microfono non disponibile: " + ex.Message; _on = false; }
    }

    public void Stop()
    {
        WasapiCapture? cap;
        lock (_gate) { cap = _capture; _capture = null; _src = null; _buffer = null; }
        _on = false;
        try { cap?.StopRecording(); cap?.Dispose(); } catch { }
        Fx.Reset();
        Peak = 0; _duckEnv = 1;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        ISampleProvider? src;
        lock (_gate) src = _src;
        if (!_on || src == null) { Array.Clear(buffer, offset, count); Peak = 0; Duck(false, count); return count; }
        int n;
        try { n = src.Read(buffer, offset, count); } catch { n = 0; }
        if (n < count) Array.Clear(buffer, offset + n, count - n);
        // gain con rampa (niente click girando la manopola)
        float g0 = _lastGain, g1 = _gain;
        for (int i = 0; i < count; i++) buffer[offset + i] *= g0 + (g1 - g0) * i / count;
        _lastGain = g1;
        float pk = 0;
        for (int i = 0; i < count; i++) { float a = Math.Abs(buffer[offset + i]); if (a > pk) pk = a; }
        Peak = pk;
        SlowPeak = pk > SlowPeak ? pk : SlowPeak * (float)Math.Exp(-count / 2.0 / SourceFactory.SampleRate / 1.0);
        Fx.Process(buffer, offset, count);
        Duck(TalkOver || (AutoDuck && pk > DuckThreshold), count);
        return count;
    }

    /// <summary>Inviluppo del talk-over: attacco ~15 ms, rilascio ~700 ms.</summary>
    private void Duck(bool active, int samples)
    {
        double target = active ? DuckGain : 1;
        double sec = samples / 2.0 / SourceFactory.SampleRate;
        double tau = target < _duckEnv ? 0.015 : 0.7;
        _duckEnv += (target - _duckEnv) * (1 - Math.Exp(-sec / tau));
    }

    public void Dispose() => Stop();
}
