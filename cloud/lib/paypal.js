const BASE = process.env.PAYPAL_ENV === "sandbox" ? "https://api-m.sandbox.paypal.com" : "https://api-m.paypal.com";

async function token() {
  const id = process.env.PAYPAL_CLIENT_ID, secret = process.env.PAYPAL_CLIENT_SECRET;
  if (!id || !secret) throw new Error("PAYPAL_CLIENT_ID / PAYPAL_CLIENT_SECRET mancanti");
  const r = await fetch(`${BASE}/v1/oauth2/token`, {
    method: "POST",
    headers: { Authorization: "Basic " + Buffer.from(`${id}:${secret}`).toString("base64"), "Content-Type": "application/x-www-form-urlencoded" },
    body: "grant_type=client_credentials",
  });
  if (!r.ok) throw new Error("PayPal auth " + r.status);
  return (await r.json()).access_token;
}

export async function createOrder({ amount, currency, machine }) {
  const t = await token();
  const r = await fetch(`${BASE}/v2/checkout/orders`, {
    method: "POST",
    headers: { Authorization: `Bearer ${t}`, "Content-Type": "application/json" },
    body: JSON.stringify({
      intent: "CAPTURE",
      purchase_units: [{
        custom_id: machine,
        description: `Donazione VOXA – licenza per ${machine}`,
        amount: { currency_code: currency, value: amount },
      }],
      application_context: { brand_name: "VOXA", shipping_preference: "NO_SHIPPING", user_action: "PAY_NOW" },
    }),
  });
  const j = await r.json();
  if (!r.ok) throw new Error("PayPal order: " + JSON.stringify(j));
  return j;
}

export async function captureOrder(orderId) {
  const t = await token();
  const r = await fetch(`${BASE}/v2/checkout/orders/${orderId}/capture`, {
    method: "POST", headers: { Authorization: `Bearer ${t}`, "Content-Type": "application/json" },
  });
  const j = await r.json();
  if (!r.ok && j?.details?.[0]?.issue !== "ORDER_ALREADY_CAPTURED") throw new Error("PayPal capture: " + JSON.stringify(j));
  return j;
}

export async function getOrder(orderId) {
  const t = await token();
  const r = await fetch(`${BASE}/v2/checkout/orders/${orderId}`, { headers: { Authorization: `Bearer ${t}` } });
  return r.ok ? r.json() : null;
}

export async function verifyWebhook(headers, body) {
  const t = await token();
  const r = await fetch(`${BASE}/v1/notifications/verify-webhook-signature`, {
    method: "POST", headers: { Authorization: `Bearer ${t}`, "Content-Type": "application/json" },
    body: JSON.stringify({
      auth_algo: headers["paypal-auth-algo"], cert_url: headers["paypal-cert-url"], transmission_id: headers["paypal-transmission-id"],
      transmission_sig: headers["paypal-transmission-sig"], transmission_time: headers["paypal-transmission-time"],
      webhook_id: process.env.PAYPAL_WEBHOOK_ID, webhook_event: body,
    }),
  });
  const j = await r.json();
  return j.verification_status === "SUCCESS";
}
