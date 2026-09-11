using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeDJ.Audio;

public sealed record AudioDevice(string Id, string Name);

/// <summary>Mixer master: Deck A + Deck B + pad → uscita WASAPI.</summary>
public sealed class AudioEngine : IDisposable
{
    private readonly MixingSampleProvider _mixer;
    private readonly VolumeSampleProvider _master;
    private IWavePlayer? _output;
    private readonly PeakMeter _meter;
    public float MasterPeakL => _meter.PeakL;
    public float MasterPeakR => _meter.PeakR;
    private double _crossfader; // -1 = tutto A, +1 = tutto B

    public AudioEngine()
    {
        DeckA = new Deck("A");
        DeckB = new Deck("B");
        _mixer = new MixingSampleProvider(SourceFactory.Format) { ReadFully = true };
        _mixer.AddMixerInput(DeckA);
        _mixer.AddMixerInput(DeckB);
        Pads = new PadPlayer(_mixer);
        _master = new VolumeSampleProvider(_mixer);
        _meter = new PeakMeter(_master);
        Crossfader = 0;
    }

    public Deck DeckA { get; }
    public Deck DeckB { get; }
    public PadPlayer Pads { get; }

    public float MasterVolume { get => _master.Volume; set => _master.Volume = Math.Clamp(value, 0f, 1.5f); }

    /// <summary>-1 … +1. Curva a potenza costante.</summary>
    public double Crossfader
    {
        get => _crossfader;
        set
        {
            _crossfader = Math.Clamp(value, -1, 1);
            double t = (_crossfader + 1) / 2; // 0..1
            DeckA.CrossGain = (float)Math.Cos(t * Math.PI / 2);
            DeckB.CrossGain = (float)Math.Sin(t * Math.PI / 2);
        }
    }

    public string? CurrentDeviceId { get; private set; }
    public string OutputDescription { get; private set; } = "";

    public static List<AudioDevice> ListOutputDevices()
    {
        var list = new List<AudioDevice> { new("", "Dispositivo predefinito di Windows") };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    public void Start(string? deviceId)
    {
        StopOutput();
        IWavePlayer player;
        try
        {
            MMDevice? dev = null;
            if (!string.IsNullOrEmpty(deviceId))
            {
                using var en = new MMDeviceEnumerator();
                dev = en.GetDevice(deviceId);
            }
            player = dev != null
                ? new WasapiOut(dev, AudioClientShareMode.Shared, true, 80)
                : new WasapiOut(AudioClientShareMode.Shared, 80);
            OutputDescription = "WASAPI " + (dev?.FriendlyName ?? "predefinito");
        }
        catch
        {
            player = new WaveOutEvent { DesiredLatency = 150 };
            OutputDescription = "WaveOut (fallback)";
        }
        player.Init(_meter);
        player.Play();
        _output = player;
        CurrentDeviceId = deviceId;
    }

    private void StopOutput()
    {
        if (_output == null) return;
        try { _output.Stop(); _output.Dispose(); } catch { }
        _output = null;
    }

    /// <summary>Passa il segnale inalterato misurando il picco per canale.</summary>
    private sealed class PeakMeter : ISampleProvider
    {
        private readonly ISampleProvider _src;
        public volatile float PeakL, PeakR;
        public PeakMeter(ISampleProvider src) => _src = src;
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            float pl = 0, pr = 0;
            for (int i = 0; i + 1 < n; i += 2) { float a = Math.Abs(buffer[offset + i]); if (a > pl) pl = a; float b = Math.Abs(buffer[offset + i + 1]); if (b > pr) pr = b; }
            PeakL = pl; PeakR = pr;
            return n;
        }
    }

    public void Dispose()
    {
        StopOutput();
        DeckA.Eject();
        DeckB.Eject();
    }
}
