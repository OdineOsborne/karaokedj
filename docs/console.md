# Console MIDI in Mixfonia — fai da te

Mixfonia riconosce da sola **42 console** (Pioneer, Hercules, Numark, Denon, Reloop, Roland, Novation Launchpad):
la colleghi e in 3 secondi la barra di stato dice *"Console riconosciuta: …"*. Tutto il resto lo puoi sistemare tu,
senza aspettare un aggiornamento. Impostazioni → **MIDI e tastiera**.

## 1. Un tasto fa la cosa sbagliata (o niente)
Muovi il controllo: la riga *"Ricevuto: … → …"* dice cosa arriva e cosa fa. Poi nella lista dei comandi trova
quello giusto, premi **Impara MIDI** e muovi il controllo. Fatto: la tua scelta vince sul preset e resta salvata.
- Fader o manopola che lavora **al contrario** → spunta **inverti** sulla riga.
- Manopola **senza fine corsa** (encoder: browser, loop, a volte il tempo) → spunta **encoder**.
- Tasto destro su un comando nell'app fa la stessa cosa di "Impara".
- **Mappatura di fabbrica** riporta la console al preset.

## 2. La tua console non è riconosciuta
Tre strade, dalla più veloce:
1. **Impara** i comandi che ti servono (play, cue, fader, EQ, crossfader, jog: 10 minuti).
2. **Importa mappatura…** da un file:
   - **Mixxx** (`*.midi.xml`): oltre 200 console pronte su
     https://github.com/mixxxdj/mixxx/tree/main/res/controllers — scarica il file della tua console e importalo.
     Mixfonia converte i numeri MIDI nelle sue funzioni (play, cue, fader, EQ, hot cue, loop, jog…); le funzioni che non ha
     equivalente vengono ignorate.
   - **djay** (`*.djayMidiMapping`): i file condivisi sul forum Algoriddim.
   - **Mixfonia** (`*.json`): un file esportato da un altro utente.
   Alla conferma metti il **nome della porta MIDI** (proposto in automatico se la console è collegata): da lì in poi è plug & play.
3. Chiedi: manda il nome esatto della console e, se ce l'hai, il documento "MIDI mapping" del produttore.

## 3. Condividi la tua correzione
**Esporta la mia mappatura…** salva preset + correzioni in un JSON. Mandacelo: lo aggiungiamo all'app per tutti.
**Cartella mappature** apre `%AppData%\KaraokeDJ\controllers`: un file per console, quelli tuoi vincono su quelli dell'app
(stesso `id`), e puoi modificarli con un editor di testo:

```json
{ "id": "mia-console", "name": "La mia console", "match": ["parte del nome della porta MIDI"],
  "mappings": [ { "type": "note", "channel": 1, "number": 11, "action": "a.play" },
                { "type": "cc", "channel": 1, "number": 8, "action": "a.tempo", "invert": true },
                { "type": "cc", "channel": 1, "number": 48, "action": "a.jog", "relative": true } ] }
```
`type` = `note` o `cc`, `channel` 1–16, `number` 0–127. Le azioni sono quelle della lista in Impostazioni
(`a.`/`b.` = deck A/B: play, cue, sync, fader, volume, tempo, eqlow/eqmid/eqhigh, filtervalue, jog, jogtouch, hotcue1–8,
loop1/2/4/8, loopexit, jumpback4/jumpfwd4, cuepfl, keyup/keydown, brake, backspin…; globali: crossfader, master, browse,
browseload, loadA/loadB, automix, mic, talk, fill, rotation, pad1–12, padstop, cuemix, cuevolume…).

## 4. Launchpad e griglie di pad
Novation Launchpad (X, Mini MK3, MK2, Pro MK3, S, Mini) è riconosciuto come **griglia di comandi**, dall'alto:
hot cue A · hot cue B · loop A · loop B · jingle 1–8 · jingle 9–12 + stop + ritmi · trasporto A · trasporto B;
colonna destra: automix, prossimo, mic, talk, riempimento, rotazione, carica A/B; tasti in alto: libreria e sfumature.
I pad **non si illuminano** (il feedback luminoso verso la console arriva con l'uscita MIDI, in roadmap).
Qualsiasi altra griglia (Akai APC/MPD, Arturia, tastiere con pad) si mappa con **Impara** o con un file Mixxx.

## Limiti attuali
- Niente LED/feedback verso la console (uscita MIDI non ancora implementata).
- I jog vengono letti come encoder relativi; lo scratch parte quando la console manda il "tocco" del piatto.
- Le mappature convertite da Mixxx/djay coprono le funzioni di base; i pad in modalità speciali (slicer, toneplay, FX) restano scoperti.
