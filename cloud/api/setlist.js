import { setSession, getSession, setState, getState, setLibrary, getLibrary, pushCommand, popCommands } from "../lib/store.js";

// Scaletta remota: l'app Mixfonia pubblica stato e libreria (con il token segreto), il telefono legge lo stato
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
    // ---- pagina pubblica /canta: senza PIN, solo lettura + prenotazioni (la sessione stessa è il segreto) ----
    if (op === "plib" || op === "pstate" || op === "preq") {
      if (!ok(session)) return res.status(400).json({ error: "sessione" });
      const meta = await getSession(session);
      if (!meta) return res.status(404).json({ error: "Serata non attiva" });
      if (op === "plib") return res.json({ tracks: (await getLibrary(session)) || [] });
      if (op === "pstate") {
        const st = (await getState(session)) || {};
        const now = [st.decks?.a, st.decks?.b].filter(d => d && d.playing && d.title).map(d => (d.singer ? d.singer + " — " : "") + d.title + (d.artist ? " · " + d.artist : ""));
        const next = (st.queue || []).slice(0, 5).map(q => (q.singer ? q.singer + " — " : "") + q.title + (q.artist ? " · " + q.artist : ""));
        return res.json({ now, next });
      }
      const b = body(req);
      const singer = String(b.singer || "").trim().slice(0, 40), note = String(b.note || "").trim().slice(0, 140), title = String(b.title || "").trim().slice(0, 120);
      if (!b.id && !title) return res.status(400).json({ error: "brano" });
      await pushCommand(session, { cmd: "request", id: b.id || null, title, singer, note, at: Date.now() });
      return res.json({ ok: true });
    }
    const pin = String(req.query?.pin || (req.method === "POST" ? body(req).pin : "") || "");
    if (!ok(session)) return res.status(400).json({ error: "sessione" });
    const meta = await getSession(session);
    if (!meta) return res.status(404).json({ error: "Serata non attiva: riapri la finestra Scaletta remota in Mixfonia" });
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
