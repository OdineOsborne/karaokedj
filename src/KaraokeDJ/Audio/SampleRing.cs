namespace KaraokeDJ.Audio;

/// <summary>
/// Anello di campioni fra due thread audio (es. il mixer principale scrive, l'uscita cuffia legge).
/// Se chi legge resta indietro oltre <see cref="MaxLatency"/> campioni, i più vecchi vengono scartati: la cuffia
/// resta sempre "adesso", al prezzo di un click impercettibile invece di un ritardo che cresce.
/// </summary>
public sealed class SampleRing
{
    private readonly float[] _buf;
    private readonly object _gate = new();
    private int _w, _r, _count;

    public SampleRing(int capacity, int maxLatency) { _buf = new float[capacity]; MaxLatency = Math.Min(maxLatency, capacity); }

    public int MaxLatency { get; }
    public int Count { get { lock (_gate) return _count; } }

    public void Write(float[] src, int offset, int n)
    {
        lock (_gate)
        {
            for (int i = 0; i < n; i++)
            {
                _buf[_w] = src[offset + i];
                _w = (_w + 1) % _buf.Length;
                if (_count < _buf.Length) _count++; else _r = (_r + 1) % _buf.Length; // pieno: scarta il più vecchio
            }
            if (_count > MaxLatency) { int drop = _count - MaxLatency; _r = (_r + drop) % _buf.Length; _count -= drop; }
        }
    }

    /// <summary>Riempie sempre n campioni: quelli mancanti sono silenzio.</summary>
    public int Read(float[] dst, int offset, int n)
    {
        lock (_gate)
        {
            int m = Math.Min(n, _count);
            for (int i = 0; i < m; i++) { dst[offset + i] = _buf[_r]; _r = (_r + 1) % _buf.Length; }
            _count -= m;
            if (m < n) Array.Clear(dst, offset + m, n - m);
            return n;
        }
    }

    /// <summary>Come Read, ma somma nel buffer di destinazione (moltiplicando per gain).</summary>
    public void ReadAdd(float[] dst, int offset, int n, float gain)
    {
        lock (_gate)
        {
            int m = Math.Min(n, _count);
            for (int i = 0; i < m; i++) { dst[offset + i] += _buf[_r] * gain; _r = (_r + 1) % _buf.Length; }
            _count -= m;
        }
    }

    public void Clear() { lock (_gate) { _w = _r = _count = 0; } }
}
