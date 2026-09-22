import { normalizeEmail } from "../lib/license.js";
import { getAccount } from "../lib/store.js";
import { stripe } from "../lib/stripe.js";

// Portale clienti Stripe: cambiare carta, disdire gli aggiornamenti, scaricare le fatture.
export default async function handler(req, res) {
  if (req.method !== "POST") return res.status(405).end();
  try {
    const email = normalizeEmail(req.body?.email);
    const acc = email ? await getAccount(email) : null;
    if (!acc?.customerId) return res.status(404).json({ error: "nessun acquisto per questa email" });
    const base = (process.env.APP_URL || `https://${req.headers.host}`).replace(/\/$/, "");
    const s = await stripe().billingPortal.sessions.create({ customer: acc.customerId, return_url: `${base}/dona?email=${encodeURIComponent(email)}` });
    res.json({ url: s.url });
  } catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
