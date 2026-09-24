namespace KaraokeDJ.Models;

public sealed class AppSettings
{
    public List<string> LibraryFolders { get; set; } = new();
    public string? OutputDeviceId { get; set; }
    /// <summary>Seconda uscita per la cuffia (pre-ascolto). Vuoto = nessuna.</summary>
    public string? CueDeviceId { get; set; }
    public double CueMix { get; set; } = 0;
    public double CueVolume { get; set; } = 1;
    /// <summary>Microfono: ingresso, gain, effetti voce, talk-over automatico.</summary>
    public string? MicDeviceId { get; set; }
    public bool MicOn { get; set; }
    public double MicGainDb { get; set; } = 0;
    public bool MicEcho { get; set; }
    public bool MicReverb { get; set; } = true;
    public bool MicAutoDuck { get; set; } = true;
    public double MicDuckDb { get; set; } = -10;
    public double MicDuckThresholdDb { get; set; } = -30;
    public double MicEqLow { get; set; }
    public double MicEqMid { get; set; }
    public double MicEqHigh { get; set; }
    /// <summary>Hot cue/loop/salti agganciati alla griglia.</summary>
    public bool Quantize { get; set; } = true;
    /// <summary>Musica di riempimento fra un cantante e l'altro: playlist (id) e volume.</summary>
    public bool FillMusicOn { get; set; }
    /// <summary>Striscia messaggi sul proiettore (un messaggio per riga).</summary>
    public bool TickerOn { get; set; }
    public string TickerText { get; set; } = "";
    public string? FillPlaylistId { get; set; }
    public double FillVolume { get; set; } = 0.7;
    /// <summary>Rotazione equa dei cantanti nella coda.</summary>
    public bool RotationOn { get; set; } = true;
    /// <summary>Prenotazioni dal pubblico (pagina /canta via QR) accettate.</summary>
    public bool PublicRequestsOn { get; set; } = true;
    /// <summary>Filtro libreria: solo brani in tonalità compatibile col deck in riproduzione.</summary>
    public bool KeyCompatFilter { get; set; }
    public int ProjectorScreenIndex { get; set; } = -1; // -1 = primo schermo non principale
    public double CrossfadeSeconds { get; set; } = 6;
    public bool AutoMix { get; set; }
    public bool AutoMixUseCues { get; set; } = true;
    /// <summary>Analizza automaticamente (BPM, tonalità, forma d'onda) i brani nuovi dopo la scansione.</summary>
    public bool AutoAnalyze { get; set; } = true;
    public bool BpmLock { get; set; }
    public double BpmLockValue { get; set; } = 120;
    public bool BpmMatch { get; set; } = true;
    /// <summary>Coerenza dei suggerimenti: "" libero, "decade" stessa decade, "genre" stesso genere.</summary>
    public string SuggestBy { get; set; } = "";
    /// <summary>Stile del passaggio automatico: fade · glide · bass · echo.</summary>
    public string TransitionStyle { get; set; } = "auto";
    /// <summary>Automix senza fine: coda vuota → pesca dai suggerimenti (mai silenzio).</summary>
    public bool AutoMixEndless { get; set; } = true;
    /// <summary>Generi della serata per l'automix (testo, separati da virgola).</summary>
    public string SetGenres { get; set; } = "";
    /// <summary>Generi creati a mano dall'utente: compaiono sempre negli elenchi.</summary>
    public List<string> CustomGenres { get; set; } = new();
    public bool MixViewVisible { get; set; } = true;
    /// <summary>Effetto vetro di Windows 11 (Mica) dietro la finestra principale.</summary>
    public bool GlassEffect { get; set; } = true;
    /// <summary>Nasconde in libreria i titoli incomprensibili.</summary>
    public bool HideCryptic { get; set; } = true;
    /// <summary>Pannelli FX dei deck aperti.</summary>
    public bool FxPanelsVisible { get; set; }
    /// <summary>Striscia in basso (pad + download) visibile.</summary>
    public bool BottomStripVisible { get; set; } = true;
    /// <summary>Chiave API Anthropic, protetta con DPAPI (utente corrente).</summary>
    public string? AnthropicApiKeyProtected { get; set; }
    /// <summary>Sorgente di importazione scelta (id) e client_id Jamendo (gratuito).</summary>
    public string? ImportSourceId { get; set; }
    public string? JamendoClientId { get; set; }
    /// <summary>Chiave licenza (perpetua, legata alla macchina).</summary>
    public string? LicenseCode { get; set; }
    /// <summary>Token aggiornamenti dell'account (rinnovo annuale), valido su tutte le macchine dell'account.</summary>
    public string? UpdatesToken { get; set; }
    /// <summary>Inizio del periodo di prova (30 giorni completi senza licenza).</summary>
    public DateTime? TrialStart { get; set; }
    /// <summary>Cartella dove salvare i download (vuota = MusicaKaraokeDJ Downloads).</summary>
    public string? DownloadFolder { get; set; }
    public DateTime? LastSupportReminder { get; set; }
    public int CdgOffsetMs { get; set; } = 0;
    public double MasterVolume { get; set; } = 1.0;
    public string IdleTitle { get; set; } = "MIX · SING · TOGETHER";
    /// <summary>Intestazione del borderò SIAE (programma musicale), ricordata fra le serate.</summary>
    public string BorderoOrganizzatore { get; set; } = "";
    public string BorderoLocale { get; set; } = "";
    public string BorderoComune { get; set; } = "";
    public string BorderoEsecutore { get; set; } = "";
    public string BorderoPermesso { get; set; } = "";
    public string IdleSubtitle { get; set; } = "Prenota la tua canzone al DJ";
    public bool ShowNextSingersOnProjector { get; set; } = true;
    public List<PadDto> Pads { get; set; } = new();
    public string? MidiDeviceName { get; set; }
    /// <summary>Preset di console scelto a mano: vince sul riconoscimento dal nome della porta.</summary>
    public string? ControllerPresetId { get; set; }
    /// <summary>Accendere i LED dei pad sulla console.</summary>
    public bool ControllerLeds { get; set; } = true;
    public List<Services.MidiMapping> MidiMappings { get; set; } = new();
    /// <summary>Scorciatoie da tastiera (vuoto = predefinite).</summary>
    public List<Services.KeyMapping> KeyMappings { get; set; } = new();
    /// <summary>Statistiche d'uso anonime per migliorare i suggerimenti (chieste una volta, sempre disattivabili).</summary>
    public bool UsageStatsOptIn { get; set; }
    /// <summary>true quando la domanda è già stata fatta (così non si ripete).</summary>
    public bool UsageStatsAsked { get; set; }
    /// <summary>Codice casuale dell'installazione per le statistiche: non è la licenza né l'ID macchina.</summary>
    public string UsageAnonId { get; set; } = "";

    /// <summary>Dimensione dell'interfaccia: 0 = automatica (si adatta alla finestra), altrimenti 0,6…1,5.</summary>
    public double UiScale { get; set; }
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 960;
}
