import { PRODUCTS } from "../lib/stripe.js";

export default function handler(_req, res) {
  const prices = Object.fromEntries(Object.entries(PRODUCTS).map(([k, v]) => [k, { cents: v.cents, mode: v.mode, label: v.label }]));
  res.json({ ready: !!process.env.STRIPE_SECRET_KEY, prices, currency: "EUR" });
}
