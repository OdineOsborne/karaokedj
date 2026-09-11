using NAudio.Dsp;

namespace KaraokeDJ.Audio;

/// <summary>
/// Catena effetti stereo in-place: rimozione voce → filtro DJ → flanger → echo → riverbero.
/// I parametri sono campi semplici, modificabili dal thread UI senza lock (letture atomiche).
/// </summary>
public sealed class FxChain
{
    private const int Fs = SourceFactory.SampleRate;

    // ---------------- rimozione voce (cancellazione del centro)
    public volatile bool VocalRemove;
    /// <summary>0..1: quanto centro togliere.</summary>
    public float VocalStrength = 1f;
    private readonly BiQuadFilter _bassKeep = BiQuadFilter.LowPassFilter(Fs, 140, 0.7f);
    private readonly BiQuadFilter _airKeep = BiQuadFilter.HighPassFilter(Fs, 7500, 0.7f);

    // ---------------- filtro DJ: -1 = low-pass chiuso … 0 = neutro … +1 = high-pass chiuso
    public float Filter;
    private float _filterApplied = 999;
    private readonly BiQuadFilter[] _lp = { BiQuadFilter.LowPassFilter(Fs, 20000, 0.9f), BiQuadFilter.LowPassFilter(Fs, 20000, 0.9f) };
    private readonly BiQuadFilter[] _hp = { BiQuadFilter.HighPassFilter(Fs, 10, 0.9f), BiQuadFilter.HighPassFilter(Fs, 10, 0.9f) };

    // ---------------- flanger
    public volatile bool FlangerOn;
    public float FlangerRate = 0.4f;   // Hz
    public float FlangerDepth = 0.7f;  // 0..1
    private readonly float[] _flBuf = new float[2 * 1024];
    private int _flPos;
    private double _flPhase;

    // ---------------- echo (delay)
    public volatile bool EchoOn;
    public float EchoTimeSec = 0.375f;
    public float EchoFeedback = 0.45f;
    public float EchoMix = 0.35f;
    /// <summary>0..1: quanto segnale diretto passa (0 = solo code dell'eco → "echo out").</summary>
    public float Dry = 1f;
    private readonly float[] _dlBuf = new float[2 * Fs * 2]; // 2 s stereo
    private int _dlPos;
    private readonly BiQuadFilter[] _dlTone = { BiQuadFilter.LowPassFilter(Fs, 4500, 0.7f), BiQuadFilter.LowPassFilter(Fs, 4500, 0.7f) };

    // ---------------- riverbero (Freeverb semplificato)
    public volatile bool ReverbOn;
    public float ReverbSize = 0.6f;   // 0..1
    public float ReverbDamp = 0.4f;   // 0..1
    public float ReverbMix = 0.25f;   // 0..1
    private static readonly int[] CombTuning = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
    private static readonly int[] AllpassTuning = { 556, 441, 341, 225 };
    private const int StereoSpread = 23;
    private readonly Comb[][] _combs = new Comb[2][];
    private readonly Allpass[][] _allpasses = new Allpass[2][];

    // ---------------- EQ 3 bande (dB, -30 = kill)
    public float EqLow, EqMid, EqHigh;
    private float _eqLowApplied = 999, _eqMidApplied = 999, _eqHighApplied = 999;
    private readonly BiQuadFilter[] _eqL = { BiQuadFilter.LowShelf(Fs, 250, 0.7f, 0), BiQuadFilter.LowShelf(Fs, 250, 0.7f, 0) };
    private readonly BiQuadFilter[] _eqM = { BiQuadFilter.PeakingEQ(Fs, 1200, 0.8f, 0), BiQuadFilter.PeakingEQ(Fs, 1200, 0.8f, 0) };
    private readonly BiQuadFilter[] _eqH = { BiQuadFilter.HighShelf(Fs, 4000, 0.7f, 0), BiQuadFilter.HighShelf(Fs, 4000, 0.7f, 0) };

    // ---------------- phaser (4 stadi all-pass con LFO)
    public volatile bool PhaserOn;
    public float PhaserRate = 0.5f;
    private readonly float[,] _phState = new float[2, 4];
    private double _phPhase;

    // ---------------- bitcrusher
    public volatile bool CrushOn;
    public float CrushAmount = 0.5f; // 0..1
    private readonly float[] _crHold = new float[2];
    private float _crCounter;

    // ---------------- gate / trans (a tempo)
    public volatile bool GateOn;
    public float GateTimeSec = 0.125f;
    public float GateDepth = 1f;
    private double _gatePhase;

    // rampa del gain "dry" per evitare click
    private float _dryApplied = 1f;

    public FxChain()
    {
        for (int ch = 0; ch < 2; ch++)
        {
            _combs[ch] = CombTuning.Select(t => new Comb(t + ch * StereoSpread)).ToArray();
            _allpasses[ch] = AllpassTuning.Select(t => new Allpass(t + ch * StereoSpread)).ToArray();
        }
    }

    public bool AnyActive => VocalRemove || FlangerOn || EchoOn || ReverbOn || PhaserOn || CrushOn || GateOn
        || Math.Abs(Filter) > 0.01f || Dry < 0.999f || Math.Abs(EqLow) > 0.05f || Math.Abs(EqMid) > 0.05f || Math.Abs(EqHigh) > 0.05f;

    public void Reset()
    {
        Array.Clear(_dlBuf);
        Array.Clear(_flBuf);
        foreach (var cs in _combs) foreach (var c in cs) c.Clear();
        foreach (var aps in _allpasses) foreach (var a in aps) a.Clear();
        _dryApplied = Dry;
    }

    /// <summary>Elabora <paramref name="n"/> campioni interleaved (stereo) a partire da <paramref name="offset"/>.</summary>
    public void Process(float[] buf, int offset, int n)
    {
        if (!AnyActive && _dryApplied >= 0.999f) return;
        int frames = n / 2;

        if (VocalRemove) ProcessVocal(buf, offset, frames);
        ProcessEq(buf, offset, frames);
        if (Math.Abs(Filter) > 0.01f || Math.Abs(_filterApplied) > 0.01f) ProcessFilter(buf, offset, frames);
        if (CrushOn) ProcessCrush(buf, offset, frames);
        if (PhaserOn) ProcessPhaser(buf, offset, frames);
        if (FlangerOn) ProcessFlanger(buf, offset, frames);
        if (GateOn) ProcessGate(buf, offset, frames);
        ProcessEcho(buf, offset, frames);   // gestisce anche Dry e la coda quando l'eco viene spento
        if (ReverbOn) ProcessReverb(buf, offset, frames);
    }

    // ------------------------------------------------------------------ voce

    private void ProcessVocal(float[] buf, int o, int frames)
    {
        float s = Math.Clamp(VocalStrength, 0f, 1f);
        for (int i = 0; i < frames; i++)
        {
            float l = buf[o + 2 * i], r = buf[o + 2 * i + 1];
            float mid = 0.5f * (l + r);
            float side = 0.5f * (l - r);
            // togliamo il centro ma rimettiamo bassi e "aria" del centro (cassa, basso, piatti)
            float keep = _bassKeep.Transform(mid) + 0.6f * _airKeep.Transform(mid);
            float nl = (1 - s) * l + s * (side + keep);
            float nr = (1 - s) * r + s * (-side + keep);
            buf[o + 2 * i] = nl;
            buf[o + 2 * i + 1] = nr;
        }
    }

    // ------------------------------------------------------------------ filtro

    private void ProcessFilter(float[] buf, int o, int frames)
    {
        float f = Math.Clamp(Filter, -1f, 1f);
        if (Math.Abs(f - _filterApplied) > 0.005f)
        {
            _filterApplied = f;
            // mappa esponenziale: LP da 20 kHz a 150 Hz, HP da 20 Hz a 6 kHz
            float lpFreq = f < 0 ? (float)(20000 * Math.Pow(150.0 / 20000, -f)) : 20000f;
            float hpFreq = f > 0 ? (float)(20 * Math.Pow(6000.0 / 20, f)) : 10f;
            for (int ch = 0; ch < 2; ch++)
            {
                _lp[ch].SetLowPassFilter(Fs, lpFreq, 1.0f);
                _hp[ch].SetHighPassFilter(Fs, hpFreq, 1.0f);
            }
        }
        if (Math.Abs(f) < 0.01f) return;
        for (int i = 0; i < frames; i++)
        {
            for (int ch = 0; ch < 2; ch++)
            {
                int idx = o + 2 * i + ch;
                float v = buf[idx];
                v = f < 0 ? _lp[ch].Transform(v) : _hp[ch].Transform(v);
                buf[idx] = v;
            }
        }
    }

    // ------------------------------------------------------------------ flanger

    private void ProcessFlanger(float[] buf, int o, int frames)
    {
        int len = _flBuf.Length / 2;
        double inc = 2 * Math.PI * FlangerRate / Fs;
        float depth = Math.Clamp(FlangerDepth, 0f, 1f);
        for (int i = 0; i < frames; i++)
        {
            // ritardo tra 1 e 8 ms modulato
            double d = (1.0 + 3.5 * (1 + Math.Sin(_flPhase)) * depth) * Fs / 1000.0;
            _flPhase += inc; if (_flPhase > 2 * Math.PI) _flPhase -= 2 * Math.PI;
            for (int ch = 0; ch < 2; ch++)
            {
                int idx = o + 2 * i + ch;
                float x = buf[idx];
                double rp = _flPos - d; if (rp < 0) rp += len;
                int i0 = (int)rp; int i1 = (i0 + 1) % len; float fr = (float)(rp - i0);
                float delayed = _flBuf[i0 * 2 + ch] * (1 - fr) + _flBuf[i1 * 2 + ch] * fr;
                _flBuf[_flPos * 2 + ch] = x + delayed * 0.5f;
                buf[idx] = 0.6f * x + 0.6f * delayed;
            }
            _flPos = (_flPos + 1) % len;
        }
    }

    // ------------------------------------------------------------------ echo

    private void ProcessEcho(float[] buf, int o, int frames)
    {
        int len = _dlBuf.Length / 2;
        int delay = Math.Clamp((int)(EchoTimeSec * Fs), 10, len - 1);
        float fb = Math.Clamp(EchoFeedback, 0f, 0.95f);
        float mix = Math.Clamp(EchoMix, 0f, 1f);
        float dryTarget = Math.Clamp(Dry, 0f, 1f);
        // rampa fissa di ~30 ms verso il target, indipendente dalla dimensione del buffer
        float dryStep = Math.Sign(dryTarget - _dryApplied) * (1f / (0.03f * Fs));
        bool on = EchoOn;

        for (int i = 0; i < frames; i++)
        {
            if (Math.Abs(dryTarget - _dryApplied) <= Math.Abs(dryStep)) _dryApplied = dryTarget; else _dryApplied += dryStep;
            int rp = _dlPos - delay; if (rp < 0) rp += len;
            for (int ch = 0; ch < 2; ch++)
            {
                int idx = o + 2 * i + ch;
                float x = buf[idx] * _dryApplied;
                float echo = _dlBuf[rp * 2 + ch];
                float input = on ? x : 0f; // eco spento: la coda si spegne da sola
                _dlBuf[_dlPos * 2 + ch] = _dlTone[ch].Transform(input + echo * fb);
                buf[idx] = x + echo * mix;
            }
            _dlPos = (_dlPos + 1) % len;
        }
    }

    // ------------------------------------------------------------------ EQ

    private void ProcessEq(float[] buf, int o, int frames)
    {
        bool any = Math.Abs(EqLow) > 0.05f || Math.Abs(EqMid) > 0.05f || Math.Abs(EqHigh) > 0.05f;
        if (!any && Math.Abs(_eqLowApplied) < 0.05f && Math.Abs(_eqMidApplied) < 0.05f && Math.Abs(_eqHighApplied) < 0.05f) return;
        if (Math.Abs(EqLow - _eqLowApplied) > 0.05f) { _eqLowApplied = EqLow; for (int c = 0; c < 2; c++) _eqL[c] = BiQuadFilter.LowShelf(Fs, 250, 0.7f, EqLow); }
        if (Math.Abs(EqMid - _eqMidApplied) > 0.05f) { _eqMidApplied = EqMid; foreach (var f in _eqM) f.SetPeakingEq(Fs, 1200, 0.8f, EqMid); }
        if (Math.Abs(EqHigh - _eqHighApplied) > 0.05f) { _eqHighApplied = EqHigh; for (int c = 0; c < 2; c++) _eqH[c] = BiQuadFilter.HighShelf(Fs, 4000, 0.7f, EqHigh); }
        if (!any) return;
        for (int i = 0; i < frames; i++)
            for (int ch = 0; ch < 2; ch++)
            {
                int idx = o + 2 * i + ch;
                float v = buf[idx];
                v = _eqL[ch].Transform(v);
                v = _eqM[ch].Transform(v);
                v = _eqH[ch].Transform(v);
                buf[idx] = v;
            }
    }

    // ------------------------------------------------------------------ phaser

    private void ProcessPhaser(float[] buf, int o, int frames)
    {
        double inc = 2 * Math.PI * PhaserRate / Fs;
        for (int i = 0; i < frames; i++)
        {
            // frequenza di taglio degli all-pass tra 300 e 3000 Hz
            double fc = 300 + 1350 * (1 + Math.Sin(_phPhase));
            _phPhase += inc; if (_phPhase > 2 * Math.PI) _phPhase -= 2 * Math.PI;
            float a = (float)((Math.Tan(Math.PI * fc / Fs) - 1) / (Math.Tan(Math.PI * fc / Fs) + 1));
            for (int ch = 0; ch < 2; ch++)
            {
                int idx = o + 2 * i + ch;
                float x = buf[idx];
                float y = x;
                for (int s = 0; s < 4; s++)
                {
                    float st = _phState[ch, s];
                    float outv = a * y + st;
                    _phState[ch, s] = y - a * outv;
                    y = outv;
                }
                buf[idx] = 0.5f * (x + y);
            }
        }
    }

    // ------------------------------------------------------------------ bitcrusher

    private void ProcessCrush(float[] buf, int o, int frames)
    {
        float amt = Math.Clamp(CrushAmount, 0f, 1f);
        int bits = (int)Math.Round(16 - 12 * amt);          // 16 → 4 bit
        float step = 1f / (1 << (bits - 1));
        float decim = 1 + 15 * amt;                         // 1 → 16 (riduzione frequenza di campionamento)
        for (int i = 0; i < frames; i++)
        {
            _crCounter += 1;
            bool sample = _crCounter >= decim;
            if (sample) _crCounter -= decim;
            for (int ch = 0; ch < 2; ch++)
            {
                int idx = o + 2 * i + ch;
                if (sample) _crHold[ch] = (float)Math.Round(buf[idx] / step) * step;
                buf[idx] = _crHold[ch];
            }
        }
    }

    // ------------------------------------------------------------------ gate / trans

    private void ProcessGate(float[] buf, int o, int frames)
    {
        double period = Math.Max(0.02, GateTimeSec) * Fs;
        float depth = Math.Clamp(GateDepth, 0f, 1f);
        for (int i = 0; i < frames; i++)
        {
            double p = _gatePhase / period;      // 0..1
            // finestra: aperta per metà periodo con bordi ammorbiditi
            double g = p < 0.5 ? 1.0 : 0.0;
            double edge = 0.03;
            if (p < edge) g = p / edge; else if (p > 0.5 - edge && p < 0.5) g = (0.5 - p) / edge;
            float gain = (float)(1 - depth + depth * g);
            buf[o + 2 * i] *= gain; buf[o + 2 * i + 1] *= gain;
            _gatePhase += 1; if (_gatePhase >= period) _gatePhase -= period;
        }
    }

    // ------------------------------------------------------------------ riverbero

    private void ProcessReverb(float[] buf, int o, int frames)
    {
        float room = 0.7f + 0.28f * Math.Clamp(ReverbSize, 0f, 1f);
        float damp = 0.4f * Math.Clamp(ReverbDamp, 0f, 1f);
        float wet = Math.Clamp(ReverbMix, 0f, 1f) * 0.5f;
        float dry = 1f - wet * 0.6f;
        for (int i = 0; i < frames; i++)
        {
            float inL = buf[o + 2 * i], inR = buf[o + 2 * i + 1];
            float input = (inL + inR) * 0.015f;
            for (int ch = 0; ch < 2; ch++)
            {
                float outv = 0;
                foreach (var c in _combs[ch]) outv += c.Process(input, room, damp);
                foreach (var a in _allpasses[ch]) outv = a.Process(outv);
                int idx = o + 2 * i + ch;
                buf[idx] = buf[idx] * dry + outv * wet;
            }
        }
    }

    private sealed class Comb
    {
        private readonly float[] _buf; private int _pos; private float _store;
        public Comb(int size) => _buf = new float[size];
        public void Clear() { Array.Clear(_buf); _store = 0; }
        public float Process(float input, float feedback, float damp)
        {
            float output = _buf[_pos];
            _store = output * (1 - damp) + _store * damp;
            _buf[_pos] = input + _store * feedback;
            if (++_pos >= _buf.Length) _pos = 0;
            return output;
        }
    }

    private sealed class Allpass
    {
        private readonly float[] _buf; private int _pos;
        public Allpass(int size) => _buf = new float[size];
        public void Clear() => Array.Clear(_buf);
        public float Process(float input)
        {
            float bufout = _buf[_pos];
            float output = -input + bufout;
            _buf[_pos] = input + bufout * 0.5f;
            if (++_pos >= _buf.Length) _pos = 0;
            return output;
        }
    }
}
