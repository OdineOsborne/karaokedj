using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaraokeDJ.Services;

/// <summary>Mappatura di fabbrica di una console (Assets/Controllers/*.json): riconosciuta dal nome della porta MIDI.</summary>
public sealed class ControllerPreset
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Sottostringhe del nome della porta MIDI di Windows (es. "DDJ-FLX4").</summary>
    [JsonPropertyName("match")] public List<string> Match { get; set; } = new();
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("mappings")] public List<MidiMapping> Mappings { get; set; } = new();
}

/// <summary>
/// Plug &amp; play delle console: all'avvio e a ogni collegamento la porta MIDI viene confrontata con i preset e,
/// se combacia, la console è pronta senza configurare niente. Le mappature imparate dall'utente restano sopra al preset.
/// </summary>
public static class ControllerPresets
{
    private static List<ControllerPreset>? _all;

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Assets", "Controllers");

    public static IReadOnlyList<ControllerPreset> All
    {
        get
        {
            if (_all != null) return _all;
            var list = new List<ControllerPreset>();
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var f in Directory.EnumerateFiles(Dir, "*.json").OrderBy(x => x))
                    {
                        try
                        {
                            var p = JsonSerializer.Deserialize<ControllerPreset>(File.ReadAllText(f));
                            if (p != null && p.Mappings.Count > 0) list.Add(p);
                        }
                        catch { }
                    }
            }
            catch { }
            return _all = list;
        }
    }

    /// <summary>Il preset che riconosce questa porta MIDI (il match più lungo vince), o null.</summary>
    public static ControllerPreset? Find(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;
        ControllerPreset? best = null; int bestLen = 0;
        foreach (var p in All)
            foreach (var m in p.Match)
                if (m.Length > bestLen && deviceName.Contains(m, StringComparison.OrdinalIgnoreCase)) { best = p; bestLen = m.Length; }
        return best;
    }

    public static ControllerPreset? ById(string? id) => id == null ? null : All.FirstOrDefault(p => p.Id == id);
}
