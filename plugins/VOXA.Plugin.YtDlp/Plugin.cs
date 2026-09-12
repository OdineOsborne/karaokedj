using VOXA.Plugins;

namespace VOXA.Plugin.YtDlp;

/// <summary>Plugin yt-dlp: YouTube (video, playlist), link Spotify (risolti in ricerca YouTube) e ricerca libera.</summary>
public sealed class YtDlpPlugin : IVoxaPlugin, IImportSource
{
    private readonly DownloadService _svc = new();
    private IPluginHost? _host;

    public string Id => "ytdlp";
    public string Name => "yt-dlp (YouTube / Spotify)";
    public string Version => "1.0.0";
    public string Description => "Importa da YouTube con yt-dlp; i link Spotify vengono risolti in artista/titolo e cercati su YouTube. Uso a responsabilità dell'utente: rispetta i termini dei servizi e il diritto d'autore.";
    public string InputHint => "Link YouTube (video/playlist), link Spotify (brano/playlist/album) o 'artista titolo'";
    public bool SupportsVideo => true;

    public void Initialize(IPluginHost host)
    {
        _host = host;
        AppPaths.ToolsDir = host.ToolsDir;
        AppPaths.Root = host.DataDir;
        _svc.TrackExists = host.TrackExists;
    }

    public IEnumerable<IImportSource> ImportSources => new[] { this };
    public string? MaintenanceLabel => "Aggiorna yt-dlp";
    public Func<IProgress<ImportProgress>?, CancellationToken, Task>? Maintenance =>
        (p, ct) => _svc.UpdateYtDlpAsync(Wrap(p), ct);

    public bool CanHandle(string input) => true; // URL o testo libero

    public async Task<IReadOnlyList<string>> ImportAsync(string input, bool video, string destFolder, IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        AppPaths.DownloadsDir = destFolder;
        return await _svc.DownloadAsync(input, video, Wrap(progress), ct);
    }

    private static IProgress<DownloadStatus>? Wrap(IProgress<ImportProgress>? p) =>
        p == null ? null : new Progress<DownloadStatus>(s => p.Report(new ImportProgress(s.Message, s.Percent)));
}
