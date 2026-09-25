using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaraokeDJ.Services;

/// <summary>Mappatura di fabbrica di una console (Assets/Controllers/*.json o quelle dell'utente): riconosciuta dal nome della porta MIDI.</summary>
public sealed class ControllerPreset
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Sottostringhe del nome della porta MIDI di Windows (es. "DDJ-FLX4").</summary>
    [JsonPropertyName("match")] public List<string> Match { get; set; } = new();
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("mappings")] public List<MidiMapping> Mappings { get; set; } = new();
    /// <summary>Colori dei LED di questa console (facoltativo: senza, quelli della Instinct P8).</summary>
    [JsonPropertyName("leds")] public LedPalette? Leds { get; set; }
    /// <summary>true se viene dalla cartella dell'utente (importata o esportata da lui), non dall'app.</summary>
    [JsonIgnore] public bool IsUser { get; set; }

    /// <summary>Comandi senza i quali una console non si usa in serata.</summary>
    private static readonly (string Action, string Label)[] Essential =
    {
        ("a.play", "play"), ("a.cue", "cue"), ("a.fader", "fader"), ("crossfader", "crossfader"),
        ("a.jog", "piatti"), ("a.eqlow", "EQ"), ("a.hotcue1", "hot cue"), ("browse", "browse"),
    };

    /// <summary>Cosa copre questo preset e cosa gli manca: si legge prima di sceglierlo, senza provare la console.</summary>
    [JsonIgnore]
    public (string Has, string Missing) Coverage
    {
        get
        {
            var acts = Mappings.Select(m => m.Action).ToHashSet();
            var has = Essential.Where(e => acts.Contains(e.Action)).Select(e => e.Label);
            var missing = Essential.Where(e => !acts.Contains(e.Action)).Select(e => e.Label);
            return (string.Join(", ", has), string.Join(", ", missing));
        }
    }

    /// <summary>Preset senza play o senza cue: si puo usare, ma il DJ deve saperlo prima.</summary>
    [JsonIgnore] public bool Incomplete => Mappings.All(m => m.Action != "a.play") || Mappings.All(m => m.Action != "a.cue");

    /// <summary>Da dove vengono i numeri, in una riga: documento del produttore, console vera, o mappatura della comunita.</summary>
    [JsonIgnore]
    public string Provenance =>
        System.Text.RegularExpressions.Regex.IsMatch(Source ?? "", "registratore|console vera|misurat", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "provato su console vera"
        : System.Text.RegularExpressions.Regex.IsMatch(Source ?? "", "ufficiale|MIDI Mapping|Command List|manuale", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "dal documento del produttore"
        : IsUser ? "tua" : "da mappatura della comunita, non provata";
}

/// <summary>
/// Plug &amp; play delle console: all'avvio e a ogni collegamento la porta MIDI viene confrontata con i preset e,
/// se combacia, la console è pronta senza configurare niente. Le mappature imparate dall'utente restano sopra al preset.
/// I preset dell'utente (%AppData%\KaraokeDJ\controllers) vincono su quelli dell'app con lo stesso id: così chi corregge
/// una console può tenersi la sua versione (e mandarcela).
/// </summary>
public static class ControllerPresets
{
    private static List<ControllerPreset>? _all;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Assets", "Controllers");
    public static string UserDir => Path.Combine(AppPaths.Root, "controllers");

    public static IReadOnlyList<ControllerPreset> All => _all ??= Load();

    public static void Reload() => _all = null;

    private static List<ControllerPreset> Load()
    {
        var list = new List<ControllerPreset>();
        foreach (var (dir, user) in new[] { (Dir, false), (UserDir, true) })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.json").OrderBy(x => x))
                {
                    try
                    {
                        var p = JsonSerializer.Deserialize<ControllerPreset>(File.ReadAllText(f), Json);
                        if (p == null || p.Mappings.Count == 0) continue;
                        if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Path.GetFileNameWithoutExtension(f);
                        p.IsUser = user;
                        list.RemoveAll(x => x.Id == p.Id); // l'utente sovrascrive l'app
                        list.Add(p);
                    }
                    catch { }
                }
            }
            catch { }
        }
        return list;
    }

    /// <summary>Il preset che riconosce questa porta MIDI (il match più lungo vince; a parità vince quello dell'utente), o null.</summary>
    public static ControllerPreset? Find(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;
        ControllerPreset? best = null; int bestLen = 0;
        foreach (var p in All)
            foreach (var m in p.Match)
                if (m.Length > 0 && deviceName.Contains(m, StringComparison.OrdinalIgnoreCase) && (m.Length > bestLen || (m.Length == bestLen && p.IsUser && best?.IsUser != true))) { best = p; bestLen = m.Length; }
        return best;
    }

    public static ControllerPreset? ById(string? id) => id == null ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>Salva nella cartella dell'utente (sovrascrive lo stesso id) e ricarica.</summary>
    public static string SaveUser(ControllerPreset p)
    {
        Directory.CreateDirectory(UserDir);
        var safe = string.Concat((string.IsNullOrWhiteSpace(p.Id) ? p.Name : p.Id).ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        if (safe.Length == 0) safe = "console";
        p.Id = safe;
        var path = Path.Combine(UserDir, safe + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(p, Json));
        Reload();
        return path;
    }

    public static bool DeleteUser(string id)
    {
        var path = Path.Combine(UserDir, id + ".json");
        if (!File.Exists(path)) return false;
        File.Delete(path); Reload(); return true;
    }

    /// <summary>
    /// Importa una mappatura da file: JSON Mixfonia, XML Mixxx (<c>*.midi.xml</c>) o djay (<c>*.djayMidiMapping</c>).
    /// Restituisce il preset (non ancora salvato) e un rapporto; lancia se il file non è riconosciuto.
    /// </summary>
    public static (ControllerPreset Preset, string Report) Import(string path)
    {
        var text = File.ReadAllText(path);
        var name = Path.GetFileNameWithoutExtension(path);
        if (text.TrimStart().StartsWith("{"))
        {
            var p = JsonSerializer.Deserialize<ControllerPreset>(text, Json) ?? throw new InvalidDataException("JSON non valido");
            if (p.Mappings.Count == 0) throw new InvalidDataException("Il file non contiene mappature");
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = name;
            if (p.Match.Count == 0) p.Match.Add(p.Name);
            return (p, $"{p.Mappings.Count} controlli");
        }
        if (text.Contains("<MixxxMIDIPreset") || text.Contains("<MixxxControllerPreset"))
        {
            var (mappings, total, controllerId, author) = MappingImporters.FromMixxx(text);
            if (mappings.Count == 0) throw new InvalidDataException("Nessun controllo riconosciuto (mappatura tutta a script?)");
            var pretty = name.Replace(".midi", "").Replace('_', ' ').Replace('-', ' ');
            var match = new List<string>();
            if (!string.IsNullOrWhiteSpace(controllerId) && controllerId.Length >= 4) match.Add(controllerId.Trim());
            match.Add(pretty);
            return (new ControllerPreset { Id = pretty, Name = pretty, Match = match, Source = $"Mixxx mapping \"{name}\" ({author}), GPL — usati solo i numeri MIDI; importata dall'utente", Mappings = mappings },
                    $"{mappings.Count} controlli riconosciuti su {total}");
        }
        if (text.Contains("<plist") && text.Contains("midiMessageType"))
        {
            var (mappings, total) = MappingImporters.FromDjay(text);
            if (mappings.Count == 0) throw new InvalidDataException("Nessun controllo riconosciuto");
            return (new ControllerPreset { Id = name, Name = name, Match = new List<string> { name }, Source = $"mappatura djay \"{name}\" — usati solo i numeri MIDI; importata dall'utente", Mappings = mappings },
                    $"{mappings.Count} controlli riconosciuti su {total}");
        }
        throw new InvalidDataException("Formato non riconosciuto: accetto JSON Mixfonia, XML Mixxx (*.midi.xml) e djay (*.djayMidiMapping)");
    }

    /// <summary>Il preset dell'app o dell'utente più le personalizzazioni, pronto da esportare/condividere.</summary>
    public static ControllerPreset Export(string deviceName, ControllerPreset? basePreset, IEnumerable<MidiMapping> current)
    {
        var name = basePreset?.Name ?? deviceName;
        return new ControllerPreset
        {
            Id = (basePreset?.Id ?? deviceName) + (basePreset != null ? "-mia" : ""),
            Name = name + (basePreset != null ? " (mia versione)" : ""),
            Match = basePreset?.Match.ToList() ?? new List<string> { deviceName },
            Source = (basePreset?.Source ?? "mappatura imparata a mano") + "; esportata da Mixfonia il " + DateTime.Now.ToString("yyyy-MM-dd"),
            Mappings = current.ToList(),
        };
    }
}
