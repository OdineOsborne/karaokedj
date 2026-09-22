using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeDJ.Audio;

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// Mixer master: Deck A + Deck B + pad + ritmi + microfono → uscita WASAPI.
/// Seconda uscita opzionale "cuffia" (PFL): pre-ascolto dei deck con il tasto 🎧, miscelato col master (manopola cue/master),
/// su un'altra scheda audio. I deck scrivono il loro segnale pre-fader in un anello, la cuffia lo legge sul suo thread.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly MixingSampleProvider _mixer;
    private readonly VolumeSampleProvider _master;
    private IWavePlayer? _output;
    private IWavePlayer? _cueOutput;
    private readonly MasterTap _tap;
    private readonly CueProvider _cue;
    public float MasterPeakL => _tap.PeakL;
    public float MasterPeakR => _tap.PeakR;
    private double _crossfader; // -1 = tutto A, +1 = tutto B

    public AudioEngine()
    {
        DeckA = new Deck("A");
        DeckB = new Deck("B");
        _mixer = new MixingSampleProvider(SourceFactory.Format) { ReadFully = true };
        _mixer.AddMixerInput(DeckA);
        _mixer.AddMixerInput(DeckB);
        Pads = new PadPlayer(_mixer);
        Rhythm = new RhythmEngine();
        _mixer.AddMixerInput(Rhythm);
        Mic = new MicInput();
        _mixer.AddMixerInput(Mic);
        _master = new VolumeSampleProvider(_mixer);
        _tap = new MasterTap(_master, this);
        _cue = new CueProvider(this);
        Crossfader = 0;
    }

    public Deck DeckA { get; }
    public Deck DeckB { get; }
    public PadPlayer Pads { get; }
    /// <summary>Sequencer ritmico (traccia di supporto).</summary>
    public RhythmEngine Rhythm { get; }
    /// <summary>Canale microfono (talk-over, effetti voce).</summary>
    public MicInput Mic { get; }

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

    // ---------------------------------------------------------------- resilienza in serata
    /// <summary>Uscita caduta (scheda staccata, driver, decoder) e riavviata da sola: messaggio per la barra di stato. Arriva da un thread audio.</summary>
    public event Action<string>? OutputRestarted;
    public int OutputRestarts { get; private set; }
    public string? LastOutputError { get; private set; }
    private readonly List<DateTime> _restartTimes = new();

    private void OnOutputStopped(IWavePlayer player, Exception? ex)
    {
        if (player != _output || ex == null) return; // fermata voluta (Stop/Dispose) o fine normale
        LastOutputError = ex.Message;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            lock (_restartTimes)
            {
                _restartTimes.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 60);
                if (_restartTimes.Count >= 5) { OutputRestarted?.Invoke("USCITA AUDIO CADUTA: " + ex.Message + " · scegli un'altra scheda in Impostazioni"); return; }
                _restartTimes.Add(DateTime.UtcNow);
            }
            Thread.Sleep(400);
            string how;
            try { Start(CurrentDeviceId); how = "riavviata"; }
            catch
            {
                try { Start(null); how = "passata alla scheda predefinita"; }
                catch (Exception e2) { OutputRestarted?.Invoke("USCITA AUDIO CADUTA: " + e2.Message); return; }
            }
            OutputRestarts++;
            OutputRestarted?.Invoke($"Uscita audio {how} dopo un errore ({ex.Message})");
        });
    }

    private void OnCueStopped(IWavePlayer player, Exception? ex)
    {
        if (player != _cueOutput || ex == null) return;
        var dev = CueDeviceId;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            Thread.Sleep(400);
            StartCue(dev);
            OutputRestarted?.Invoke(CueRunning ? "Cuffia riavviata dopo un errore (" + ex.Message + ")" : "Cuffia non disponibile: " + ex.Message);
        });
    }

    // ---------------------------------------------------------------- cuffia (PFL)

    /// <summary>Anello del master per la cuffia (mix cue/master).</summary>
    internal SampleRing MasterRing { get; } = new(SourceFactory.SampleRate * 2, SourceFactory.SampleRate / 5 * 2);
    /// <summary>Id speciale per "cuffia sui canali 3-4 della scheda principale".</summary>
    public const string CueOnMainId = "ch34";
    private bool _cueOnMain;
    private QuadProvider? _quad;
    public bool CueRunning => _cueOutput != null || _quad != null;
    public string? CueDeviceId { get; private set; }
    public string CueDescription { get; private set; } = "";
    /// <summary>0 = solo pre-ascolto, 1 = solo master.</summary>
    public float CueMix { get => _cue.Mix; set => _cue.Mix = Math.Clamp(value, 0f, 1f); }
    public float CueVolume { get => _cue.Volume; set => _cue.Volume = Math.Clamp(value, 0f, 1.5f); }

    public void StartCue(string? deviceId)
    {
        StopCue();
        if (string.IsNullOrEmpty(deviceId)) { CueDescription = "Cuffia: nessuna scheda scelta"; return; }
        if (deviceId == CueOnMainId)
        {
            _cueOnMain = true;
            Start(CurrentDeviceId); // riparte a 4 canali
            CueDeviceId = _quad != null ? CueOnMainId : null;
            return;
        }
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDevice(deviceId);
            var player = new WasapiOut(dev, AudioClientShareMode.Shared, true, 60);
            MasterRing.Clear(); DeckA.CueRing.Clear(); DeckB.CueRing.Clear();
            player.Init(_cue);
            player.PlaybackStopped += (_, a) => OnCueStopped(player, a.Exception);
            _cueOutput = player;
            player.Play();
            CueDeviceId = deviceId;
            CueDescription = "Cuffia: " + dev.FriendlyName;
        }
        catch (Exception ex) { CueDescription = "Cuffia non disponibile: " + ex.Message; }
    }

    public void StopCue()
    {
        if (_cueOnMain)
        {
            _cueOnMain = false; CueDeviceId = null; CueDescription = "";
            if (_quad != null) Start(CurrentDeviceId); // torna stereo
            return;
        }
        var c = _cueOutput; if (c == null) return;
        _cueOutput = null; CueDeviceId = null; CueDescription = "";
        try { c.Stop(); c.Dispose(); } catch { }
    }

    /// <summary>Master (canali 1-2) + cuffia (canali 3-4) in un solo flusso per le schede integrate nelle console.</summary>
    private sealed class QuadProvider : ISampleProvider
    {
        private readonly ISampleProvider _master, _cue;
        private float[] _a = Array.Empty<float>(), _b = Array.Empty<float>();
        public QuadProvider(ISampleProvider master, ISampleProvider cue) { _master = master; _cue = cue; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SourceFactory.SampleRate, 4);
        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / 4, st = frames * 2;
            if (_a.Length < st) { _a = new float[st]; _b = new float[st]; }
            int n = _master.Read(_a, 0, st);
            if (n < st) Array.Clear(_a, n, st - n);
            int m = _cue.Read(_b, 0, st);
            if (m < st) Array.Clear(_b, m, st - m);
            for (int i = 0, o = offset; i < frames; i++, o += 4)
            {
                buffer[o] = _a[i * 2]; buffer[o + 1] = _a[i * 2 + 1];
                buffer[o + 2] = _b[i * 2]; buffer[o + 3] = _b[i * 2 + 1];
            }
            return frames * 4;
        }
    }

    /// <summary>Uscita cuffia: somma dei deck con 🎧 acceso (pre-fader) e master, a potenza costante.</summary>
    private sealed class CueProvider : ISampleProvider
    {
        private readonly AudioEngine _e;
        public float Mix = 0.0f, Volume = 1f;
        public CueProvider(AudioEngine e) => _e = e;
        public WaveFormat WaveFormat => SourceFactory.Format;
        public int Read(float[] buffer, int offset, int count)
        {
            Array.Clear(buffer, offset, count);
            float gc = (float)Math.Cos(Mix * Math.PI / 2) * Volume, gm = (float)Math.Sin(Mix * Math.PI / 2) * Volume;
            if (_e.DeckA.CueOn) _e.DeckA.CueRing.ReadAdd(buffer, offset, count, gc); else _e.DeckA.CueRing.Clear();
            if (_e.DeckB.CueOn) _e.DeckB.CueRing.ReadAdd(buffer, offset, count, gc); else _e.DeckB.CueRing.Clear();
            _e.MasterRing.ReadAdd(buffer, offset, count, gm);
            return count;
        }
    }

    // ---------------------------------------------------------------- uscita principale

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
            // console con scheda audio integrata (Instinct, Inpulse, DDJ-400…): un solo dispositivo a 4 canali,
            // master su 1-2 e cuffia su 3-4
            if (_cueOnMain)
            {
                int ch = 2;
                try { ch = (dev ?? new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)).AudioClient.MixFormat.Channels; } catch { }
                if (ch >= 4) { _quad = new QuadProvider(_tap, _cue); CueDescription = "Cuffia: canali 3-4 di " + (dev?.FriendlyName ?? "scheda predefinita"); }
                else { _quad = null; CueDescription = "Cuffia: la scheda ha solo " + ch + " canali, scegli un'altra uscita"; }
            }
            else _quad = null;
        }
        catch
        {
            player = new WaveOutEvent { DesiredLatency = 150 };
            OutputDescription = "WaveOut (fallback)";
            _quad = null;
        }
        if (_quad != null) { MasterRing.Clear(); DeckA.CueRing.Clear(); DeckB.CueRing.Clear(); }
        player.Init((ISampleProvider?)_quad ?? _tap);
        player.PlaybackStopped += (_, a) => OnOutputStopped(player, a.Exception);
        _output = player;
        player.Play();
        CurrentDeviceId = deviceId;
    }

    private void StopOutput()
    {
        var o = _output; if (o == null) return;
        _output = null; // prima di Stop: così PlaybackStopped capisce che è una fermata voluta
        try { o.Stop(); o.Dispose(); } catch { }
    }

    /// <summary>Passa il master inalterato: misura i picchi, alimenta la cuffia, applica il talk-over del microfono ai deck.</summary>
    private sealed class MasterTap : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private readonly AudioEngine _e;
        public volatile float PeakL, PeakR;
        public MasterTap(ISampleProvider src, AudioEngine e) { _src = src; _e = e; }
        public WaveFormat WaveFormat => _src.WaveFormat;
        public int Read(float[] buffer, int offset, int count)
        {
            // il fattore di talk-over calcolato nel giro precedente vale per questo giro (ritardo di un buffer, ~80 ms)
            float duck = _e.Mic.IsOn ? _e.Mic.DuckFactor : 1f;
            _e.DeckA.Duck = duck; _e.DeckB.Duck = duck;
            int n = _src.Read(buffer, offset, count);
            float pl = 0, pr = 0;
            for (int i = 0; i + 1 < n; i += 2) { float a = Math.Abs(buffer[offset + i]); if (a > pl) pl = a; float b = Math.Abs(buffer[offset + i + 1]); if (b > pr) pr = b; }
            PeakL = pl; PeakR = pr;
            if (_e.CueRunning) _e.MasterRing.Write(buffer, offset, n);
            return n;
        }
    }

    public void Dispose()
    {
        StopCue();
        StopOutput();
        Mic.Dispose();
        DeckA.Eject();
        DeckB.Eject();
    }
}
