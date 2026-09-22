// Preset Novation Launchpad come griglia di comandi (hot cue, loop, jingle, trasporto, funzioni live).
// Due layout MIDI: "moderno" (X, Mini MK3, MK2, Pro MK3 in modalità Session/Programmer: nota = 10*riga + colonna, riga 1 in basso)
// e "classico" (Launchpad S, Mini MK1/MK2, originale: nota = 16*riga + colonna, riga 0 in alto). Canale 1.
// I LED non vengono accesi (serve l'uscita MIDI, in roadmap): i pad funzionano lo stesso.
const fs = require("fs");
const path = require("path");
const OUT = process.argv[2];

// righe dall'alto (1 = in alto) → 8 azioni per riga
const ROWS = [
  [1, 2, 3, 4, 5, 6, 7, 8].map(i => `a.hotcue${i}`),
  [1, 2, 3, 4, 5, 6, 7, 8].map(i => `b.hotcue${i}`),
  ["a.loop1", "a.loop2", "a.loop4", "a.loop8", "a.jumpback4", "a.jumpfwd4", "a.loopexit", "a.quantize"],
  ["b.loop1", "b.loop2", "b.loop4", "b.loop8", "b.jumpback4", "b.jumpfwd4", "b.loopexit", "b.quantize"],
  [1, 2, 3, 4, 5, 6, 7, 8].map(i => `pad${i}`),
  ["pad9", "pad10", "pad11", "pad12", "padstop", "rhythm.play", "rhythm.tap", "rhythm.resync"],
  ["a.play", "a.cue", "a.sync", "a.cuepfl", "a.keydown", "a.keyup", "a.temporeset", "a.keymatch"],
  ["b.play", "b.cue", "b.sync", "b.cuepfl", "b.keydown", "b.keyup", "b.temporeset", "b.keymatch"],
];
const RIGHT = ["automix", "next", "mic", "talk", "fill", "rotation", "loadA", "loadB"];          // colonna destra, dall'alto
const TOP = ["browseup", "browsedown", "browseload", "addqueue", "fadeA", "fadeB", "monitor", "projector"]; // riga di tasti in alto

function build(id, name, match, layout) {
  const M = [];
  const note = (n, action) => M.push({ type: "note", channel: 1, number: n, action });
  const cc = (n, action) => M.push({ type: "cc", channel: 1, number: n, action });
  ROWS.forEach((row, r) => row.forEach((action, c) => note(layout.grid(r, c), action)));
  RIGHT.forEach((action, r) => note(layout.right(r), action));
  TOP.forEach((action, c) => cc(layout.top(c), action));
  const preset = { id, name, match, source: "scritto per Mixfonia dalla documentazione Novation (Programmer's reference); griglia 8x8: hot cue A/B, loop A/B, jingle, trasporto A/B; colonna destra: automix, prossimo, mic, talk, riempimento, rotazione, carica A/B; tasti in alto: libreria, sfuma, proiettore", mappings: M };
  fs.writeFileSync(path.join(OUT, id + ".json"), JSON.stringify(preset, null, 1));
  console.log(id, M.length);
}

// moderno: riga r (0 = in alto) → riga MIDI 8-r; nota = 10*(8-r) + (c+1); colonna destra = nota x9; tasti in alto CC 91..98
build("novation-launchpad", "Novation Launchpad X / Mini MK3 / MK2 / Pro MK3", ["Launchpad X", "LPX", "Launchpad Mini MK3", "LPMiniMK3", "Launchpad MK2", "Launchpad Pro MK3", "LPProMK3", "Launchpad Pro"], {
  grid: (r, c) => 10 * (8 - r) + (c + 1), right: r => 10 * (8 - r) + 9, top: c => 91 + c,
});
// classico: nota = 16*r + c; colonna destra = 16*r + 8; tasti in alto CC 104..111
build("novation-launchpad-classic", "Novation Launchpad S / Mini / originale", ["Launchpad S", "Launchpad Mini", "Launchpad"], {
  grid: (r, c) => 16 * r + c, right: r => 16 * r + 8, top: c => 104 + c,
});
