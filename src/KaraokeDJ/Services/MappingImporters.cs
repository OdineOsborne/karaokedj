using System.Text.RegularExpressions;

namespace KaraokeDJ.Services;

/// <summary>
/// Converte mappature di altri programmi nelle nostre: solo i numeri MIDI dei controlli e l'azione corrispondente.
/// Mixxx (XML): gruppo+chiave, con euristiche sui nomi delle funzioni script; djay (plist): keyPath.
/// Stessa logica di tools/controllers/convert.js e script-map.js, così un utente può importare da solo
/// una qualsiasi delle 200+ console che Mixxx conosce.
/// </summary>
public static class MappingImporters
{
    // ------------------------------------------------------------------ Mixxx

    public static (List<MidiMapping> Mappings, int Total, string ControllerId, string Author) FromMixxx(string xml)
    {
        var controllerId = Regex.Match(xml, "<controller id=\"([^\"]*)\"").Groups[1].Value;
        var author = Regex.Match(xml, "<author>([^<]*)</author>").Groups[1].Value;
        var outList = new List<MidiMapping>(); var seen = new HashSet<MidiKey>(); int total = 0;
        foreach (Match m in Regex.Matches(xml, "<control>([\\s\\S]*?)</control>"))
        {
            var c = m.Groups[1].Value;
            string? g = Regex.Match(c, "<group>([^<]*)</group>").Groups[1].Value.Trim();
            string? k = Regex.Match(c, "<key>([^<]*)</key>").Groups[1].Value.Trim();
            var st = Regex.Match(c, "<status>0x([0-9A-Fa-f]+)</status>").Groups[1].Value;
            var no = Regex.Match(c, "<midino>0x([0-9A-Fa-f]+)</midino>").Groups[1].Value;
            if (g.Length == 0 || k.Length == 0 || st.Length == 0 || no.Length == 0) continue;
            var opts = Regex.Match(c, "<options>([\\s\\S]*?)</options>").Groups[1].Value.ToLowerInvariant();
            if (opts.Contains("fourteen-bit-lsb")) continue;
            int status = Convert.ToInt32(st, 16), number = Convert.ToInt32(no, 16);
            int kind = status >> 4, ch = (status & 0x0f) + 1;
            string type; if (kind == 0x9) type = "note"; else if (kind == 0xB) type = "cc"; else continue;
            total++;
            var desc = Regex.Match(c, "<description>([^<]*)</description>").Groups[1].Value;
            var action = MapAction(g, k) ?? ((k.Contains('.') || opts.Contains("script-binding")) ? ScriptAction(DeckOf(g), k, desc, g, number) : null);
            if (action == null) continue;
            var key = new MidiKey(type, ch, number);
            if (!seen.Add(key)) continue;
            var e = new MidiMapping { Type = type, Channel = ch, Number = number, Action = action };
            if (opts.Contains("<invert/>")) e.Invert = true;
            if (opts.Contains("selectknob") || opts.Contains("spread64") || action.Contains("jog") || action == "browse") e.Relative = true;
            outList.Add(e);
        }
        return (outList, total, controllerId, author);
    }

    private static string? DeckOf(string g)
    {
        var m = Regex.Match(g, "\\[Channel(\\d)\\]");
        return !m.Success ? null : m.Groups[1].Value == "1" ? "a" : m.Groups[1].Value == "2" ? "b" : null;
    }

    private static readonly Dictionary<string, string> DeckKeys = new()
    {
        ["play"] = "play", ["play_indicator"] = "play", ["start_stop"] = "stop", ["cue_default"] = "cue", ["cue_gotoandplay"] = "playcue", ["cue_gotoandstop"] = "cue",
        ["sync_enabled"] = "sync", ["beatsync"] = "sync", ["beatsync_tempo"] = "sync", ["sync_master"] = "sync",
        ["rate"] = "tempo", ["rate_set_default"] = "temporeset", ["volume"] = "fader", ["pregain"] = "volume", ["pfl"] = "cuepfl", ["keylock"] = "keylock",
        ["pitch_up"] = "keyup", ["pitch_down"] = "keydown", ["reset_key"] = "keyreset", ["pitch_adjust_up"] = "keyup", ["pitch_adjust_down"] = "keydown",
        ["filterHigh"] = "eqhigh", ["filterMid"] = "eqmid", ["filterLow"] = "eqlow", ["filterHighKill"] = "killhigh", ["filterMidKill"] = "killmid", ["filterLowKill"] = "killlow",
        ["loop_halve"] = "loophalf", ["loop_double"] = "loopdouble", ["reloop_toggle"] = "loopexit", ["reloop_exit"] = "loopexit", ["loop_exit"] = "loopexit",
        ["beatloop_activate"] = "loop4", ["beatloop_1_toggle"] = "loop1", ["beatloop_1_activate"] = "loop1", ["beatloop_2_toggle"] = "loop2", ["beatloop_2_activate"] = "loop2",
        ["beatloop_4_toggle"] = "loop4", ["beatloop_4_activate"] = "loop4", ["beatloop_8_toggle"] = "loop8", ["beatloop_8_activate"] = "loop8",
        ["beatjump_backward"] = "jumpback4", ["beatjump_forward"] = "jumpfwd4", ["beatjump_4_backward"] = "jumpback4", ["beatjump_4_forward"] = "jumpfwd4",
        ["beatjump_8_backward"] = "jumpback8", ["beatjump_8_forward"] = "jumpfwd8", ["beatjump_1_backward"] = "jumpback4", ["beatjump_1_forward"] = "jumpfwd4",
        ["eject"] = "eject", ["eject_track"] = "eject", ["reverse"] = "rev", ["brake"] = "brake", ["back"] = "back", ["fwd"] = "fwd", ["quantize"] = "quantize",
    };

    private static string? MapAction(string group, string k)
    {
        var d = DeckOf(group);
        var sm = Regex.Match(group, "^\\[Sampler(\\d+)\\]$");
        if (sm.Success)
        {
            int n = int.Parse(sm.Groups[1].Value);
            return n is >= 1 and <= 12 && Regex.IsMatch(k, "^(cue_gotoandplay|start_play|play|LoadSelectedTrackAndPlay)$") ? $"pad{n}" : null;
        }
        if (group == "[Master]" || group == "[Main]")
            return k switch { "crossfader" => "crossfader", "volume" or "gain" => "master", "headMix" => "cuemix", "headVolume" or "headGain" => "cuevolume", _ => null };
        if (group == "[AutoDJ]" && k == "enabled") return "automix";
        if (group == "[Library]" || group == "[Playlist]")
            return k switch { "MoveVertical" or "SelectTrackKnob" => "browse", "SelectPrevTrack" => "browseup", "SelectNextTrack" => "browsedown", "GoToItem" or "LoadSelectedIntoFirstStopped" => "browseload", _ => null };
        var eq = Regex.Match(group, "^\\[EqualizerRack1_\\[Channel(\\d)\\]_Effect1\\]$");
        if (eq.Success)
        {
            var dd = DeckOf(group); if (dd == null) return null;
            return k switch { "parameter1" => $"{dd}.eqlow", "parameter2" => $"{dd}.eqmid", "parameter3" => $"{dd}.eqhigh", "button_parameter1" => $"{dd}.killlow", "button_parameter2" => $"{dd}.killmid", "button_parameter3" => $"{dd}.killhigh", _ => null };
        }
        var qf = Regex.Match(group, "^\\[QuickEffectRack1_\\[Channel(\\d)\\]\\]$");
        if (qf.Success)
        {
            var dd = DeckOf(group); if (dd == null) return null;
            return k switch { "super1" => $"{dd}.filtervalue", "enabled" => $"{dd}.filter", _ => null };
        }
        if (d == null) return null;
        if (k == "LoadSelectedTrack") return d == "a" ? "loadA" : "loadB";
        if (DeckKeys.TryGetValue(k, out var t)) return $"{d}.{t}";
        var hc = Regex.Match(k, "^hotcue_(\\d)_activate$");
        if (hc.Success && int.Parse(hc.Groups[1].Value) <= 8) return $"{d}.hotcue{hc.Groups[1].Value}";
        return null;
    }

    private static bool R(string pattern, string s) => Regex.IsMatch(s, pattern);

    /// <summary>Euristiche per i controlli legati a script (nome funzione + descrizione + gruppo + numero).</summary>
    private static string? ScriptAction(string? d, string key, string desc, string group, int number)
    {
        bool eqGroup = group.Contains("EqualizerRack");
        var k = Regex.Replace(key, "^[A-Za-z0-9_]+\\.", "").ToLowerInvariant();
        var ds = desc.ToLowerInvariant();
        if (R("shift", k) && !R("shifttoggle", k)) return null;
        if (R("lsb", k)) return null;
        if (R("rightdeck\\.", k)) d = "b"; else if (R("leftdeck\\.", k)) d = "a";
        var kk = Regex.Replace(Regex.Replace(k, "^decks?\\[\\d\\]\\.", ""), "^deck[ab]\\.", "");
        var idx = Regex.Match(kk, "\\[(\\d+)\\]");
        var hc = Regex.Match(ds, "hot ?cue ?(\\d)"); if (!hc.Success) hc = Regex.Match(ds, "pad ?(\\d)\\b");
        int N(Match m) => m.Success ? int.Parse(m.Groups[1].Value) : -1;
        if (R("samplerbutton|sampler.*pad|customsampleplayback", k)) { int n = idx.Success ? N(idx) : N(hc); return n is >= 1 and <= 12 ? $"pad{n}" : null; }
        if (R("hotcue|padunit\\.padscontainer\\.pads\\[|padsection\\.padpress|padbuttons", k))
        {
            int n = idx.Success ? N(idx) : N(hc);
            if (R("pads\\[", k) && idx.Success) n = N(idx) + 1;
            return n is >= 1 and <= 8 && d != null ? $"{d}.hotcue{n}" : null;
        }
        if (d != null && R("\\bplay button\\b", ds)) return $"{d}.play";
        if (d != null && R("\\bcue button\\b", ds)) return $"{d}.cue";
        if (d != null && R("\\bsync button\\b", ds)) return $"{d}.sync";
        if (d != null && R("^loop on\\b", ds)) return $"{d}.loop4";
        var lb = Regex.Match(ds, "^loop (\\d+) beat");
        if (d != null && lb.Success) { int n = N(lb); return n switch { 1 => $"{d}.loop1", 2 => $"{d}.loop2", 4 => $"{d}.loop4", 8 or 16 => $"{d}.loop8", _ => null }; }
        var pd = Regex.Match(ds, "^pad (\\d)$");
        if (d != null && pd.Success)
        {
            int n = N(pd), bank = number >> 4;
            if (bank == 0 && n <= 8) return $"{d}.hotcue{n}";
            if (bank == 1) return n switch { 1 => $"{d}.loop1", 2 => $"{d}.loop2", 3 => $"{d}.loop4", 4 => $"{d}.loop8", _ => null };
            if (bank == 7) return n switch { 1 => $"{d}.jumpback4", 2 => $"{d}.jumpfwd4", 3 => $"{d}.jumpback8", 4 => $"{d}.jumpfwd8", _ => null };
            return null;
        }
        var ek = Regex.Match(k, "eqknob\\[(\\d)\\]");
        if (d != null && ek.Success) return N(ek) switch { 1 => $"{d}.eqlow", 2 => $"{d}.eqmid", 3 => $"{d}.eqhigh", _ => null };
        if (d == null && R("movelibrary|movevertical|libraryknob|browserknob", k)) return "browse";
        if (d == null && R("browser button", ds) && R("press", k)) return "browseload";
        if (d != null && R("ratemsb|(^|\\.)pitch$", k)) return $"{d}.tempo";
        if (d != null && R("ratereset", k)) return $"{d}.temporeset";
        if (d != null && R("beatsync", k)) return $"{d}.sync";
        if (d != null && R("(^|\\.)stop$", k)) return $"{d}.stop";
        if (d != null && R("killhigh|killtreble", k)) return $"{d}.killhigh";
        if (d != null && R("killmid", k)) return $"{d}.killmid";
        if (d != null && R("killlow|killbass", k)) return $"{d}.killlow";
        if (d != null && R("(^|\\.)treble$", k)) return $"{d}.eqhigh";
        if (d != null && R("(^|\\.)medium$", k)) return $"{d}.eqmid";
        if (d != null && R("(^|\\.)bass$", k)) return $"{d}.eqlow";
        var kp = Regex.Match(k, "keypad(\\d)$");
        if (d != null && kp.Success) { int n = N(kp); return n <= 8 ? $"{d}.hotcue{n}" : null; }
        bool playlist = group.Contains("[Playlist]", StringComparison.OrdinalIgnoreCase), master = group.Contains("[Master]", StringComparison.OrdinalIgnoreCase);
        if (d == null && playlist && R("(^|\\.)up$", k)) return "browseup";
        if (d == null && playlist && R("(^|\\.)down$", k)) return "browsedown";
        if (d == null && master && R("(^|\\.)volume$", k)) return "master";
        if (R("fx123toggle|fxtoggle", k)) return d != null ? $"{d}.echo" : null;
        if (d == null)
        {
            if (R("crossfader", k)) return "crossfader";
            if (R("browse.*(encoder|knob|turn)|browseencoder\\.input$", k)) return "browse";
            if (R("master.*(volume|level)", k)) return "master";
            if (R("head.*mix|cue.*mix", k)) return "cuemix";
            if (R("head.*(volume|gain)", k)) return "cuevolume";
            return null;
        }
        if (eqGroup)
        {
            if (R("treble|high|(^|\\.)hi(\\.|$)", k)) return $"{d}.eqhigh";
            if (R("(^|\\.)mid", k)) return $"{d}.eqmid";
            if (R("bass|(^|\\.)low", k)) return $"{d}.eqlow";
        }
        if (R("jog.*touch|wheeltouch|platter.*touch|scratch\\.input", k)) return $"{d}.jogtouch";
        if (R("jog|wheel|platter", k)) return $"{d}.jog";
        if (R("spinback|backspin", k)) return $"{d}.backspin";
        if (R("brake", k)) return $"{d}.brake";
        if (R("playbutton|(^|\\.)play(\\.input)?$", k)) return $"{d}.play";
        if (R("headphone|pfl", k)) return $"{d}.cuepfl";
        if (R("cuebutton|(^|\\.)cue(\\.input)?$", k)) return $"{d}.cue";
        if (R("synclong|syncmaster", k)) return null;
        if (R("syncbutton|syncpressed|(^|\\.)sync(\\.input)?$", k)) return $"{d}.sync";
        if (R("temposlider|tempofader|pitchfader|pitchslider|ratefader|rateslider|(^|\\.)rate(\\.|$)", k)) return $"{d}.tempo";
        if (R("deckfader|volume|linefader|channelfader", k)) return $"{d}.fader";
        if (R("pregain|(^|\\.)gain|trim", k)) return $"{d}.volume";
        if (R("loadbutton|loadtrack|(^|\\.)load(\\.input)?$", k)) return d == "a" ? "loadA" : "loadB";
        if (R("keylock", k)) return $"{d}.keylock";
        if (R("keyreset", k)) return $"{d}.keyreset";
        if (R("pitchbend(up|plus)", k)) return $"{d}.nudgeup";
        if (R("pitchbend(down|minus)", k)) return $"{d}.nudgedown";
        if (R("loophalf|loophalve", k)) return $"{d}.loophalf";
        if (R("loopdouble", k)) return $"{d}.loopdouble";
        if (R("reloop|loopexit", k)) return $"{d}.loopexit";
        if (R("autoloop|(^|\\.)loop(\\.input|button)?$", k)) return $"{d}.loop4";
        if (R("quant", k)) return $"{d}.quantize";
        if (R("reverse|censor", k)) return $"{d}.rev";
        if (R("filter", k)) return $"{d}.filtervalue";
        return null;
    }

    // ------------------------------------------------------------------ djay (plist)

    private static readonly Dictionary<string, string> DjayDeck = new()
    {
        ["playPause"] = "play", ["cuePositionOrJumpConsideringPlayState1"] = "cue", ["bpmSync"] = "sync", ["autoLoopOnOff"] = "loop4", ["loopIn"] = "loop4", ["loopOutAndReloopOrUnloop"] = "loopexit",
        ["autoLoopDurationHalf"] = "loophalf", ["autoLoopDurationDouble"] = "loopdouble", ["scratchingMode"] = "keylock", ["censor"] = "rev", ["fx1Enabled"] = "echo",
        ["lowEQ"] = "eqlow", ["midEQ"] = "eqmid", ["highEQ"] = "eqhigh", ["gain"] = "volume", ["filter"] = "filtervalue", ["speed"] = "tempo",
        ["pitchBendMove"] = "jog", ["scratchingMove"] = "jog", ["jogSeekMove"] = "jog", ["skipRotary"] = "jog",
        ["fx1ParameterValueMinus"] = "jumpback4", ["fx1ParameterValuePlus"] = "jumpfwd4", ["deckSlipToggle"] = "quantize",
    };
    private static readonly Dictionary<string, string> DjayGlobal = new()
    {
        ["mixer.crossfade"] = "crossfader", ["mixer.masterLevel"] = "master", ["mixer.monitorLevel"] = "cuevolume", ["mixer.monitorMixToMiddle"] = "cuemix",
        ["mixer.lineVolume1"] = "a.fader", ["mixer.lineVolume2"] = "b.fader", ["mixer.monitorActive1"] = "a.cuepfl", ["mixer.monitorActive2"] = "b.cuepfl",
        ["musicLibrary.load1"] = "loadA", ["musicLibrary.load2"] = "loadB", ["musicLibrary.libraryRotary"] = "browse", ["application.automix"] = "automix",
    };

    private static string? DjayAction(string kp)
    {
        var t = Regex.Match(kp, "^turntable(\\d)\\.(.+)$");
        if (t.Success)
        {
            var d = t.Groups[1].Value == "1" ? "a" : t.Groups[1].Value == "2" ? "b" : null; if (d == null) return null;
            var k = t.Groups[2].Value;
            if (DjayDeck.TryGetValue(k, out var a)) return $"{d}.{a}";
            var hc = Regex.Match(k, "^cueOrJumpIfAlreadySet(\\d)$");
            if (hc.Success && int.Parse(hc.Groups[1].Value) <= 8) return $"{d}.hotcue{hc.Groups[1].Value}";
            return null;
        }
        var s = Regex.Match(kp, "^sampler\\.turntable\\d\\.player(\\d+)\\.");
        if (s.Success && int.Parse(s.Groups[1].Value) <= 12) return $"pad{s.Groups[1].Value}";
        return DjayGlobal.TryGetValue(kp, out var g) ? g : null;
    }

    public static (List<MidiMapping> Mappings, int Total) FromDjay(string plist)
    {
        var outList = new List<MidiMapping>(); var seen = new HashSet<MidiKey>(); int total = 0;
        foreach (var c in plist.Replace("\r", "").Split("<dict>").Skip(1))
        {
            string? G(string k, string t) { var m = Regex.Match(c, "<key>" + k + "</key>\\s*<" + t + ">([^<]*)</" + t + ">"); return m.Success ? m.Groups[1].Value : null; }
            var kp = G("keyPath", "string"); if (kp == null) continue;
            if (!int.TryParse(G("midiMessageType", "integer"), out int mt) || !int.TryParse(G("midiChannel", "integer"), out int ch0) || !int.TryParse(G("midiData", "integer"), out int number)) continue;
            string? type = mt == 1 ? "note" : mt == 3 ? "cc" : null; if (type == null) continue;
            total++;
            var action = DjayAction(kp); if (action == null) continue;
            var key = new MidiKey(type, ch0 + 1, number); if (!seen.Add(key)) continue;
            var e = new MidiMapping { Type = type, Channel = ch0 + 1, Number = number, Action = action };
            bool rel = action.Contains("jog") || action == "browse";
            if (Regex.IsMatch(c, "<key>flipped</key>\\s*<true/>") && type == "cc" && !rel) e.Invert = true;
            if (rel) e.Relative = true;
            outList.Add(e);
        }
        return (outList, total);
    }
}
