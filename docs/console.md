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

## SHIFT: due comandi per ogni controllo

Quasi tutte le console hanno un tasto SHIFT che cambia il significato di manopole e tasti
(sulla Instinct P8, per esempio, la manopola fa il loop da sola e il filtro con SHIFT premuto).

Mixfonia tiene due strati di mappatura. Per configurarlo:

1. Impostazioni → MIDI e tastiera, riga **«SHIFT della console (tieni premuto)»** → «Impara MIDI» → premi il tasto SHIFT.
2. Riga del comando che vuoi **senza** shift (es. «Deck A – Loop: lunghezza») → «Impara MIDI» → muovi la manopola normalmente.
3. Riga del comando che vuoi **con** shift (es. «Deck A – Filtro») → «Impara MIDI» → **tieni premuto SHIFT** e muovi la stessa manopola.

Chi è assegnato col tasto shift si riconosce dalla scritta `SHIFT + cc ch1 #52` nella colonna del controllo.
Il registratore MIDI segna `SHIFT+` davanti ai messaggi ricevuti mentre il tasto era premuto.

## Scegliere la console a mano, e sapere cosa si prende

Impostazioni → MIDI e tastiera → **«La mia console»**: l'elenco di tutte le console conosciute.
Serve quando la porta MIDI ha un nome generico, quando due modelli si chiamano uguale o quando
si vuole provare il preset di un modello vicino. «Riconoscimento automatico» torna al comportamento normale.

Sotto all'elenco c'è scritto **quanto ci si può fidare** di quel preset:

- *provato su console vera* — numeri verificati col registratore MIDI sull'hardware;
- *dal documento del produttore* — presi dalla tabella MIDI ufficiale;
- *da mappatura della comunità, non provata* — derivati da Mixxx/djay: quasi sempre giusti, ma nessuno li ha verificati.

E c'è **cosa copre**: play, cue, fader, crossfader, piatti, EQ, hot cue, browse. Se manca qualcosa lo dice,
così lo si assegna con «Impara MIDI» prima della serata invece di scoprirlo sul palco. I preset senza play o cue
sono segnati con ⚠ nell'elenco.

## LED dei pad

Con **«Accendi i LED dei pad sulla console»** Mixfonia accende i pad degli hot cue (blu sul deck A, rosso sul B)
e il tasto play mentre il deck suona. Usa l'uscita MIDI della console: il LED di un tasto risponde allo stesso
numero di nota che il tasto manda quando lo premi, quindi non serve una seconda tabella. Se la tua console non
funziona così non si accende niente e non si rompe niente; per provare:

```bash
KaraokeDJ.exe --ledtest
```

## Come sono controllati i preset

`node tools/controllers/check.js src/KaraokeDJ/Assets/Controllers` cerca gli errori che ci sono costati una giornata
sulla P8: stesso controllo su due azioni, azioni inesistenti, comandi essenziali mancanti, una manopola sparsa su
tanti CC (segno che i numeri erano tirati a indovinare). Gli stessi controlli girano a ogni build dentro `--selftest`
(riga `preset:`).
