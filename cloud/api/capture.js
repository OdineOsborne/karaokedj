import { captureOrder } from "../lib/paypal.js";
import { issueLicense, normalizeMachine } from "../lib/license.js";
import { saveLicense, getLicense } from "../lib/store.js";

// Cattura l'ordine PayPal e, se completato, emette subito la licenza per la macchina indicata nell'ordine.
export default async function handler(req, res) {
  if (req.method !== "POST") return res.status(405).end();
  try {
    const { orderId } = req.body ?? {};
    if (!orderId) return res.status(400).json({ error: "orderId mancante" });
    const cap = await captureOrder(orderId);
    const unit = cap.purchase_units?.[0];
    const capture = unit?.payments?.captures?.[0];
    const machine = normalizeMachine(unit?.custom_id);
    if (!machine) return res.status(400).json({ error: "ordine senza ID macchina" });
    if (cap.status !== "COMPLETED" && capture?.status !== "COMPLETED")
      return res.status(402).json({ error: "pagamento non completato", status: cap.status });

    const existing = await getLicense(machine);
    if (existing?.key) return res.json({ machine, key: existing.key, name: existing.name, already: true });

    const payer = cap.payer ?? {};
    const name = [payer.name?.given_name, payer.name?.surname].filter(Boolean).join(" ") || payer.email_address || "Donatore VOXA";
    const key = issueLicense({ name, machine, note: `paypal:${capture?.id ?? orderId}` });
    const record = { key, name, email: payer.email_address ?? null, amount: capture?.amount?.value, currency: capture?.amount?.currency_code, captureId: capture?.id ?? null, orderId };
    await saveLicense(machine, record);
    res.json({ machine, key, name });
  } catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
