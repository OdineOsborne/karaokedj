// Controllo di tutti i preset delle console: cerca gli errori della classe che ci ha morso sulla P8
// (stesso controllo su due azioni, la stessa azione sparsa su CC inventati, comandi essenziali mancanti).
//   node check.js ../../src/KaraokeDJ/Assets/Controllers
const fs = require("fs"), path = require("path");
const dir = process.argv[2] || "../../src/KaraokeDJ/Assets/Controllers";

// azioni che l'app conosce davvero (estratte da AppActions.cs, cosi un refuso salta fuori subito)
const src = fs.readFileSync(path.join(__dirname, "../../src/KaraokeDJ/Services/AppActions.cs"), "utf8");
const deckTpl = [...src.matchAll(/\("([A-Za-z0-9.]+)",\s*"[^"]*",\s*(true|false),\s*(true|false)\)/g)].map(m => m[1]);
const globals = [...src.matchAll(/new\("([A-Za-z0-9.]+)",\s*"[^"]*"/g)].map(m => m[1]);
const known = new Set([...globals, ...deckTpl.flatMap(a => [`a.${a}`, `b.${a}`])]);

// comandi senza i quali una console e inutilizzabile
const essential = ["a.play", "b.play", "a.cue", "b.cue", "a.fader", "b.fader", "crossfader"];
// le griglie di pad (Launchpad) non hanno cursori ne piatti: non si pretende quello che non c'e
const padGrid = f => /launchpad|p32/.test(f);
const wanted = ["a.jog", "b.jog", "a.eqlow", "b.eqlow", "a.volume", "b.volume", "browse", "loadA", "loadB"];

let bad = 0, warned = 0;
const files = fs.readdirSync(dir).filter(f => f.endsWith(".json")).sort();
for (const f of files) {
  const p = JSON.parse(fs.readFileSync(path.join(dir, f), "utf8"));
  const problems = [], notes = [];
  const byKey = new Map(), byAction = new Map();
  for (const m of p.mappings) {
    const k = `${m.type} ch${m.channel} #${m.number}`;
    if (!byKey.has(k)) byKey.set(k, []);
    byKey.get(k).push(m.action);
    if (!byAction.has(m.action)) byAction.set(m.action, []);
    byAction.get(m.action).push(k);
  }
  // 1) stesso controllo su due azioni diverse: uno dei due non funzionera mai
  for (const [k, acts] of byKey) {
    const uniq = [...new Set(acts)];
    if (uniq.length > 1) problems.push(`${k} fa due cose: ${uniq.join(" e ")}`);
  }
  // 2) azioni che non esistono (refusi)
  for (const a of byAction.keys()) if (!known.has(a)) problems.push(`azione inesistente: ${a}`);
  // 3) comandi essenziali mancanti
  const missing = (padGrid(f) ? [] : essential).filter(a => !byAction.has(a));
  if (missing.length) problems.push(`manca l'essenziale: ${missing.join(", ")}`);
  // 4) una manopola sparsa su tanti CC diversi = numeri tirati a indovinare (il caso P8)
  for (const [a, keys] of byAction) {
    if (keys.length >= 3 && !a.includes("jog") && a !== "browse" && !a.startsWith("pad"))
      notes.push(`${a} su ${keys.length} controlli (${keys.join(", ")})`);
  }
  // 5) deck sbilanciati
  const na = [...byAction.keys()].filter(a => a.startsWith("a.")).length;
  const nb = [...byAction.keys()].filter(a => a.startsWith("b.")).length;
  if (Math.abs(na - nb) > 2) notes.push(`deck sbilanciati: A ${na} comandi, B ${nb}`);
  // 6) comandi utili che mancano
  const miss2 = (padGrid(f) ? [] : wanted).filter(a => !byAction.has(a));
  if (miss2.length) notes.push(`senza: ${miss2.join(", ")}`);

  const verified = /misurat|registratore|console vera/i.test(p.source || "");
  if (problems.length) { bad++; console.log(`\nERRORE  ${f}  (${p.mappings.length} mappature)`); problems.forEach(x => console.log("   ! " + x)); notes.forEach(x => console.log("   . " + x)); }
  else if (notes.length) { warned++; console.log(`\nda guardare  ${f}${verified ? " [verificato]" : ""}`); notes.forEach(x => console.log("   . " + x)); }
}
console.log(`\n${files.length} preset · ${bad} con errori · ${warned} da guardare`);
