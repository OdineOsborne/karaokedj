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
        _mixer.AddMixerInput(new StudioTap(this));
        // la registrazione prende il mix prima del volume master: quello che e stato suonato,
        // indipendentemente da quanto era alta la sala
        Recorder = new NightRecorder(_mixer);
        _master = new VolumeSampleProvider(Recorder);
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

    /// <summary>Registrazione della serata su file (vedi <see cref="NightRecorder"/>).</summary>
    public NightRecorder Recorder { get; }

    public float MasterVolume { get => _master.Volume; set => _master.Volume = Math.Clamp(value, 0f, 1.5f); }

    /// <summary>
    /// Curva del crossfader, come il selettore della console: "mix", "scratch" (taglio) o "off" (escluso, si mixa coi
    /// fader di canale). Per mix e scratch conta la larghezza della sfumata: la parte di corsa in cui un deck si
    /// chiude. 1 = tutta la corsa (potenza costante); 0,5 = al centro tutti e due pieni; pochi % = taglio agli estremi.
    /// </summary>
    public string CrossfaderCurve { get => _curve; set { _curve = value; Crossfader = _crossfader; } }
    private string _curve = "mix";
    public double CrossfaderMixWidth { get => _mixW; set { _mixW = Math.Clamp(value, 0.02, 1); Crossfader = _crossfader; } }
    private double _mixW = 0.5;
    public double CrossfaderScratchWidth { get => _scrW; set { _scrW = Math.Clamp(value, 0.01, 1); Crossfader = _crossfader; } }
    private double _scrW = 0.06;
    /// <summary>Durante un passaggio automatico la curva è a potenza costante su tutta la corsa: l'automix la sa usare.</summary>
    public bool ForceMixCurve { get => _forceMix; set { if (_forceMix == value) return; _forceMix = value; Crossfader = _crossfader; } }
    private bool _forceMix;

    /// <summary>-1 … +1.</summary>
    public double Crossfader
    {
        get => _crossfader;
        set
        {
            _crossfader = Math.Clamp(value, -1, 1);
            double t = (_crossfader + 1) / 2; // 0..1
            if (!_forceMix && _curve == "off") { DeckA.CrossGain = DeckB.CrossGain = 1f; return; }
            double w = _forceMix ? 1 : _curve == "scratch" ? _scrW : _mixW;
            // ogni deck resta pieno finché il crossfader non entra nella sua zona di chiusura, poi scende a potenza
            // costante (seno): con w = 1 è la curva classica cos/sin
            DeckA.CrossGain = (float)Math.Sin(Math.PI / 2 * Math.Clamp((1 - t) / w, 0, 1));
            DeckB.CrossGain = (float)Math.Sin(Math.PI / 2 * Math.Clamp(t / w, 0, 1));
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
            // NAudio ci passa un byte[] travestito da float[] (WaveBuffer): Array.Clear guarderebbe il tipo vero e
            // azzererebbe count byte, cioè un quarto del blocco. Il resto restava sporco e sommato dava la "tromba"
            // dello Studio e del pre-ascolto in cuffia su una scheda separata (5/10/2026). Lo Span azzera count float.
            buffer.AsSpan(offset, count).Clear();
            float gc =(float)Math.Cos(Mix * Math.PI / 2) * Volume, gm = (float)Math.Sin(Mix * Math.PI / 2) * Volume;
            if (_e.DeckA.CueOn) _e.DeckA.CueRing.ReadAdd(buffer, offset, count, gc); else _e.DeckA.CueRing.Clear();
            if (_e.DeckB.CueOn) _e.DeckB.CueRing.ReadAdd(buffer, offset, count, gc); else _e.DeckB.CueRing.Clear();
            _e.MasterRing.ReadAdd(buffer, offset, count, gm);
            // pre-ascolto dalla libreria: sempre udibile in cuffia (non segue il mix cue/master), mai nel master
            if (_e.PreviewOn) _e.PreviewRing.ReadAdd(buffer, offset, count, Volume * 0.9f);
            if (_e.StudioPlaying && !_e.StudioOnMaster) _e.StudioRing.ReadAdd(buffer, offset, count, Volume);
            return count;
        }
    }

    // ---------------------------------------------------------------- pre-ascolto dalla libreria (solo cuffia)

    /// <summary>
    /// Il file del pre-ascolto si legge in un thread suo, a bassa priorità, dentro questo anello (mezzo secondo di
    /// scorta): con la cuffia sui canali 3-4 cuffia e master sono lo stesso flusso, e leggere dal disco dentro il
    /// thread audio potrebbe far saltare la musica in sala se il disco rallenta.
    /// </summary>
    public SampleRing PreviewRing { get; } = new(SourceFactory.SampleRate * 2 * 2, SourceFactory.SampleRate * 2);
    private readonly object _pvGate = new();
    private WaveStream? _pvReader;
    private CancellationTokenSource? _pvCts;
    public bool PreviewOn => _pvCts != null;
    public double PreviewPositionSec { get { lock (_pvGate) return _pvReader?.CurrentTime.TotalSeconds ?? 0; } }
    public double PreviewDurationSec { get { lock (_pvGate) return _pvReader?.TotalTime.TotalSeconds ?? 0; } }

    public void StartPreview(string path, double startSec)
    {
        StopPreview();
        var (reader, provider) = SourceFactory.Open(path);
        if (startSec > 0 && startSec < reader.TotalTime.TotalSeconds - 5) reader.CurrentTime = TimeSpan.FromSeconds(startSec);
        var cts = new CancellationTokenSource();
        lock (_pvGate) _pvReader = reader;
        PreviewRing.Clear();
        _pvCts = cts;
        new Thread(() => PreviewLoop(reader, provider, cts)) { IsBackground = true, Name = "Pre-ascolto", Priority = ThreadPriority.BelowNormal }.Start();
    }

    private void PreviewLoop(WaveStream reader, ISampleProvider provider, CancellationTokenSource cts)
    {
        var buf = new float[4096];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (PreviewRing.Count > SourceFactory.SampleRate) { Thread.Sleep(15); continue; }   // mezzo secondo di scorta basta
                int n;
                lock (_pvGate) n = provider.Read(buf, 0, buf.Length);
                if (n == 0) break;
                PreviewRing.Write(buf, 0, n);
            }
        }
        catch { /* file illeggibile: il pre-ascolto finisce, la serata no */ }
        finally
        {
            lock (_pvGate) { if (_pvReader == reader) _pvReader = null; }
            if (_pvCts == cts) _pvCts = null;
            try { reader.Dispose(); } catch { }
        }
    }

    public void SeekPreview(double sec)
    {
        lock (_pvGate)
        {
            if (_pvReader == null) return;
            _pvReader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(sec, 0, Math.Max(0, _pvReader.TotalTime.TotalSeconds - 1)));
        }
        PreviewRing.Clear();
    }

    public void StopPreview()
    {
        var c = _pvCts; _pvCts = null;
        c?.Cancel();
        PreviewRing.Clear();
    }

    // ---------------------------------------------------------------- Studio (anteprima del mix in preparazione)

    /// <summary>
    /// Lo Studio suona in cuffia (in serata, senza disturbare la sala) o sul master (a casa, senza cuffia
    /// configurata). Come il pre-ascolto, si calcola in un thread suo dentro un anello di mezzo secondo.
    /// </summary>
    public SampleRing StudioRing { get; } = new(SourceFactory.SampleRate * 2 * 2, SourceFactory.SampleRate * 2);
    private CancellationTokenSource? _stCts;
    public volatile bool StudioOnMaster;
    public bool StudioPlaying => _stCts != null;
    /// <summary>Secondi già calcolati e non ancora ascoltati (per mostrare la posizione giusta).</summary>
    public double StudioBufferedSec => StudioRing.Count / (2.0 * SourceFactory.SampleRate);

    public void StartStudio(ISampleProvider source, bool onMaster)
    {
        StopStudio();
        StudioOnMaster = onMaster;
        StudioRing.Clear();
        var cts = new CancellationTokenSource();
        _stCts = cts;
        new Thread(() =>
        {
            var buf = new float[2048];
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (StudioRing.Count > SourceFactory.SampleRate / 2) { Thread.Sleep(8); continue; }   // ~250 ms di scorta
                    int n = source.Read(buf, 0, buf.Length);
                    if (n == 0) break;
                    StudioRing.Write(buf, 0, n);
                }
            }
            catch { /* un errore nello Studio non deve mai toccare la musica in sala */ }
            finally { if (_stCts == cts) _stCts = null; }
        }) { IsBackground = true, Name = "Studio", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void StopStudio()
    {
        var c = _stCts; _stCts = null;
        c?.Cancel();
        StudioRing.Clear();
    }

    private sealed class StudioTap : ISampleProvider
    {
        private readonly AudioEngine _e;
        public StudioTap(AudioEngine e) => _e = e;
        public WaveFormat WaveFormat => SourceFactory.Format;
        public int Read(float[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Clear();   // vedi CueProvider: mai Array.Clear su un buffer di NAudio
            if (_e.StudioPlaying && _e.StudioOnMaster) _e.StudioRing.ReadAdd(buffer, offset, count, 1f);
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
        MMDevice? dev = null;
        WaveFormat? quadFormat = null;
        try
        {
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
            _quad = null;
            if (_cueOnMain)
            {
                WaveFormat? mix = null;
                try { mix = (dev ?? new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)).AudioClient.MixFormat; } catch { }
                int ch = mix?.Channels ?? 2;
                bool floatMix = mix != null && mix.BitsPerSample == 32 && (mix.Encoding == WaveFormatEncoding.IeeeFloat || mix.Encoding == WaveFormatEncoding.Extensible);
                if (ch == 4 && floatMix && mix!.SampleRate == SourceFactory.SampleRate)
                {
                    _quad = new QuadProvider(_tap, _cue); quadFormat = mix;
                    CueDescription = "Cuffia: canali 3-4 di " + (dev?.FriendlyName ?? "scheda predefinita");
                }
                else CueDescription = ch < 4
                    ? "Cuffia: la scheda ha solo " + ch + " canali, scegli un'altra uscita"
                    : $"Cuffia: formato della scheda non gestito ({mix?.SampleRate} Hz, {ch} canali): scegli un'altra uscita per la cuffia";
            }
        }
        catch
        {
            player = new WaveOutEvent { DesiredLatency = 150 };
            OutputDescription = "WaveOut (fallback)";
            _quad = null;
        }
        if (_quad != null) { MasterRing.Clear(); DeckA.CueRing.Clear(); DeckB.CueRing.Clear(); }
        try
        {
            // A 4 canali Windows accetta solo il formato esatto della scheda (Extensible con la mappa dei canali,
            // sulla Inpulse 500: 44,1 kHz, 32 bit float, 0x33): un 4 canali "semplice" dava "Value does not fall
            // within the expected range" e l'uscita restava chiusa, cioè niente musica (27/9/2026).
            if (_quad != null) player.Init(new AsDeviceFormat(_quad, quadFormat!));
            else player.Init(_tap);
        }
        catch (Exception ex) when (_quad != null)
        {
            // rete di sicurezza: se i 4 canali non passano, la musica esce comunque sul master a 2 canali
            try { player.Dispose(); } catch { }
            _quad = null;
            CueDescription = "Cuffia sui canali 3-4 non disponibile (" + ex.Message + "): il master suona, scegli un'altra uscita per la cuffia";
            player = dev != null ? new WasapiOut(dev, AudioClientShareMode.Shared, true, 80) : new WasapiOut(AudioClientShareMode.Shared, 80);
            player.Init(_tap);
        }
        player.PlaybackStopped += (_, a) => OnOutputStopped(player, a.Exception);
        _output = player;
        player.Play();
        CurrentDeviceId = deviceId;
    }

    /// <summary>
    /// Passa i campioni float alla scheda dichiarando il suo formato esatto (WaveFormatExtensible): la conversione
    /// standard di NAudio accetta solo il float "semplice", che Windows rifiuta sopra i 2 canali.
    /// </summary>
    private sealed class AsDeviceFormat : IWaveProvider
    {
        private readonly ISampleProvider _src;
        private float[] _buf = Array.Empty<float>();
        public AsDeviceFormat(ISampleProvider src, WaveFormat deviceFormat) { _src = src; WaveFormat = deviceFormat; }
        public WaveFormat WaveFormat { get; }
        public int Read(byte[] buffer, int offset, int count)
        {
            int n = count / 4;
            if (_buf.Length < n) _buf = new float[n];
            int got = _src.Read(_buf, 0, n);
            Buffer.BlockCopy(_buf, 0, buffer, offset, got * 4);
            return got * 4;
        }
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
        Recorder.Dispose();
        StopCue();
        StopOutput();
        Mic.Dispose();
        DeckA.Eject();
        DeckB.Eject();
    }
}
