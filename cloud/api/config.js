export default function handler(req, res) {
  res.json({ clientId: process.env.PAYPAL_CLIENT_ID ?? null, currency: process.env.PAYPAL_CURRENCY || "EUR", env: process.env.PAYPAL_ENV || "live" });
}
