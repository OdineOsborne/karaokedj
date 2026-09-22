// Elenca i controlli Mixxx (gruppo/chiave/descrizione) che il convertitore non ha mappato, per file
const fs = require("fs");
const path = require("path");
const src = fs.readFileSync(path.join(__dirname, "convert.js"), "utf8");
const lib = {};
new Function("require", "module", "__dirname", "exports", "process",
  src.replace(/fs\.mkdirSync\(OUT[\s\S]*$/, "") + "\nexports.mapAction = mapAction; exports.scriptAction = scriptAction; exports.deckOf = deckOf;")
  (require, { exports: lib }, __dirname, lib, { argv: [] });
for (const f of process.argv.slice(2)) {
  const xml = fs.readFileSync(f, "utf8");
  const re = /<control>([\s\S]*?)<\/control>/g; let m; const rows = new Set();
  while ((m = re.exec(xml))) {
    const c = m[1];
    const g = /<group>([^<]*)<\/group>/.exec(c)?.[1]?.trim(); const k = /<key>([^<]*)<\/key>/.exec(c)?.[1]?.trim();
    const st = /<status>0x([0-9A-Fa-f]+)<\/status>/.exec(c)?.[1]; const no = /<midino>0x([0-9A-Fa-f]+)<\/midino>/.exec(c)?.[1];
    if (!g || !k || !st || !no) continue;
    const opts = (/<options>([\s\S]*?)<\/options>/.exec(c)?.[1] || "").toLowerCase();
    const desc = /<description>([^<]*)<\/description>/.exec(c)?.[1] || "";
    const a = lib.mapAction(g, k, opts) || ((/\./.test(k) || opts.includes("script-binding")) ? lib.scriptAction(lib.deckOf(g), k, desc, g) : null);
    if (!a) rows.add(`${st}/${no}  ${g}  ${k}  ${desc}`);
  }
  console.log("==== " + path.basename(f) + " (" + rows.size + " non mappati)");
  for (const r of [...rows].sort()) console.log("  " + r);
}
