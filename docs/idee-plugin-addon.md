# Plugin, addon e giochi: idee per dopo la serata del 2 ottobre

Brainstorming del 27 settembre 2026. **Niente di questo va toccato prima del 2 ottobre**: fino alla prima
serata conta solo che l'app non si fermi. Qui c'è cosa si può costruire dopo, quanto è fattibile, e come
venderlo.

## Da dove partiamo (cosa c'è già nel codice)

| Pezzo | Dove | Serve a |
|---|---|---|
| Separazione degli strumenti (Demucs htdemucs, CPU, 1–3 min/brano) | `Services/StemService.cs`, venv in `%AppData%\KaraokeDJ\tools` | togli strumento, giochi "solo batteria", melodia della voce |
| BPM, tonalità, cromagramma, griglia dei battiti, struttura (intro/ritornelli/finale) | `Audio/AudioAnalyzer.cs`, `BeatTracker.cs`, `StructureAnalyzer.cs` | accordi, builder, medley |
| Cambio di tempo senza cambiare intonazione e viceversa | `Audio/SoundTouchSampleProvider.cs` | studio lento, medley nella stessa tonalità |
| 15.700 karaoke MIDI `.kar` con la **melodia esatta** | `E:\Karaoke\MIDI`, `Services/MidiRenderService.cs` | punteggio karaoke senza dover indovinare la melodia |
| Microfono in ingresso | `Audio/MicInput.cs` | sfida karaoke |
| Proiettore, striscia messaggi, applausometro | `Views/ProjectorWindow.xaml` | giochi sul maxischermo |
| Telefoni del pubblico (richieste via QR) | `Views/RemoteWindow.xaml`, `Services/RemoteSetlistService.cs` | risposte ai quiz, tombola |
| Automix che passa sul cambio di sezione, blocco BPM, registrazione su file | `ViewModels`, `Audio/NightRecorder.cs` | builder di sequenze |
| Licenze e pagamenti (Stripe) | `cloud/`, `Services/LicenseService.cs` | vendita degli addon |

**Il limite:** oggi un plugin (`src/Mixfonia.Plugins/Contracts.cs`) può fare una cosa sola, portare brani
in libreria (`IImportSource`, come il plugin yt-dlp). Tutto il resto richiede di allargare il contratto.

## 1. Piattaforma plugin v2

Punti di aggancio da aggiungere al contratto, ognuno opzionale:

- **Pannello**: un controllo WPF che l'app mette in una scheda o in una finestra.
- **Deck**: brano caricato, posizione, BPM, griglia, tonalità; comandi (carica, play, cue, loop).
- **Audio in ascolto**: uscita master e microfono, in sola lettura, con i campioni già al tempo del deck.
- **Analisi**: il plugin aggiunge dati suoi a un brano (accordi, melodia, trascrizione) salvati accanto a
  quelli dell'app, e l'app li rilegge senza rifare il calcolo.
- **Proiettore**: disegnare sopra o al posto del testo (giochi, punteggi).
- **Telefoni del pubblico**: domande e risposte (quiz, voti, tombola).
- **Licenza**: il plugin dichiara a quale addon appartiene; l'app lo attiva solo se la licenza lo comprende.

Principi: un plugin rotto non deve mai fermare la musica (caricamento isolato, errori nella barra di stato,
come oggi); la versione del contratto è dichiarata, così un plugin vecchio viene rifiutato con un messaggio
invece di andare in errore (è successo con `VOXA.Plugin.YtDlp` dopo il cambio di nome).

### Plugin di terzi (integrazioni), dal più facile al più difficile

- **OSC / MIDI in uscita** verso luci ed effetti; con **QLC+ o Art-Net (DMX)** le luci seguono i battiti e i
  cambi di sezione. Per chi fa serate vale molto.
- **Sovrapposizioni per OBS**: titolo, cantante, punteggio in una pagina locale da aggiungere come sorgente.
- **Ableton Link**: tempo condiviso con musicisti e altre app. Attenzione alla licenza: Link è GPL-2.0,
  per un'app chiusa serve la licenza commerciale di Ableton (da chiedere).
- **Streaming con licenza DJ** (Beatport, Beatsource, TIDAL, SoundCloud): richiede un accordo da partner,
  settimane o mesi. Spotify dà l'integrazione DJ solo ai partner (Serato, rekordbox, djay).
- **Effetti VST**: la cosa più chiesta e la più costosa; ospitare VST in un'app .NET è un progetto a sé. Dopo.

## 2. Cosa si ricava dall'analisi approfondita di un brano

| Cosa | Fattibilità | Come |
|---|---|---|
| BPM, tonalità, griglia, struttura | ✅ fatto | già nell'app |
| **Togliere strumenti a scelta** | ✅ quasi pronto | Demucs dà voce, batteria, basso, altro; il modello a 6 tracce (`htdemucs_6s`) anche chitarra e piano |
| **Accordi nel tempo** | ✅ buona precisione | dal cromagramma che calcoliamo già, per battuta |
| **Melodia della voce** | ✅ buona | dalla voce separata con CREPE o pYIN |
| **Basso in note e tablatura** | 🟡 discreta | monofonico: si trascrive bene dopo la separazione |
| **Chitarra in tablatura** | 🟠 approssimativa | polifonia e diteggiature ambigue: accordi e riff sì, assoli veloci no |
| **Spartito** | 🟡 | esportazione MusicXML da aprire in MuseScore; serve una pulizia a mano |

Librerie open source adatte: **basic-pitch** (Spotify, Apache-2.0) audio → MIDI polifonico;
**CREPE** (MIT) per l'intonazione della voce; **Demucs** (MIT) già in uso. Controllare anche la licenza dei
pesi dei modelli prima di distribuirli.

## 3. Addon «Studio» (per musicisti: lo studio, non gli effetti)

- togli la tua parte e suonaci sopra (es. «senza basso»);
- rallenta senza cambiare intonazione, loop di un passaggio, cambio di tonalità (tutto già presente);
- accordi che scorrono a tempo sul brano; tablatura e spartito da esportare;
- click generato dalla griglia, conteggio delle battute, strofa/ritornello/ponte segnati;
- per i cantanti: melodia guida sovrapposta e controllo dell'intonazione (vedi punto 5).

Pubblico diverso dai DJ (insegnanti, band, studenti), abituato a pagare un abbonamento.

## 4. Builder di sequenze mixate

Una **timeline** dove si trascinano i brani, si scelgono i punti di attacco, l'app allinea e fa sentire il
passaggio, poi **esporta un unico file** (mp3/wav) con la scaletta. I mattoni ci sono già (griglia,
struttura, blocco BPM, automix sul cambio di sezione, registrazione).

**Addon «Fitness»**
- BPM costante per tutta la lezione, oppure a blocchi (riscaldamento 120, lavoro 128–132, defaticamento 100);
- passaggi solo a fine frase (32 battiti), così i conteggi degli istruttori non saltano;
- conteggi vocali e segnali («ultimi 8!», fischio), timer per HIIT/tabata;
- esportazione con scaletta.
- Attenzione: in palestra la musica commerciale in pubblico richiede licenza (SIAE/SCF); molti istruttori
  usano cataloghi «fitness» dedicati. Un'integrazione con uno di questi cataloghi vale oro.

**Addon «Live»: medley e band**
- l'analisi della struttura trova i ritornelli; si scelgono i pezzi e si portano **tutti nella stessa
  tonalità e allo stesso tempo**; il builder prepara i passaggi;
- esporta la **traccia click** e le **basi senza lo strumento** di chi suona dal vivo, più la scaletta con
  le tonalità.

## 5. Sfida karaoke con punteggio (addon «Party»)

La più forte per serate e vendite: abbiamo **la melodia esatta** di 15.700 canzoni nei `.kar`; per i brani
audio si ricava dalla voce separata.

- **Intonazione**: nota per nota contro il riferimento, con tolleranza regolabile, senza penalizzare chi canta
  un'ottava sopra o sotto.
- **Attacco**: anticipo o ritardo di ogni frase rispetto alla base. Serve una **calibrazione della latenza**
  (microfono + scheda audio), altrimenti si punisce chiunque.
- **Tenuta**: note lunghe mantenute, vibrato.
- **Proiettore**: la linea della melodia che scorre con il pallino del cantante; alla fine pagella
  («attacchi», «intonazione», «grinta» da volume e applausometro).
- **Modalità**: duello a due microfoni, squadre, torneo della serata con classifica, voto del pubblico dal
  telefono unito al punteggio tecnico.

Prototipo (punteggio sui `.kar`, un microfono): qualche settimana. Versione rifinita: molte prove con cantanti veri.

## 6. Altri giochi musicali (addon «Party»)

- **Indovina la canzone** dall'intro di 3-5-10 secondi, risposte dal telefono, classifica live.
- **Solo la batteria / solo il basso**: si sente un solo strumento separato e si indovina il brano.
- **Completa il testo**: la musica si ferma e il testo si interrompe sul proiettore.
- **Tombola musicale**: cartelle sul telefono con i titoli al posto dei numeri. Adatta a feste e anziani.
- **Indovina l'anno o il decennio**, per i tempi morti.
- **Voce al contrario o accelerata**, da serata.
- **Balla e fermati (freeze dance)** per bambini.
- **Sfida di ritmo** con i pad della console: premere a tempo sui battiti.
- **Duello di ballo** deciso dall'applausometro.

## 7. Come venderlo

Prezzi della base già in cassa (`cloud/lib/stripe.js`): **licenza 20 €** (1 PC, 1 anno di aggiornamenti),
**aggiornamenti 10 €/anno**, 2° e 3° PC 10 €, trasferimento 1,30 €.

Proposta per gli addon (decisa il 27/9/2026, pacchetto da confermare):

| | Acquisto (1° anno di aggiornamenti incluso) | Aggiornamenti dal 2° anno |
|---|---|---|
| Mixfonia base | 20 € | 10 €/anno |
| Ogni addon (Party, Studio, Fitness, Live, Luci) | 5 € | 2 €/anno |
| Tutto a prezzo pieno (base + 5 addon) | 45 € | 20 €/anno |
| **Pacchetto completo in offerta** | **35 €** *(proposta)* | **15 €/anno** *(proposta)* |

Da decidere e fare prima di vendere:
- ~~`LICENSE.md` va riscritto~~ fatto il 27/9/2026: ora è un contratto di licenza a pagamento (su indicazione
  della commercialista, niente donationware) e prevede già i moduli aggiuntivi (punto 5).
- Nuovi articoli in Stripe (`scripts/stripe-setup.mjs`): un `lookup_key` per addon, uno per il pacchetto,
  abbonamenti addon; la licenza sul server deve elencare gli addon attivi per account.
- Chi ha già la licenza: sconto sul pacchetto addon (es. 20 € per i 5 addon)?
- L'abbonamento aggiornamenti copre anche gli addon comprati o è separato? (proposta: uno solo per account,
  prezzo = base + 2 € per addon, tetto 15 €).

## 8. Ordine consigliato (dopo il 2 ottobre)

1. **Plugin v2**: è la base di tutto il resto.
2. **Sfida karaoke sui `.kar`**: massimo effetto in serata con materiale che c'è già.
3. **Togli strumento + accordi** (Studio).
4. **Builder Fitness** con esportazione.
5. Giochi del telefono (indovina la canzone, tombola), luci DMX, medley Live.
