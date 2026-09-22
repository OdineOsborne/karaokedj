import { Redis } from "@upstash/redis";

// Statistiche d'uso anonime: riceve i passaggi fra brani delle serate (solo se il DJ ha acconsentito nell'app)
// e restituisce il modello aggregato che l'app usa per suggerire meglio.
//
// Non riceviamo né salviamo: nomi di cantanti o persone, percorsi di file, email, licenze, posizione, nome del locale.
// Teniamo solo contatori per coppia di ARTISTI (non i titoli): "dopo X di solito funziona Y".
// L'id installazione serve solo a limitare gli abusi e a non contare mille volte lo stesso PC: è un codice casuale.

let redis;
function db() {
  if (!redis) {
    const url = process.env.KV_REST_API_URL || process.env.UPSTASH_REDIS_REST_URL;
    const token = process.env.KV_REST_API_TOKEN || process.env.UPSTASH_REDIS_REST_TOKEN;
    if (!url || !token) throw new Error("Redis non configurato");
    redis = new Redis({ url, token });
  }
  return redis;
}

const norm = s => (s || "").toString().toLowerCase().normalize("NFD").replace(/[̀-ͯ]/g, "")
  .replace(/[^a-z0-9 ]+/g, " ").replace(/\s+/g, " ").trim().slice(0, 60);

// quanto pesa un evento: un brano fatto suonare fino in fondo vale più di uno messo in coda; uno scartato toglie
const WEIGHT = { played: 1, accepted: 1.5, rejected: -2 };

export default async function handler(req, res) {
  try {
    if (req.method === "GET") {
      // modello pubblico: coppie "artista>artista" → fattore 0.8…1.25, nient'altro
      const model = (await db().get("learn:model")) || {};
      res.setHeader("Cache-Control", "public, max-age=3600");
      return res.status(200).json(model);
    }
    if (req.method !== "POST") return res.status(405).json({ error: "metodo non ammesso" });

    const { install, version, events } = req.body || {};
    if (!install || !Array.isArray(events)) return res.status(400).json({ error: "richiesta non valida" });
    if (events.length > 500) return res.status(413).json({ error: "troppi eventi" });

    // un tetto per installazione al giorno: evita che un singolo PC sposti le statistiche
    const day = new Date().toISOString().slice(0, 10);
    const cap = await db().incrby(`learn:cap:${install}:${day}`, events.length);
    await db().expire(`learn:cap:${install}:${day}`, 60 * 60 * 48);
    if (cap > 2000) return res.status(200).json({ ok: true, ignored: true });

    let counted = 0;
    for (const e of events) {
      const from = norm(e.fromArtist ?? e.FromArtist);
      const to = norm(e.toArtist ?? e.ToArtist);
      if (!from || !to || from === to) continue;              // stesso artista: non insegna niente
      const kind = (e.kind ?? e.Kind ?? "played").toString();
      const w = WEIGHT[kind] ?? 0;
      if (!w) continue;
      const bonus = (e.completed ?? e.Completed) ? 0.5 : 0;
      const key = `${from}>${to}`;
      await db().hincrbyfloat("learn:pairs", key, w + bonus);
      await db().hincrby("learn:seen", key, 1);
      counted++;
    }
    await db().hincrby("learn:versions", (version || "?").toString().slice(0, 12), 1);
    res.status(200).json({ ok: true, counted });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
}
