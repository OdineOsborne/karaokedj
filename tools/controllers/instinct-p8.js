// Preset Hercules DJControl Instinct P8 dalla "MIDI Command List v1.1" ufficiale Hercules (canale 1, note 9x / CC Bx).
const fs = require("fs");
const M = [];
const note = (n, action, extra) => M.push({ type: "note", channel: 1, number: n, action, ...(extra || {}) });
const cc = (n, action, extra) => M.push({ type: "cc", channel: 1, number: n, action, ...(extra || {}) });

for (const [d, base] of [["a", 0x00], ["b", 0x30]]) {
  // pad CUE 1-4 e SHIFT+CUE 5-8 → hot cue 1..8
  for (let i = 0; i < 4; i++) note(base + 0x01 + i, `${d}.hotcue${i + 1}`);
  for (let i = 0; i < 4; i++) note(base + 0x05 + i, `${d}.hotcue${i + 5}`);
  // pad FX: ECHO, REVERB, FILTER, FLANGER; con SHIFT: spegni tutto, BRAKE, BACKSPIN, tonalità compatibile
  note(base + 0x09, `${d}.echo`); note(base + 0x0A, `${d}.reverb`); note(base + 0x0B, `${d}.filter`); note(base + 0x0C, `${d}.flanger`);
  note(base + 0x0D, `${d}.fxreset`); note(base + 0x0E, `${d}.brake`); note(base + 0x0F, `${d}.backspin`); note(base + 0x10, `${d}.keymatch`);
  // pad LOOP: 1/2/4/8 battute; con SHIFT: salta −4, salta +4, esci dal loop, quantizza
  note(base + 0x19, `${d}.loop1`); note(base + 0x1A, `${d}.loop2`); note(base + 0x1B, `${d}.loop4`); note(base + 0x1C, `${d}.loop8`);
  note(base + 0x1D, `${d}.jumpback4`); note(base + 0x1E, `${d}.jumpfwd4`); note(base + 0x1F, `${d}.loopexit`); note(base + 0x20, `${d}.quantize`);
  // trasporto
  note(base + 0x21, `${d}.play`); note(base + 0x22, `${d}.cue`); note(base + 0x23, `${d}.sync`);
  note(base + 0x24, `${d}.stop`); note(base + 0x25, `${d}.playcue`); note(base + 0x26, `${d}.temporeset`);
}
// pad SAMPLE: deck A = pad 1-4 (SHIFT 5-8), deck B = pad 9-12 (SHIFT: stop pad, ritmi avvia/ferma, tap, riparti)
for (let i = 0; i < 4; i++) note(0x11 + i, `pad${i + 1}`);
for (let i = 0; i < 4; i++) note(0x15 + i, `pad${i + 5}`);
for (let i = 0; i < 4; i++) note(0x41 + i, `pad${i + 9}`);
note(0x45, "padstop"); note(0x46, "rhythm.play"); note(0x47, "rhythm.tap"); note(0x48, "rhythm.resync");
// encoder loop premuto: loop 4 battute; con SHIFT esci dal loop
note(0x57, "a.loop4"); note(0x58, "b.loop4"); note(0x59, "a.loopexit"); note(0x5A, "b.loopexit");
// cuffia (LISTEN), SHIFT+LISTEN = key lock
note(0x5B, "a.cuepfl"); note(0x5C, "b.cuepfl"); note(0x64, "a.keylock"); note(0x65, "b.keylock");
// carica, browser premuto = carica sul deck libero, SHIFT = in coda
note(0x5F, "loadA"); note(0x60, "loadB"); note(0x2B, "browseload"); note(0x2C, "addqueue");
// mano sul piatto (scratch), SHIFT+SCRATCH = automix
note(0x61, "a.jogtouch"); note(0x62, "b.jogtouch"); note(0x2E, "automix");
// (0x2D SCRATCH, 0x2F/0x63 SHIFT, 0x30 MODE, 0x5D/0x5E volume cuffia: gestiti dalla console o senza funzione)

// jog: pitch bend / scratch; SHIFT+jog = tempo (encoder relativo accumulato); i CC "JOG_TOUCH" arrivano quando il piatto è toccato
// CC 0x30/0x32 = piatto senza mano (pitch bend); 0x69/0x6B = piatto CON la mano sopra → scratch anche all'indietro
cc(0x30, "a.jog", { relative: true }); cc(0x69, "a.jogscratch", { relative: true }); cc(0x31, "a.tempo", { relative: true }); cc(0x6A, "a.tempo", { relative: true });
cc(0x32, "b.jog", { relative: true }); cc(0x6B, "b.jogscratch", { relative: true }); cc(0x33, "b.tempo", { relative: true }); cc(0x6C, "b.tempo", { relative: true });
// encoder LOOP (nei 4 modi pad) = filtro del deck, relativo
for (const n of [0x34, 0x57, 0x59, 0x61]) cc(n, "a.filtervalue", { relative: true });
for (const n of [0x36, 0x63, 0x65, 0x67]) cc(n, "b.filtervalue", { relative: true });
// browser
cc(0x38, "browse", { relative: true }); cc(0x39, "browse", { relative: true });
// fader di canale, SHIFT+fader = gain
cc(0x40, "a.fader"); cc(0x42, "b.fader"); cc(0x41, "a.volume"); cc(0x43, "b.volume");
// EQ
cc(0x44, "a.eqlow"); cc(0x46, "a.eqmid"); cc(0x48, "a.eqhigh");
cc(0x50, "b.eqlow"); cc(0x52, "b.eqmid"); cc(0x54, "b.eqhigh");
// crossfader
cc(0x56, "crossfader");

const preset = {
  id: "hercules-instinct-p8",
  name: "Hercules DJControl Instinct P8",
  match: ["Instinct P8", "InstinctP8", "Instinct-P8"],
  source: "Hercules \"DJControl Instinct P8 MIDI Command List v1.1\" (documento ufficiale Hercules), canale 1",
  mappings: M,
};
fs.writeFileSync(require("path").join(process.argv[2] || "../../src/KaraokeDJ/Assets/Controllers", "hercules-instinct-p8.json"), JSON.stringify(preset, null, 1));
// controllo doppioni
const seen = new Set();
for (const m of M) { const k = m.type + m.number; if (seen.has(k)) console.log("DOPPIO", k, m.action); seen.add(k); }
console.log("ok", M.length, "mappature");
