// Converte le mappature MIDI di Mixxx (XML, GPL) nei preset di Mixfonia (JSON): solo i numeri MIDI dei controlli
// e la nostra azione corrispondente. Uso: node convert.js <out-dir>
const fs = require("fs");
const path = require("path");
const OUT = process.argv[2];

const FILES = [
  ["Pioneer-DDJ-FLX4", "pioneer-ddj-flx4", "Pioneer DDJ-FLX4", ["DDJ-FLX4"]],
  ["Pioneer-DDJ-400", "pioneer-ddj-400", "Pioneer DDJ-400", ["DDJ-400"]],
  ["Pioneer-DDJ-SB3", "pioneer-ddj-sb3", "Pioneer DDJ-SB3", ["DDJ-SB3"]],
  ["Pioneer DDJ-200", "pioneer-ddj-200", "Pioneer DDJ-200", ["DDJ-200"]],
  ["Pioneer DDJ-SX", "pioneer-ddj-sx", "Pioneer DDJ-SX", ["DDJ-SX"]],
  ["Numark Mixtrack Platinum FX", "numark-mixtrack-platinum-fx", "Numark Mixtrack Platinum FX", ["Mixtrack Platinum FX"]],
  ["Numark Mixtrack Pro FX", "numark-mixtrack-pro-fx", "Numark Mixtrack Pro FX", ["Mixtrack Pro FX"]],
  ["Numark-Mixtrack-3", "numark-mixtrack-3", "Numark Mixtrack 3 / Pro 3 / Platinum", ["Mixtrack 3", "Mixtrack Pro 3", "Mixtrack Platinum"]],
  ["Numark-Party-Mix", "numark-party-mix", "Numark Party Mix", ["Party Mix"]],
  ["Numark NS4FX", "numark-ns4fx", "Numark NS4FX", ["NS4FX"]],
  ["Numark NS6II", "numark-ns6ii", "Numark NS6II", ["NS6II"]],
  ["Numark_DJ2GO2_Touch", "numark-dj2go2-touch", "Numark DJ2GO2 Touch", ["DJ2GO2"]],
  ["Hercules_DJControl_Inpulse_200", "hercules-inpulse-200", "Hercules DJControl Inpulse 200", ["Inpulse 200"]],
  ["Hercules_DJControl_Inpulse_300", "hercules-inpulse-300", "Hercules DJControl Inpulse 300", ["Inpulse 300"]],
  ["Hercules_DJControl_Inpulse_500", "hercules-inpulse-500", "Hercules DJControl Inpulse 500", ["Inpulse 500"]],
  ["Hercules DJControl Starlight", "hercules-starlight", "Hercules DJControl Starlight", ["Starlight"]],
  ["Denon MC4000", "denon-mc4000", "Denon MC4000", ["MC4000"]],
  ["Denon-MC7000", "denon-mc7000", "Denon MC7000", ["MC7000"]],
  ["Denon-MC6000MK2", "denon-mc6000mk2", "Denon MC6000MK2", ["MC6000MK2"]],
  ["Reloop-Beatmix-2-4-MK2", "reloop-beatmix-mk2", "Reloop Beatmix 2/4 MK2", ["Beatmix 2 MK2", "Beatmix 4 MK2", "Beatmix"]],
  ["Roland_DJ-505", "roland-dj-505", "Roland DJ-505", ["DJ-505"]],
  ["Traktor Kontrol X1", "traktor-kontrol-x1", "Native Instruments Traktor Kontrol X1", ["Kontrol X1", "Traktor Kontrol X1"]],
];

const scriptAction = require("./script-map.js");
const deckOf = g => { const m = /^\[Channel(\d)\]$/.exec(g) || /\[Channel(\d)\]/.exec(g); return m ? (m[1] === "1" ? "a" : m[1] === "2" ? "b" : null) : null; };

// (gruppo, chiave) → azione Mixfonia; null = non gestita
function mapAction(group, key, opts) {
  const d = deckOf(group);
  const k = key;
  if (/^\[Sampler(\d+)\]$/.test(group)) {
    const n = Number(RegExp.$1);
    if (n >= 1 && n <= 12 && /^(cue_gotoandplay|start_play|play|LoadSelectedTrackAndPlay)$/.test(k)) return `pad${n}`;
    return null;
  }
  if (group === "[Master]" || group === "[Main]") {
    if (k === "crossfader") return "crossfader";
    if (k === "volume" || k === "gain") return "master";
    if (k === "headMix") return "cuemix";
    if (k === "headVolume" || k === "headGain") return "cuevolume";
    return null;
  }
  if (group === "[AutoDJ]" && k === "enabled") return "automix";
  if (group === "[Library]" || group === "[Playlist]") {
    if (k === "MoveVertical" || k === "SelectTrackKnob" || k === "SelectNextTrack" || k === "SelectPrevTrack") return k === "SelectPrevTrack" ? "browseup" : k === "SelectNextTrack" ? "browsedown" : "browse";
    if (k === "GoToItem" || k === "LoadSelectedIntoFirstStopped") return "browseload";
    return null;
  }
  if (/^\[EqualizerRack1_\[Channel(\d)\]_Effect1\]$/.test(group)) {
    const dd = deckOf(group); if (!dd) return null;
    if (k === "parameter1") return `${dd}.eqlow`; if (k === "parameter2") return `${dd}.eqmid`; if (k === "parameter3") return `${dd}.eqhigh`;
    if (k === "button_parameter1") return `${dd}.killlow`; if (k === "button_parameter2") return `${dd}.killmid`; if (k === "button_parameter3") return `${dd}.killhigh`;
    return null;
  }
  if (/^\[QuickEffectRack1_\[Channel(\d)\]\]$/.test(group)) {
    const dd = deckOf(group); if (!dd) return null;
    if (k === "super1") return `${dd}.filtervalue`; if (k === "enabled") return `${dd}.filter`;
    return null;
  }
  if (!d) return null;
  const t = {
    play: "play", play_indicator: "play", start_stop: "stop", cue_default: "cue", cue_gotoandplay: "playcue", cue_gotoandstop: "cue",
    sync_enabled: "sync", beatsync: "sync", beatsync_tempo: "sync", sync_master: "sync",
    rate: "tempo", rate_set_default: "temporeset", volume: "fader", pregain: "volume", pfl: "cuepfl", keylock: "keylock",
    pitch_up: "keyup", pitch_down: "keydown", reset_key: "keyreset", pitch_adjust_up: "keyup", pitch_adjust_down: "keydown",
    filterHigh: "eqhigh", filterMid: "eqmid", filterLow: "eqlow", filterHighKill: "killhigh", filterMidKill: "killmid", filterLowKill: "killlow",
    loop_halve: "loophalf", loop_double: "loopdouble", reloop_toggle: "loopexit", reloop_exit: "loopexit", loop_exit: "loopexit",
    beatloop_activate: "loop4", beatloop_1_toggle: "loop1", beatloop_1_activate: "loop1", beatloop_2_toggle: "loop2", beatloop_2_activate: "loop2",
    beatloop_4_toggle: "loop4", beatloop_4_activate: "loop4", beatloop_8_toggle: "loop8", beatloop_8_activate: "loop8",
    beatjump_backward: "jumpback4", beatjump_forward: "jumpfwd4", beatjump_4_backward: "jumpback4", beatjump_4_forward: "jumpfwd4",
    beatjump_8_backward: "jumpback8", beatjump_8_forward: "jumpfwd8", beatjump_1_backward: "jumpback4", beatjump_1_forward: "jumpfwd4",
    LoadSelectedTrack: d === "a" ? "loadA" : "loadB", eject: "eject", reverse: "rev", brake: "brake", back: "back", fwd: "fwd",
    quantize: "quantize", eject_track: "eject",
  };
  if (t[k]) return t[k].includes(".") || /^(loadA|loadB)$/.test(t[k]) ? t[k] : `${d}.${t[k]}`;
  const hc = /^hotcue_(\d)_activate$/.exec(k); if (hc && Number(hc[1]) <= 8) return `${d}.hotcue${hc[1]}`;
  return null;
}

function convert(file) {
  const xml = fs.readFileSync(file, "utf8");
  const out = []; const seen = new Set();
  const re = /<control>([\s\S]*?)<\/control>/g; let m;
  while ((m = re.exec(xml))) {
    const c = m[1];
    const g = /<group>([^<]*)<\/group>/.exec(c)?.[1]?.trim(); const k = /<key>([^<]*)<\/key>/.exec(c)?.[1]?.trim();
    const st = /<status>0x([0-9A-Fa-f]+)<\/status>/.exec(c)?.[1]; const no = /<midino>0x([0-9A-Fa-f]+)<\/midino>/.exec(c)?.[1];
    if (!g || !k || !st || !no) continue;
    const opts = (/<options>([\s\S]*?)<\/options>/.exec(c)?.[1] || "").toLowerCase();
    if (opts.includes("fourteen-bit-lsb")) continue;               // teniamo solo l'MSB
    const status = parseInt(st, 16), number = parseInt(no, 16);
    const kind = status >> 4, ch = (status & 0x0f) + 1;
    let type; if (kind === 0x9) type = "note"; else if (kind === 0xB) type = "cc"; else continue;
    const desc = /<description>([^<]*)<\/description>/.exec(c)?.[1] || "";
    const action = mapAction(g, k, opts) || ((/\./.test(k) || opts.includes("script-binding")) ? scriptAction(deckOf(g), k, desc, g, number) : null);
    if (!action) continue;
    // SHIFT: la stessa nota con shift arriva su un altro canale/numero, va bene; ma se un controllo è già mappato non lo sovrascriviamo
    const id = `${type}:${ch}:${number}`;
    if (seen.has(id)) continue; seen.add(id);
    const e = { type, channel: ch, number, action };
    if (opts.includes("<invert/>")) e.invert = true;
    if (opts.includes("selectknob") || opts.includes("spread64") || /jog/.test(action)) e.relative = true;
    out.push(e);
  }
  return out;
}

fs.mkdirSync(OUT, { recursive: true });
const index = [];
for (const [file, id, name, match] of FILES) {
  const src = path.join(__dirname, file + ".midi.xml");
  if (!fs.existsSync(src)) { console.log("manca", file); continue; }
  const mappings = convert(src);
  const author = /<author>([^<]*)<\/author>/.exec(fs.readFileSync(src, "utf8"))?.[1] || "";
  const preset = { id, name, match, source: `Mixxx mapping "${file}" (${author}), GPL — usati solo i numeri MIDI`, mappings };
  fs.writeFileSync(path.join(OUT, id + ".json"), JSON.stringify(preset, null, 1));
  const acts = new Set(mappings.map(m => m.action));
  index.push({ id, name, n: mappings.length, play: acts.has("a.play"), fader: acts.has("a.fader"), jog: acts.has("a.jog"), hot: acts.has("a.hotcue1"), eq: acts.has("a.eqlow"), xf: acts.has("crossfader") });
}
console.table(index);
