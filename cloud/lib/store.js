import { Redis } from "@upstash/redis";

let redis;
function db() {
  if (!redis) {
    const url = process.env.KV_REST_API_URL || process.env.UPSTASH_REDIS_REST_URL;
    const token = process.env.KV_REST_API_TOKEN || process.env.UPSTASH_REDIS_REST_TOKEN;
    if (!url || !token) throw new Error("Redis non configurato (KV_REST_API_URL / KV_REST_API_TOKEN)");
    redis = new Redis({ url, token });
  }
  return redis;
}

export async function saveLicense(machine, record) { await db().set(`lic:${machine}`, record); await db().lpush("lic:log", JSON.stringify({ ...record, machine, at: Date.now() })); }
export async function getLicense(machine) { return db().get(`lic:${machine}`); }
export async function saveMessage(event, msg) { await db().rpush(`msg:${event}`, JSON.stringify(msg)); }
export async function getMessages(event) { return (await db().lrange(`msg:${event}`, 0, -1)).map(s => (typeof s === "string" ? JSON.parse(s) : s)); }

// ---- scaletta remota (sessione della serata) ----
const TTL = 60 * 60 * 14; // 14 ore: una serata
export async function setSession(id, meta) { await db().set(`set:${id}:meta`, meta, { ex: TTL }); }
export async function getSession(id) { return db().get(`set:${id}:meta`); }
export async function setState(id, state) { await db().set(`set:${id}:state`, state, { ex: TTL }); }
export async function getState(id) { return db().get(`set:${id}:state`); }
export async function setLibrary(id, lib) { await db().set(`set:${id}:lib`, lib, { ex: TTL }); }
export async function getLibrary(id) { return db().get(`set:${id}:lib`); }
export async function pushCommand(id, cmd) { await db().rpush(`set:${id}:cmd`, JSON.stringify(cmd)); await db().expire(`set:${id}:cmd`, TTL); }
export async function popCommands(id) {
  const key = `set:${id}:cmd`;
  const items = await db().lrange(key, 0, -1);
  if (items.length) await db().ltrim(key, items.length, -1);
  return items.map(s => (typeof s === "string" ? JSON.parse(s) : s));
}
