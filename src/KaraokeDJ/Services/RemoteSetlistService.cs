using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace KaraokeDJ.Services;

/// <summary>Comando arrivato dal telefono.</summary>
public sealed class RemoteCommand
{
    [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("index")] public int? Index { get; set; }
    [JsonPropertyName("to")] public int? To { get; set; }
    [JsonPropertyName("singer")] public string? Singer { get; set; }
}

/// <summary>
/// Scaletta remota via cloud: l'app pubblica lo stato (deck, coda, suggeriti) e l'indice della libreria,
/// il telefono (pagina /scaletta?s=…) li legge con il PIN e manda comandi che vengono applicati qui.
/// </summary>
public sealed class RemoteSetlistService : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _baseUrl;
    private readonly Func<object> _stateProvider;
    private readonly Func<object> _libraryProvider;
    private readonly Action<RemoteCommand> _apply;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _lastStateJson = "";
    private bool _busy;
    private int _tick;

    public string SessionId { get; } = NewId(10);
    public string Token { get; } = NewId(24);
    public string Pin { get; private set; } = Random.Shared.Next(1000, 9999).ToString();
    public int LibraryVersion { get; private set; }
    public bool IsRunning => _timer.IsEnabled;
    public string Status { get; private set; } = "";
    public event Action? StatusChanged;
    public string PageUrl => $"{_baseUrl}/scaletta?s={SessionId}";

    public RemoteSetlistService(string baseUrl, Func<object> stateProvider, Func<object> libraryProvider, Action<RemoteCommand> apply)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _stateProvider = stateProvider; _libraryProvider = libraryProvider; _apply = apply;
        _timer.Tick += async (_, _) => await TickAsync();
    }

    private static string NewId(int len)
    {
        const string abc = "abcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = RandomNumberGenerator.GetBytes(len);
        return new string(bytes.Select(b => abc[b % abc.Length]).ToArray());
    }

    public void Start()
    {
        _lastStateJson = ""; _libraryDirty = true;
        _timer.Start();
        _ = TickAsync();
    }

    public void Stop() { _timer.Stop(); SetStatus("Fermo"); }

    public void NewPin() { Pin = Random.Shared.Next(1000, 9999).ToString(); _lastStateJson = ""; }

    private bool _libraryDirty = true;
    /// <summary>Da chiamare quando la libreria cambia: l'indice viene ripubblicato al prossimo giro.</summary>
    public void LibraryChanged() => _libraryDirty = true;

    private void SetStatus(string s) { Status = s; StatusChanged?.Invoke(); }

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _tick++;
            // 1) stato (solo se cambiato, e comunque ogni ~20 s per tenere viva la sessione)
            var state = _stateProvider();
            var json = JsonSerializer.Serialize(state);
            if (json != _lastStateJson || _tick % 10 == 0)
            {
                var r = await Http.PostAsJsonAsync($"{_baseUrl}/api/setlist?op=push", new { session = SessionId, token = Token, pin = Pin, state });
                if (!r.IsSuccessStatusCode) { SetStatus("Cloud: " + (int)r.StatusCode); return; }
                _lastStateJson = json;
            }
            // 2) indice libreria (quando cambia)
            if (_libraryDirty)
            {
                _libraryDirty = false;
                var r = await Http.PostAsJsonAsync($"{_baseUrl}/api/setlist?op=lib", new { session = SessionId, token = Token, tracks = _libraryProvider() });
                if (r.IsSuccessStatusCode) { LibraryVersion++; _lastStateJson = ""; } else _libraryDirty = true;
            }
            // 3) comandi dal telefono
            var pr = await Http.PostAsJsonAsync($"{_baseUrl}/api/setlist?op=poll", new { session = SessionId, token = Token });
            if (pr.IsSuccessStatusCode)
            {
                var doc = await pr.Content.ReadFromJsonAsync<JsonElement>();
                if (doc.TryGetProperty("commands", out var arr))
                    foreach (var c in arr.EnumerateArray())
                    {
                        var cmd = c.Deserialize<RemoteCommand>();
                        if (cmd != null) { try { _apply(cmd); } catch { } _lastStateJson = ""; }
                    }
            }
            SetStatus("Collegato · " + DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (Exception ex) { SetStatus("Errore: " + ex.Message); }
        finally { _busy = false; }
    }

    public void Dispose() { _timer.Stop(); }
}
