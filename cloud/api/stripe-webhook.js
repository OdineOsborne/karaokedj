import { stripe } from "../lib/stripe.js";
import { fulfill } from "../lib/fulfill.js";
import { claimEvent, getAccount, saveAccount } from "../lib/store.js";
import { normalizeEmail, normalizeMachine } from "../lib/license.js";

// Dal pagamento alla licenza. Firma Stripe verificata sul corpo grezzo (per questo la firma Web: request.text()).
//   checkout.session.completed (mode=payment)  → licenza / PC aggiuntivo / trasferimento
//   invoice.paid (abbonamento, prima rata e rinnovi) → +1 anno di aggiornamenti per l'account
//   customer.subscription.deleted → l'account resta, la scadenza non cambia: semplicemente non si rinnova più
export async function POST(request) {
  const raw = await request.text();
  const sig = request.headers.get("stripe-signature") ?? "";
  let event;
  try { event = stripe().webhooks.constructEvent(raw, sig, process.env.STRIPE_WEBHOOK_SECRET ?? ""); }
  catch (e) { return Response.json({ error: "firma non valida: " + e.message }, { status: 400 }); }
  if (!(await claimEvent(event.id))) return Response.json({ ok: true, already: true });

  try {
    const o = event.data.object;
    switch (event.type) {
      case "checkout.session.completed": {
        if (o.mode !== "payment" || o.payment_status !== "paid") break;
        const md = o.metadata ?? {};
        const email = normalizeEmail(md.email ?? o.customer_details?.email);
        if (!email) break;
        const r = await fulfill({
          product: md.product, email, machine: normalizeMachine(md.machine), from: md.from ? normalizeMachine(md.from) : null,
          name: o.customer_details?.name ?? null, sessionId: o.id, amount: o.amount_total, currency: o.currency,
          customerId: typeof o.customer === "string" ? o.customer : o.customer?.id,
        });
        return Response.json({ ok: true, ...r, key: undefined, token: undefined });
      }
      case "invoice.paid": {
        const md = o.subscription_details?.metadata ?? o.parent?.subscription_details?.metadata ?? o.lines?.data?.[0]?.metadata ?? {};
        const subId = typeof o.subscription === "string" ? o.subscription : o.subscription?.id ?? o.parent?.subscription_details?.subscription ?? null;
        let email = normalizeEmail(md.email ?? o.customer_email);
        if (!email && subId) { const s = await stripe().subscriptions.retrieve(subId); email = normalizeEmail(s.metadata?.email); }
        if (!email) break;
        const r = await fulfill({ product: "voxa_updates", email, name: o.customer_name ?? null, sessionId: o.id, amount: o.amount_paid, currency: o.currency, subscriptionId: subId, customerId: typeof o.customer === "string" ? o.customer : o.customer?.id });
        return Response.json({ ok: true, updatesUntil: r.updatesUntil });
      }
      case "customer.subscription.deleted": {
        const email = normalizeEmail(o.metadata?.email);
        const acc = email ? await getAccount(email) : null;
        if (acc && acc.subscriptionId === o.id) { acc.subscriptionId = null; acc.history.push({ at: Date.now(), product: "voxa_updates", cancelled: true }); await saveAccount(acc); }
        break;
      }
    }
    return Response.json({ ok: true, ignored: event.type });
  } catch (e) { return Response.json({ error: String(e.message || e) }, { status: 500 }); }
}
