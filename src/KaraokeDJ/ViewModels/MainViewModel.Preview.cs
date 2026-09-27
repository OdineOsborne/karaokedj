using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaraokeDJ.Models;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Pre-ascolto dalla libreria: il brano si sente solo in cuffia, senza caricarlo su un deck (come nei software DJ).
/// L'audio lo fa <see cref="Audio.AudioEngine.StartPreview"/>; qui scelta del brano, punto di partenza e barra.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private Track? _previewTrack;
    [ObservableProperty] private string _previewLabel = "";
    [ObservableProperty] private double _previewFraction;
    public bool IsPreviewing => PreviewTrack != null;
    partial void OnPreviewTrackChanged(Track? value) => OnPropertyChanged(nameof(IsPreviewing));

    /// <summary>Ascolta in cuffia il brano (o il selezionato); se è già quello in ascolto, lo ferma.</summary>
    [RelayCommand]
    private void PreviewTrackToggle(Track? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;
        if (PreviewTrack == t && Engine.PreviewOn) { StopPreview(); return; }
        if (!Engine.CueRunning) { StatusText = "Pre-ascolto: la cuffia non è configurata (Impostazioni → Audio → Cuffia)"; return; }
        if (t.IsMidi) { StatusText = "I karaoke MIDI non si pre-ascoltano: non hanno un audio da leggere"; return; }
        if (t.Kind == TrackKind.CdgZip) { StatusText = "Le basi CDG compresse (.zip) si ascoltano caricandole su un deck"; return; }
        // si parte dal punto utile: dopo l'intro se l'analisi l'ha trovata, altrimenti a un terzo del brano
        double start = t.IntroEndSec > 5 ? t.IntroEndSec : t.DurationSec > 60 ? t.DurationSec * 0.3 : 0;
        try
        {
            Engine.StartPreview(t.FilePath, start);
            PreviewTrack = t;
            StatusText = "🎧 In cuffia: " + t.Display;
        }
        catch (Exception ex) { PreviewTrack = null; StatusText = "Pre-ascolto non riuscito: " + ex.Message; }
    }

    [RelayCommand]
    private void StopPreview()
    {
        Engine.StopPreview();
        PreviewTrack = null; PreviewLabel = ""; PreviewFraction = 0;
    }

    /// <summary>Salta avanti/indietro di N secondi (parametro "15" o "-15").</summary>
    [RelayCommand]
    private void PreviewJump(string? seconds)
    {
        if (!Engine.PreviewOn || !double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var s)) return;
        Engine.SeekPreview(Engine.PreviewPositionSec + s);
    }

    /// <summary>Click sulla barra del pre-ascolto: salta a quella posizione (0..1).</summary>
    public void PreviewSeekFraction(double f)
    {
        if (!Engine.PreviewOn) return;
        Engine.SeekPreview(Math.Clamp(f, 0, 1) * Engine.PreviewDurationSec);
    }

    /// <summary>Dal timer dell'interfaccia: barra e tempo, e fine del brano.</summary>
    private void TickPreview()
    {
        if (PreviewTrack == null) return;
        if (!Engine.PreviewOn) { StopPreview(); return; }
        double pos = Engine.PreviewPositionSec, dur = Engine.PreviewDurationSec;
        PreviewFraction = dur > 0 ? pos / dur : 0;
        PreviewLabel = $"{TimeSpan.FromSeconds(pos):m\\:ss} / {TimeSpan.FromSeconds(dur):m\\:ss}";
    }
}
