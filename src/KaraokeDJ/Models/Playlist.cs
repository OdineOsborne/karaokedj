using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KaraokeDJ.Models;

/// <summary>Playlist interna: elenco ordinato di id brano.</summary>
public sealed partial class Playlist : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [ObservableProperty] private string _name = "Nuova playlist";
    public ObservableCollection<string> TrackIds { get; set; } = new();

    [JsonIgnore] public string Display => $"{Name} ({TrackIds.Count})";

    public void NotifyCountChanged() => OnPropertyChanged(nameof(Display));
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Display));
}
