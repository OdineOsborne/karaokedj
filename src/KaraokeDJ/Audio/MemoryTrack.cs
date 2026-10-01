using NAudio.Wave;

namespace KaraokeDJ.Audio;

/// <summary>
/// Il brano del deck decodificato tutto in memoria (16 bit, 44,1 kHz stereo: ~50 MB per 5 minuti).
/// Il salto di MediaFoundation non è preciso: su certi mp3 (i download a 48 kHz) sbaglia fino a mezzo secondo,
/// cioè un battito intero, e l'automix agganciava i battiti calcolati mentre l'audio vero era altrove. Da qui
/// ogni salto, cue, loop e cambio di velocità è esatto al campione, e in serata non si legge più dal disco USB.
/// </summary>
public sealed class MemoryTrack : WaveStream, ISampleProvider
{
    private readonly short[] _pcm;        // interleaved stereo
    private readonly long _frames;
    private long _frame;                  // posizione in frame
    private static readonly WaveFormat Fmt = SourceFactory.Format;

    private MemoryTrack(short[] pcm, long frames) { _pcm = pcm; _frames = frames; }

    /// <summary>Oltre questa durata (mix di un'ora, registrazioni) si resta a leggere dal file: troppa memoria.</summary>
    public const double MaxSeconds = 15 * 60;

    /// <summary>Decodifica dall'inizio alla fine (la griglia dei battiti è calcolata allo stesso modo). Null se non si può.</summary>
    public static MemoryTrack? Decode(string path, CancellationToken ct)
    {
        var (reader, sp) = SourceFactory.Open(path);
        using (reader)
        {
            double dur = reader.TotalTime.TotalSeconds;
            if (dur <= 0 || dur > MaxSeconds) return null;
            // un po' di margine: TotalTime degli mp3 VBR è una stima
            long cap = (long)((dur + 10) * SourceFactory.SampleRate) * 2;
            var pcm = new short[cap];
            var buf = new float[16384];
            long w = 0; int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (w + n > pcm.Length) Array.Resize(ref pcm, (int)Math.Min(int.MaxValue - 64, pcm.Length + SourceFactory.SampleRate * 2 * 30L));
                for (int i = 0; i < n; i++)
                {
                    float f = buf[i];
                    pcm[w++] = (short)(f >= 1f ? short.MaxValue : f <= -1f ? -short.MaxValue : f * 32767f);
                }
            }
            return new MemoryTrack(pcm, w / 2);
        }
    }

    public override WaveFormat WaveFormat => Fmt;
    public override long Length => _frames * Fmt.BlockAlign;
    public override long Position
    {
        get => _frame * Fmt.BlockAlign;
        set => _frame = Math.Clamp(value / Fmt.BlockAlign, 0, _frames);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        long avail = (_frames - _frame) * 2;
        int n = (int)Math.Min(count & ~1, avail);
        long src = _frame * 2;
        const float k = 1f / 32768f;
        for (int i = 0; i < n; i++) buffer[offset + i] = _pcm[src + i] * k;
        _frame += n / 2;
        return n;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // per chi la usa come WaveStream (float a 32 bit): si passa dalla versione a campioni
        int floats = count / 4;
        var tmp = new float[floats];
        int n = Read(tmp, 0, floats);
        Buffer.BlockCopy(tmp, 0, buffer, offset, n * 4);
        return n * 4;
    }
}
