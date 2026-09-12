using CommunityToolkit.Mvvm.ComponentModel;

namespace KaraokeDJ.Models;

public sealed partial class QueueEntry : ObservableObject
{
    [ObservableProperty] private string _singer = "";
    [ObservableProperty] private int _keyShift;
    /// <summary>Perché è in coda (es. "automix · stesso genere"), vuoto se messo a mano.</summary>
    [ObservableProperty] private string _note = "";
    public Track Track { get; set; } = new();

    public string KeyLabel => KeyShift == 0 ? "" : (KeyShift > 0 ? $"+{KeyShift}" : KeyShift.ToString());

    partial void OnKeyShiftChanged(int value) => OnPropertyChanged(nameof(KeyLabel));
}

/// <summary>Forma serializzabile della coda (per ripristino dopo crash).</summary>
public sealed class QueueEntryDto
{
    public string Singer { get; set; } = "";
    public int KeyShift { get; set; }
    public string TrackId { get; set; } = "";
}
