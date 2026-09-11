import { normalizeMachine } from "../lib/license.js";
import { getLicense } from "../lib/store.js";

// L'app interroga questo endpoint dopo aver aperto la pagina di donazione: appena la chiave esiste si attiva da sola.
export default async function handler(req, res) {
  const m = normalizeMachine(req.query?.m);
  if (!m) return res.status(400).json({ error: "ID macchina non valido" });
  try {
    const rec = await getLicense(m);
    if (!rec?.key) return res.status(404).json({ found: false });
    res.json({ found: true, key: rec.key, name: rec.name });
  } catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
