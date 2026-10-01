using CommunityToolkit.Mvvm.ComponentModel;
using KaraokeDJ.Models;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Automix da una playlist: la scaletta la decide la playlist (quella della festeggiata, i balli di gruppo…), non il
/// momento. «In ordine» la suona com'è; altrimenti l'automix sceglie dentro la playlist il brano che si lega meglio
/// (BPM, tonalità, energia). Finita la playlist si torna da soli al momento della serata, e lo si dice.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty] private Playlist? _autoMixPlaylist;
    [ObservableProperty] private bool _autoMixPlaylistInOrder;

    /// <summary>Per la tendina: la prima voce toglie la playlist (l'automix torna al momento).</summary>
    public IEnumerable<object> AutoMixPlaylistChoices => new object[] { NoPlaylist }.Concat(Playlists);
    public static readonly string NoPlaylist = "— dal momento —";
    public object AutoMixPlaylistChoice
    {
        get => (object?)AutoMixPlaylist ?? NoPlaylist;
        set => AutoMixPlaylist = value as Playlist;
    }

    partial void OnAutoMixPlaylistChanged(Playlist? value)
    {
        Settings.AutoMixPlaylistId = value?.Id;
        OnPropertyChanged(nameof(AutoMixPlaylistChoice));
        StatusText = value == null ? $"Automix: si torna al momento «{CurrentMoment.Name}»"
            : $"Automix dalla playlist «{value.Name}» ({value.TrackIds.Count} brani)" + (AutoMixPlaylistInOrder ? ", in ordine" : ", scelti per legarsi bene");
        ReplanAutoMix();
    }

    partial void OnAutoMixPlaylistInOrderChanged(bool value)
    {
        Settings.AutoMixPlaylistInOrder = value;
        if (AutoMixPlaylist != null) ReplanAutoMix();
    }

    private void RestoreAutoMixPlaylist()
    {
        _autoMixPlaylistInOrder = Settings.AutoMixPlaylistInOrder;
        _autoMixPlaylist = Playlists.FirstOrDefault(p => p.Id == Settings.AutoMixPlaylistId);
        Playlists.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AutoMixPlaylistChoices));
    }

    /// <summary>
    /// Brani della playlist ancora da suonare stasera (non in coda, non sui deck, non bocciati), nell'ordine della
    /// playlist. Vuota se non c'è una playlist scelta.
    /// </summary>
    private List<Track> PlaylistRemaining(HashSet<string> onDecks, HashSet<string> songsTaken)
    {
        var pl = AutoMixPlaylist;
        if (pl == null) return new();
        var byId = Tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        return pl.TrackIds.Select(id => byId.GetValueOrDefault(id)).OfType<Track>()
            .Where(t => !t.PlayedThisSession && !onDecks.Contains(t.Id) && !t.Missing && !songsTaken.Contains(SongKey(t)) && !Feedback.IsRejectedNow(t))
            .ToList();
    }
}
