#!/usr/bin/env node
// Crea (o controlla) prodotti, prezzi e webhook di VOXA su Stripe. Come EndurancePRO: ogni prezzo ha una
// lookup_key con cui api/checkout.js lo cerca; cambiare un prezzo = nuovo Price con la stessa chiave.
//   STRIPE_SECRET_KEY=sk_... node scripts/stripe-setup.mjs            # crea quello che manca
//   STRIPE_SECRET_KEY=sk_... node scripts/stripe-setup.mjs --check    # solo elenco
// Il webhook viene creato su APP_URL (default https://voxa-cloud.vercel.app) e lo script stampa il signing secret
// da mettere in Vercel: vercel env add STRIPE_WEBHOOK_SECRET production
const KEY = process.env.STRIPE_SECRET_KEY;
if (!KEY) { console.error("manca STRIPE_SECRET_KEY nell'ambiente"); process.exit(1); }
const check = process.argv.includes("--check");
const APP_URL = (process.env.APP_URL || "https://voxa-cloud.vercel.app").replace(/\/$/, "");
console.log("Stripe:", KEY.startsWith("sk_live") ? "LIVE" : "test");

const PRODOTTI = [
  { id: "license", nome: "VOXA – Licenza", desc: "Licenza perpetua per 1 PC con 1 anno di aggiornamenti", prezzi: [["voxa_license", 2000, null]] },
  { id: "seat", nome: "VOXA – PC aggiuntivo", desc: "Secondo o terzo PC sullo stesso account", prezzi: [["voxa_seat", 1000, null]] },
  { id: "updates", nome: "VOXA – Aggiornamenti", desc: "Un anno di aggiornamenti per tutti i PC dell'account", prezzi: [["voxa_updates", 1000, "year"]] },
  { id: "transfer", nome: "VOXA – Trasferimento licenza", desc: "Sposta la licenza da un PC a un altro (1 € + commissioni)", prezzi: [["voxa_transfer", 130, null]] },
];

async function api(path, params) {
  const res = await fetch("https://api.stripe.com/v1/" + path, {
    method: params ? "POST" : "GET",
    headers: { Authorization: "Basic " + Buffer.from(KEY + ":").toString("base64"), "Content-Type": "application/x-www-form-urlencoded" },
    body: params ? new URLSearchParams(params).toString() : undefined,
  });
  const j = await res.json();
  if (!res.ok) throw new Error(path + ": " + (j.error?.message ?? res.status));
  return j;
}

for (const p of PRODOTTI) {
  const found = await api(`products/search?query=${encodeURIComponent(`metadata['voxa']:'${p.id}'`)}`);
  let prod = found.data[0];
  if (!prod && !check) prod = await api("products", { name: p.nome, description: p.desc, "metadata[voxa]": p.id, tax_code: "txcd_10103001" });
  console.log(`${p.nome}: ${prod ? prod.id : "(da creare)"}`);
  for (const [key, cents, interval] of p.prezzi) {
    const ex = await api(`prices?lookup_keys[]=${key}&active=true&limit=1`);
    const cur = ex.data[0];
    if (cur && cur.unit_amount === cents && (cur.recurring?.interval ?? null) === interval) { console.log(`  ${key}: ok (${cents / 100} €)`); continue; }
    if (check || !prod) { console.log(`  ${key}: ${cur ? "DIVERSO (" + cur.unit_amount / 100 + " €)" : "manca"} → ${cents / 100} €`); continue; }
    const params = { product: prod.id, currency: "eur", unit_amount: String(cents), lookup_key: key, transfer_lookup_key: "true", tax_behavior: "inclusive" };
    if (interval) params["recurring[interval]"] = interval;
    const np = await api("prices", params);
    console.log(`  ${key}: creato ${np.id} (${cents / 100} €)`);
  }
}

// webhook
const url = `${APP_URL}/api/stripe-webhook`;
const hooks = await api("webhook_endpoints?limit=100");
const have = hooks.data.find(h => h.url === url);
if (have) console.log(`webhook: ok (${have.id}) – il secret si vede solo alla creazione; se manca in Vercel, cancellalo dalla dashboard e rilancia`);
else if (check) console.log(`webhook: manca (${url})`);
else {
  const h = await api("webhook_endpoints", { url, "enabled_events[]": ["checkout.session.completed", "invoice.paid", "customer.subscription.deleted"], description: "VOXA cloud" });
  console.log(`webhook: creato ${h.id}\n  STRIPE_WEBHOOK_SECRET=${h.secret}\n  → vercel env add STRIPE_WEBHOOK_SECRET production`);
}
