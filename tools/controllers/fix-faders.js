// Il cursore di canale deve essere il FADER, non il gain: a fondo corsa deve fare silenzio.
// Diverse mappature della comunita usano "pregain" per i cursori: da noi diventerebbe una manopola
// di guadagno che al minimo fa -12 dB, cioe un cursore che non spegne mai il canale.
const fs = require("fs"), path = require("path");
const dir = process.argv[2] || "../../src/KaraokeDJ/Assets/Controllers";
for (const f of fs.readdirSync(dir).filter(x => x.endsWith(".json"))) {
  const p = JSON.parse(fs.readFileSync(path.join(dir, f), "utf8"));
  let changed = 0;
  for (const d of ["a", "b"]) {
    const hasFader = p.mappings.some(m => m.action === `${d}.fader`);
    if (hasFader) continue;
    for (const m of p.mappings) if (m.action === `${d}.volume`) { m.action = `${d}.fader`; changed++; }
  }
  if (changed) {
    fs.writeFileSync(path.join(dir, f), JSON.stringify(p, null, 1));
    console.log(`${f}: ${changed} cursori spostati da gain a fader`);
  }
}
