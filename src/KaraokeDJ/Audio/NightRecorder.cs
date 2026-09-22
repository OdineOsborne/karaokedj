using NAudio.Wave;

namespace KaraokeDJ.Audio;

/// <summary>
/// Registra la serata su file WAV prendendo il mix subito dopo il mixer (prima del volume master):
/// quello che è stato suonato, indipendentemente da quanto era alta la sala.
///
/// Regola di fondo: il thread audio non deve MAI aspettare il disco. Qui copia i campioni in un anello
/// e se ne va; a scrivere ci pensa un thread suo. Se il disco non sta dietro si perdono campioni e li contiamo,
/// ma la musica in sala non si ferma.
/// </summary>
public sealed class NightRecorder : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _src;
    private readonly object _gate = new();
    private readonly float[] _ring = new float[SourceFactory.SampleRate * 2 * 10]; // 10 secondi di margine
    private int _w, _r, _count;
    private WaveFileWriter? _writer;
    private Thread? _thread;
    private volatile bool _running;
    private float[] _drain = new float[16384];

    public NightRecorder(ISampleProvider src) => _src = src;

    public WaveFormat WaveFormat => _src.WaveFormat;

    public bool IsRecording => _running;
    public string? FilePath { get; private set; }
    public DateTime StartedUtc { get; private set; }
    /// <summary>Campioni scartati perché il disco non stava dietro (0 = registrazione integra).</summary>
    public long Dropped { get; private set; }
    public long BytesWritten { get; private set; }
    public double Seconds => BytesWritten / (double)(SourceFactory.SampleRate * 2 * 2);
    public string? LastError { get; private set; }
    /// <summary>Registrazione finita da sola (disco pieno o errore): messaggio per la barra di stato.</summary>
    public event Action<string>? StoppedByItself;

    /// <summary>Spazio libero sotto il quale ci si ferma da soli, per non riempire il disco durante la serata.</summary>
    private const long MinFreeBytes = 500L * 1024 * 1024;

    public void Start(string path)
    {
        if (_running) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new WaveFileWriter(path, new WaveFormat(SourceFactory.SampleRate, 16, 2));
        FilePath = path;
        StartedUtc = DateTime.UtcNow;
        Dropped = 0; BytesWritten = 0; LastError = null;
        lock (_gate) { _w = _r = _count = 0; }
        _running = true;
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "registrazione serata", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Chiude il file. Restituisce il percorso, o null se non si stava registrando.</summary>
    public string? Stop()
    {
        if (!_running) return null;
        _running = false;
        try { _thread?.Join(3000); } catch { }
        _thread = null;
        var path = FilePath;
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        return path;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int n = _src.Read(buffer, offset, count);
        if (!_running || n <= 0) return n;
        lock (_gate)
        {
            for (int i = 0; i < n; i++)
            {
                if (_count >= _ring.Length) { Dropped += n - i; break; }   // disco indietro: meglio un buco che un blocco
                _ring[_w] = buffer[offset + i];
                _w = (_w + 1) % _ring.Length;
                _count++;
            }
        }
        return n;
    }

    private void WriteLoop()
    {
        var lastCheck = DateTime.UtcNow;
        while (true)
        {
            int m;
            lock (_gate)
            {
                m = Math.Min(_count, _drain.Length);
                for (int i = 0; i < m; i++) { _drain[i] = _ring[_r]; _r = (_r + 1) % _ring.Length; }
                _count -= m;
            }
            if (m == 0)
            {
                if (!_running) break;
                Thread.Sleep(20);
                continue;
            }
            try
            {
                _writer!.WriteSamples(_drain, 0, m);
                BytesWritten = _writer.Length;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _running = false;
                StoppedByItself?.Invoke("Registrazione interrotta: " + ex.Message);
                break;
            }
            if ((DateTime.UtcNow - lastCheck).TotalSeconds > 20)
            {
                lastCheck = DateTime.UtcNow;
                if (FreeBytes(FilePath) is long free && free < MinFreeBytes)
                {
                    _running = false;
                    StoppedByItself?.Invoke($"Registrazione fermata: sul disco restano meno di {MinFreeBytes / 1024 / 1024} MB");
                    break;
                }
            }
        }
        try { _writer?.Flush(); } catch { }
    }

    private static long? FreeBytes(string? path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path!))!).AvailableFreeSpace; }
        catch { return null; }
    }

    public void Dispose() => Stop();
}
