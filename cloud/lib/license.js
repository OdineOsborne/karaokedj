import crypto from "node:crypto";

// Stesso formato dell'app (LicenseService.cs): firma ECDSA P-256 / SHA-256 in formato IEEE P1363.
//   chiave v2:            {n,m,i,note,a,u,s}   payload "n\nm\ni\nnote\na\nu"
//   token aggiornamenti:  {t:"upd",a,u,i,s}    payload "upd\na\nu\ni"
function sign(payload) {
  const pem = process.env.VOXA_LICENSE_PRIVATE_KEY;
  if (!pem) throw new Error("VOXA_LICENSE_PRIVATE_KEY mancante");
  return crypto.sign("sha256", Buffer.from(payload, "utf8"), { key: pem.replace(/\\n/g, "\n"), dsaEncoding: "ieee-p1363" }).toString("base64");
}
const today = () => new Date().toISOString().slice(0, 10);
const b64 = o => Buffer.from(JSON.stringify(o), "utf8").toString("base64");

export function issueLicense({ name, machine, account, until, note }) {
  const issued = today(), a = normalizeEmail(account) ?? "";
  const s = sign(`${name}\n${machine}\n${issued}\n${note ?? ""}\n${a}\n${until}`);
  return b64({ n: name, m: machine, i: issued, note: note ?? null, a, u: until, s });
}

export function issueToken({ account, until }) {
  const issued = today(), a = normalizeEmail(account) ?? "";
  return b64({ t: "upd", a, u: until, i: issued, s: sign(`upd\n${a}\n${until}\n${issued}`) });
}

export function normalizeMachine(m) {
  const s = String(m ?? "").trim().toUpperCase();
  return /^VOXA-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(s) ? s : null;
}
export function normalizeEmail(e) {
  const s = String(e ?? "").trim().toLowerCase();
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(s) && s.length <= 120 ? s : null;
}
export const maskMachine = m => m.slice(0, 9) + "-****-****";

/** "AAAA-MM-GG" + anni, partendo dal più tardi fra oggi e la data data (rinnovo anticipato non perde giorni). */
export function extendYears(until, years = 1) {
  const base = new Date(Math.max(Date.now(), until ? Date.parse(until + "T00:00:00Z") : 0));
  base.setUTCFullYear(base.getUTCFullYear() + years);
  return base.toISOString().slice(0, 10);
}
