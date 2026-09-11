import { createOrder } from "../lib/paypal.js";
import { normalizeMachine } from "../lib/license.js";

export default async function handler(req, res) {
  if (req.method !== "POST") return res.status(405).end();
  try {
    const { machine, amount } = req.body ?? {};
    const m = normalizeMachine(machine);
    if (!m) return res.status(400).json({ error: "ID macchina non valido" });
    const value = Math.max(2, Math.min(500, Number(amount) || 10)).toFixed(2);
    const order = await createOrder({ amount: value, currency: process.env.PAYPAL_CURRENCY || "EUR", machine: m });
    res.json({ id: order.id });
  } catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
