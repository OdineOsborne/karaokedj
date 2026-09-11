# KaraokeDJ

App Windows (WPF / .NET 8) per serate di intrattenimento e karaoke: due deck con cambio
tonalità, coda dei cantanti, secondo schermo per i testi (CDG o video), jingle su tasti F1–F12,
download da YouTube/Spotify.

## Installare

Scarica **KaraokeDJ-win-Setup.exe** dall'ultima [Release su GitHub](https://github.com/OdineOsborne/karaokedj/releases): installa in `%LocalAppData%KaraokeDJ` con collegamento sul desktop, senza diritti di amministratore. L'app controlla gli aggiornamenti all'avvio (Velopack): quando c'è una versione nuova compare il pulsante "⬆ installa e riavvia" in alto a destra (oppure Impostazioni → *Cerca aggiornamenti*). La versione portable è `KaraokeDJ-win-Portable.zip`.

Per pubblicare una nuova versione: `publish.cmd 1.0.1` e poi il comando `vpk upload github …` stampato alla fine (serve un token GitHub).

## Compilare ed eseguire

```bash
dotnet build -c Release
```

L'eseguibile è in `src\KaraokeDJ\bin\Release\net8.0-windows\KaraokeDJ.exe`.

Per creare una cartella portabile (con runtime incluso, da copiare sul PC della serata):

```bash
publish.cmd
```

→ `dist\KaraokeDJ\KaraokeDJ.exe`

## Funzioni

| Area | Cosa fa |
|------|---------|
| **Deck A / B** | Play/pausa, stop, ±10 s, forma d'onda cliccabile, **tonalità ±12 semitoni**, tempo ±25 % con key lock, volume |
| **Mixer** | Crossfader a potenza costante, pulsanti "sfuma verso A/B", **PROSSIMO IN CODA** (carica il primo in coda sull'altro deck, lo avvia e sfuma), auto-mix opzionale, master |
| **Libreria** | Cartelle scansionate ricorsivamente (mp3, wav, m4a, flac, wma, mp4, mkv, avi…, **mp3+cdg** e **zip mp3+g**), ricerca istantanea per artista/titolo, tag ID3 |
| **Coda cantanti** | Nome cantante + tonalità per brano, riordino con drag&drop o ▲▼, doppio click per caricare, salvata su disco (sopravvive a crash/riavvio) |
| **Proiettore** | Finestra a schermo intero sul secondo monitor: schermo di attesa con titolo + prossimi cantanti, grafica **CDG** decodificata dall'app, **video** tramite LibVLC sincronizzato all'audio del deck, banner "PROSSIMO: …" |
| **Pad** | 12 pad (F1–F12) per jingle/applausi/effetti; assegna file da menu destro, trascinando un file o con "Assegna file" |
| **Download** | Link YouTube, link Spotify (brano singolo) o testo libero → MP3 (o MP4 per basi video con testo) nella cartella `Musica\KaraokeDJ Downloads`, aggiunto subito alla libreria |


## Funzioni DJ (aggiunte)

| Area | Cosa fa |
|------|---------|
| **Analisi brani** | BPM, tonalità (con codice Camelot), forma d'onda, **intro** e **uscita** rilevate dall'audio. Automatica dopo la scansione (`AutoAnalyze` nelle impostazioni) e al caricamento sul deck; manuale dal menu destro della libreria. Risultati in `library.json`, forme d'onda in `%AppData%\KaraokeDJ\waveforms`. La tonalità è una stima: su rock/blues può confondere tonica e dominante. |
| **Deck** | Forma d'onda cliccabile con intro (azzurro) e uscita (magenta) evidenziate, BPM effettivo con il tempo, tonalità trasposta con la manopola. **🔒 KEY** (key lock): acceso = cambiare velocità non cambia il tono; spento = stile vinile. |
| **Auto-mix** | Con "Usa intro/uscita rilevate" mixa all'inizio dell'uscita e fa partire il brano successivo dopo l'intro. |
| **Ricerca intelligente** | Ignora accenti, tollera refusi (`batisti emozion`), prefissi. Un numero cerca per BPM (±3 %), `8a` / `Am` per tonalità. Chip **🎯 Compatibili**: ordina la libreria per affinità (BPM ±8 % anche a tempo doppio/metà, tonalità Camelot vicina) con il brano in riproduzione o selezionato. |
| **Playlist interne** | Riga PLAYLIST: crea/rinomina/elimina, ▲▼ per l'ordine, "▶ In coda" mette tutto in coda. Con una playlist selezionata, scrivendo nella ricerca vedi tutta la libreria: cerca → seleziona → **＋ Aggiungi**. Menu destro → "Aggiungi alla playlist". Salvate in `playlists.json`. |
| **Download** | Playlist YouTube (`list=`), playlist/album Spotify (pubblici, via pagina embed). Anti-duplicati: archivio yt-dlp (`download-archive.txt`) per YouTube, confronto artista/titolo con la libreria per Spotify. Log in `ytdlp.log`. Scarica anche Deno (runtime JS richiesto da yt-dlp per YouTube). |
| **Controller MIDI** | Impostazioni → *Controller MIDI*: scegli il dispositivo, "Collega", poi **Impara** su ogni azione e muovi la manopola/premi il tasto. Crossfader, volumi, tempo (CC) e play/stop/tonalità/pad/prossimo (note o tasti CC). |
| **Effetti (FX)** | Pulsante **FX** sul deck: **🎤 VOCE OFF** (rimozione voce: cancellazione del centro con bassi e "aria" conservati — funziona sui mix con voce al centro, non è una separazione AI), **FILTER** (sinistra low-pass, destra high-pass), **ECHO** a tempo sui BPM (1/4…2/1, feedback, mix), **REVERB** (size, mix), **FLANGER**, **ECHO OUT** (taglia il brano lasciando la coda dell'eco e mette in pausa), ✕ spegne tutto. |
| **BPM di passaggio** | "Aggancia BPM nel passaggio": nell'automix e con PROSSIMO il brano entrante viene portato ai BPM di quello in uscita (×1, ×2 o ×½, max ±25 %). **SYNC** sul deck fa lo stesso a mano. "Blocca BPM a N": ogni brano caricato viene velocizzato/rallentato a N. Le basi karaoke non vengono mai alterate. |
| **Storico** | Colonna "Suonato": ✓ e ora se suonato stasera (riga attenuata), altrimenti il numero di riproduzioni totali. Menu destro → "Azzera i segni". |
| **Suggeriti** | Sotto la coda: i 6 brani più compatibili col brano in riproduzione (BPM/tonalità), esclusi quelli già suonati stasera e quelli in coda, con "＋ coda" e "→ deck". |
| **EQ 3 bande** | LOW / MID / HI (−30 dB … +8 dB) sempre visibili sul deck; click sul nome = kill, ↺ = piatto. |
| **Loop a battute** | ¼ ½ 1 2 4 8 battute dalla posizione attuale (lunghezza dai BPM del brano), ÷2 / ×2, EXIT. Tasto destro tenuto premuto = **loop roll** (al rilascio il brano riprende da dove sarebbe arrivato). Regione gialla sulla forma d'onda. |
| **Altri effetti** | PHASER, CRUSH (bitcrusher), GATE/trans a tempo (1/8…1/1), **BRAKE** (frenata vinile), **BACKSPIN** (riavvolgimento vinile). |
| **Voce AI (Demucs)** | **🤖 VOCE AI** nel pannello FX: la prima volta genera la base senza voce con Demucs (htdemucs, CPU: 1–3 min), poi si attiva/disattiva al volo restando in posizione. Menu destro → "Prepara base senza voce". Motore: Python 3.11 + PyTorch CPU + Demucs in `%AppData%\KaraokeDJ\tools\venv` (Impostazioni → *Installa motore AI*), stem in `%AppData%\KaraokeDJ\stems\<id>\`. |
| **Rinomina intelligente** | Menu destro → "Rinomina intelligente": toglie dal titolo l'artista ripetuto e il rumore ("Official Video", "Remastered 2012"…), e se l'artista è un canale (…VEVO, "Karaoke Academy") lo sostituisce con quello vero preso dal titolo. Scrive i tag ID3; i nomi dei file non cambiano. Applicata automaticamente ai download. |
| **🎉 Animazione (festeggiato)** | Pulsante in barra: nome, occasione, messaggi "Parlaci di lui/lei/loro" (a mano ora, dal QR in futuro). **Genera testo con AI** (Claude, chiave API in Impostazioni; senza chiave i messaggi diventano strofe così come sono) → **Copia e apri Suno** (testo in clipboard, si apre suno.com/create: Custom → incolla → stile → genera). Il file scaricato nella cartella `Musica\KaraokeDJ Downloads\Suno` entra da solo in libreria con titolo e **dedica**, mostrata sul proiettore mentre il brano suona. |
| **Sicurezza deck** | Caricare un brano su un deck che sta suonando chiede conferma (i caricamenti automatici vanno sempre sul deck fermo). Pulsante **⬆ vX.Y.Z** in barra per cercare aggiornamenti. |

## Scorciatoie

| Tasto | Azione |
|-------|--------|
| F1 – F12 | Pad jingle |
| Ctrl+1 / Ctrl+2 | Play/pausa Deck A / B |
| Ctrl+N | Prossimo in coda |
| Ctrl+P | Apri/chiudi proiettore |
| Ctrl+F | Cerca in libreria |
| Ctrl+Invio | Aggiungi brano selezionato alla coda |
| Ctrl+Spazio | Stop tutti i pad |
| Esc (sul proiettore) | Chiude il proiettore |

## Formati karaoke

- **MP3+G**: `Brano.mp3` + `Brano.cdg` nella stessa cartella, oppure `Brano.zip` che li contiene.
- **Video**: mp4/mkv/avi con il testo già nel video. L'audio viene decodificato dall'app (così
  funziona il cambio tonalità); il video viene mostrato muto e tenuto in sincrono.

## Note tecniche

- Audio: NAudio → WASAPI shared (80 ms). Tonalità/tempo: SoundTouch (bypass totale a 0 / 100 %).
- Il decoder CD+G è in `src/KaraokeDJ/Cdg/CdgDecoder.cs` (300 pacchetti/s, 300×216, 16 colori).
- Se i testi risultano in anticipo/ritardo sull'audio, regola **Offset testi** nelle Impostazioni.
- Download: `yt-dlp.exe` e `ffmpeg.exe` vengono scaricati automaticamente la prima volta in
  `%AppData%\KaraokeDJ\tools`. Se YouTube smette di funzionare: Impostazioni → *Aggiorna yt-dlp*.
  Spotify non permette il download diretto: il link viene risolto in "artista titolo" e cercato
  su YouTube (come fa spotdl).
- Dati utente in `%AppData%\KaraokeDJ` (settings.json, library.json, queue.json).

## Avvertenza

Il download da YouTube viola i termini di servizio di YouTube; per l'uso pubblico di musica
coperta da diritto d'autore servono le licenze del caso (SIAE/SCF). La responsabilità è dell'utente.
