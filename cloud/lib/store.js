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
