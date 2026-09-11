using KaraokeDJ.Audio;

namespace KaraokeDJ.Services;

/// <summary>
/// Monitora la cartella "Suno" nei download: ogni brano scaricato dal browser viene importato
/// in libreria appena il file è completo (dimensione stabile per 2 s).
/// </summary>
public sealed class SunoWatcher : IDisposable
{
    public static string Folder => Path.Combine(AppPaths.DownloadsDir, "Suno");
    public const string SunoCreateUrl = "https://suno.com/create";

    private readonly FileSystemWatcher _fsw;
    private readonly Dictionary<string, (long size, DateTime seen)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File pronto da importare (sul thread UI).</summary>
    public event Action<string>? FileReady;

    public SunoWatcher()
    {
        Directory.CreateDirectory(Folder);
        foreach (var f in Directory.EnumerateFiles(Folder)) _known.Add(f);
        _fsw = new FileSystemWatcher(Folder) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        _fsw.Created += (_, e) => Track(e.FullPath);
        _fsw.Renamed += (_, e) => Track(e.FullPath);
        _fsw.Changed += (_, e) => Track(e.FullPath);
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    private void Track(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!SourceFactory.AudioExtensions.Contains(ext) && !SourceFactory.VideoExtensions.Contains(ext)) return;
        lock (_pending) _pending[path] = (-1, DateTime.UtcNow);
    }

    private void Poll()
    {
        List<string> ready = new();
        lock (_pending)
        {
            foreach (var kv in _pending.ToList())
            {
                var path = kv.Key;
                if (!File.Exists(path)) { _pending.Remove(path); continue; }
                long size;
                try { size = new FileInfo(path).Length; } catch { continue; }
                if (size != kv.Value.size) { _pending[path] = (size, DateTime.UtcNow); continue; }
                if ((DateTime.UtcNow - kv.Value.seen).TotalSeconds < 2) continue;
                if (!CanOpen(path)) continue;
                _pending.Remove(path);
                if (_known.Add(path)) ready.Add(path);
            }
        }
        foreach (var p in ready) FileReady?.Invoke(p);
    }

    private static bool CanOpen(string path)
    {
        try { using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return true; }
        catch { return false; }
    }

    public void Dispose() { _timer.Stop(); _fsw.Dispose(); }
}
