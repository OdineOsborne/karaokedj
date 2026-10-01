using KaraokeDJ.Models;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Le playlist importate (Spotify, YouTube…) arrivano come cartelle dentro "_Nuovi": in Mixfonia si cercavano fra le
/// playlist e non c'erano. Ogni cartella di importazione diventa da sola la playlist "Importata · nome", e i brani
/// che arrivano dopo ci entrano. Le playlist puntano ai brani: nessun file viene copiato.
/// </summary>
public partial class MainViewModel
{
    public const string ImportPrefix = "Importata · ";
    private const string ImportFolderName = "_Nuovi";

    /// <summary>Allinea le playlist delle importazioni con le cartelle. Aggiunge soltanto: quello che il DJ toglie a mano resta tolto.</summary>
    public void SyncImportPlaylists()
    {
        try
        {
            var since = Settings.ImportPlaylistsSyncedUtc;
            var groups = Tracks
                .Select(t => (t, folder: ImportFolderOf(t.FilePath)))
                .Where(x => x.folder != null && !x.t.Missing)
                .GroupBy(x => x.folder!, StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool changed = false;
            foreach (var g in groups)
            {
                string name = ImportPrefix + g.Key.Trim();
                var pl = Playlists.FirstOrDefault(p => p.Name == name);
                bool isNew = pl == null;
                if (pl == null) { pl = new Playlist { Name = name }; Playlists.Insert(0, pl); changed = true; }
                var have = new HashSet<string>(pl.TrackIds);
                // la stessa canzone in due file (es. copia "(2)") entra una volta sola
                var songs = new HashSet<string>(pl.TrackIds.Select(id => Tracks.FirstOrDefault(t => t.Id == id)).OfType<Track>().Select(SongKey));
                foreach (var (t, _) in g.OrderBy(x => x.t.Artist).ThenBy(x => x.t.Title))
                {
                    // in una playlist che c'era già entrano solo i file arrivati dopo l'ultimo allineamento
                    if (have.Contains(t.Id) || !isNew && t.FileModified <= since) continue;
                    if (!songs.Add(SongKey(t))) continue;
                    pl.TrackIds.Add(t.Id); changed = true;
                }
                pl.NotifyCountChanged();
            }
            if (changed) SavePlaylists();
            Settings.ImportPlaylistsSyncedUtc = DateTime.UtcNow;
        }
        catch (Exception ex) { StatusText = "Playlist delle importazioni non aggiornate: " + ex.Message; }
    }

    /// <summary>"E:\Musica\_Nuovi\Cava💙\brano.mp3" → "Cava💙"; null se il file non è in una cartella di importazione.</summary>
    private static string? ImportFolderOf(string path)
    {
        var parts = path.Split('\\', '/');
        int i = Array.FindIndex(parts, p => p.Equals(ImportFolderName, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 2 < parts.Length ? parts[i + 1] : null;
    }
}
