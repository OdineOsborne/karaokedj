using System.Windows.Input;

namespace KaraokeDJ.Services;

/// <summary>Un'azione controllabile da tastiera o MIDI.</summary>
/// <param name="Id">es. "a.play", "next"</param>
/// <param name="Label">testo mostrato</param>
/// <param name="IsContinuous">CC continuo (fader/manopola)</param>
/// <param name="IsHold">azione "tieni premuto": parte alla pressione e finisce al rilascio</param>
public sealed record AppAction(string Id, string Label, bool IsContinuous, bool IsHold = false);

/// <summary>Catalogo di tutte le azioni: globali + quelle di ciascun deck (prefisso "a." / "b.").</summary>
public static class AppActions
{
    private static readonly (string Id, string Label, bool Cont, bool Hold)[] DeckTemplate =
    {
        ("play", "Play/Pausa", false, false), ("stop", "Stop", false, false), ("cue", "CUE", false, false), ("playcue", "Parti dal cue", false, false),
        ("tap", "Tap tempo", false, false), ("sync", "SYNC (aggancia BPM)", false, false),
        ("back10", "Indietro 10 s", false, false), ("fwd10", "Avanti 10 s", false, false), ("eject", "Scarica il deck", false, false),
        ("volume", "Gain", true, false), ("pan", "Pan", true, false), ("panreset", "Pan al centro", false, false), ("tempo", "Tempo", true, false), ("temporeset", "Tempo 0 %", false, false),
        ("keyup", "Tonalità +", false, false), ("keydown", "Tonalità −", false, false), ("keyreset", "Tonalità 0", false, false), ("keylock", "Key lock on/off", false, false),
        ("loop1", "Loop 1 battuta", false, false), ("loop2", "Loop 2 battute", false, false), ("loop4", "Loop 4 battute", false, false), ("loop8", "Loop 8 battute", false, false),
        ("loophalf", "Loop ÷2", false, false), ("loopdouble", "Loop ×2", false, false), ("loopexit", "Esci dal loop", false, false),
        ("filter", "FILTER on/off", false, false), ("filtervalue", "Filtro (manopola)", true, false), ("filterreset", "Filtro al centro", false, false),
        ("echo", "ECHO on/off", false, false), ("pingpong", "ECHO ping-pong on/off", false, false), ("echoout", "ECHO OUT", false, false), ("reverb", "REVERB on/off", false, false), ("flanger", "FLANGER on/off", false, false),
        ("phaser", "PHASER on/off", false, false), ("crush", "CRUSH on/off", false, false), ("gate", "GATE on/off", false, false), ("fxreset", "Spegni tutti gli effetti", false, false),
        ("vocaloff", "VOCE OFF (centro)", false, false), ("aivocal", "VOCE AI (Demucs)", false, false), ("stems", "STEMS on/off", false, false),
        ("stemvocals", "Stem voce (livello)", true, false), ("stemdrums", "Stem batteria (livello)", true, false), ("stembass", "Stem basso (livello)", true, false), ("stemother", "Stem altro (livello)", true, false),
        ("eqlow", "EQ bassi", true, false), ("eqmid", "EQ medi", true, false), ("eqhigh", "EQ alti", true, false),
        ("killlow", "Kill bassi", false, false), ("killmid", "Kill medi", false, false), ("killhigh", "Kill alti", false, false), ("eqreset", "EQ piatto", false, false),
        ("brake", "BRAKE", false, false), ("backspin", "BACKSPIN", false, false), ("spinfwd", "Girata avanti", false, false), ("spinback", "Girata indietro", false, false),
        ("rev", "REV (tieni premuto)", false, true), ("slow", "SLOW (tieni premuto)", false, true), ("fwd", "▶▶ (tieni premuto)", false, true), ("back", "◀◀ (tieni premuto)", false, true),
        ("jog", "Jog (manopola infinita)", true, false),
    };

    private static readonly AppAction[] Global =
    {
        new("crossfader", "Crossfader", true), new("master", "Volume master", true),
        new("next", "Prossimo in coda / mix now", false), new("fadeA", "Sfuma verso A", false), new("fadeB", "Sfuma verso B", false),
        new("automix", "Auto-mix on/off", false), new("projector", "Proiettore on/off", false), new("monitor", "Monitor proiettore on/off", false),
        new("search", "Vai alla ricerca", false), new("addqueue", "Brano selezionato in coda", false), new("queuetop", "Brano selezionato in cima alla coda", false),
        new("mixnow", "Mixa ora il brano selezionato", false),
        new("rhythm.play", "Ritmi: avvia/ferma", false), new("rhythm.tap", "Ritmi: tap tempo", false), new("rhythm.resync", "Ritmi: riparti dall'1", false), new("rhythm.volume", "Ritmi: volume", true),
        new("padstop", "Stop tutti i pad", false),
        new("pad1", "Pad 1", false), new("pad2", "Pad 2", false), new("pad3", "Pad 3", false), new("pad4", "Pad 4", false),
        new("pad5", "Pad 5", false), new("pad6", "Pad 6", false), new("pad7", "Pad 7", false), new("pad8", "Pad 8", false),
        new("pad9", "Pad 9", false), new("pad10", "Pad 10", false), new("pad11", "Pad 11", false), new("pad12", "Pad 12", false),
    };

    public static readonly IReadOnlyList<AppAction> All = Build();

    private static AppAction[] Build()
    {
        var list = new List<AppAction>(Global);
        foreach (var deck in new[] { "a", "b" })
            foreach (var (id, label, cont, hold) in DeckTemplate)
                list.Add(new AppAction($"{deck}.{id}", $"Deck {deck.ToUpperInvariant()} – {label}", cont, hold));
        return list.ToArray();
    }

    public static AppAction? Find(string id) => All.FirstOrDefault(a => a.Id == id);
    public static string LabelOf(string id) => Find(id)?.Label ?? id;

    /// <summary>Scorciatoie di tastiera predefinite (gesto → azione).</summary>
    public static readonly (string Gesture, string Action)[] DefaultKeys =
    {
        ("Ctrl+D1", "a.play"), ("Ctrl+D2", "b.play"), ("Ctrl+P", "projector"), ("Ctrl+N", "next"), ("Ctrl+F", "search"),
        ("Ctrl+Return", "addqueue"), ("Ctrl+Space", "padstop"),
        ("F1", "pad1"), ("F2", "pad2"), ("F3", "pad3"), ("F4", "pad4"), ("F5", "pad5"), ("F6", "pad6"),
        ("F7", "pad7"), ("F8", "pad8"), ("F9", "pad9"), ("F10", "pad10"), ("F11", "pad11"), ("F12", "pad12"),
        ("Q", "a.cue"), ("W", "a.play"), ("O", "b.cue"), ("P", "b.play"),
    };
}

/// <summary>Scorciatoia da tastiera salvata.</summary>
public sealed class KeyMapping
{
    public string Action { get; set; } = "";
    /// <summary>es. "Ctrl+Shift+K", "F5", "Space"</summary>
    public string Gesture { get; set; } = "";
}

/// <summary>Mappa gesti di tastiera → azioni, con lettura/scrittura testuale dei gesti.</summary>
public sealed class KeyboardService
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Load(IEnumerable<KeyMapping> mappings, bool useDefaultsIfEmpty)
    {
        _map.Clear();
        foreach (var m in mappings) if (!string.IsNullOrEmpty(m.Gesture) && !string.IsNullOrEmpty(m.Action)) _map[m.Gesture] = m.Action;
        if (_map.Count == 0 && useDefaultsIfEmpty) foreach (var (g, a) in AppActions.DefaultKeys) _map[g] = a;
    }

    public List<KeyMapping> Export() => _map.Select(kv => new KeyMapping { Gesture = kv.Key, Action = kv.Value }).ToList();

    public string? GestureFor(string action) => _map.FirstOrDefault(kv => kv.Value == action).Key;
    public string? ActionFor(string gesture) => _map.TryGetValue(gesture, out var a) ? a : null;

    public void Set(string action, string gesture)
    {
        foreach (var k in _map.Where(kv => kv.Value == action).Select(kv => kv.Key).ToList()) _map.Remove(k);
        _map[gesture] = action;
    }

    public void Clear(string action)
    {
        foreach (var k in _map.Where(kv => kv.Value == action).Select(kv => kv.Key).ToList()) _map.Remove(k);
    }

    /// <summary>Testo del gesto da un evento tastiera ("Ctrl+Shift+K"); null per i soli modificatori.</summary>
    public static string? GestureText(Key key, ModifierKeys mods)
    {
        if (key == Key.System) key = Key.LeftAlt;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Versione leggibile ("Ctrl+1", "Spazio", "Invio").</summary>
    public static string Pretty(string? gesture)
    {
        if (string.IsNullOrEmpty(gesture)) return "—";
        return gesture.Replace("+D1", "+1").Replace("+D2", "+2").Replace("+D3", "+3").Replace("+D4", "+4").Replace("+D5", "+5")
            .Replace("+D6", "+6").Replace("+D7", "+7").Replace("+D8", "+8").Replace("+D9", "+9").Replace("+D0", "+0")
            .Replace("Return", "Invio").Replace("Space", "Spazio").Replace("OemPlus", "+").Replace("OemMinus", "−")
            .Replace("OemComma", ",").Replace("OemPeriod", ".").Replace("Escape", "Esc")
            .Replace("Left", "←").Replace("Right", "→").Replace("Up", "↑").Replace("Down", "↓")
            .Replace("PageUp", "PagSu").Replace("PageDown", "PagGiù");
    }
}
