// Stampa i controlli di una mappatura djay (.djayMidiMapping, plist XML)
const fs = require("fs");
const x = fs.readFileSync(process.argv[2], "utf8").replace(/\r/g, "");
const rows = [];
for (const c of x.split("<dict>").slice(1)) {
  const g = (k, t) => { const r = new RegExp("<key>" + k + "</key>\\s*<" + t + ">([^<]*)</" + t + ">").exec(c); return r ? r[1] : null; };
  const kp = g("keyPath", "string"); if (!kp) continue;
  rows.push([g("midiMessageType", "integer"), g("midiChannel", "integer"), g("midiData", "integer"), kp, /<key>flipped<\/key>\s*<true\/>/.test(c) ? "flip" : "", g("controlType", "string") || "", g("relative", "integer") || ""]);
}
rows.sort((a, b) => a[0] - b[0] || a[1] - b[1] || a[2] - b[2]);
for (const r of rows) console.log(r.join("\t"));
console.log(rows.length);
