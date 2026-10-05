using System.Collections.Concurrent;
using KaraokeDJ.Models.Studio;
using NAudio.Wave;

namespace KaraokeDJ.Audio.Studio;

/// <summary>
/// Motore dello Studio: suona il progetto dalla timeline, per l'ascolto in cuffia e per l'esportazione. È lo stesso
/// codice nei due casi, così quello che si sente in anteprima è esattamente il file che si esporta.
/// </summary>
public sealed class StudioRenderer : ISampleProvider
{
    private const int Fs = SourceFactory.SampleRate;
    private const int Block = 512;

    private StudioProject _p;
    private readonly Func<StudioClip, string?> _pathOf;
    private readonly bool _sync;
    private readonly object _gate = new();
    private readonly Dictionary<string, ClipVoice> _voices = new();
    private long _frame;
    private float[] _tmp = new float[Block * 2];
    private readonly float[] _mix = new float[Block * 2];
    private readonly Limiter _limiter = new();
    private CancellationTokenSource? _prefetchCts;

    public static readonly DecodeCache Cache = new();

    /// <param name="sync">true = esportazione: se una clip non è ancora decodificata la si decodifica subito (niente buchi)</param>
    public StudioRenderer(StudioProject project, Func<StudioClip, string?> pathOf, bool sync)
    {
        _p = project; _pathOf = pathOf; _sync = sync;
        if (!sync) StartPrefetch();
    }

    public WaveFormat WaveFormat => SourceFactory.Format;
    public double PositionSec { get { lock (_gate) return _frame / (double)Fs; } }
    /// <summary>In anteprima: una clip non era ancora pronta (decodifica in corso) ed è rimasta muta per un attimo.</summary>
    public volatile bool MissedData;

    /// <summary>Nuova versione del progetto (dopo una modifica): le clip ripartono dal punto giusto.</summary>
    public void SetProject(StudioProject p)
    {
        lock (_gate) { _p = p; _voices.Clear(); }
    }

    public void Seek(double sec)
    {
        lock (_gate) { _frame = (long)(Math.Max(0, sec) * Fs); _voices.Clear(); _limiter.Reset(); }
    }

    public void Stop() => _prefetchCts?.Cancel();

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            int done = 0;
            while (done < count)
            {
                int frames = Math.Min(Block, (count - done) / 2);
                if (frames <= 0) break;
                RenderBlock(frames);
                Array.Copy(_mix, 0, buffer, offset + done, frames * 2);
                done += frames * 2;
                _frame += frames;
            }
            return done;
        }
    }

    private void RenderBlock(int frames)
    {
        Array.Clear(_mix, 0, frames * 2);
        double t0 = _frame / (double)Fs, t1 = (_frame + frames) / (double)Fs;
        var p = _p;
        bool anySolo = p.Lanes.Any(l => l.Solo);

        // abbassamento della musica quando parla qualcuno (corsie voce): rampa di 150 ms, niente scatti
        double duck = 0;
        foreach (var c in p.Clips)
        {
            var lane = p.Lane(c.LaneId);
            if (lane is not { DucksMusic: true } || lane.Mute || (anySolo && !lane.Solo)) continue;
            double a = c.StartSec - 0.15, b = c.EndSec + 0.3;
            if (t1 < a || t0 > b) continue;
            double tm = (t0 + t1) / 2;
            double f = tm < c.StartSec ? (tm - a) / 0.15 : tm > c.EndSec ? 1 - (tm - c.EndSec) / 0.3 : 1;
            duck = Math.Min(duck, lane.DuckDb * Math.Clamp(f, 0, 1));
        }
        float duckLin = (float)Math.Pow(10, duck / 20);

        var live = new HashSet<string>();
        foreach (var c in p.Clips)
        {
            double tail = TailSec(c);
            if (t1 <= c.StartSec || t0 >= c.EndSec + tail) continue;
            var lane = p.Lane(c.LaneId);
            if (lane == null || lane.Mute || (anySolo && !lane.Solo)) continue;
            live.Add(c.Id);
            if (!_voices.TryGetValue(c.Id, out var v))
            {
                var path = _pathOf(c);
                if (path == null) continue;
                var data = _sync ? Cache.GetOrDecode(path, c) : Cache.TryGet(path, c);
                if (data == null) { MissedData = true; continue; }
                v = new ClipVoice(c, data);
                v.Seek(Math.Max(0, t0 - c.StartSec));
                _voices[c.Id] = v;
            }
            float laneGain = (float)Math.Pow(10, lane.GainDb / 20) * (lane.Kind == LaneKind.Music ? duckLin : 1f);
            if (_tmp.Length < frames * 2) _tmp = new float[frames * 2];
            v.Render(_tmp, frames, t0 - c.StartSec, laneGain, lane.Pan, p.Normalize);
            for (int i = 0; i < frames * 2; i++) _mix[i] += _tmp[i];
        }
        foreach (var id in _voices.Keys.Where(k => !live.Contains(k)).ToList()) _voices.Remove(id);

        float master = (float)Math.Pow(10, p.MasterGainDb / 20);
        if (Math.Abs(master - 1) > 1e-4) for (int i = 0; i < frames * 2; i++) _mix[i] *= master;
        if (p.Normalize) _limiter.Process(_mix, frames);
        else for (int i = 0; i < frames * 2; i++) _mix[i] = Math.Clamp(_mix[i], -1f, 1f);
    }

    /// <summary>Echo e riverbero continuano dopo la fine della clip: le code vanno lasciate suonare.</summary>
    private static double TailSec(StudioClip c)
    {
        double len = c.LengthSec;
        return c.ValueAt("echo", len) > 0.01 || c.ValueAt("reverb", len) > 0.01 ? 4 : 0;
    }

    // ------------------------------------------------------------ decodifica in anticipo (anteprima)

    private void StartPrefetch()
    {
        var cts = new CancellationTokenSource();
        _prefetchCts = cts;
        new Thread(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    StudioProject p; double pos;
                    lock (_gate) { p = _p; pos = _frame / (double)Fs; }
                    // le clip che partono nei prossimi 20 secondi, in ordine di partenza
                    var next = p.Clips.Where(c => c.EndSec > pos && c.StartSec < pos + 20).OrderBy(c => c.StartSec).ToList();
                    bool worked = false;
                    foreach (var c in next)
                    {
                        if (cts.IsCancellationRequested) break;
                        var path = _pathOf(c);
                        if (path == null || Cache.TryGet(path, c) != null) continue;
                        Cache.GetOrDecode(path, c);
                        worked = true;
                        break;
                    }
                    if (!worked) Thread.Sleep(120);
                }
                catch { Thread.Sleep(300); }
            }
        }) { IsBackground = true, Name = "Studio: decodifica", Priority = ThreadPriority.BelowNormal }.Start();
    }
}

/// <summary>Il pezzo di file di una clip, decodificato in memoria (44,1 kHz stereo), con il volume medio misurato.</summary>
public sealed class DecodedClip
{
    public required float[] Data;        // interleaved stereo
    public required double StartSec;     // secondo del file del primo campione
    public double Rms;
    /// <summary>La decodifica è arrivata alla fine del file: il pezzo è completo anche se dura qualche campione meno del previsto.</summary>
    public bool ReachedEnd;
    public double AutoGainDb => Rms <= 1e-5 ? 0 : Math.Clamp(20 * Math.Log10(0.2 / Rms), -12, 6);
}

/// <summary>
/// Cache dei pezzi decodificati (pochi alla volta: un brano intero sono ~50 MB). La chiave è file + pezzo usato,
/// arrotondato, così spostare una clip sulla timeline non costringe a decodificarla di nuovo.
/// </summary>
public sealed class DecodeCache
{
    private readonly ConcurrentDictionary<string, DecodedClip> _map = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly object _decodeGate = new();
    private const int MaxEntries = 5;   // un brano intero decodificato pesa ~100 MB

    private static (double from, double to) Range(StudioClip c)
    {
        // un po' di margine: accorciare di poco la clip non rifà la decodifica
        double from = Math.Max(0, Math.Floor(c.InSec / 4) * 4 - 4);
        double to = Math.Ceiling(Math.Max(c.OutSec, c.Loops.Count > 0 ? c.Loops.Max(l => l.AtSec + l.LengthSec) : 0) / 4) * 4 + 4;
        return (from, to);
    }

    private static string Key(string path, StudioClip c) { var (a, b) = Range(c); return $"{path}|{a}|{b}"; }

    public DecodedClip? TryGet(string path, StudioClip c)
    {
        var (from, to) = Range(c);
        foreach (var kv in _map)
            if (kv.Key.StartsWith(path + "|") && kv.Value.StartSec <= from + 1e-6
                && (kv.Value.ReachedEnd || kv.Value.StartSec + kv.Value.Data.Length / 2.0 / SourceFactory.SampleRate >= Math.Min(to, c.OutSec + 0.01)))
                return kv.Value;
        return null;
    }

    public DecodedClip? GetOrDecode(string path, StudioClip c)
    {
        var hit = TryGet(path, c);
        if (hit != null) return hit;
        lock (_decodeGate)
        {
            hit = TryGet(path, c);
            if (hit != null) return hit;
            var (from, to) = Range(c);
            var d = Decode(path, from, to);
            if (d == null) return null;
            var key = Key(path, c);
            _map[key] = d;
            _order.Enqueue(key);
            while (_map.Count > MaxEntries && _order.TryDequeue(out var old)) _map.TryRemove(old, out _);
            return d;
        }
    }

    /// <summary>
    /// Si decodifica dall'inizio del file (e si tiene solo il pezzo che serve): lo spostamento con MediaFoundation
    /// non è preciso al campione, e la griglia dei battiti è stata calcolata leggendo il file dall'inizio.
    /// </summary>
    private static DecodedClip? Decode(string path, double from, double to)
    {
        try
        {
            var (reader, sp) = SourceFactory.Open(path);
            using (reader)
            {
                long total = (long)(reader.TotalTime.TotalSeconds * SourceFactory.SampleRate + SourceFactory.SampleRate) * 2;
                long f0 = (long)(from * SourceFactory.SampleRate) * 2, f1 = Math.Min(total, (long)(to * SourceFactory.SampleRate) * 2);
                if (f1 <= f0) return null;
                var outp = new float[f1 - f0];
                long w = 0;
                var buf = new float[16384];
                long pos = 0; int n;
                double sq = 0; long sqN = 0;
                while (pos < f1 && (n = sp.Read(buf, 0, buf.Length)) > 0)
                {
                    for (int i = 0; i < n; i++, pos++)
                    {
                        if (pos < f0 || pos >= f1) continue;
                        float s = buf[i];
                        outp[w++] = s;
                        sq += s * s; sqN++;
                    }
                }
                // il decoder può dare qualche millisecondo in meno della durata dichiarata: senza questo segno una
                // clip che arriva alla fine del brano non risultava mai pronta e lo Studio restava muto
                bool reachedEnd = w < outp.Length;
                if (reachedEnd) Array.Resize(ref outp, (int)(w & ~1L));
                return new DecodedClip { Data = outp, StartSec = from, Rms = sqN > 0 ? Math.Sqrt(sq / sqN) : 0, ReachedEnd = reachedEnd };
            }
        }
        catch { return null; }
    }
}

/// <summary>Una clip che suona: pezzi del file (con i loop) → tempo/tonalità → effetti automatizzati → volume e panorama.</summary>
internal sealed class ClipVoice
{
    private readonly StudioClip _c;
    private readonly SegmentReader _seg;
    private readonly SoundTouchSampleProvider _st;
    private readonly FxChain _fx = new();
    private readonly double _autoGainDb;

    public ClipVoice(StudioClip c, DecodedClip d)
    {
        _c = c;
        _seg = new SegmentReader(d, c.Segments());
        _st = new SoundTouchSampleProvider(_seg) { Tempo = c.Tempo, Semitones = c.KeyShift, KeyLock = true };
        _autoGainDb = double.IsNaN(c.AutoGainDb) ? d.AutoGainDb : c.AutoGainDb;
    }

    public void Seek(double localSec)
    {
        _seg.SeekSource(localSec * _c.Tempo);
        _st.Reset();
        _fx.Reset();
    }

    public void Render(float[] outBuf, int frames, double tLocal, float laneGain, double lanePan, bool normalize)
    {
        int n = frames * 2;
        int got = tLocal < _c.LengthSec ? _st.Read(outBuf, 0, n) : 0;
        if (got < n) Array.Clear(outBuf, got, n - got);

        double dur = frames / (double)SourceFactory.SampleRate, tm = tLocal + dur / 2;
        double beat = _c.Bpm > 0 ? 60.0 / _c.EffectiveBpm : 0.5;
        _fx.EqLow = (float)_c.ValueAt("low", tm);
        _fx.EqMid = (float)_c.ValueAt("mid", tm);
        _fx.EqHigh = (float)_c.ValueAt("high", tm);
        _fx.Filter = (float)_c.ValueAt("filter", tm);
        double echo = _c.ValueAt("echo", tm);
        _fx.EchoOn = echo > 0.01; _fx.EchoMix = (float)Math.Min(1, echo * 1.1); _fx.EchoFeedback = (float)(0.3 + 0.4 * echo); _fx.EchoTimeSec = (float)(beat * 0.75);
        _fx.Dry = (float)_c.ValueAt("dry", tm);
        double rev = _c.ValueAt("reverb", tm);
        _fx.ReverbOn = rev > 0.01; _fx.ReverbMix = (float)(rev * 0.7); _fx.ReverbSize = (float)(0.5 + 0.4 * rev);
        double fl = _c.ValueAt("flanger", tm);
        _fx.FlangerOn = fl > 0.01; _fx.FlangerDepth = (float)fl; _fx.FlangerRate = (float)(1 / (beat * 8));
        _fx.PhaserOn = _c.ValueAt("phaser", tm) > 0.01; _fx.PhaserRate = (float)(1 / (beat * 4));
        double gate = _c.ValueAt("gate", tm);
        _fx.GateOn = gate > 0.01; _fx.GateDepth = (float)gate; _fx.GateTimeSec = (float)(beat / 2);
        double crush = _c.ValueAt("crush", tm);
        _fx.CrushOn = crush > 0.01; _fx.CrushAmount = (float)crush;
        _fx.Process(outBuf, 0, n);

        // volume: interpolato campione per campione fra inizio e fine del blocco (niente "scalini")
        double g = Math.Pow(10, (_c.GainDb + (normalize ? _autoGainDb : 0)) / 20) * laneGain;
        double v0 = Level(tLocal) * g, v1 = Level(tLocal + dur) * g;
        double pan = Math.Clamp(lanePan + _c.ValueAt("pan", tm), -1, 1);
        float pl = (float)Math.Cos((pan + 1) * Math.PI / 4) * 1.4142f, pr = (float)Math.Sin((pan + 1) * Math.PI / 4) * 1.4142f;
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(v0 + (v1 - v0) * i / frames);
            outBuf[i * 2] *= v * pl;
            outBuf[i * 2 + 1] *= v * pr;
        }
    }

    private double Level(double t)
    {
        double v = _c.ValueAt("vol", t);
        if (_c.FadeInSec > 0 && t < _c.FadeInSec) v *= Math.Max(0, t / _c.FadeInSec);
        double left = _c.LengthSec - t;
        if (_c.FadeOutSec > 0 && left < _c.FadeOutSec) v *= Math.Max(0, left / _c.FadeOutSec);
        if (t < 0) v = 0;
        return v;
    }
}

/// <summary>Legge i pezzi del file uno dopo l'altro (i loop sono pezzi ripetuti), dalla memoria.</summary>
internal sealed class SegmentReader : ISampleProvider
{
    private readonly float[] _data;
    private readonly (long from, long to)[] _segs;   // indici di frame dentro _data
    private int _si;
    private long _pos;                                  // frame dentro il pezzo corrente

    public SegmentReader(DecodedClip d, List<(double From, double To)> segs)
    {
        _data = d.Data;
        long frames = d.Data.Length / 2;
        _segs = segs.Select(s => (
            Math.Clamp((long)Math.Round((s.From - d.StartSec) * SourceFactory.SampleRate), 0, frames),
            Math.Clamp((long)Math.Round((s.To - d.StartSec) * SourceFactory.SampleRate), 0, frames))).ToArray();
    }

    public WaveFormat WaveFormat => SourceFactory.Format;

    public void SeekSource(double sec)
    {
        long f = (long)(sec * SourceFactory.SampleRate);
        _si = 0; _pos = 0;
        while (_si < _segs.Length)
        {
            long len = _segs[_si].to - _segs[_si].from;
            if (f < len) { _pos = f; return; }
            f -= len; _si++;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int done = 0;
        while (done < count && _si < _segs.Length)
        {
            var (from, to) = _segs[_si];
            long avail = to - from - _pos;
            if (avail <= 0) { _si++; _pos = 0; continue; }
            int take = (int)Math.Min(avail, (count - done) / 2);
            if (take <= 0) break;
            // piccola dissolvenza ai giunti dei loop (2 ms) per non sentire il "click" del taglio
            Array.Copy(_data, (from + _pos) * 2, buffer, offset + done, take * 2);
            const int fade = 88;
            if (_segs.Length > 1)
                for (int i = 0; i < take; i++)
                {
                    long k = _pos + i, rem = to - from - k;
                    float f = k < fade && _si > 0 ? k / (float)fade : rem < fade && _si < _segs.Length - 1 ? rem / (float)fade : 1f;
                    if (f < 1f) { buffer[offset + done + i * 2] *= f; buffer[offset + done + i * 2 + 1] *= f; }
                }
            _pos += take; done += take * 2;
        }
        return done;
    }
}

/// <summary>Limitatore sul master (tetto −1 dBFS) con 5 ms di anticipo: il volume uniformato non deve mai distorcere.</summary>
internal sealed class Limiter
{
    private const int Look = 220;
    private const float Ceil = 0.89f;
    private readonly float[] _delay = new float[Look * 2];
    private int _d;
    private float _g = 1f;
    private readonly float _release = 1f - (float)Math.Exp(-1.0 / (0.15 * SourceFactory.SampleRate));

    public void Reset() { Array.Clear(_delay); _g = 1f; _d = 0; }

    public void Process(float[] buf, int frames)
    {
        // il guadagno necessario si calcola sul campione che entra e si applica a quello di 5 ms fa: quando arriva
        // il picco il volume è già sceso
        float attack = 1f / Look;
        for (int i = 0; i < frames; i++)
        {
            float l = buf[i * 2], r = buf[i * 2 + 1];
            float pk = Math.Max(Math.Abs(l), Math.Abs(r));
            float need = pk > Ceil ? Ceil / pk : 1f;
            if (need < _g) _g = Math.Max(need, _g - Math.Max(attack, (_g - need) * 0.25f));
            else _g += (1f - _g) * _release;
            float dl = _delay[_d * 2], dr = _delay[_d * 2 + 1];
            _delay[_d * 2] = l; _delay[_d * 2 + 1] = r;
            _d = (_d + 1) % Look;
            buf[i * 2] = Math.Clamp(dl * _g, -0.99f, 0.99f);
            buf[i * 2 + 1] = Math.Clamp(dr * _g, -0.99f, 0.99f);
        }
    }
}
