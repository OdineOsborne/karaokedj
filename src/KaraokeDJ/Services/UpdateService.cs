using Velopack;
using Velopack.Sources;

namespace KaraokeDJ.Services;

/// <summary>Aggiornamenti automatici da GitHub Releases tramite Velopack.</summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/OdineOsborne/karaokedj";

    private readonly UpdateManager _mgr = new(new GithubSource(RepoUrl, null, false));
    private UpdateInfo? _pending;

    public bool IsInstalled => _mgr.IsInstalled;
    public string CurrentVersion => _mgr.IsInstalled ? _mgr.CurrentVersion?.ToString() ?? "?" : (typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "dev");
    public string? AvailableVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>Controlla se c'è una versione più nuova. Ritorna la versione disponibile o null.</summary>
    public async Task<string?> CheckAsync()
    {
        if (!_mgr.IsInstalled) return null; // in debug / cartella portabile non aggiorniamo
        _pending = await _mgr.CheckForUpdatesAsync();
        return AvailableVersion;
    }

    /// <summary>Scarica e installa l'aggiornamento, poi riavvia l'app.</summary>
    public async Task DownloadAndApplyAsync(IProgress<int>? progress)
    {
        if (_pending == null) return;
        await _mgr.DownloadUpdatesAsync(_pending, p => progress?.Report(p));
        _mgr.ApplyUpdatesAndRestart(_pending);
    }
}
