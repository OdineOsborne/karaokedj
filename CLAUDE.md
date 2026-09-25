# Mixfonia — istruzioni per chi lavora su questo progetto

App Windows per DJ e karaoke: due deck, mixer, proiettore con i testi, coda cantanti, console MIDI.
WPF / .NET 8, `src/KaraokeDJ`. Il nome visibile è **Mixfonia**; l'identificativo dell'installazione resta
`KaraokeDJ` (packId Velopack) perché gli aggiornamenti delle versioni già installate continuino a funzionare.

**L'app è scritta in italiano**: interfaccia, messaggi, commenti del codice e messaggi di commit.
I commenti spiegano *perché* una cosa è fatta così, non cosa fa la riga.

## Regole non negoziabili

- **Chiavi e segreti**: la chiave di licenza privata sta in `F:\PROGR\KaraokeDJ-private\` (fuori dal repo,
  in `.gitignore`), le chiavi Stripe non si scrivono mai in chat né in un file del repo: le mette l'utente
  con `vercel env add`. Il token GitHub si legge con `git credential fill` e non si stampa mai.
- **Pubblicare**: si pubblica su GitHub Releases **solo** quando l'utente lo chiede esplicitamente.
  Preparare l'installer in locale va sempre bene; pubblicarlo no.
- **Il plugin yt-dlp non si distribuisce** con l'app su GitHub.
- **La serata viene prima di tutto**: la prima serata vera è il **2 ottobre 2026**, senza software di riserva.
  Qualsiasi modifica che possa fermare, far partire o rallentare la musica va trattata come critica.
  Regola di ferro già codificata: *la musica parte solo se lo decide il DJ* (vedi `stop:` nel selftest).

## Come si verifica quello che si fa

Ogni funzione importante ha una prova a riga di comando che si può lanciare **senza far rumore**
(quelle che suonano tengono il volume master a zero, quindi si possono lanciare con le casse accese):

```bash
KaraokeDJ.exe --selftest        # apre tutte le finestre + prove: import, corsa MIDI, drop, preset, shift, stop
KaraokeDJ.exe --jogtest         # i piatti vanno avanti E indietro, e non avviano un deck fermo
KaraokeDJ.exe --rectest         # la registrazione della serata contiene davvero il mix
KaraokeDJ.exe --layouttest      # niente fuori dalla finestra sui portatili da 13"
KaraokeDJ.exe --preflight       # controllo pre-serata (audio, cuffia, mic, proiettore, licenza, disco…)
KaraokeDJ.exe --audiotest       # EQ, filtro, fader, trim cambiano davvero il suono
KaraokeDJ.exe --gridtest 20     # la griglia dei battiti sta sui colpi veri
KaraokeDJ.exe --structtest 20   # la struttura dei brani (intro/ritornelli/finale) vale qualcosa
KaraokeDJ.exe --suggesttest "titolo"   # cosa verrebbe proposto dopo quel brano, e perché
KaraokeDJ.exe --midiwatch 15    # ascolta la console senza eseguire niente
KaraokeDJ.exe --ledtest         # accende i pad della console per 5 secondi
KaraokeDJ.exe --soak 240        # 4 ore da "DJ impazzito" (FA RUMORE: casse spente)
KaraokeDJ.exe --sections        # calcola la struttura ai brani che non ce l'hanno (silenzioso)
KaraokeDJ.exe --shot file.png   # fotografa la finestra (MIXFONIA_SHOT_WINDOW=impostazioni|preserata,
                                #  MIXFONIA_SHOT_TRACK="titolo", MIXFONIA_SIZE=1366x768)
```

**Una modifica non è finita finché la prova non gira.** Quando si corregge un difetto, il modo di lavorare è:
prima si scrive la prova che lo fa fallire, poi si corregge, poi si rimette la vecchia logica per un momento
per vedere che la prova fallisca davvero (controprova). È così che sono stati chiusi il piatto che andava solo
avanti e la musica che non si fermava.

## Come si costruisce

```bash
dotnet build src/KaraokeDJ/KaraokeDJ.csproj -c Release
cmd /c "publish.cmd 1.6.0-beta.NN"     # installer in build\Releases (NON pubblica)
node tools/controllers/check.js src/KaraokeDJ/Assets/Controllers   # audit dei preset console
```

## Trappole già pagate

- **Niente patch via heredoc di bash**: barre rovesce e `\n` vengono mangiati e il C# esce rotto.
  Si usano gli strumenti Write/Edit, oppure `python - <<'PY'` con `Environment.NewLine` al posto di `\n`.
- `publish.cmd` va lanciato col percorso completo: `cmd /c "F:\PROGR\KaraokeDJ\publish.cmd 1.6.0-beta.NN"`.
- Se `vpk` dice che esiste già una release ≥ della versione, si cancella il `.nupkg` di quella versione da
  `build\Releases` e si rilancia.
- **Un errore XAML non si vede compilando**: si vede solo aprendo la finestra. Per questo `--selftest` apre
  *tutte* le finestre. Dopo aver toccato uno XAML, lanciarlo.
- I dati dell'utente (libreria, impostazioni, coda, registrazioni) stanno in `%AppData%\KaraokeDJ`.

## Dove sono le cose

| | |
|---|---|
| Audio (deck, mixer, effetti, analisi, griglia, struttura) | `src/KaraokeDJ/Audio` |
| Logica (view model: mixer, live, suggerimenti, console, show) | `src/KaraokeDJ/ViewModels` |
| Servizi (libreria, MIDI, preset console, prove, telemetria) | `src/KaraokeDJ/Services` |
| Interfaccia | `src/KaraokeDJ/Views` |
| Preset delle console (42) | `src/KaraokeDJ/Assets/Controllers` |
| Strumenti di conversione e controllo dei preset | `tools/controllers` |
| Documentazione di lavoro | `docs/` — **`docs/subentro.md` è il punto di partenza** |
