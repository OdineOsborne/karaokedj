import { Redis } from "@upstash/redis";

let redis;
function db() {
  if (!redis) {
    if (!process.env.UPSTASH_REDIS_REST_URL) throw new Error("Redis non configurato (UPSTASH_REDIS_REST_URL)");
    redis = Redis.fromEnv();
  }
  return redis;
}

export async function saveLicense(machine, record) { await db().set(`lic:${machine}`, record); await db().lpush("lic:log", JSON.stringify({ ...record, machine, at: Date.now() })); }
export async function getLicense(machine) { return db().get(`lic:${machine}`); }
export async function saveMessage(event, msg) { await db().rpush(`msg:${event}`, JSON.stringify(msg)); }
export async function getMessages(event) { return (await db().lrange(`msg:${event}`, 0, -1)).map(s => (typeof s === "string" ? JSON.parse(s) : s)); }
