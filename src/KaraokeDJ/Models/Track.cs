using System.Text.Json.Serialization;

namespace KaraokeDJ.Models;

public enum TrackKind
{
    Audio,
    Cdg,      // .mp3 + .cdg accanto
    CdgZip,   // .zip contenente mp3 + cdg
    Video,    // mp4 / mkv / avi con testo integrato
    Midi,     // .mid / .kar: reso in audio con FluidSynth, testo dagli eventi lyric
    Lrc,      // audio + testo sincronizzato .lrc (o .txt con [mm:ss]) accanto al file
}

public sealed class Track
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string FilePath { get; set; } = "";
    public string? CdgPath { get; set; }
    /// <summary>File .lrc/.txt sincronizzato accanto al brano (Kind = Lrc).</summary>
    public string? LyricsPath { get; set; }
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
    /// <summary>Anno dal tag (0 = sconosciuto).</summary>
    public int Year { get; set; }
    /// <summary>Genere dal tag ("" = sconosciuto).</summary>
    public string Genre { get; set; } = "";
    /// <summary>Generi come tag multipli: "Dance; Pop italiano" → ["Dance", "Pop italiano"].</summary>
    [JsonIgnore] public IEnumerable<string> Genres => SplitGenres(Genre);
    public static IEnumerable<string> SplitGenres(string? s) =>
        (s ?? "").Split(new[] { ';', ',', '/', '|' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0);
    public bool HasGenre(string g) => Genres.Any(x => string.Equals(x, g.Trim(), StringComparison.OrdinalIgnoreCase));
    /// <summary>Aggiunge o toglie un tag di genere.</summary>
    public void ToggleGenre(string g)
    {
        var list = Genres.ToList();
        var i = list.FindIndex(x => string.Equals(x, g.Trim(), StringComparison.OrdinalIgnoreCase));
        if (i >= 0) list.RemoveAt(i); else list.Add(g.Trim());
        Genre = string.Join("; ", list);
    }
    /// <summary>Autori/compositori dal tag TCOM (per il borderò SIAE); "" se assenti.</summary>
    public string Composer { get; set; } = "";
    /// <summary>Versione dei metadati letti: se inferiore a LibraryService.TagsVersion il file viene riletto alla scansione.</summary>
    public int TagsVersion { get; set; }
    [JsonIgnore] public string YearLabel => Year > 0 ? Year.ToString() : "";
    /// <summary>"80s", "2000s"… vuoto se anno sconosciuto.</summary>
    [JsonIgnore] public string Decade => Year <= 0 ? "" : (Year < 2000 ? (Year / 10 * 10 % 100).ToString("00") : (Year / 10 * 10).ToString()) + "s";
    /// <summary>Fine dell'intro (s). 0 = nessuna intro rilevata.</summary>
    public double IntroEndSec { get; set; }
    /// <summary>Inizio dell'uscita (s). 0 = sconosciuto.</summary>
    public double OutroStartSec { get; set; }
    /// <summary>Intro/uscita impostate a mano: l'analisi automatica non le sovrascrive.</summary>
    public bool CuesManual { get; set; }
    /// <summary>Punto cue (s), -1 = non impostato.</summary>
    public double CueSec { get; set; } = -1;
    /// <summary>Hot cue 1–8 in secondi (-1 = vuoto).</summary>
    public double[] HotCues { get; set; } = EmptyHotCues();
    public static double[] EmptyHotCues() => new[] { -1.0, -1, -1, -1, -1, -1, -1, -1 };
    public double HotCue(int i) => HotCues != null && i >= 0 && i < HotCues.Length ? HotCues[i] : -1;
    public void SetHotCue(int i, double sec) { if (HotCues == null || HotCues.Length < 8) HotCues = EmptyHotCues(); if (i >= 0 && i < 8) HotCues[i] = sec; }
    /// <summary>Fase della griglia dei battiti: secondi del primo "1" (-1 = non ancora stimata).</summary>
    public double BeatOffsetSec { get; set; } = -1;
    /// <summary>Griglia corretta a mano: la stima automatica non la sovrascrive.</summary>
    public bool BeatManual { get; set; }
    public int PlayCount { get; set; }
    /// <summary>Versione senza voce (Demucs), se generata.</summary>
    public string? InstrumentalPath { get; set; }
    public string? VocalsPath { get; set; }
    /// <summary>Cartella con i 4 stem Demucs (vocals/drums/bass/other.mp3), se generati.</summary>
    public string? StemsDir { get; set; }
    [JsonIgnore] public bool HasStems => Audio.StemMixReader.HasAll(StemsDir);
    /// <summary>Dedica mostrata sul proiettore mentre il brano suona (serata Animazione).</summary>
    public string? Dedication { get; set; }
    public string? DedicationTitle { get; set; }
    /// <summary>Brano generato con Suno (importato dalla cartella monitorata).</summary>
    public bool IsSuno { get; set; }
    [JsonIgnore] public bool HasInstrumental => (!string.IsNullOrEmpty(InstrumentalPath) && File.Exists(InstrumentalPath)) || HasStems;
    public DateTime? LastPlayedUtc { get; set; }

    /// <summary>Impostato dall'app: suonato in questa serata (sessione).</summary>
    [JsonIgnore] public bool PlayedThisSession { get; set; }
    [JsonIgnore] public string PlayedLabel => PlayedThisSession && LastPlayedUtc != null ? "✓ " + LastPlayedUtc.Value.ToLocalTime().ToString("HH:mm") : (PlayCount > 0 ? $"{PlayCount}×" : "");

    [JsonIgnore] public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
    [JsonIgnore] public bool IsKaraoke => Kind != TrackKind.Audio;
    [JsonIgnore] public bool IsVideo => Kind == TrackKind.Video;
    [JsonIgnore] public bool IsMidi => Kind == TrackKind.Midi;
    [JsonIgnore] public bool IsLrc => Kind == TrackKind.Lrc;
    [JsonIgnore] public bool IsCdg => Kind is TrackKind.Cdg or TrackKind.CdgZip;

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        TrackKind.Audio => "Audio",
        TrackKind.Video => "Video",
        TrackKind.Midi => "MIDI",
        TrackKind.Lrc => "LRC",
        _ => "CDG",
    };

    [JsonIgnore] public string BpmLabel => Bpm > 0 ? Bpm.ToString("0") : "";
    [JsonIgnore] public string KeyLabel => string.IsNullOrEmpty(Key) ? "" : Key + (Audio.AudioAnalyzer.CamelotOfKey(Key) is { Length: > 0 } c ? $" ({c})" : "");

    [JsonIgnore] public string DurationLabel => DurationSec <= 0 ? "--:--" : TimeSpan.FromSeconds(DurationSec).ToString(@"m\:ss");

    [JsonIgnore] public string MatchLabel { get; set; } = "";
    /// <summary>Se non null, questo brano è una copia doppia di un altro e viene nascosto in libreria (il file resta).</summary>
    [JsonIgnore] public Track? HiddenDuplicateOf { get; set; }
    /// <summary>File non raggiungibile in questo momento (disco scollegato): nascosto, non cancellato.</summary>
    [JsonIgnore] public bool Missing { get; set; }

    private string[]? _words;
    [JsonIgnore] public string[] SearchWords => _words ??= Services.SearchUtil.Words(Artist + " " + Title + " " + Genre + " " + Decade + " " + Path.GetFileNameWithoutExtension(FilePath));
    public void InvalidateSearchCache() { _words = null; }

    [JsonIgnore] public string SearchKey => (Artist + " " + Title + " " + Path.GetFileNameWithoutExtension(FilePath)).ToLowerInvariant();
}
