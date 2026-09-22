import { Redis } from "@upstash/redis";

// Ricalcola il modello pubblico dai contatori: coppia di artisti → fattore 0,8…1,25.
// Da chiamare ogni tanto (cron di Vercel o a mano con ?key=...): tiene solo le coppie viste da abbastanza serate,
// così una singola serata strana non sposta i suggerimenti di tutti.

let redis;
function db() {
  if (!redis) {
    redis = new Redis({
      url: process.env.KV_REST_API_URL || process.env.UPSTASH_REDIS_REST_URL,
      token: process.env.KV_REST_API_TOKEN || process.env.UPSTASH_REDIS_REST_TOKEN,
    });
  }
  return redis;
}

const MIN_SEEN = 4;      // sotto questo numero di osservazioni non generalizziamo
const MAX_PAIRS = 5000;  // il file che scaricano le app resta piccolo

export default async function handler(req, res) {
  try {
    const secret = process.env.LEARN_BUILD_KEY;
    if (secret && req.query.key !== secret) return res.status(401).json({ error: "non autorizzato" });

    const pairs = (await db().hgetall("learn:pairs")) || {};
    const seen = (await db().hgetall("learn:seen")) || {};
    const model = {};
    const rows = Object.entries(pairs)
      .map(([k, v]) => ({ k, score: Number(v) || 0, n: Number(seen[k]) || 0 }))
      .filter(r => r.n >= MIN_SEEN)
      .sort((a, b) => Math.abs(b.score) - Math.abs(a.score))
      .slice(0, MAX_PAIRS);

    for (const r of rows) {
      const avg = r.score / r.n;                       // media per osservazione: −2 … +2
      const factor = Math.min(1.25, Math.max(0.8, 1 + avg * 0.12));
      if (Math.abs(factor - 1) > 0.02) model[r.k] = Math.round(factor * 1000) / 1000;
    }
    await db().set("learn:model", model);
    res.status(200).json({ ok: true, pairs: Object.keys(model).length, considered: rows.length });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
}
