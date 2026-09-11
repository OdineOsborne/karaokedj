using System.Text.Json.Serialization;

namespace KaraokeDJ.Models;

public enum TrackKind
{
    Audio,
    Cdg,      // .mp3 + .cdg accanto
    CdgZip,   // .zip contenente mp3 + cdg
    Video,    // mp4 / mkv / avi con testo integrato
}

public sealed class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public string? CdgPath { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public double DurationSec { get; set; }
    public TrackKind Kind { get; set; }
    public long FileSize { get; set; }
    public DateTime FileModified { get; set; }
    /// <summary>0 = sconosciuto. Da tag TBPM o dall'analisi integrata.</summary>
    public double Bpm { get; set; }
    /// <summary>Tonalità tipo "Am", "F#". Vuota = sconosciuta.</summary>
    public string Key { get; set; } = "";
    public bool Analyzed { get; set; }
    /// <summary>Fine dell'intro (s). 0 = nessuna intro rilevata.</summary>
    public double IntroEndSec { get; set; }
    /// <summary>Inizio dell'uscita (s). 0 = sconosciuto.</summary>
    public double OutroStartSec { get; set; }
    /// <summary>Intro/uscita impostate a mano: l'analisi automatica non le sovrascrive.</summary>
    public bool CuesManual { get; set; }
    public int PlayCount { get; set; }
    /// <summary>Versione senza voce (Demucs), se generata.</summary>
    public string? InstrumentalPath { get; set; }
    public string? VocalsPath { get; set; }
    /// <summary>Dedica mostrata sul proiettore mentre il brano suona (serata Animazione).</summary>
    public string? Dedication { get; set; }
    public string? DedicationTitle { get; set; }
    /// <summary>Brano generato con Suno (importato dalla cartella monitorata).</summary>
    public bool IsSuno { get; set; }
    [JsonIgnore] public bool HasInstrumental => !string.IsNullOrEmpty(InstrumentalPath) && File.Exists(InstrumentalPath);
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Impostato dall'app: suonato in questa serata (sessione).</summary>
    [JsonIgnore] public bool PlayedThisSession { get; set; }
    [JsonIgnore] public string PlayedLabel => PlayedThisSession && LastPlayedUtc != null ? "✓ " + LastPlayedUtc.Value.ToLocalTime().ToString("HH:mm") : (PlayCount > 0 ? $"{PlayCount}×" : "");

    [JsonIgnore] public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
    [JsonIgnore] public bool IsKaraoke => Kind != TrackKind.Audio;
    [JsonIgnore] public bool IsVideo => Kind == TrackKind.Video;
    [JsonIgnore] public bool IsCdg => Kind is TrackKind.Cdg or TrackKind.CdgZip;

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        TrackKind.Audio => "Audio",
        TrackKind.Video => "Video",
        _ => "CDG",
    };

    [JsonIgnore] public string BpmLabel => Bpm > 0 ? Bpm.ToString("0") : "";
    [JsonIgnore] public string KeyLabel => string.IsNullOrEmpty(Key) ? "" : Key + (Audio.AudioAnalyzer.CamelotOfKey(Key) is { Length: > 0 } c ? $" ({c})" : "");

    [JsonIgnore] public string DurationLabel => DurationSec <= 0 ? "--:--" : TimeSpan.FromSeconds(DurationSec).ToString(@"m\:ss");

    [JsonIgnore] public string MatchLabel { get; set; } = "";

    private string[]? _words;
    [JsonIgnore] public string[] SearchWords => _words ??= Services.SearchUtil.Words(Artist + " " + Title + " " + Path.GetFileNameWithoutExtension(FilePath));
    public void InvalidateSearchCache() { _words = null; }

    [JsonIgnore] public string SearchKey => (Artist + " " + Title + " " + Path.GetFileNameWithoutExtension(FilePath)).ToLowerInvariant();
}
