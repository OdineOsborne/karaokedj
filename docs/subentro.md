# Subentro: a che punto siamo, e cosa manca

Documento per chi riprende il lavoro su un altro computer. Aggiornato al **25 settembre 2026**.

## La situazione in due righe

Davide fa la sua **prima serata vera il 2 ottobre 2026**, senza un software di riserva. Da qui in poi
la priorità non è aggiungere funzioni, è che quella sera non si fermi niente. L'ultima versione pubblicata è
la **1.6.0-beta.16** (GitHub Releases, prerelease).

## Cosa è stato fatto negli ultimi giorni

**Rete di sicurezza dello spettacolo**
- `⛔ FERMA TUTTO` (tasto Esc, tasto rosso nella barra LIVE): ferma deck, pad, batteria, riempimento,
  annulla il passaggio in corso, spegne auto-mix e "mai fermarsi".
- Il piatto della console **non può far partire** un deck fermo, né riavviarlo dopo la pausa.
- **Auto-mix e riempimento partono sempre spenti**: aprendo il programma non suona mai niente da solo.
- `🎛 IGNORA CONSOLE` isola una console impazzita senza staccare il cavo.
- Tutto questo è verificato a ogni build dalla riga `stop:` del selftest.

**Console**
- I piatti: la velocità si ricava dalla **frequenza** degli scatti, non dal valore (vedi `Audio/JogMeter.cs`).
  Con la mano sopra fa scratch vero avanti e indietro; senza mano è pitch bend ±25 %; a deck fermo cerca il
  punto in silenzio. Prova: `--jogtest`.
- Strato **SHIFT** per le console che non lo gestiscono da sole (`MidiMapping.Shift`).
- Preset Hercules Instinct P8 rifatto sul documento ufficiale del produttore e verificato sull'hardware.
- In Impostazioni → MIDI si sceglie la console dall'elenco, con scritto **da dove vengono i numeri** e
  **cosa copre il preset**. Se la console manda un comando sconosciuto, l'app lo dice.
- LED dei pad (hot cue e play) usando l'uscita MIDI.

**Altro**
- Registrazione della serata su WAV (`Audio/NightRecorder.cs`), circa 10 MB al minuto.
- Struttura del brano (Foote) usata dall'auto-mix per passare sul cambio di sezione.
- Striscia messaggi e applausometro sul proiettore.
- Controllo pre-serata (tasto ✅ nella barra in alto, o `--preflight`).

## Cosa resta aperto

1. **Soak da 4 ore** (`--soak 240`): mai fatto. Fa rumore, serve il PC libero e le casse spente.
   Deve finire con `SOAK OK` (memoria stabile, zero errori audio, zero riavvii dell'uscita).
2. **La seconda console**: da provare sul PC della serata. Se qualcosa non risponde, il giro è:
   Impostazioni → MIDI → «Registra cosa manda la console» → muovere *solo* quel controllo → fermare →
   guardare `%AppData%\KaraokeDJ\midi-log.txt`. Poi cercare la tabella MIDI ufficiale del produttore
   (per Hercules stanno su `ts.hercules.com/download/sound/MIDI_Mapping/…`) e correggere il preset in
   `src/KaraokeDJ/Assets/Controllers`, meglio ancora rigenerandolo dal convertitore in `tools/controllers`.
3. **LED**: sulla P8 l'uscita MIDI si apre e vede 18 pad, ma nessuno ha ancora confermato che si accendano
   davvero. `--ledtest` li accende per 5 secondi.
4. **Preset delle console**: tutti e 42 sono stati controllati strutturalmente (`tools/controllers/check.js`,
   e la riga `preset:` del selftest), ma **solo la P8 è stata verificata sull'hardware**. `numark-ns4fx` è
   senza play e cue (la mappatura Mixxx di origine li fa via script) ed è segnato come incompleto.
5. **Prova a secco della serata**: `docs/test-serata.md` ha la lista, da fare con l'impianto vero.
6. **Ciclo di apprendimento in cloud**: `cloud/api/learn.js` e `learn-build.js` non sono ancora su Vercel
   (servono le variabili d'ambiente, le mette Davide).

## Preparare un computer nuovo

Servono: **.NET 8 SDK**, **git**, **node** (solo per gli strumenti dei preset), **vpk** (solo per impacchettare:
`dotnet tool install -g vpk`).

```bash
git clone https://github.com/OdineOsborne/karaokedj.git
cd karaokedj
dotnet build src/KaraokeDJ/KaraokeDJ.csproj -c Release
src/KaraokeDJ/bin/Release/net8.0-windows/KaraokeDJ.exe --selftest
```

Il repo **non contiene**: la chiave di licenza privata (`KaraokeDJ-private`, da copiare a mano da chi ce l'ha),
il plugin yt-dlp, la musica. I dati dell'utente (libreria, impostazioni, coda) stanno in `%AppData%\KaraokeDJ`
e su un PC nuovo sono vuoti: vanno aggiunte le cartelle della musica e lanciata l'analisi
(`--analyze`, poi `--sections`), che sul PC della serata conviene fare **prima**, non la sera stessa.

Prima di considerare pronto il PC della serata: `--preflight` deve essere tutto verde o giallo, mai rosso.
