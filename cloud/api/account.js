import { normalizeMachine, normalizeEmail } from "../lib/license.js";
import { accountStatus } from "../lib/fulfill.js";

// Stato dell'account (email) visto da una macchina: quanti PC, aggiornamenti fino a quando, cosa può comprare.
export default async function handler(req, res) {
  const email = normalizeEmail(req.query?.email);
  const m = normalizeMachine(req.query?.m);
  if (!email) return res.status(400).json({ error: "email non valida" });
  try { res.json(await accountStatus(email, m)); }
  catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
