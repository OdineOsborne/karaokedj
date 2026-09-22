import Stripe from "stripe";

// La cassa (Stripe), come EndurancePRO: articoli = lookup_key dei prezzi creati da scripts/stripe-setup.mjs.
//   voxa_license   20 €  una tantum: licenza perpetua per 1 PC + 1 anno di aggiornamenti (apre l'account)
//   voxa_seat      10 €  una tantum: 2° o 3° PC sullo stesso account (max 3)
//   voxa_updates   10 €/anno abbonamento: aggiornamenti per tutti i PC dell'account
//   voxa_transfer  1,30 € una tantum (1 € + commissioni): sposta una licenza da un PC a un altro
export const PRODUCTS = {
  voxa_license: { cents: 2000, mode: "payment", label: "Licenza Mixfonia (1 PC, 1 anno di aggiornamenti)" },
  voxa_seat: { cents: 1000, mode: "payment", label: "PC aggiuntivo" },
  voxa_updates: { cents: 1000, mode: "subscription", label: "Aggiornamenti annuali" },
  voxa_transfer: { cents: 130, mode: "payment", label: "Trasferimento licenza" },
};

let client;
export function stripe() {
  if (!client) {
    if (!process.env.STRIPE_SECRET_KEY) throw new Error("STRIPE_SECRET_KEY mancante");
    client = new Stripe(process.env.STRIPE_SECRET_KEY);
  }
  return client;
}

export async function priceId(key) {
  const r = await stripe().prices.list({ lookup_keys: [key], active: true, limit: 1 });
  if (!r.data[0]) throw new Error("prezzo non configurato: " + key + " (lancia scripts/stripe-setup.mjs)");
  return r.data[0].id;
}

/** Un Customer Stripe per account (email), riusato per portale e fatture. */
export async function customerFor(acc) {
  if (acc.customerId) return acc.customerId;
  const ex = await stripe().customers.list({ email: acc.email, limit: 1 });
  const c = ex.data[0] ?? await stripe().customers.create({ email: acc.email, name: acc.name ?? undefined, metadata: { voxa: acc.email } });
  acc.customerId = c.id;
  return c.id;
}
