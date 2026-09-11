import { verifyWebhook, getOrder } from "../lib/paypal.js";
import { issueLicense, normalizeMachine } from "../lib/license.js";
import { saveLicense, getLicense } from "../lib/store.js";

// Backup: se la pagina si chiude prima della cattura lato client, PayPal ci avvisa comunque e la licenza viene emessa.
export default async function handler(req, res) {
  if (req.method !== "POST") return res.status(405).end();
  try {
    const body = req.body;
    if (process.env.PAYPAL_WEBHOOK_ID && !(await verifyWebhook(req.headers, body))) return res.status(400).json({ error: "firma webhook non valida" });
    if (body?.event_type !== "PAYMENT.CAPTURE.COMPLETED") return res.json({ ignored: body?.event_type });
    const r = body.resource ?? {};
    let machine = normalizeMachine(r.custom_id);
    let payerName = null;
    const orderId = r.supplementary_data?.related_ids?.order_id;
    if (orderId) {
      const order = await getOrder(orderId);
      machine ??= normalizeMachine(order?.purchase_units?.[0]?.custom_id);
      const p = order?.payer;
      payerName = [p?.name?.given_name, p?.name?.surname].filter(Boolean).join(" ") || p?.email_address || null;
    }
    if (!machine) return res.json({ ignored: "no machine id" });
    if (await getLicense(machine)) return res.json({ ok: true, already: true });
    const key = issueLicense({ name: payerName ?? "Donatore VOXA", machine, note: `paypal:${r.id}` });
    await saveLicense(machine, { key, name: payerName, amount: r.amount?.value, currency: r.amount?.currency_code, captureId: r.id, orderId: orderId ?? null, via: "webhook" });
    res.json({ ok: true });
  } catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
