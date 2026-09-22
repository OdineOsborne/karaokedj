using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>Un passaggio fra due brani come è andato davvero in serata.</summary>
public sealed class FlowEvent
{
    public string FromArtist { get; set; } = "";
    public string FromTitle { get; set; } = "";
    public string ToArtist { get; set; } = "";
    public string ToTitle { get; set; } = "";
    /// <summary>played = messo dal DJ · accepted = suggerito e accettato · rejected = suggerito e scartato (👎).</summary>
    public string Kind { get; set; } = "played";
    public double FromBpm { get; set; }
    public double ToBpm { get; set; }
    public string FromKey { get; set; } = "";
    public string ToKey { get; set; } = "";
    /// <summary>true se il brano è stato fatto suonare fino alla fine (in una serata è il vero voto del pubblico).</summary>
    public bool Completed { get; set; }
    public string Day { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
}

/// <summary>
/// Statistiche d'uso **facoltative** (si chiedono una volta, si possono spegnere quando si vuole):
/// servono a imparare dalle serate vere quali passaggi funzionano e quali no, e a migliorare i suggerimenti per tutti.
///
/// Cosa esce dal PC: solo artista/titolo dei due brani, BPM e tonalità, se il brano è arrivato in fondo, il giorno,
/// la versione dell'app e un codice casuale creato qui (non è la licenza, non è l'ID della macchina, non è l'email).
/// Cosa NON esce mai: nomi dei cantanti e delle persone, percorsi dei file, contenuto della libreria, dediche,
/// email, dati della licenza, posizione. I dati vengono aggregati e usati solo in forma statistica.
/// </summary>
public static class UsageStats
{
    private static readonly List<FlowEvent> _pending = new();
    private static readonly object _gate = new();
    private static DateTime _lastSend = DateTime.MinValue;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string File => Path.Combine(AppPaths.Root, "usage-pending.json");
    public static string Endpoint { get; set; } = "https://voxa-cloud.vercel.app/api/learn";

    /// <summary>Codice casuale per non contare due volte la stessa installazione: non è legato alla licenza né alla macchina.</summary>
    public static string InstallId(AppSettings s)
    {
        if (string.IsNullOrEmpty(s.UsageAnonId)) s.UsageAnonId = Guid.NewGuid().ToString("N");
        return s.UsageAnonId;
    }

    /// <summary>Registra un passaggio. Se le statistiche sono spente non fa assolutamente niente.</summary>
    public static void Record(AppSettings settings, Track? from, Track to, string kind, bool completed = false)
    {
        if (!settings.UsageStatsOptIn) return;
        if (to.IsKaraoke && string.IsNullOrWhiteSpace(to.Artist)) return;   // basi senza metadati: non dicono niente
        var e = new FlowEvent
        {
            FromArtist = Trim(from?.Artist), FromTitle = Trim(from?.Title),
            ToArtist = Trim(to.Artist), ToTitle = Trim(to.Title),
            Kind = kind, Completed = completed,
            FromBpm = Math.Round(from?.Bpm ?? 0), ToBpm = Math.Round(to.Bpm),
            FromKey = from?.Key ?? "", ToKey = to.Key,
        };
        lock (_gate) { _pending.Add(e); if (_pending.Count > 500) _pending.RemoveAt(0); }
    }

    private static string Trim(string? s) => (s ?? "").Trim() is { Length: > 120 } l ? l[..120] : (s ?? "").Trim();

    /// <summary>Quanti eventi sono in attesa di essere mandati (mostrato nelle impostazioni, per trasparenza).</summary>
    public static int PendingCount { get { lock (_gate) return _pending.Count; } }

    /// <summary>Gli eventi in attesa, per mostrarli all'utente prima dell'invio (trasparenza: "guarda cosa mando").</summary>
    public static List<FlowEvent> Peek() { lock (_gate) return _pending.ToList(); }

    public static void Clear() { lock (_gate) _pending.Clear(); try { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); } catch { } }

    public static void Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;
            var list = JsonSerializer.Deserialize<List<FlowEvent>>(System.IO.File.ReadAllText(File));
            if (list != null) lock (_gate) _pending.AddRange(list);
        }
        catch { }
    }

    public static void Save()
    {
        try
        {
            List<FlowEvent> copy; lock (_gate) copy = _pending.ToList();
            if (copy.Count == 0) { if (System.IO.File.Exists(File)) System.IO.File.Delete(File); return; }
            Directory.CreateDirectory(AppPaths.Root);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(copy));
        }
        catch { }
    }

    /// <summary>
    /// Manda gli eventi accumulati (a fine serata o ogni 6 ore). Se non c'è rete restano sul PC e si riproverà.
    /// </summary>
    public static async Task<string> SendAsync(AppSettings settings, bool force = false)
    {
        if (!settings.UsageStatsOptIn) return "statistiche spente";
        List<FlowEvent> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return "niente da mandare";
            if (!force && (DateTime.UtcNow - _lastSend).TotalHours < 6) return "già mandato di recente";
            batch = _pending.ToList();
        }
        try
        {
            var body = new { install = InstallId(settings), version = Version(), events = batch };
            var res = await Http.PostAsJsonAsync(Endpoint, body);
            if (!res.IsSuccessStatusCode) return "non riuscito: " + (int)res.StatusCode;
            lock (_gate) { _pending.RemoveRange(0, Math.Min(batch.Count, _pending.Count)); _lastSend = DateTime.UtcNow; }
            Save();
            return $"mandati {batch.Count} passaggi";
        }
        catch (Exception ex) { return "non riuscito: " + ex.Message; }
    }

    private static string Version()
    {
        try { return typeof(UsageStats).Assembly.GetName().Version?.ToString(3) ?? "?"; } catch { return "?"; }
    }

    // ------------------------------------------------------------------ quello che si impara

    /// <summary>Passaggi che funzionano, imparati dalle serate di tutti (scaricati dal cloud, salvati in locale).</summary>
    public static string ModelFile => Path.Combine(AppPaths.Root, "flow-model.json");
    private static Dictionary<string, double>? _model;

    public static int ModelSize { get { LoadModel(); return _model!.Count; } }

    private static void LoadModel()
    {
        if (_model != null) return;
        _model = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.IO.File.Exists(ModelFile))
                _model = JsonSerializer.Deserialize<Dictionary<string, double>>(System.IO.File.ReadAllText(ModelFile)) ?? _model;
        }
        catch { }
    }

    /// <summary>Chiave di un passaggio, con gli artisti normalizzati (i titoli non servono per generalizzare).</summary>
    public static string Key(Track from, Track to) =>
        SearchUtil.NormalizeForCompare(from.Artist) + ">" + SearchUtil.NormalizeForCompare(to.Artist);

    /// <summary>
    /// Quanto è "collaudato" questo passaggio secondo le serate degli altri: 1 = neutro, fino a 1,25 se funziona spesso,
    /// 0,8 se di solito viene scartato. Senza dati resta 1: il motore decide da solo.
    /// </summary>
    public static double Bonus(Track from, Track to)
    {
        LoadModel();
        if (_model!.Count == 0) return 1;
        return _model.TryGetValue(Key(from, to), out var v) ? Math.Clamp(v, 0.8, 1.25) : 1;
    }

    /// <summary>Scarica il modello aggregato (nessun dato personale: solo coppie di artisti con un punteggio).</summary>
    public static async Task<string> FetchModelAsync()
    {
        try
        {
            var txt = await Http.GetStringAsync(Endpoint + "?model=1");
            var m = JsonSerializer.Deserialize<Dictionary<string, double>>(txt);
            if (m == null || m.Count == 0) return "nessun dato ancora";
            Directory.CreateDirectory(AppPaths.Root);
            System.IO.File.WriteAllText(ModelFile, txt);
            _model = new Dictionary<string, double>(m, StringComparer.OrdinalIgnoreCase);
            return $"imparati {m.Count} passaggi dalle serate";
        }
        catch (Exception ex) { return "non riuscito: " + ex.Message; }
    }
}
