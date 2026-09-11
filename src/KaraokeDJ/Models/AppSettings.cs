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
    /// <summary>Chiave API Anthropic, protetta con DPAPI (utente corrente).</summary>
    public string? AnthropicApiKeyProtected { get; set; }
    /// <summary>Chiave licenza donationware (legata alla macchina).</summary>
    public string? LicenseCode { get; set; }
    public DateTime? LastSupportReminder { get; set; }
    public int CdgOffsetMs { get; set; } = 0;
    public double MasterVolume { get; set; } = 1.0;
    public string IdleTitle { get; set; } = "KARAOKE NIGHT";
    public string IdleSubtitle { get; set; } = "Prenota la tua canzone al DJ";
    public bool ShowNextSingersOnProjector { get; set; } = true;
    public List<PadDto> Pads { get; set; } = new();
    public string? MidiDeviceName { get; set; }
    public List<Services.MidiMapping> MidiMappings { get; set; } = new();
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 960;
}
