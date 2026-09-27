import { normalizeMachine, normalizeEmail } from "../lib/license.js";
import { validate } from "../lib/fulfill.js";
import { getAccount, saveAccount } from "../lib/store.js";
import { stripe, priceId, customerFor, PRODUCTS } from "../lib/stripe.js";

// Apre una sessione Stripe Checkout per un articolo. Il pagamento arriva poi dal webhook (api/stripe-webhook.js).
export default async function handler(req, res) {
  if (req.method !== "POST") return res.status(405).end();
  try {
    const { product, email: e, machine: m, from: f, name, consent } = req.body ?? {};
    const email = normalizeEmail(e), machine = normalizeMachine(m), from = f ? normalizeMachine(f) : null;
    const art = PRODUCTS[product];
    if (!art) return res.status(400).json({ error: "articolo sconosciuto" });
    // Senza consenso al contratto e all'esecuzione immediata (perdita del recesso) non si apre il pagamento:
    // la pagina blocca già i pulsanti, qui si ricontrolla perché la pagina si può aggirare.
    if (consent?.terms !== true || consent?.immediate !== true)
      return res.status(400).json({ error: "Per procedere accetta il contratto di licenza e la fornitura immediata (perdita del diritto di recesso)." });
    if (!email) return res.status(400).json({ error: "email non valida" });
    if (!machine && product !== "voxa_updates") return res.status(400).json({ error: "ID macchina non valido" });
    if (product === "voxa_transfer" && !from) return res.status(400).json({ error: "ID del PC di origine non valido" });
    const why = await validate({ product, email, machine, from });
    if (why) return res.status(409).json({ error: why });

    const acc = (await getAccount(email)) ?? { email, name: name || null, seats: [], history: [], updatesUntil: null, token: null };
    const customer = await customerFor(acc);
    if (acc.seats.length || acc.customerId) await saveAccount(acc); // ricorda il customer anche prima del pagamento

    const base = (process.env.APP_URL || `https://${req.headers.host}`).replace(/\/$/, "");
    const q = `m=${encodeURIComponent(machine ?? "")}&email=${encodeURIComponent(email)}`;
    // prova del consenso, conservata nel pagamento Stripe (e nell'abbonamento): quando e su quale testo
    const meta = { product, email, machine: machine ?? "", from: from ?? "",
                   consenso: `contratto di licenza (LICENSE.md 27/09/2026) + esecuzione immediata e perdita del recesso · ${new Date().toISOString()}` };
    const session = await stripe().checkout.sessions.create({
      customer,
      mode: art.mode,
      line_items: [{ price: await priceId(product), quantity: 1 }],
      allow_promotion_codes: true,
      customer_update: { address: "auto", name: "auto" },
      ...(process.env.STRIPE_TAX === "1" ? { automatic_tax: { enabled: true }, tax_id_collection: { enabled: true } } : {}),
      client_reference_id: email,
      metadata: meta,
      ...(art.mode === "payment" ? { invoice_creation: { enabled: true } } : { subscription_data: { metadata: meta } }),
      success_url: `${base}/dona?${q}&ok=${product}&session_id={CHECKOUT_SESSION_ID}`,
      cancel_url: `${base}/dona?${q}&ko=1`,
      locale: "auto",
    });
    res.json({ url: session.url });
  } catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
