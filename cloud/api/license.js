import { normalizeMachine } from "../lib/license.js";
import { getLicense, getAccount } from "../lib/store.js";

// L'app interroga questo endpoint all'avvio e dopo un acquisto: chiave della macchina + token aggiornamenti dell'account.
export default async function handler(req, res) {
  const m = normalizeMachine(req.query?.m);
  if (!m) return res.status(400).json({ error: "ID macchina non valido" });
  try {
    const rec = await getLicense(m);
    if (!rec?.key || rec.revoked) return res.status(404).json({ found: false, revoked: !!rec?.revoked });
    const acc = rec.account ? await getAccount(rec.account) : null;
    res.json({ found: true, key: rec.key, name: rec.name, account: rec.account ?? null, token: acc?.token ?? null, updatesUntil: acc?.updatesUntil ?? null, subscription: !!acc?.subscriptionId });
  } catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
