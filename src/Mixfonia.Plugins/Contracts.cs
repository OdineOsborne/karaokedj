namespace Mixfonia.Plugins;

/// <summary>Avanzamento di un'operazione lunga (messaggio, percentuale 0-100 oppure -1 = indeterminata).</summary>
public sealed record ImportProgress(string Message, double Percent = -1);

/// <summary>
/// Una sorgente da cui importare brani in libreria (servizio in streaming con download consentito, archivio, ecc.).
/// L'app mostra un campo di testo: la sorgente decide cosa farne (URL, ricerca "artista titolo"…).
/// </summary>
public interface IImportSource
{
    string Id { get; }
    string Name { get; }
    /// <summary>Una riga: cosa è e a quali condizioni (es. "brani Creative Commons scaricabili").</summary>
    string Description { get; }
    /// <summary>Suggerimento nel campo di testo (es. "artista titolo" oppure "link…").</summary>
    string InputHint { get; }
    bool SupportsVideo { get; }
    /// <summary>true se questa sorgente sa gestire l'input (es. riconosce l'URL). Le sorgenti "di ricerca" accettano qualunque testo.</summary>
    bool CanHandle(string input);
    /// <summary>Importa in <paramref name="destFolder"/> e ritorna i file creati.</summary>
    Task<IReadOnlyList<string>> ImportAsync(string input, bool video, string destFolder, IProgress<ImportProgress>? progress, CancellationToken ct);
}

/// <summary>Servizi che l'app mette a disposizione del plugin.</summary>
public interface IPluginHost
{
    /// <summary>Cartella dati privata del plugin (%AppData%\KaraokeDJ\plugins\&lt;id&gt;).</summary>
    string DataDir { get; }
    /// <summary>Cartella strumenti condivisa (ffmpeg, ecc.).</summary>
    string ToolsDir { get; }
    /// <summary>(artista, titolo) → true se il brano è già in libreria: per saltare i doppioni.</summary>
    Func<string, string, bool> TrackExists { get; }
    void SetStatus(string text);
}

/// <summary>Un plugin Mixfonia: una DLL in %AppData%\KaraokeDJ\plugins\&lt;cartella&gt;\ con una classe che implementa questa interfaccia.</summary>
public interface IMixfoniaPlugin
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    /// <summary>Riga di descrizione mostrata in Impostazioni → Plugin.</summary>
    string Description { get; }
    void Initialize(IPluginHost host);
    IEnumerable<IImportSource> ImportSources { get; }
    /// <summary>Operazione di manutenzione facoltativa (es. "aggiorna strumenti"); null se non prevista.</summary>
    Func<IProgress<ImportProgress>?, CancellationToken, Task>? Maintenance { get; }
    string? MaintenanceLabel { get; }
}
