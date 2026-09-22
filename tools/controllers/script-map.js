// euristiche per i controlli legati a script (nome della funzione + descrizione + gruppo)
module.exports = function scriptAction(d, key, desc, group, number) {
  const eqGroup = /EqualizerRack/.test(group || "");
  const k = key.replace(/^[A-Za-z0-9_]+\./, "").toLowerCase();   // via il prefisso "PioneerDDJSB3."
  const ds = (desc || "").toLowerCase();
  if (/shift/.test(k) && !/shifttoggle/.test(k)) return null;      // varianti con SHIFT: no
  if (/lsb/.test(k)) return null;
  if (/rightdeck\./.test(k)) d = "b"; else if (/leftdeck\./.test(k)) d = "a";
  const kk = k.replace(/^decks?\[\d\]\./, "").replace(/^deck[ab]\./, "");
  const idx = /\[(\d+)\]/.exec(kk);
  const hc = /hot ?cue ?(\d)/.exec(ds) || /pad ?(\d)\b/.exec(ds);
  if (/samplerbutton|sampler.*pad|customsampleplayback/.test(k)) { const n = idx ? Number(idx[1]) : hc ? Number(hc[1]) : 0; return n >= 1 && n <= 12 ? `pad${n}` : null; }
  if (/hotcue|padunit\.padscontainer\.pads\[|padsection\.padpress|padbuttons/.test(k)) {
    let n = idx ? Number(idx[1]) : hc ? Number(hc[1]) : -1;
    if (/pads\[/.test(k) && idx) n = Number(idx[1]) + 1;             // pads[0..7] → 1..8
    return n >= 1 && n <= 8 && d ? `${d}.hotcue${n}` : null;
  }
  // descrizioni esplicite (mappature tutte a script, es. Inpulse 300 MK2 "DJCi300.*")
  if (d && /\bplay button\b/.test(ds)) return `${d}.play`;
  if (d && /\bcue button\b/.test(ds)) return `${d}.cue`;
  if (d && /\bsync button\b/.test(ds)) return `${d}.sync`;
  if (d && /^loop on\b/.test(ds)) return `${d}.loop4`;
  if (d && /^loop (\d+) beat/.test(ds)) { const n = Number(RegExp.$1); return n === 1 ? `${d}.loop1` : n === 2 ? `${d}.loop2` : n === 4 ? `${d}.loop4` : n === 8 || n === 16 ? `${d}.loop8` : null; }
  if (d && /^pad (\d)$/.test(ds) && typeof number === "number") {
    // famiglia Hercules Inpulse: il banco è nel nibble alto della nota (0x0_ hot cue, 0x1_ loop, 0x7_ beat jump)
    const n = Number(RegExp.$1), bank = number >> 4;
    if (bank === 0 && n <= 8) return `${d}.hotcue${n}`;
    if (bank === 1) return n === 1 ? `${d}.loop1` : n === 2 ? `${d}.loop2` : n === 3 ? `${d}.loop4` : n === 4 ? `${d}.loop8` : null;
    if (bank === 7) return n === 1 ? `${d}.jumpback4` : n === 2 ? `${d}.jumpfwd4` : n === 3 ? `${d}.jumpback8` : n === 4 ? `${d}.jumpfwd8` : null;
    return null;
  }
  if (d && /eqknob\[(\d)\]/.test(k)) { const n = Number(RegExp.$1); return n === 1 ? `${d}.eqlow` : n === 2 ? `${d}.eqmid` : n === 3 ? `${d}.eqhigh` : null; }
  if (!d && /movelibrary|movevertical|libraryknob|browserknob/.test(k)) return "browse";
  if (!d && /browser button/.test(ds) && /press/.test(k)) return "browseload";
  // vecchie Hercules (RMX, Steel, Compact, Mk2/Mk4): funzioni con nomi piatti
  if (d && /ratemsb|(^|\.)pitch$/.test(k)) return `${d}.tempo`;
  if (d && /ratereset/.test(k)) return `${d}.temporeset`;
  if (d && /beatsync/.test(k)) return `${d}.sync`;
  if (d && /(^|\.)stop$/.test(k)) return `${d}.stop`;
  if (d && /killhigh|killtreble/.test(k)) return `${d}.killhigh`;
  if (d && /killmid/.test(k)) return `${d}.killmid`;
  if (d && /killlow|killbass/.test(k)) return `${d}.killlow`;
  if (d && /(^|\.)treble$/.test(k)) return `${d}.eqhigh`;
  if (d && /(^|\.)medium$/.test(k)) return `${d}.eqmid`;
  if (d && /(^|\.)bass$/.test(k)) return `${d}.eqlow`;
  if (d && /keypad(\d)$/.test(k)) { const n = Number(RegExp.$1); return n <= 8 ? `${d}.hotcue${n}` : null; }
  if (!d && /\[playlist\]/i.test(group || "") && /(^|\.)up$/.test(k)) return "browseup";
  if (!d && /\[playlist\]/i.test(group || "") && /(^|\.)down$/.test(k)) return "browsedown";
  if (!d && /\[master\]/i.test(group || "") && /(^|\.)volume$/.test(k)) return "master";
  if (/fx123toggle|fxtoggle/.test(k)) return d ? `${d}.echo` : null;
  if (!d) {
    if (/crossfader/.test(k)) return "crossfader";
    if (/browse.*(encoder|knob|turn)|browseencoder\.input$/.test(k)) return "browse";
    if (/master.*(volume|level)/.test(k)) return "master";
    if (/head.*mix|cue.*mix/.test(k)) return "cuemix";
    if (/head.*(volume|gain)/.test(k)) return "cuevolume";
    return null;
  }
  if (eqGroup) {
    if (/treble|high|(^|\.)hi(\.|$)/.test(k)) return `${d}.eqhigh`;
    if (/(^|\.)mid/.test(k)) return `${d}.eqmid`;
    if (/bass|(^|\.)low/.test(k)) return `${d}.eqlow`;
  }
  if (/jog.*touch|wheeltouch|platter.*touch|scratch\.input/.test(k)) return `${d}.jogtouch`;
  if (/jog|wheel|platter/.test(k)) return `${d}.jog`;
  if (/spinback|backspin/.test(k)) return `${d}.backspin`;
  if (/brake/.test(k)) return `${d}.brake`;
  if (/playbutton|(^|\.)play(\.input)?$/.test(k)) return `${d}.play`;
  if (/headphone|pfl/.test(k)) return `${d}.cuepfl`;
  if (/cuebutton|(^|\.)cue(\.input)?$/.test(k)) return `${d}.cue`;
  if (/synclong|syncmaster/.test(k)) return null;
  if (/syncbutton|syncpressed|(^|\.)sync(\.input)?$/.test(k)) return `${d}.sync`;
  if (/temposlider|tempofader|pitchfader|pitchslider|ratefader|rateslider|(^|\.)rate(\.|$)/.test(k)) return `${d}.tempo`;
  if (/deckfader|volume|linefader|channelfader/.test(k)) return `${d}.fader`;
  if (/pregain|(^|\.)gain|trim/.test(k)) return `${d}.volume`;
  if (/loadbutton|loadtrack|(^|\.)load(\.input)?$/.test(k)) return d === "a" ? "loadA" : "loadB";
  if (/keylock/.test(k)) return `${d}.keylock`;
  if (/keyreset/.test(k)) return `${d}.keyreset`;
  if (/pitchbend(up|plus)/.test(k)) return `${d}.nudgeup`;
  if (/pitchbend(down|minus)/.test(k)) return `${d}.nudgedown`;
  if (/loophalf|loophalve/.test(k)) return `${d}.loophalf`;
  if (/loopdouble/.test(k)) return `${d}.loopdouble`;
  if (/reloop|loopexit/.test(k)) return `${d}.loopexit`;
  if (/autoloop|(^|\.)loop(\.input|button)?$/.test(k)) return `${d}.loop4`;
  if (/quant/.test(k)) return `${d}.quantize`;
  if (/reverse|censor/.test(k)) return `${d}.rev`;
  if (/filter/.test(k)) return `${d}.filtervalue`;
  return null;
};
