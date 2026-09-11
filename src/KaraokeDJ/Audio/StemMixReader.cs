using NAudio.Wave;

namespace KaraokeDJ.Audio;

/// <summary>
/// Sorgente che legge in parallelo i 4 stem di Demucs (voce, batteria, basso, altro) e li somma con guadagni
/// regolabili al volo. Si comporta come un unico WaveStream (seek/durata) e ISampleProvider per il deck.
/// </summary>
public sealed class StemMixReader : WaveStream, ISampleProvider
{
    public const int Count = 4;
    public static readonly string[] Names = { "vocals", "drums", "bass", "other" };
    public static readonly string[] Labels = { "VOCE", "BATT.", "BASSO", "ALTRO" };

    private readonly WaveStream[] _readers = new WaveStream[Count];
    private readonly ISampleProvider[] _providers = new ISampleProvider[Count];
    private readonly float[] _gains = { 1f, 1f, 1f, 1f };
    private readonly float[] _last = { 1f, 1f, 1f, 1f };
    private float[] _tmp = new float[8192];
    private readonly object _gate = new();
    private readonly long _length;

    public StemMixReader(string dir)
    {
        try
        {
            for (int i = 0; i < Count; i++)
            {
                var path = Path.Combine(dir, Names[i] + ".mp3");
                if (!File.Exists(path)) throw new FileNotFoundException("Stem mancante: " + Names[i], path);
                (_readers[i], _providers[i]) = SourceFactory.Open(path);
            }
        }
        catch { foreach (var r in _readers) r?.Dispose(); throw; }
        double secs = _readers.Min(r => r.TotalTime.TotalSeconds);
        _length = (long)(secs * SourceFactory.SampleRate) * SourceFactory.Channels * 4;
    }

    public static bool HasAll(string? dir) => !string.IsNullOrEmpty(dir) && Names.All(n => File.Exists(Path.Combine(dir, n + ".mp3")));

    /// <summary>Guadagno dello stem (0 = muto, 1 = originale, fino a 2).</summary>
    public float GetGain(int i) => _gains[i];
    public void SetGain(int i, float g) => _gains[i] = Math.Clamp(g, 0f, 2f);

    public override WaveFormat WaveFormat => SourceFactory.Format;
    public override long Length => _length;

    public override long Position
    {
        get => (long)(_readers[0].CurrentTime.TotalSeconds * SourceFactory.SampleRate) * SourceFactory.Channels * 4;
        set
        {
            var t = TimeSpan.FromSeconds((double)value / 4 / SourceFactory.Channels / SourceFactory.SampleRate);
            lock (_gate) foreach (var r in _readers) { try { r.CurrentTime = t; } catch { } }
        }
    }

    public override TimeSpan TotalTime => TimeSpan.FromSeconds((double)_length / 4 / SourceFactory.Channels / SourceFactory.SampleRate);
    public override TimeSpan CurrentTime { get => _readers[0].CurrentTime; set => Position = (long)(value.TotalSeconds * SourceFactory.SampleRate) * SourceFactory.Channels * 4; }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            if (_tmp.Length < count) _tmp = new float[count];
            Array.Clear(buffer, offset, count);
            int max = 0;
            for (int i = 0; i < Count; i++)
            {
                float target = _gains[i], start = _last[i];
                int n = _providers[i].Read(_tmp, 0, count);
                if (n > max) max = n;
                if (target == 0f && start == 0f) continue;
                for (int k = 0; k < n; k++)
                {
                    float g = start + (target - start) * k / count;
                    buffer[offset + k] += _tmp[k] * g;
                }
                _last[i] = target;
            }
            // gli stem finiscono insieme: se uno è più corto, gli altri riempiono comunque
            return max;
        }
    }

    // WaveStream a byte: usato raramente (il deck legge float), convertiamo
    public override int Read(byte[] buffer, int offset, int count)
    {
        int samples = count / 4;
        var f = new float[samples];
        int n = Read(f, 0, samples);
        Buffer.BlockCopy(f, 0, buffer, offset, n * 4);
        return n * 4;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var r in _readers) r?.Dispose();
        base.Dispose(disposing);
    }
}
