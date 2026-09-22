// Famiglia Hercules → preset Mixfonia. Riusa il convertitore Mixxx (convert.js: mapAction/convert) e aggiunge
// il formato djay (.djayMidiMapping, plist) per le console senza mappatura Mixxx. Uso: node convert-hercules.js <out-dir>
const fs = require("fs");
const path = require("path");
const OUT = process.argv[2];
const src = fs.readFileSync(path.join(__dirname, "convert.js"), "utf8");
// prendiamo mapAction/convert/scriptAction senza far girare la parte finale (che rigenera gli altri preset)
const lib = {};
new Function("require", "module", "__dirname", "exports", "process",
  src.replace(/fs\.mkdirSync\(OUT[\s\S]*$/, "") + "\nexports.convert = convert; exports.mapAction = mapAction;")
  (require, { exports: lib }, __dirname, lib, { argv: [null, null, OUT] });

// [file Mixxx (senza .midi.xml), id, nome, stringhe di riconoscimento porta MIDI, nota]
const MIXXX = [
  ["Hercules_DJControl_Inpulse_200mk3", "hercules-inpulse-200-mk3", "Hercules DJControl Inpulse 200 MK3", ["Inpulse 200 MK3"], "mappatura comunitaria (DukeBox, forum Mixxx, aprile 2026)"],
  ["Hercules_DJControl_Inpulse_300_Mk2", "hercules-inpulse-300-mk2", "Hercules DJControl Inpulse 300 MK2", ["Inpulse 300 MK2", "Inpulse 300 Mk2"], "mappatura comunitaria (ElHanko, github alxthedesigner/dj-cyberdeck)"],
  ["Hercules DJ Console 4-Mx", "hercules-console-4mx", "Hercules DJ Console 4-Mx", ["4-Mx", "4Mx"], ""],
  ["Hercules DJ Console Mk2", "hercules-console-mk2", "Hercules DJ Console Mk2", ["DJ Console Mk2"], ""],
  ["Hercules DJ Console Mk4", "hercules-console-mk4", "Hercules DJ Console Mk4", ["DJ Console Mk4"], ""],
  ["Hercules DJ Console RMX", "hercules-console-rmx", "Hercules DJ Console RMX", ["DJ Console RMX"], ""],
  ["Hercules DJ Console RMX 2", "hercules-console-rmx2", "Hercules DJ Console RMX 2", ["DJ Console RMX 2", "DJConsole RMX2", "RMX2"], ""],
  ["Hercules DJ Control AIR", "hercules-control-air", "Hercules DJ Control AIR", ["DJ Control AIR", "DJControl AIR"], ""],
  ["Hercules DJ Control Instinct", "hercules-instinct", "Hercules DJControl Instinct / Instinct S", ["Instinct"], ""],
  ["Hercules DJ Control MP3 e2", "hercules-control-mp3-e2", "Hercules DJ Control MP3 e2 / MP3 LE / Glow", ["DJ Control MP3 e2", "DJ Control MP3 LE", "DJControl Glow", "DJ Control Glow"], ""],
  ["Hercules DJ Control MP3", "hercules-control-mp3", "Hercules DJ Control MP3", ["DJ Control MP3"], ""],
  ["Hercules DJ Control Steel", "hercules-control-steel", "Hercules DJ Control Steel", ["DJ Control Steel"], ""],
  ["Hercules DJControl Compact", "hercules-compact", "Hercules DJControl Compact", ["DJControl Compact"], ""],
  ["Hercules DJControl MIX", "hercules-mix", "Hercules DJControl MIX", ["DJControl MIX"], ""],
  ["Hercules P32 DJ", "hercules-p32", "Hercules P32 DJ", ["P32"], ""],
  ["Hercules_DJControl_Jogvision", "hercules-jogvision", "Hercules DJControl Jogvision", ["Jogvision"], ""],
];

// djay: keyPath → azione Mixfonia (midiChannel è 0-based; type 1 = nota, 3 = CC)
function djayAction(kp) {
  const t = /^turntable(\d)\.(.+)$/.exec(kp);
  if (t) {
    const d = t[1] === "1" ? "a" : t[1] === "2" ? "b" : null; if (!d) return null;
    const k = t[2];
    const m = {
      playPause: "play", cuePositionOrJumpConsideringPlayState1: "cue", bpmSync: "sync", autoLoopOnOff: "loop4", loopIn: "loop4", loopOutAndReloopOrUnloop: "loopexit",
      autoLoopDurationHalf: "loophalf", autoLoopDurationDouble: "loopdouble", scratchingMode: "keylock", censor: "rev", fx1Enabled: "echo",
      lowEQ: "eqlow", midEQ: "eqmid", highEQ: "eqhigh", gain: "volume", filter: "filtervalue", speed: "tempo",
      pitchBendMove: "jog", scratchingMove: "jog", jogSeekMove: "jog", skipRotary: "jog",
      fx1ParameterValueMinus: "jumpback4", fx1ParameterValuePlus: "jumpfwd4", deckSlipToggle: "quantize",
    };
    if (m[k]) return `${d}.${m[k]}`;
    const hc = /^cueOrJumpIfAlreadySet(\d)$/.exec(k); if (hc && Number(hc[1]) <= 8) return `${d}.hotcue${hc[1]}`;
    return null;
  }
  const s = /^sampler\.turntable\d\.player(\d+)\./.exec(kp); if (s && Number(s[1]) <= 12) return `pad${s[1]}`;
  const m = { "mixer.crossfade": "crossfader", "mixer.masterLevel": "master", "mixer.monitorLevel": "cuevolume", "mixer.monitorMixToMiddle": "cuemix",
    "mixer.lineVolume1": "a.fader", "mixer.lineVolume2": "b.fader", "mixer.monitorActive1": "a.cuepfl", "mixer.monitorActive2": "b.cuepfl",
    "musicLibrary.load1": "loadA", "musicLibrary.load2": "loadB", "musicLibrary.libraryRotary": "browse", "application.automix": "automix" };
  return m[kp] || null;
}
function convertDjay(file) {
  const x = fs.readFileSync(file, "utf8").replace(/\r/g, "");
  const out = []; const seen = new Set();
  for (const c of x.split("<dict>").slice(1)) {
    const g = (k, t) => { const r = new RegExp("<key>" + k + "</key>\\s*<" + t + ">([^<]*)</" + t + ">").exec(c); return r ? r[1] : null; };
    const kp = g("keyPath", "string"); if (!kp) continue;
    const mt = Number(g("midiMessageType", "integer")), ch = Number(g("midiChannel", "integer")) + 1, number = Number(g("midiData", "integer"));
    const type = mt === 1 ? "note" : mt === 3 ? "cc" : null; if (!type) continue;
    const action = djayAction(kp); if (!action) continue;
    const id = `${type}:${ch}:${number}`; if (seen.has(id)) continue; seen.add(id);
    const e = { type, channel: ch, number, action };
    if (/<key>flipped<\/key>\s*<true\/>/.test(c) && type === "cc" && !/jog|browse/.test(action)) e.invert = true;
    if (/jog|browse/.test(action)) e.relative = true;
    out.push(e);
  }
  return out;
}

fs.mkdirSync(OUT, { recursive: true });
const index = [];
function emit(id, name, match, source, mappings) {
  fs.writeFileSync(path.join(OUT, id + ".json"), JSON.stringify({ id, name, match, source, mappings }, null, 1));
  const a = new Set(mappings.map(m => m.action));
  index.push({ id, n: mappings.length, play: a.has("a.play"), fader: a.has("a.fader"), jog: a.has("a.jog"), hot: a.has("a.hotcue1"), eq: a.has("a.eqlow"), tempo: a.has("a.tempo"), xf: a.has("crossfader") });
}
for (const [file, id, name, match, note] of MIXXX) {
  const p = path.join(__dirname, file + ".midi.xml");
  if (!fs.existsSync(p)) { console.log("manca", file); continue; }
  const author = /<author>([^<]*)<\/author>/.exec(fs.readFileSync(p, "utf8"))?.[1] || "";
  emit(id, name, match, `Mixxx mapping "${file}" (${author}), GPL — usati solo i numeri MIDI${note ? "; " + note : ""}`, lib.convert(p));
}
// Inpulse 200 MK2 dal file djay (formato plist) condiviso sul forum Algoriddim
emit("hercules-inpulse-200-mk2", "Hercules DJControl Inpulse 200 MK2", ["Inpulse 200 MK2", "Inpulse 200 Mk2"],
  "mappatura djay \"DJControl Inpulse 200 Mk2\" (th3StonedApe, forum Algoriddim) — usati solo i numeri MIDI", convertDjay(path.join(__dirname, "inpulse200mk2.djayMidiMapping")));
// Inpulse T7: nessuna mappatura pubblica; stessa famiglia della 500 (stessi controlli tranne i piatti motorizzati)
const i500 = JSON.parse(fs.readFileSync(path.join(OUT, "hercules-inpulse-500.json"), "utf8"));
emit("hercules-inpulse-t7", "Hercules DJControl Inpulse T7 (derivata dalla 500, da verificare)", ["Inpulse T7"],
  "derivata dal preset Inpulse 500 (stessa famiglia): piatti motorizzati non gestiti, controlli da verificare", i500.mappings);
console.table(index);
