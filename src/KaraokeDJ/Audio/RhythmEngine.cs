using NAudio.Wave;

namespace KaraokeDJ.Audio;

/// <summary>Suoni sintetizzati del kit (nessun file esterno).</summary>
public enum DrumSound { Kick, Snare, HatClosed, HatOpen, Clap, Rim, TomLow, TomHigh, Cowbell, Shaker, Sample }

/// <summary>Una traccia del sequencer: 16 step (semicrome di una battuta 4/4), un suono, volume e mute.</summary>
public sealed class RhythmTrack
{
    public const int Steps = 16;
    public string Name { get; set; } = "";
    public DrumSound Sound { get; set; }
    /// <summary>Percorso del campione utente (solo se Sound == Sample).</summary>
    public string? SamplePath { get; set; }
    public bool[] Pattern { get; set; } = new bool[Steps];
    public float Volume { get; set; } = 0.8f;
    public bool Muted { get; set; }
    /// <summary>Campione renderizzato (stereo interleaved, 44.1 kHz).</summary>
    internal float[] Buffer = Array.Empty<float>();
}

/// <summary>
/// Step sequencer ritmico agganciato ai BPM della serata: kick/snare/hat/… sintetizzati oppure campioni
/// dell'utente, mixati nel master come traccia di supporto.
/// </summary>
public sealed class RhythmEngine : ISampleProvider
{
    private const int Sr = SourceFactory.SampleRate;
    private readonly object _gate = new();
    private readonly List<Voice> _voices = new();
    private double _bpm = 120;
    private long _stepPos;          // campioni (per canale) trascorsi nello step corrente
    private int _step;              // 0..15
    private volatile bool _running;
    private float _lastGain = 1f;

    public WaveFormat WaveFormat => SourceFactory.Format;
    public List<RhythmTrack> Tracks { get; } = new();
    public float Volume { get; set; } = 0.8f;
    public bool IsRunning => _running;
    /// <summary>Step in esecuzione (per l'evidenziazione in UI).</summary>
    public int CurrentStep => _step;
    /// <summary>Swing 0..0.5: ritarda gli step dispari (0 = dritto).</summary>
    public float Swing { get; set; }

    public double Bpm { get => _bpm; set => _bpm = Math.Clamp(value, 40, 240); }

    public void Start() { lock (_gate) { _step = 0; _stepPos = 0; _running = true; } }
    public void Stop() { lock (_gate) { _running = false; _voices.Clear(); } }
    /// <summary>Riparte dall'1 senza fermarsi: per riallineare a orecchio col brano.</summary>
    public void Resync() { lock (_gate) { _step = 0; _stepPos = 0; } }

    public RhythmTrack AddTrack(string name, DrumSound sound, string? samplePath = null)
    {
        var t = new RhythmTrack { Name = name, Sound = sound, SamplePath = samplePath };
        Render(t);
        lock (_gate) Tracks.Add(t);
        return t;
    }

    public void RemoveTrack(RhythmTrack t) { lock (_gate) Tracks.Remove(t); }

    /// <summary>Rigenera il buffer del suono (dopo cambio suono o campione).</summary>
    public void Render(RhythmTrack t)
    {
        t.Buffer = t.Sound == DrumSound.Sample ? LoadSample(t.SamplePath) : DrumSynth.Render(t.Sound);
    }

    /// <summary>Suona subito il suono della traccia (anteprima / pad).</summary>
    public void Trigger(RhythmTrack t)
    {
        if (t.Buffer.Length == 0) return;
        lock (_gate) _voices.Add(new Voice(t.Buffer, t.Volume));
    }

    private double SamplesPerStep => Sr * 60.0 / _bpm / 4.0;

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        if (!_running && _voices.Count == 0) { _lastGain = Volume; return count; }
        lock (_gate)
        {
            int frames = count / 2;
            int done = 0;
            while (done < frames)
            {
                int chunk = frames - done;
                if (_running)
                {
                    // durata dello step corrente (swing: gli step dispari partono più tardi, i pari prima)
                    double sps = SamplesPerStep;
                    double len = (_step % 2 == 0) ? sps * (1 + Swing) : sps * (1 - Swing);
                    if (_stepPos == 0) TriggerStep(_step);
                    long left = (long)len - _stepPos;
                    if (left <= 0) { _step = (_step + 1) % RhythmTrack.Steps; _stepPos = 0; continue; }
                    chunk = (int)Math.Min(chunk, left);
                }
                MixVoices(buffer, offset + done * 2, chunk);
                done += chunk;
                if (_running)
                {
                    _stepPos += chunk;
                }
            }
            _voices.RemoveAll(v => v.Done);
        }
        // rampa del volume master del ritmo (evita click)
        float target = Volume, start = _lastGain;
        int n = count;
        for (int i = 0; i < n; i += 2)
        {
            float g = start + (target - start) * i / n;
            buffer[offset + i] = SoftClip(buffer[offset + i] * g);
            buffer[offset + i + 1] = SoftClip(buffer[offset + i + 1] * g);
        }
        _lastGain = target;
        return count;
    }

    /// <summary>Limiter morbido: lineare fino a 0.7, poi compressione dolce; i colpi sovrapposti non spaccano.</summary>
    private static float SoftClip(float x)
    {
        const float knee = 0.7f;
        float a = Math.Abs(x);
        if (a <= knee) return x;
        float y = knee + (1 - knee) * MathF.Tanh((a - knee) / (1 - knee));
        return x < 0 ? -y : y;
    }

    private void TriggerStep(int step)
    {
        foreach (var t in Tracks)
        {
            if (t.Muted || !t.Pattern[step] || t.Buffer.Length == 0) continue;
            // hat chiuso "tronca" quello aperto (choke), come su una batteria vera
            if (t.Sound == DrumSound.HatClosed)
                foreach (var v in _voices) if (v.Sound == DrumSound.HatOpen) v.Kill();
            _voices.Add(new Voice(t.Buffer, t.Volume) { Sound = t.Sound });
        }
    }

    private void MixVoices(float[] buffer, int offset, int frames)
    {
        foreach (var v in _voices) v.Mix(buffer, offset, frames);
    }

    private static float[] LoadSample(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<float>();
        try
        {
            var (reader, provider) = SourceFactory.Open(path);
            using (reader)
            {
                var list = new List<float>();
                var buf = new float[Sr * 2];
                int max = Sr * 2 * 8; // max 8 s
                int n;
                while ((n = provider.Read(buf, 0, buf.Length)) > 0 && list.Count < max)
                    for (int i = 0; i < n; i++) list.Add(buf[i]);
                var arr = list.ToArray();
                // dissolvenza finale per evitare click se il campione è stato troncato
                int fade = Math.Min(arr.Length / 2, 2000);
                for (int i = 0; i < fade; i++)
                {
                    float g = (float)i / fade;
                    int idx = arr.Length - 2 - i * 2;
                    if (idx < 0) break;
                    arr[idx] *= g; arr[idx + 1] *= g;
                }
                return arr;
            }
        }
        catch { return Array.Empty<float>(); }
    }

    private sealed class Voice
    {
        private readonly float[] _buf;
        private readonly float _gain;
        private int _pos;
        private float _fade = 1f;
        private bool _killing;
        public DrumSound Sound;
        public Voice(float[] buf, float gain) { _buf = buf; _gain = gain; }
        public bool Done => _pos >= _buf.Length || (_killing && _fade <= 0f);
        public void Kill() => _killing = true;
        public void Mix(float[] dst, int offset, int frames)
        {
            for (int f = 0; f < frames && _pos + 1 < _buf.Length; f++)
            {
                float g = _gain * _fade;
                dst[offset + f * 2] += _buf[_pos] * g;
                dst[offset + f * 2 + 1] += _buf[_pos + 1] * g;
                _pos += 2;
                if (_killing) { _fade -= 1f / 400f; if (_fade <= 0f) { _fade = 0f; return; } }
            }
        }
    }
}

/// <summary>Sintesi dei suoni del kit: rendering una tantum in buffer stereo.</summary>
public static class DrumSynth
{
    private const int Sr = SourceFactory.SampleRate;

    public static float[] Render(DrumSound s) => s switch
    {
        DrumSound.Kick => Kick(),
        DrumSound.Snare => Snare(),
        DrumSound.HatClosed => Hat(0.06, 8000),
        DrumSound.HatOpen => Hat(0.35, 7000),
        DrumSound.Clap => Clap(),
        DrumSound.Rim => Rim(),
        DrumSound.TomLow => Tom(120, 0.35),
        DrumSound.TomHigh => Tom(200, 0.28),
        DrumSound.Cowbell => Cowbell(),
        DrumSound.Shaker => Hat(0.10, 5000, 0.5f),
        _ => Array.Empty<float>(),
    };

    private static float[] Mono(double seconds, Func<int, double, double> gen)
    {
        int n = (int)(seconds * Sr);
        var m = new float[n];
        for (int i = 0; i < n; i++) m[i] = (float)gen(i, (double)i / Sr);
        return m;
    }

    private static float[] Stereo(float[] mono, float level = 0.9f)
    {
        // normalizzazione + copia su due canali
        float peak = 1e-6f;
        foreach (var v in mono) peak = Math.Max(peak, Math.Abs(v));
        float g = level / peak;
        var st = new float[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++) { st[i * 2] = mono[i] * g; st[i * 2 + 1] = mono[i] * g; }
        return st;
    }

    private static readonly Random Rng = new(12345);
    private static double Noise() => Rng.NextDouble() * 2 - 1;

    private static float[] Kick()
    {
        double phase = 0;
        var m = Mono(0.45, (i, t) =>
        {
            double f = 45 + 160 * Math.Exp(-t * 28);       // pitch sweep 205 → 45 Hz
            phase += 2 * Math.PI * f / Sr;
            double env = Math.Exp(-t * 7);
            double click = t < 0.004 ? Noise() * 0.6 * (1 - t / 0.004) : 0;
            double x = Math.Sin(phase) * env + click;
            return Math.Tanh(x * 1.8);
        });
        return Stereo(m, 1.0f);
    }

    private static float[] Snare()
    {
        double p1 = 0, p2 = 0;
        var m = Mono(0.25, (i, t) =>
        {
            p1 += 2 * Math.PI * 185 / Sr; p2 += 2 * Math.PI * 330 / Sr;
            double tone = (Math.Sin(p1) * 0.6 + Math.Sin(p2) * 0.4) * Math.Exp(-t * 30);
            double noise = Noise() * Math.Exp(-t * 14);
            return tone * 0.5 + noise * 0.8;
        });
        return Stereo(m, 0.85f);
    }

    private static float[] Hat(double dur, double hp, float level = 0.55f)
    {
        // rumore filtrato passa-alto (filtro a un polo semplice) con decadimento
        double prevIn = 0, prevOut = 0;
        double rc = 1.0 / (2 * Math.PI * hp), dt = 1.0 / Sr, a = rc / (rc + dt);
        var m = Mono(dur, (i, t) =>
        {
            double x = Noise();
            double y = a * (prevOut + x - prevIn);
            prevIn = x; prevOut = y;
            return y * Math.Exp(-t * (6 / dur));
        });
        return Stereo(m, level);
    }

    private static float[] Clap()
    {
        double prevIn = 0, prevOut = 0;
        double rc = 1.0 / (2 * Math.PI * 1200), dt = 1.0 / Sr, a = rc / (rc + dt);
        var m = Mono(0.30, (i, t) =>
        {
            double x = Noise();
            double y = a * (prevOut + x - prevIn); prevIn = x; prevOut = y;
            // tre "colpi" ravvicinati poi coda
            double env = 0;
            foreach (var st in new[] { 0.0, 0.012, 0.024 })
                if (t >= st) env = Math.Max(env, Math.Exp(-(t - st) * 90));
            if (t >= 0.036) env = Math.Max(env, Math.Exp(-(t - 0.036) * 12) * 0.8);
            return y * env;
        });
        return Stereo(m, 0.8f);
    }

    private static float[] Rim()
    {
        double p = 0;
        var m = Mono(0.08, (i, t) =>
        {
            p += 2 * Math.PI * 1700 / Sr;
            return (Math.Sin(p) * 0.7 + Noise() * 0.3) * Math.Exp(-t * 90);
        });
        return Stereo(m, 0.6f);
    }

    private static float[] Tom(double f0, double dur)
    {
        double p = 0;
        var m = Mono(dur, (i, t) =>
        {
            double f = f0 * (1 + 0.6 * Math.Exp(-t * 25));
            p += 2 * Math.PI * f / Sr;
            return Math.Sin(p) * Math.Exp(-t * (5 / dur)) + Noise() * 0.1 * Math.Exp(-t * 60);
        });
        return Stereo(m, 0.8f);
    }

    private static float[] Cowbell()
    {
        double p1 = 0, p2 = 0;
        var m = Mono(0.25, (i, t) =>
        {
            p1 += 2 * Math.PI * 587 / Sr; p2 += 2 * Math.PI * 845 / Sr;
            double sq1 = Math.Sign(Math.Sin(p1)), sq2 = Math.Sign(Math.Sin(p2));
            return (sq1 + sq2) * 0.5 * Math.Exp(-t * 18);
        });
        return Stereo(m, 0.55f);
    }
}
