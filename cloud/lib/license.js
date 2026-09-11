import crypto from "node:crypto";

// Stesso formato dell'app (LicenseService.cs): payload "nome\nmacchina\ndata\nnota", firma ECDSA P-256 / SHA-256 in formato IEEE P1363.
export function issueLicense({ name, machine, note }) {
  const pem = process.env.VOXA_LICENSE_PRIVATE_KEY;
  if (!pem) throw new Error("VOXA_LICENSE_PRIVATE_KEY mancante");
  const issued = new Date().toISOString().slice(0, 10);
  const payload = `${name}\n${machine}\n${issued}\n${note ?? ""}`;
  const sig = crypto.sign("sha256", Buffer.from(payload, "utf8"), { key: pem.replace(/\n/g, "\n"), dsaEncoding: "ieee-p1363" });
  const json = JSON.stringify({ n: name, m: machine, i: issued, note: note ?? null, s: sig.toString("base64") });
  return Buffer.from(json, "utf8").toString("base64");
}

export function normalizeMachine(m) {
  const s = String(m ?? "").trim().toUpperCase();
  return /^VOXA-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$/.test(s) ? s : null;
}
