namespace KaraokeDJ.Models;

public sealed class AppSettings
{
    public List<string> LibraryFolders { get; set; } = new();
    public string? OutputDeviceId { get; set; }
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
    public string TransitionStyle { get; set; } = "bass";
    /// <summary>Automix senza fine: coda vuota → pesca dai suggerimenti (mai silenzio).</summary>
    public bool AutoMixEndless { get; set; } = true;
    public bool MixViewVisible { get; set; } = true;
    /// <summary>Chiave API Anthropic, protetta con DPAPI (utente corrente).</summary>
    public string? AnthropicApiKeyProtected { get; set; }
    /// <summary>Chiave licenza donationware (legata alla macchina).</summary>
    public string? LicenseCode { get; set; }
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
    public List<Services.MidiMapping> MidiMappings { get; set; } = new();
    /// <summary>Scorciatoie da tastiera (vuoto = predefinite).</summary>
    public List<Services.KeyMapping> KeyMappings { get; set; } = new();
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 960;
}
