# Mixfonia — piano di test prima della serata del 2 ottobre 2026

Obiettivo: arrivare al 2 ottobre con una versione che **non si ferma** durante lo show. Non serve che ogni funzione sia perfetta:
serve che musica e testi non si interrompano mai e che, se qualcosa cade, riparta da solo.

## Cosa fa l'app da sola (beta 15)

| Guasto | Comportamento |
|---|---|
| File audio corrotto a metà brano | il brano finisce (silenzio), il mixer continua, `crash.log` annota l'errore |
| Scheda audio staccata / driver che cade | uscita riavviata da sola entro mezzo secondo; se la scheda non c'è più passa alla predefinita; messaggio nella barra di stato |
| Cuffia / microfono staccati | riavvio automatico sullo stesso dispositivo |
| Errore in una finestra (UI) | registrato in `crash.log` e mostrato in barra di stato, **nessuna finestra modale** che blocca la regia |
| Crash vero del processo | salva coda + impostazioni e **riparte da solo** (`--recovered`), coda intatta; non riparte in ciclo se cade entro 1 minuto dall'avvio |
| Salvataggio | coda salvata a ogni modifica, impostazioni ogni 60 s |
| Console che manda da sola / piatto toccato per sbaglio | il piatto **non può far partire** un deck fermo, e nemmeno riavviarlo dopo la pausa; `🎛 IGNORA CONSOLE` la isola senza staccare il cavo |
| Musica che non si ferma, qualunque sia il motivo | **`⛔ FERMA TUTTO` (tasto Esc)**: deck, pad, batteria, riempimento e passaggio in corso fermi, auto-mix spento; da lì non riparte niente da solo |

Diario errori: `%AppData%\KaraokeDJ\crash.log` — da guardare il giorno dopo ogni prova.

## Test automatici (li faccio io, ogni build)

```bash
KaraokeDJ.exe --selftest
```
Apre tutte le finestre e il proiettore, esce 0. Log `%TEMP%\mixfonia-selftest.log`.
Dentro c'è anche `stop:`, che verifica la regola più importante: **la musica parte solo se lo decide il DJ** (il piatto non avvia un deck fermo)
e **`FERMA TUTTO` riporta davvero il silenzio**.

```bash
KaraokeDJ.exe --midiwatch 15
```
Ascolta la console per 15 secondi **senza eseguire niente** e scrive quanti messaggi manda da sola, su quali controlli e con che valori.
Da usare quando una console sembra avere vita propria.

```bash
KaraokeDJ.exe --soak 60
```
Un'ora da "DJ impazzito" sulla libreria vera: carica brani a caso, hot cue, loop, salti, tonalità, tempo, EQ, filtro, effetti, dissolvenze,
automix, pad, PFL, mic (se configurato), proiettore aperto. Ogni 30 s annota memoria ed errori. Deve finire con `SOAK OK`
(memoria stabile, zero errori audio, zero riavvii). Log `%TEMP%\mixfonia-soak.log`. Prima della serata: **una corsa da 4 ore** (`--soak 240`).

## Test manuali con l'hardware (da fare tu, nell'ordine)

Serve la stessa configurazione della serata: PC, scheda audio/mixer, cuffia sulla seconda uscita, microfono, proiettore/TV, console MIDI se la usi.

### 1. Setup (30 min) — una sola volta
- [ ] Installa la beta 15 sul PC della serata. Tieni nella stessa cartella anche l'installer della **beta 14** (piano B: si reinstalla in 1 minuto).
- [ ] Impostazioni → Audio: uscita principale sulla scheda della serata, **cuffia** sulla seconda scheda, **microfono** sull'ingresso giusto.
- [ ] Windows → Audio: disattiva "consenti alle app di assumere il controllo esclusivo" sulla scheda principale; disattiva i miglioramenti audio.
- [ ] Windows → Alimentazione: prestazioni elevate, mai sospensione, niente spegnimento schermo (il proiettore!), USB selective suspend **off**.
- [ ] Windows Update: installa tutto **ora**, poi metti in pausa aggiornamenti per 2 settimane. Notifiche: Non disturbare.
- [ ] Collega il proiettore **prima** di aprire l'app e verifica che Windows non abbia cambiato l'uscita audio sull'HDMI.
- [ ] Lancia l'app, apri il proiettore, attiva la licenza, controlla che la barra di stato dica "Uscita: WASAPI <la tua scheda>".

### 2. Prova a secco n. 1 (1 ora) — dal divano
- [ ] Coda di 10 brani misti (MP3, karaoke CDG, video, MIDI/KAR, LRC): partono, testi sincronizzati, passaggio automatico al successivo.
- [ ] Automix on per 20 minuti senza toccare nulla: le dissolvenze suonano bene, nessun buco.
- [ ] Riempimento: con la rotazione accesa, fra un cantante e l'altro parte la musica di riempimento e si abbassa quando parte la base.
- [ ] Microfono: TALK premuto → la musica scende; rilasciato → risale; EQ / FX apre le manopole; eco e riverbero senza fischi.
- [ ] Cuffia: 🎧 su B mentre A suona in sala → in cuffia senti B, in sala solo A; manopola cue/master funziona.
- [ ] Hot cue, loop 4, beat jump, tonalità ±2, tempo ±4 %: nessun click o salto brutto.
- [ ] **Stacca la scheda USB mentre suona** → entro un secondo l'audio riparte (sulla predefinita) e la barra di stato lo dice. Riattaccala → Impostazioni → riseleziona.
- [ ] Stacca il microfono mentre è acceso → messaggio, riattacchi, torna.
- [ ] Chiudi il coperchio / lascia il PC fermo 15 minuti con la musica che va: non deve andare in sospensione.

### 3. Prova a secco n. 2 (1 ora) — con il telefono
- [ ] Pagina pubblica `/canta`: QR sul proiettore, dal telefono cerchi e prenoti un brano con nome → arriva nel pannello richieste, accetti, va in coda con il cantante e la tonalità memorizzata.
- [ ] Scaletta remota dal telefono (comandi salta/pausa/volume).
- [ ] Rotazione: 4 cantanti finti, 8 brani: ordine equo, "prossimo sul palco" sul proiettore.
- [ ] Console MIDI: la colleghi → in 3 s la barra di stato dice "Console: <nome> (preset)"; play/cue/fader/jog/EQ rispondono. Se un controllo è invertito, me lo scrivi (nome console + controllo).

### 4. Prova generale (la serata simulata, 2–3 ore, la settimana prima)
- [ ] Stessa scaletta reale, stessi cavi, stesso ordine di accensione, dall'inizio alla fine senza riavviare l'app.
- [ ] Un amico/collega che fa il pubblico col telefono.
- [ ] Il giorno dopo: `crash.log` vuoto o solo cose che conosciamo → via libera. Altrimenti me lo mandi.

## Piano B in sala (kit di emergenza)
- Installer **beta 14** e **beta 15** su chiavetta.
- Cartella con la libreria su chiavetta (o disco esterno) e una playlist "serata" esportata: se salta il PC si va avanti con un altro PC in 10 minuti (licenza: 3 PC per account).
- Cavo audio jack di riserva per collegare direttamente l'uscita del PC al mixer se la scheda USB muore.
- Windows: un secondo profilo audio "solo PC" già provato.

## Cosa NON provare in serata
Niente funzioni nuove usate per la prima volta sul palco: stems in tempo reale sul primo brano, effetti che non hai provato, console appena comprata.
Le usi solo se le hai passate al punto 2 o 3.

## Cosa vede il pubblico (beta 16)

- **Striscia messaggi**: Impostazioni → Proiettore → «Striscia messaggi». Un messaggio per riga; scorrono in basso
  sopra a testi, CDG e video, a velocità costante (un messaggio lungo impiega di più, non corre di più).
- **Applausometro**: tasto 👏 nella barra LIVE (o azione `applause` su console/tastiera). Sette secondi di ascolto
  dal microfono, punteggio grande sul proiettore, poi sparisce da solo. Se il microfono è spento lo accende per la
  misura e lo rimette com'era. Non tocca mai la musica.

```bash
KaraokeDJ.exe --showtest schermo.png
```
Fotografa il proiettore con striscia e applausometro (due file: `schermo-striscia.png` e `schermo.png`).
