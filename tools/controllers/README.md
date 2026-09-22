# Preset console (Assets/Controllers/*.json)

Generati dalle mappature MIDI di Mixxx (XML, GPL: usiamo solo i numeri MIDI) e, dove Mixxx non ha la console, da altre fonti
(documento ufficiale Hercules per la Instinct P8, file djay per la Inpulse 200 MK2).

```
cd tools/controllers
# scaricare i .midi.xml (e gli -scripts.js) da https://github.com/mixxxdj/mixxx/tree/main/res/controllers nella stessa cartella
node convert.js ../../src/KaraokeDJ/Assets/Controllers          # Pioneer, Numark, Denon, Reloop, Roland, Inpulse 200/300/500, Starlight
node convert-hercules.js ../../src/KaraokeDJ/Assets/Controllers # resto della famiglia Hercules (+ 200 MK2 da djay, T7 derivata dalla 500)
node unmapped.js "<file>.midi.xml"                              # cosa il convertitore non ha saputo mappare
```

`script-map.js` contiene le euristiche per i controlli legati a script (nome funzione + descrizione + gruppo + numero).
La Instinct P8 è scritta a mano (scratchpad p8.js → hercules-instinct-p8.json) dal PDF "MIDI Command List v1.1" di Hercules.

Scritti a mano: `instinct-p8.js` (Hercules Instinct P8, dal PDF ufficiale) e `launchpad.js` (Novation Launchpad, due layout).
La stessa logica di conversione vive anche nell'app (`Services/MappingImporters.cs`): Impostazioni → MIDI → *Importa mappatura…*
accetta direttamente i file Mixxx/djay/JSON, così l'utente non ha bisogno di questi script. Guida utente: docs/console.md.
