import { issueLicense, issueToken, extendYears, maskMachine } from "./license.js";
import { getAccount, saveAccount, getLicense, saveLicense } from "./store.js";

export const MAX_SEATS = 3;
const today = () => new Date().toISOString().slice(0, 10);

/** Stato di un account per la pagina: cosa può comprare da questa macchina. */
export async function accountStatus(email, machine) {
  const acc = email ? await getAccount(email) : null;
  const lic = machine ? await getLicense(machine) : null;
  const licensed = !!(lic?.key && !lic.revoked);
  const seats = acc?.seats ?? [];
  const mine = licensed && (!lic.account || lic.account === email);
  return {
    exists: !!acc, name: acc?.name ?? null, seats: seats.length, maxSeats: MAX_SEATS,
    machines: seats.map(s => maskMachine(s.machine)),
    updatesUntil: acc?.updatesUntil ?? null, subscription: !!acc?.subscriptionId,
    thisMachine: licensed ? (mine ? "mine" : "other") : "free",
    can: {
      license: !licensed && seats.length === 0,
      seat: !licensed && seats.length > 0 && seats.length < MAX_SEATS,
      transfer: !licensed && seats.length > 0,
      updates: seats.length > 0 && !acc?.subscriptionId,
    },
  };
}

/** Controlla che l'acquisto abbia senso PRIMA di aprire la cassa. Ritorna un messaggio d'errore o null. */
export async function validate({ product, email, machine, from }) {
  const st = await accountStatus(email, machine);
  switch (product) {
    case "voxa_license":
      if (st.can.license) return null;
      return st.thisMachine !== "free" ? "Questo PC ha già una licenza." : "Hai già una licenza: aggiungi questo PC (10 €) invece di comprarne un'altra.";
    case "voxa_seat":
      if (st.can.seat) return null;
      return st.seats >= MAX_SEATS ? `Hai già ${MAX_SEATS} PC: trasferisci una licenza (1,30 €).` : "Prima serve la licenza (20 €).";
    case "voxa_updates":
      if (st.can.updates) return null;
      return st.subscription ? "L'abbonamento agli aggiornamenti è già attivo." : "Prima serve la licenza (20 €).";
    case "voxa_transfer": {
      if (!st.can.transfer) return "Questo PC ha già una licenza o l'account non ne ha.";
      const acc = await getAccount(email);
      if (!from || !acc.seats.some(s => s.machine === from)) return "L'ID del PC da cui trasferire non è fra quelli del tuo account (scrivilo per intero: lo trovi in ❤ Licenza sul vecchio PC).";
      return null;
    }
    default: return "articolo sconosciuto";
  }
}

/** Dà quello che l'articolo promette. Chi chiama garantisce l'idempotenza (claimEvent). */
export async function fulfill({ product, email, machine, from, name, sessionId, amount, currency, subscriptionId, customerId }) {
  const acc = (await getAccount(email)) ?? { email, name: name ?? null, seats: [], history: [], updatesUntil: null, token: null };
  if (name && !acc.name) acc.name = name;
  if (customerId) acc.customerId = customerId;
  const seatName = acc.name || email;
  const hist = { at: Date.now(), product, machine: machine ?? null, from: from ?? null, amount, currency, sessionId };
  const addSeat = async (m, note) => {
    const key = issueLicense({ name: seatName, machine: m, account: email, until: acc.updatesUntil, note });
    acc.seats.push({ machine: m, key, issued: today(), note });
    await saveLicense(m, { key, name: seatName, account: email, via: "stripe" });
    return key;
  };

  switch (product) {
    case "voxa_license": {
      if (acc.seats.some(s => s.machine === machine)) return { already: true };
      acc.updatesUntil = extendYears(acc.updatesUntil, 1);
      acc.token = issueToken({ account: email, until: acc.updatesUntil });
      const key = await addSeat(machine, `stripe:${sessionId}`);
      acc.history.push(hist); await saveAccount(acc);
      return { key, token: acc.token, updatesUntil: acc.updatesUntil };
    }
    case "voxa_seat": {
      if (acc.seats.some(s => s.machine === machine)) return { already: true };
      if (acc.seats.length >= MAX_SEATS) throw new Error("account già a " + MAX_SEATS + " PC");
      const key = await addSeat(machine, `stripe:${sessionId}`);
      acc.history.push(hist); await saveAccount(acc);
      return { key, token: acc.token, updatesUntil: acc.updatesUntil };
    }
    case "voxa_transfer": {
      const old = acc.seats.find(s => s.machine === from);
      if (!old) throw new Error("macchina di origine non nell'account");
      if (acc.seats.some(s => s.machine === machine)) return { already: true };
      acc.seats = acc.seats.filter(s => s.machine !== from);
      const oldLic = await getLicense(from);
      await saveLicense(from, { ...(oldLic ?? {}), revoked: true, movedTo: machine, at: Date.now() });
      const key = await addSeat(machine, `transfer:${from}`);
      acc.history.push(hist); await saveAccount(acc);
      return { key, token: acc.token, updatesUntil: acc.updatesUntil };
    }
    case "voxa_updates": {
      // un anno in più dal più tardi fra oggi e la scadenza; il token vale per tutte le macchine dell'account
      acc.updatesUntil = extendYears(acc.updatesUntil, 1);
      acc.token = issueToken({ account: email, until: acc.updatesUntil });
      if (subscriptionId) acc.subscriptionId = subscriptionId;
      acc.history.push(hist); await saveAccount(acc);
      return { token: acc.token, updatesUntil: acc.updatesUntil };
    }
    default: throw new Error("articolo sconosciuto " + product);
  }
}
