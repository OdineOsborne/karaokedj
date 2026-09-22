# Prova con la Hercules DJControl Instinct P8 — scheda da compilare

Beta 15 (installer locale, non pubblicata). Segna accanto a ogni riga: **OK**, **NO** (non fa niente) o cosa fa di diverso.
Dove c'è da misurare qualcosa, scrivi il valore. Poi mandami la scheda: correggo tutto in una volta.

## Prima di iniziare
- [ ] Installa `KaraokeDJ-win-Setup.exe` (beta 15). Versione mostrata in basso a destra: `1.6.0-beta.15`.
- [ ] Collega la P8 **prima** di aprire l'app.
- [ ] All'avvio la barra di stato dice: `Console riconosciuta: Hercules DJControl Instinct P8 …`
      → se **non** lo dice: Impostazioni → MIDI e tastiera → copia il nome esatto che vedi in «Dispositivo MIDI»: ______________________
- [ ] Impostazioni → Audio → Uscita: quale voce hai scelto? ______________________
- [ ] Impostazioni → Audio → Cuffia: hai visto la voce *«Canali 3-4 della scheda principale»*?  OK / NO
      Scegliendola, la barra di stato dice «Cuffia: canali 3-4 di …»?  OK / NO / messaggio: ______________________
      Se dice che la scheda ha solo 2 canali: quanti canali risultano? ____  (allora Windows espone due dispositivi separati: scegli l'altro)

## 1. Trasporto e mixer (la parte che deve funzionare in serata)
| # | Controllo | Atteso | Esito |
|---|---|---|---|
| 1 | PLAY A / B | play–pausa del deck |  |
| 2 | SHIFT+PLAY | stop |  |
| 3 | CUE A / B | punto di cue |  |
| 4 | SYNC | aggancia i BPM all'altro deck |  |
| 5 | SHIFT+SYNC | tempo torna a 0 % |  |
| 6 | Fader di canale A / B | volume del deck (**scende abbassando**) |  |
| 7 | SHIFT+fader | gain (trim) |  |
| 8 | Crossfader | passa da A a B |  |
| 9 | BASS / MEDIUM / TREBLE A e B | EQ (**a destra = più**) |  |
| 10 | LISTEN (🎧) A / B | in cuffia senti quel deck, in sala no |  |
| 11 | SHIFT+LISTEN | key lock on/off |  |
| 12 | LOAD A / LOAD B | carica il brano selezionato |  |
| 13 | Encoder BROWSER (gira) | scorre la libreria |  |
| 14 | BROWSER premuto | carica sul deck libero |  |
| 15 | SHIFT+BROWSER premuto | mette in coda |  |

Se qualcosa **lavora al contrario** (fader, EQ, crossfader) scrivilo qui: ______________________
(si corregge da solo: Impostazioni → MIDI → sulla riga di quel comando spunta **inverti**)

## 2. Jog e tempo
| # | Controllo | Atteso | Esito |
|---|---|---|---|
| 16 | Jog **senza** toccare il piatto (bordo) | piccolo pitch bend, niente salti |  |
| 17 | Mano **sul piatto** + gira | scratch |  |
| 18 | Scratch: velocità | giusto / troppo veloce / troppo lento |  |
| 19 | Scratch: si ferma quando togli la mano | OK / continua a girare |  |
| 20 | SHIFT + jog | cambia il tempo (fader tempo a video si muove) |  |
| 21 | Encoder LOOP (gira) | manopola filtro del deck |  |
| 22 | Encoder LOOP premuto | loop 4 battute · SHIFT = esce dal loop |  |

## 3. Pad nei 4 modi (tasto MODE sulla console)
| # | Modo | Pad 1-4 | SHIFT 1-4 | Esito |
|---|---|---|---|---|
| 23 | CUE | hot cue 1-4 | hot cue 5-8 |  |
| 24 | FX | ECHO · REVERB · FILTER · FLANGER | spegni FX · BRAKE · BACKSPIN · tonalità compatibile |  |
| 25 | LOOP | loop 1 · 2 · 4 · 8 battute | −4 battiti · +4 · esci · quantizza |  |
| 26 | SAMPLE | deck A = jingle 1-4, deck B = jingle 9-12 | A: jingle 5-8 · B: stop pad, ritmi, tap, riparti |  |

I LED dei pad **non si accendono**: è previsto (feedback MIDI non ancora implementato). Segnala solo se un pad fa la cosa sbagliata.

## 4. Audio in serata (30 minuti veri, con casse e microfono)
| # | Prova | Esito |
|---|---|---|
| 27 | Un brano karaoke completo con testo sul proiettore |  |
| 28 | Automix acceso 15 minuti senza toccare nulla: dissolvenze pulite |  |
| 29 | Microfono: TALK premuto abbassa la musica, rilasciato la rialza |  |
| 30 | Cuffia: preascolti B mentre A suona in sala |  |
| 31 | Ritardo cuffia/sala percepibile? no / poco / fastidioso |  |
| 32 | Scratch e jog: click o rumori? no / sì (quando) |  |
| 33 | **Stacca la P8 dall'USB mentre suona** → l'audio riparte da solo entro ~1 s sulla scheda predefinita |  |
| 34 | Riattacca la P8 → la console viene riconosciuta di nuovo entro 3 s |  |
| 35 | Dopo la prova: `%AppData%\KaraokeDJ\crash.log` è vuoto? sì / no (mandamelo) |  |

## Note libere
(qualsiasi cosa strana, anche piccola: un tasto che risponde due volte, una manopola a scatti, un ritardo)

______________________________________________________________________

______________________________________________________________________
