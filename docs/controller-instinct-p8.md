# Hercules DJControl Instinct P8 — mappatura Mixfonia

Preset di fabbrica `Assets/Controllers/hercules-instinct-p8.json`, ricavato dalla *MIDI Command List v1.1* ufficiale Hercules
(canale 1). Riconosciuta automaticamente quando la porta MIDI contiene "Instinct P8": in 3 secondi la barra di stato dice
**Console: Hercules DJControl Instinct P8 (preset)**. Le mappature imparate a mano (Impostazioni → MIDI → impara) restano sopra al preset.

## Scheda audio integrata
La P8 ha la sua scheda audio (uscite 1-2 master, 3-4 cuffia). Su Windows di solito compare **un solo dispositivo a 4 canali**:
- Impostazioni → Audio → **Uscita**: la P8
- Impostazioni → Audio → **Cuffia**: *Canali 3-4 della scheda principale* → la cuffia esce dai canali 3-4 senza seconda scheda.
Se invece Windows mostra due dispositivi separati (master e cuffia), scegli il secondo come Cuffia.
I tasti **HP VOL −/+** e il volume cuffia sono gestiti dalla console.

## Per ogni deck (A a sinistra, B a destra)

| Controllo | Normale | con SHIFT |
|---|---|---|
| PLAY | play/pausa | STOP |
| CUE | cue | parti dal cue |
| SYNC | aggancia i BPM all'altro deck | tempo 0 % |
| Jog (piatto) | pitch bend; **mano sul piatto = scratch** | **tempo** (encoder: gira per cambiare la velocità) |
| LISTEN (🎧) | pre-ascolto in cuffia on/off | key lock on/off |
| Encoder LOOP | **filtro** del deck (gira) | — |
| Encoder LOOP premuto | loop 4 battute | esci dal loop |
| Fader di canale | fader | gain (trim) |
| BASS / MEDIUM / TREBLE | EQ bassi / medi / alti | — |

### Pad — modo CUE
| Pad | 1 | 2 | 3 | 4 |
|---|---|---|---|---|
| normale | hot cue 1 | hot cue 2 | hot cue 3 | hot cue 4 |
| SHIFT | hot cue 5 | hot cue 6 | hot cue 7 | hot cue 8 |
Pad vuoto = imposta il cue; pad pieno = salta lì (quantizzato). Cancellare: tasto destro sul pad a video.

### Pad — modo FX
| Pad | 1 | 2 | 3 | 4 |
|---|---|---|---|---|
| normale | ECHO | REVERB | FILTER on/off | FLANGER |
| SHIFT | spegni tutti gli effetti | BRAKE | BACKSPIN | tonalità compatibile con l'altro deck |

### Pad — modo LOOP
| Pad | 1 | 2 | 3 | 4 |
|---|---|---|---|---|
| normale | loop 1 | loop 2 | loop 4 | loop 8 battute |
| SHIFT | salta −4 battiti | salta +4 | esci dal loop | quantizza on/off |

### Pad — modo SAMPLE (jingle)
| Deck | normale | SHIFT |
|---|---|---|
| A | pad 1-4 | pad 5-8 |
| B | pad 9-12 | stop tutti i pad · ritmi avvia/ferma · tap tempo · riparti dall'1 |

## Centro
| Controllo | Normale | con SHIFT |
|---|---|---|
| Crossfader | crossfader | — |
| Encoder BROWSER | scorri la libreria | scorri la libreria |
| BROWSER premuto | carica sul deck libero | brano selezionato in coda |
| LOAD A / LOAD B | carica il brano selezionato su A / B | — |
| SCRATCH | (nessuna funzione: lo scratch parte toccando il piatto) | auto-mix on/off |
| MODE | cambia il modo dei pad (sulla console) | — |

## Cosa verificare al primo collegamento (e cosa scrivermi)
1. La barra di stato riconosce la console (se no: nome esatto della porta MIDI in Impostazioni → MIDI).
2. Play/cue/sync, fader, EQ, crossfader su entrambi i deck. **Se un EQ o un fader lavora al contrario**, dimmi quale: metto `Invert`.
3. Jog: senza mano → piccoli pitch bend; con mano → scratch. Se lo scratch è troppo veloce o troppo lento, dimmi "il doppio"/"la metà" (`JogTicksToRate`).
4. SHIFT+jog cambia il tempo (guarda il fader tempo a video).
5. Pad nei 4 modi (MODE sulla console).
6. Cuffia: LISTEN su B mentre A suona in sala.
7. LED: i pad **non** si accendono dal software (feedback MIDI non ancora implementato, in roadmap 1.7).

## I piatti, misurati (registratore MIDI, 24/09/2026)

Registrazione vera dalla P8, 2418 messaggi:

| Controllo | Messaggio | Valori |
|---|---|---|
| Piatto A libero | CC 48 | 1 = avanti, 127 = indietro (719 avanti / 626 indietro) |
| Piatto A premuto | CC 105 | idem (225 / 192) |
| Piatto B libero / premuto | CC 50 / CC 107 | idem |
| Mano sul piatto A / B | note 97 / 98 | 127 alla pressione |

**La velocita sta nella frequenza degli scatti, non nel valore**: il valore dice solo il verso.
Girata secca senza mano 280-345 scatti/s, mano appoggiata 43-96 scatti/s. Da qui la taratura
di `JogMeter`: 120 scatti/s = velocita normale del brano (1x).

Con la mano sopra il piatto fa scratch vero (avanti e indietro); senza mano fa pitch bend
(max +-25 %, il brano non torna mai indietro); a deck fermo sposta il punto di ascolto in silenzio
e **non fa partire la musica**.

```bash
KaraokeDJ.exe --jogtest
```
Rifa questi stessi scatti e misura dove finisce la puntina (master a zero: si puo lanciare con le casse accese).
