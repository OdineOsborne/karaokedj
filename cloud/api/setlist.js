import { setSession, getSession, setState, getState, setLibrary, getLibrary, pushCommand, popCommands } from "../lib/store.js";

// Scaletta remota: l'app VOXA pubblica stato e libreria (con il token segreto), il telefono legge lo stato
// e manda comandi (con il PIN mostrato nell'app). Tutto passa da qui: ?op=push|lib|state|library|cmd|poll
function body(req) { return typeof req.body === "string" ? JSON.parse(req.body || "{}") : (req.body || {}); }
const ok = s => typeof s === "string" && /^[A-Za-z0-9]{6,32}$/.test(s);

export default async function handler(req, res) {
  const op = req.query?.op;
  try {
    if (op === "push") {               // app → stato corrente (crea/rinnova la sessione)
      const b = body(req);
      if (!ok(b.session) || !ok(b.token) || !/^\d{4,6}$/.test(String(b.pin || ""))) return res.status(400).json({ error: "parametri" });
      const meta = await getSession(b.session);
      if (meta && meta.token !== b.token) return res.status(403).json({ error: "token" });
      if (!meta) await setSession(b.session, { token: b.token, pin: String(b.pin), created: Date.now() });
      else if (meta.pin !== String(b.pin)) await setSession(b.session, { ...meta, pin: String(b.pin) });
      await setState(b.session, { ...b.state, at: Date.now() });
      return res.json({ ok: true });
    }
    if (op === "lib") {                // app → indice libreria (per la ricerca sul telefono)
      const b = body(req);
      const meta = await getSession(b.session);
      if (!meta || meta.token !== b.token) return res.status(403).json({ error: "token" });
      await setLibrary(b.session, b.tracks || []);
      return res.json({ ok: true, n: (b.tracks || []).length });
    }
    if (op === "poll") {               // app ← comandi accumulati dal telefono
      const b = body(req);
      const meta = await getSession(b.session);
      if (!meta || meta.token !== b.token) return res.status(403).json({ error: "token" });
      return res.json({ commands: await popCommands(b.session) });
    }
    const session = req.query?.s;
    const pin = String(req.query?.pin || (req.method === "POST" ? body(req).pin : "") || "");
    if (!ok(session)) return res.status(400).json({ error: "sessione" });
    const meta = await getSession(session);
    if (!meta) return res.status(404).json({ error: "Serata non attiva: riapri la finestra Scaletta remota in VOXA" });
    if (meta.pin !== pin) return res.status(403).json({ error: "PIN errato" });
    if (op === "state") return res.json((await getState(session)) || {});
    if (op === "library") return res.json({ tracks: (await getLibrary(session)) || [] });
    if (op === "cmd") {                // telefono → comando
      const b = body(req);
      if (!b.cmd) return res.status(400).json({ error: "comando" });
      await pushCommand(session, { cmd: b.cmd, id: b.id, index: b.index, to: b.to, singer: b.singer, at: Date.now() });
      return res.json({ ok: true });
    }
    res.status(400).json({ error: "op" });
  } catch (e) { res.status(500).json({ error: String(e.message || e) }); }
}
