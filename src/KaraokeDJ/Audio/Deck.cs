using KaraokeDJ.Models;
using NAudio.Wave;

namespace KaraokeDJ.Audio;

/// <summary>
/// Un deck di riproduzione: sorgente → pitch/tempo → gain. Produce sempre
/// <c>count</c> campioni (silenzio se fermo) così resta stabilmente nel mixer.
/// </summary>
public sealed class Deck : ISampleProvider
{
    private readonly object _gate = new();
    private WaveStream? _reader;
    private SoundTouchSampleProvider? _st;
    private double _positionSec;
    private double _durationSec;
    private volatile bool _playing;
    private float _volume = 1f;
    private float _crossGain = 1f;
    private float _lastGain = 1f;
    /// <summary>Picco dell'ultimo buffer (post volume/crossfader), per il VU meter.</summary>
    public volatile float PeakL, PeakR;
    private int _keyShift;
    private double _tempo = 1.0;
    private bool _keyLock = true;

    public Deck(string name) => Name = name;

    public string Name { get; }
    public FxChain Fx { get; } = new();
    public WaveFormat WaveFormat => SourceFactory.Format;
    public Track? Track { get; private set; }
    public string? LoadedAudioPath { get; private set; }

    /// <summary>Sollevato dal thread audio quando il brano finisce.</summary>
    public event Action? TrackEnded;
    public event Action? Loaded;
    /// <summary>Sollevato dopo un seek esplicito (per risincronizzare video/CDG).</summary>
    public event Action? Seeked;

    public bool IsPlaying => _playing;
    public bool HasTrack => Track != null;
    public double PositionSec => _spin == SpinMode.Jog ? Math.Max(0, _positionSec - JogBehind() / SourceFactory.SampleRate * _tempo) : _positionSec;
    public double DurationSec => _durationSec;
    public double RemainingSec => Math.Max(0, _durationSec - _positionSec);

    /// <summary>Gain lineare del deck (1 = unity). Fino a +12 dB.</summary>
    public float Volume { get => _volume; set => _volume = Math.Clamp(value, 0f, 4f); }
    public float CrossGain { get => _crossGain; set => _crossGain = Math.Clamp(value, 0f, 1f); }
    /// <summary>Pan -1 (sinistra) … +1 (destra), potenza costante.</summary>
    public float Pan { get => _pan; set => _pan = Math.Clamp(value, -1f, 1f); }
    private float _pan, _lastPanL = 1f, _lastPanR = 1f;
    public float EffectiveGain => _volume * _crossGain;

    public int KeyShift
    {
        get => _keyShift;
        set
        {
            value = Math.Clamp(value, -12, 12);
            if (value == _keyShift) return;
            lock (_gate)
            {
                bool wasBypass = _st?.Bypass ?? true;
                _keyShift = value;
                if (_st != null)
                {
                    _st.Semitones = value;
                    if (wasBypass != _st.Bypass) SeekInternal(_positionSec);
                }
            }
        }
    }

    public double Tempo
    {
        get => _tempo;
        set
        {
            value = Math.Clamp(value, 0.5, 1.5);
            if (Math.Abs(value - _tempo) < 1e-6) return;
            lock (_gate)
            {
                bool wasBypass = _st?.Bypass ?? true;
                _tempo = value;
                if (_st != null)
                {
                    _st.Tempo = value;
                    if (wasBypass != _st.Bypass) SeekInternal(_positionSec);
                }
            }
        }
    }

    /// <summary>Master tempo: con true la velocità non altera il tono.</summary>
    public bool KeyLock
    {
        get => _keyLock;
        set
        {
            if (value == _keyLock) return;
            lock (_gate)
            {
                _keyLock = value;
                if (_st != null) { _st.KeyLock = value; if (!_st.Bypass) SeekInternal(_positionSec); }
            }
        }
    }

    /// <summary>Carica un brano. <paramref name="audioPath"/> può differire da Track.FilePath (es. mp3 estratto da zip).</summary>
    public void Load(Track track, string audioPath)
    {
        var (reader, provider) = SourceFactory.Open(audioPath);
        var st = new SoundTouchSampleProvider(provider) { KeyLock = _keyLock, Semitones = _keyShift, Tempo = _tempo };
        WaveStream? old;
        lock (_gate)
        {
            old = _reader;
            _playing = false;
            _reader = reader;
            _st = st;
            _positionSec = 0;
            _durationSec = reader.TotalTime.TotalSeconds;
            Track = track;
            LoadedAudioPath = audioPath;
            Fx.Reset();
        }
        old?.Dispose();
        Loaded?.Invoke();
    }

    public void Eject()
    {
        WaveStream? old;
        lock (_gate)
        {
            old = _reader;
            _reader = null;
            _st = null;
            _playing = false;
            _positionSec = 0;
            _durationSec = 0;
            Track = null;
            LoadedAudioPath = null;
        }
        old?.Dispose();
        Loaded?.Invoke();
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_reader == null) return;
            if (_positionSec >= _durationSec - 0.05) SeekInternal(0);
            _playing = true;
        }
    }

    public void Pause() { lock (_gate) { if (_spin == SpinMode.Jog) { SeekInternal(PositionSec); } _spin = SpinMode.None; _spinRate = 1; _playing = false; } }

    public void TogglePlay() { if (_playing) Pause(); else Play(); }

    public void Stop()
    {
        lock (_gate)
        {
            _playing = false; _spin = SpinMode.None; _spinRate = 1;
            if (_reader != null) SeekInternal(0);
        }
        Seeked?.Invoke();
    }

    public void Seek(double seconds)
    {
        lock (_gate)
        {
            if (_reader == null) return;
            _spin = SpinMode.None; _spinRate = 1;
            SeekInternal(Math.Clamp(seconds, 0, Math.Max(0, _durationSec - 0.1)));
        }
        Seeked?.Invoke();
    }

    private void SeekInternal(double seconds)
    {
        if (_reader == null) return;
        try { _reader.CurrentTime = TimeSpan.FromSeconds(seconds); }
        catch { /* alcuni reader non supportano seek preciso */ }
        _st?.Reset();
        _positionSec = seconds;
    }


    // ------------------------------------------------------------------ loop

    private double _loopStart = -1, _loopEnd = -1;
    private volatile bool _loopOn;
    public bool LoopOn => _loopOn;
    public double LoopStart => _loopStart;
    public double LoopEnd => _loopEnd;

    public void SetLoop(double startSec, double endSec)
    {
        lock (_gate)
        {
            if (_reader == null || endSec <= startSec + 0.01) return;
            _loopStart = Math.Max(0, startSec);
            _loopEnd = Math.Min(_durationSec, endSec);
            _loopOn = true;
        }
    }

    public void ClearLoop() { _loopOn = false; }

    // ------------------------------------------------------------------ brake / backspin

    public enum SpinMode { None, Brake, Backspin, Jog }
    private SpinMode _spin;
    private double _spinRate = 1, _spinStep;
    private double _srcFrac;
    private readonly float[] _spinLast = new float[2];
    private readonly float[] _spinScratch = new float[2];
    private readonly float[] _hist = new float[2 * SourceFactory.SampleRate * HistSeconds]; // storia dell'uscita (post-FX, pre-gain): scratch/backspin
    private int _histPos;
    private double _histRead;
    private double _spunFrames;
    public bool SpinActive => _spin != SpinMode.None;

    /// <summary>Frenata "vinile": la velocità scende a zero in <paramref name="seconds"/>, poi pausa.</summary>
    public void Brake(double seconds = 1.5)
    {
        lock (_gate)
        {
            if (_reader == null || !_playing) return;
            _spin = SpinMode.Brake; _spinRate = 1; _spinStep = 1.0 / (Math.Max(0.2, seconds) * SourceFactory.SampleRate);
            _srcFrac = 0;
        }
    }

    /// <summary>Backspin: riavvolge riproducendo all'indietro sempre più veloce, poi pausa.</summary>
    public void Backspin(double seconds = 0.8)
    {
        lock (_gate)
        {
            if (_reader == null || !_playing) return;
            _spin = SpinMode.Backspin; _spinRate = 1; _spinStep = 3.0 / (Math.Max(0.2, seconds) * SourceFactory.SampleRate);
            _histRead = _histPos; _spunFrames = 0;
        }
    }

    public void CancelSpin() { lock (_gate) { _spin = SpinMode.None; _spinRate = 1; } }

    // ------------------------------------------------------------------ jog: scratch, reverse, nudge, spin, slow

    private double _jogRate, _jogTarget, _jogSmooth = 0.002; // rate attuale, obiettivo, coefficiente di inseguimento per frame
    private bool _jogReleasing, _jogResume;
    private readonly float[] _jogPull = new float[1024]; // 512 frame (~12 ms): è anche il salto massimo al rilascio
    private const int HistSeconds = 8;

    /// <summary>Frame di uscita "indietro" rispetto alla testa della storia (0 = live; negativo = oltre la testa).</summary>
    private double JogBehind()
    {
        int len = _hist.Length / 2;
        double b = _histPos - _histRead;
        if (b < 0) b += len;
        if (b > len / 2) b -= len; // oltre la testa (in avanti)
        return b;
    }

    /// <summary>Entra in modalità jog: da qui la velocità la decide <see cref="JogRate"/> (anche negativa = all'indietro).</summary>
    public void JogStart()
    {
        lock (_gate)
        {
            if (_reader == null || _st == null) return;
            if (_spin == SpinMode.Jog) { _jogReleasing = false; return; }
            _jogResume = _playing;
            if (_spin != SpinMode.None) { _spin = SpinMode.None; _spinRate = 1; }
            _spin = SpinMode.Jog;
            _playing = true;
            _jogRate = _jogResume ? 1 : 0;
            _jogTarget = _jogRate;
            _jogReleasing = false;
            _histRead = _histPos; // partiamo dal live
            _jogSmooth = SmoothCoef(0.03);
        }
    }

    private static double SmoothCoef(double seconds) => 1 - Math.Exp(-1.0 / (Math.Max(0.005, seconds) * SourceFactory.SampleRate));

    /// <summary>Velocità obiettivo (1 = normale, 0 = fermo, negativa = indietro) raggiunta in <paramref name="smoothSeconds"/>.</summary>
    public void JogRate(double rate, double smoothSeconds = 0.03)
    {
        lock (_gate)
        {
            if (_spin != SpinMode.Jog) return;
            _jogTarget = Math.Clamp(rate, -8, 8);
            _jogSmooth = SmoothCoef(smoothSeconds);
            _jogReleasing = false;
        }
    }

    /// <summary>Rilascio: torna alla velocità normale (o si ferma se il deck era in pausa) e poi esce dal jog.</summary>
    public void JogEnd(double smoothSeconds = 0.08)
    {
        lock (_gate)
        {
            if (_spin != SpinMode.Jog) return;
            _jogTarget = _jogResume ? 1 : 0;
            _jogSmooth = SmoothCoef(smoothSeconds);
            _jogReleasing = true;
        }
    }

    /// <summary>Girata secca del disco: la velocità salta a <paramref name="peak"/> (es. +3 avanti, −3 indietro) e torna normale in <paramref name="seconds"/>.</summary>
    public void Spin(double peak, double seconds = 0.7)
    {
        JogStart();
        lock (_gate)
        {
            if (_spin != SpinMode.Jog) return;
            _jogRate = peak;
            _jogTarget = _jogResume ? 1 : 0;
            _jogSmooth = SmoothCoef(seconds / 3);
            _jogReleasing = true;
        }
    }

    public bool JogActive => _spin == SpinMode.Jog;
    public string JogDebug => $"rate={_jogRate:0.00} target={_jogTarget:0.00} behind={JogBehind():0} head={_histPos} read={_histRead:0} pos={_positionSec:0.00}";

    /// <summary>Lettura in jog: legge la storia dell'uscita (post-FX) alla velocità corrente, tirando nuovi frame dalla sorgente quando serve.</summary>
    private int ReadJog(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int len = _hist.Length / 2;
        bool stop = false;
        for (int i = 0; i < frames; i++)
        {
            _jogRate += (_jogTarget - _jogRate) * _jogSmooth;
            if (_jogReleasing && Math.Abs(_jogRate - _jogTarget) < 0.003) _jogRate = _jogTarget;
            _histRead += _jogRate;
            if (_histRead >= len) _histRead -= len;
            if (_histRead < 0) _histRead += len;

            double behind = JogBehind();
            if (_jogRate > 0 && behind < 2)
            {
                // ho raggiunto (o superato) la testa: tiro un blocco nuovo dalla sorgente
                int n = ReadStretched(_jogPull, 0, _jogPull.Length);
                if (n < 2) { stop = true; _histRead = _histPos; break; }
                Fx.Process(_jogPull, 0, n);
                RecordHistory(_jogPull, 0, n);
                if (JogBehind() < 0) { _histRead = _histPos - 1; if (_histRead < 0) _histRead += len; }
            }
            else if (_jogRate < 0 && behind > len / 2 - 4)
            {
                // fine della storia: non posso andare più indietro
                _histRead = _histPos - (len / 2 - 4); if (_histRead < 0) _histRead += len;
                _jogRate = 0;
            }

            int p = (int)_histRead;
            double frac = _histRead - p;
            int p2 = p + 1; if (p2 >= len) p2 = 0;
            // a velocità quasi zero il vinile non suona: evita la componente continua
            float g = (float)Math.Min(1, Math.Abs(_jogRate) * 6);
            buffer[offset + 2 * i] = (float)(_hist[p * 2] * (1 - frac) + _hist[p2 * 2] * frac) * g;
            buffer[offset + 2 * i + 1] = (float)(_hist[p * 2 + 1] * (1 - frac) + _hist[p2 * 2 + 1] * frac) * g;

            if (_jogReleasing && _jogRate == _jogTarget)
            {
                if (_jogTarget == 0)
                {
                    // rilascio da fermo: torno in pausa esattamente dove sono
                    double pos = Math.Max(0, _positionSec - JogBehind() / SourceFactory.SampleRate * _tempo);
                    _spin = SpinMode.None; _playing = false;
                    SeekInternal(pos);
                    Array.Clear(buffer, offset + 2 * (i + 1), count - 2 * (i + 1));
                    return count;
                }
                if (JogBehind() <= _jogPull.Length / 2) { _spin = SpinMode.None; }  // riagganciato al live (entro un blocco): si continua normalmente
            }
        }
        if (stop)
        {
            _spin = SpinMode.None;
            return 0; // sorgente finita: Read() gestisce la fine brano
        }
        return count;
    }

    // ------------------------------------------------------------------ sorgente

    /// <summary>Sostituisce il file audio (es. versione senza voce) mantenendo posizione e stato.</summary>
    public void SwapSource(string audioPath)
    {
        var (reader, provider) = SourceFactory.Open(audioPath);
        SwapSource(reader, provider, audioPath);
    }

    /// <summary>Passa ai 4 stem separati (voce/batteria/basso/altro) con guadagni regolabili.</summary>
    public void SwapToStems(string stemsDir)
    {
        var mix = new StemMixReader(stemsDir);
        SwapSource(mix, mix, stemsDir);
    }

    /// <summary>Mixer degli stem se la sorgente attuale è quella, altrimenti null.</summary>
    public StemMixReader? Stems => _reader as StemMixReader;

    public void SwapSource(WaveStream reader, ISampleProvider provider, string audioPath)
    {
        var st = new SoundTouchSampleProvider(provider) { KeyLock = _keyLock, Semitones = _keyShift, Tempo = _tempo };
        WaveStream? old;
        lock (_gate)
        {
            old = _reader;
            _reader = reader;
            _st = st;
            LoadedAudioPath = audioPath;
            SeekInternal(Math.Min(_positionSec, Math.Max(0, reader.TotalTime.TotalSeconds - 0.1)));
        }
        old?.Dispose();
    }

    // ------------------------------------------------------------------ lettura

    /// <summary>Legge dal time-stretcher rispettando il loop (torna a LoopStart quando raggiunge LoopEnd).</summary>
    private int ReadStretched(float[] buffer, int offset, int count)
    {
        int got = 0;
        int guard = 0;
        while (got < count && guard++ < 8)
        {
            int want = count - got;
            if (_loopOn && _loopEnd > _loopStart)
            {
                double secsLeft = _loopEnd - _positionSec;
                if (secsLeft <= 0.002)
                {
                    SeekInternal(_loopStart);
                    secsLeft = _loopEnd - _positionSec;
                }
                int framesLeft = (int)(secsLeft / _tempo * SourceFactory.SampleRate);
                want = Math.Min(want, Math.Max(2, framesLeft * 2));
            }
            int n = _st!.Read(buffer, offset + got, want);
            if (n <= 0)
            {
                if (_loopOn) { SeekInternal(_loopStart); continue; }
                break;
            }
            _positionSec += (n / 2.0) * _tempo / SourceFactory.SampleRate;
            got += n;
        }
        return got;
    }

    /// <summary>Brake: ricampiona l'uscita del deck a velocità decrescente (il tono scende come un giradischi).</summary>
    private int ReadBrake(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        for (int i = 0; i < frames; i++)
        {
            _srcFrac += _spinRate;
            while (_srcFrac >= 1)
            {
                int n = ReadStretched(_spinScratch, 0, 2);
                if (n < 2) { _spinScratch[0] = _spinScratch[1] = 0; }
                _spinLast[0] = _spinScratch[0]; _spinLast[1] = _spinScratch[1];
                _srcFrac -= 1;
            }
            buffer[offset + 2 * i] = _spinLast[0];
            buffer[offset + 2 * i + 1] = _spinLast[1];
            _spinRate -= _spinStep;
            if (_spinRate <= 0)
            {
                _spin = SpinMode.None; _spinRate = 1;
                _playing = false;
                Array.Clear(buffer, offset + 2 * (i + 1), count - 2 * (i + 1));
                return count;
            }
        }
        return count;
    }

    /// <summary>Backspin: rilegge la storia dell'uscita all'indietro accelerando.</summary>
    private int ReadBackspin(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int len = _hist.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            _histRead -= _spinRate;
            _spunFrames += _spinRate;
            if (_histRead < 0) _histRead += len;
            int p = (int)_histRead;
            buffer[offset + 2 * i] = _hist[p * 2];
            buffer[offset + 2 * i + 1] = _hist[p * 2 + 1];
            _spinRate += _spinStep;
            if (_spinRate >= 4 || _spunFrames >= len - SourceFactory.SampleRate)
            {
                _spin = SpinMode.None; _spinRate = 1;
                _playing = false;
                SeekInternal(Math.Max(0, _positionSec - _spunFrames / SourceFactory.SampleRate * _tempo));
                Array.Clear(buffer, offset + 2 * (i + 1), count - 2 * (i + 1));
                return count;
            }
        }
        return count;
    }

    private void RecordHistory(float[] buffer, int offset, int n)
    {
        int len = _hist.Length / 2;
        for (int i = 0; i + 1 < n; i += 2)
        {
            _hist[_histPos * 2] = buffer[offset + i];
            _hist[_histPos * 2 + 1] = buffer[offset + i + 1];
            if (++_histPos >= len) _histPos = 0;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        bool ended = false;
        lock (_gate)
        {
            if (!_playing || _st == null)
            {
                Array.Clear(buffer, offset, count);
                _lastGain = EffectiveGain;
                PeakL = PeakR = 0;
                return count;
            }

            int n;
            switch (_spin)
            {
                case SpinMode.Brake:
                    n = ReadBrake(buffer, offset, count);
                    Fx.Process(buffer, offset, n);
                    break;
                case SpinMode.Backspin:
                    n = ReadBackspin(buffer, offset, count); // già post-FX (dalla storia)
                    break;
                case SpinMode.Jog:
                    n = ReadJog(buffer, offset, count);      // già post-FX (dalla storia)
                    break;
                default:
                    n = ReadStretched(buffer, offset, count);
                    Fx.Process(buffer, offset, n);
                    RecordHistory(buffer, offset, n);
                    break;
            }
            ApplyGain(buffer, offset, n);
            float pl = 0, pr = 0;
            for (int i = 0; i + 1 < n; i += 2) { float a = Math.Abs(buffer[offset + i]); if (a > pl) pl = a; float b = Math.Abs(buffer[offset + i + 1]); if (b > pr) pr = b; }
            PeakL = pl; PeakR = pr;

            if (n < count)
            {
                Array.Clear(buffer, offset + n, count - n);
                _playing = false;
                _positionSec = _durationSec;
                ended = true;
            }
        }
        if (ended) TrackEnded?.Invoke();
        return count;
    }

    // Rampa lineare tra il gain precedente e quello attuale: evita click sui fader.
    private void ApplyGain(float[] buffer, int offset, int n)
    {
        float target = EffectiveGain;
        float start = _lastGain;
        if (n == 0) return;
        if (Math.Abs(target - start) < 1e-5f)
        {
            if (Math.Abs(target - 1f) > 1e-6f)
                for (int i = 0; i < n; i++) buffer[offset + i] *= target;
        }
        else
        {
            int frames = n / SourceFactory.Channels;
            float step = (target - start) / frames;
            float g = start;
            for (int f = 0; f < frames; f++)
            {
                int idx = offset + f * SourceFactory.Channels;
                buffer[idx] *= g;
                buffer[idx + 1] *= g;
                g += step;
            }
        }
        _lastGain = target;
        ApplyPan(buffer, offset, n);
    }

    // Pan a potenza costante con rampa (niente click girando la manopola)
    private void ApplyPan(float[] buffer, int offset, int n)
    {
        double a = (_pan + 1) / 4.0 * Math.PI;                 // 0 … π/2
        float tl = (float)(Math.Cos(a) * 1.41421356), tr = (float)(Math.Sin(a) * 1.41421356); // centro = 1/1
        if (Math.Abs(tl - 1f) < 1e-4f && Math.Abs(tr - 1f) < 1e-4f && Math.Abs(_lastPanL - 1f) < 1e-4f && Math.Abs(_lastPanR - 1f) < 1e-4f) return;
        int frames = n / 2;
        if (frames == 0) return;
        float sl = (tl - _lastPanL) / frames, sr = (tr - _lastPanR) / frames;
        float gl = _lastPanL, gr = _lastPanR;
        for (int f = 0; f < frames; f++)
        {
            int idx = offset + f * 2;
            buffer[idx] *= gl; buffer[idx + 1] *= gr;
            gl += sl; gr += sr;
        }
        _lastPanL = tl; _lastPanR = tr;
    }
}
