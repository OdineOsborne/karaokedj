using System.Text.RegularExpressions;
using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>
/// Rinomina intelligente: "Lucio Battisti - Emozioni (Official Audio)" con artista "Lucio Battisti"
/// diventa titolo "Emozioni"; se l'artista è sbagliato o è un canale (…VEVO, …Official, "Karaoke Academy")
/// e il titolo contiene "Artista - Titolo", l'artista vero viene preso dal titolo.
/// </summary>
public static class TitleCleaner
{
    private static readonly Regex Noise = new(
        @"\s*[\(\[\{]\s*(official[^\)\]\}]*|lyrics?|lyric\s*video|audio|video(clip)?|hd|hq|4k|8k|" +
        @"remaster(ed)?(\s*\d{4})?|\d{4}\s*remaster(ed)?|clip\s*officiel|video(clip)?\s*ufficiale|testo|karaoke\s*version|with\s*lyrics|visualizer|" +
        @"still\s*video|full\s*hd|upscaled?|remaster,?\s*resync\s*and\s*upscale|versione\s*karaoke[^\)\]\}]*)\s*[\)\]\}]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingNoise = new(
        @"\s*[-–|,]\s*(official\s*(music\s*)?(video|audio)|lyrics?\s*(video)?|full\s*hd|hd|4k|remaster(ed)?(\s*\d{4})?|video(clip)?\s*ufficiale|topic)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChannelLike = new(@"(vevo|official|music|records|tv|channel|karaoke|academy|videos?|topic|hits)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (string artist, string title) Clean(string artist, string title)
    {
        string a = artist.Trim().Replace('⧸', '/'), t = title.Trim().Replace('⧸', '/');

        // rumore tra parentesi e in coda
        string prev;
        do { prev = t; t = Noise.Replace(t, ""); t = TrailingNoise.Replace(t, ""); } while (t != prev);
        t = Regex.Replace(t, @"\s{2,}", " ").Trim(' ', '-', '–', '|', '_');

        // "Artista - Titolo" nel titolo (anche ripetuto: "Artista - Artista - Titolo")
        for (int pass = 0; pass < 2; pass++)
        {
            var m = Regex.Match(t, @"^(.*?)\s+[-–]\s+(.+)$");
            if (!m.Success) break;
            string left = m.Groups[1].Value.Trim(), right = m.Groups[2].Value.Trim();
            bool leftIsArtist = Similar(left, a);
            if (!leftIsArtist && Similar(right, a)) { t = left; break; } // "Titolo - Artista"
            // artista assente, canale, o comunque non presente nel titolo: fidiamoci di "X - Y"
            bool artistWrong = string.IsNullOrEmpty(a) || ChannelLike.IsMatch(a) || !t.Contains(a, StringComparison.OrdinalIgnoreCase);
            if (leftIsArtist) t = right;
            else if (artistWrong && left.Length >= 2 && left.Length <= 40 && !Regex.IsMatch(left, @"^\d+$")) { a = left; t = right; }
            else break;
        }
        // titolo che inizia con l'artista senza trattino: "Battisti Emozioni"
        if (!string.IsNullOrEmpty(a) && t.StartsWith(a + " ", StringComparison.OrdinalIgnoreCase) && t.Length > a.Length + 2)
            t = t[(a.Length + 1)..].Trim(' ', '-', '–', ':');
        // pulizia finale
        t = Noise.Replace(t, "").Trim(' ', '-', '–', '|');
        t = Regex.Replace(t, @"\s{2,}", " ");
        a = Regex.Replace(a, @"\s*(vevo|official|-\s*topic)\s*$", "", RegexOptions.IgnoreCase).Trim();
        if (t.Length == 0) t = title.Trim();
        return (a, t);
    }

    private static bool Similar(string x, string y)
    {
        if (string.IsNullOrWhiteSpace(x) || string.IsNullOrWhiteSpace(y)) return false;
        var nx = DownloadService.NormalizeForCompare(x); var ny = DownloadService.NormalizeForCompare(y);
        if (nx.Length < 2 || ny.Length < 2) return false;
        return nx == ny || nx.Contains(ny) || ny.Contains(nx);
    }

    /// <summary>Applica la pulizia al brano; ritorna true se qualcosa è cambiato.</summary>
    public static bool Apply(Track t, bool writeTags)
    {
        var (a, title) = Clean(t.Artist, t.Title);
        if (a == t.Artist && title == t.Title) return false;
        t.Artist = a; t.Title = title;
        t.InvalidateSearchCache();
        if (writeTags && t.Kind != TrackKind.CdgZip)
        {
            try
            {
                using var tf = TagLib.File.Create(t.FilePath);
                tf.Tag.Title = title;
                if (!string.IsNullOrEmpty(a)) tf.Tag.Performers = new[] { a };
                tf.Save();
                var info = new FileInfo(t.FilePath);
                t.FileSize = info.Length; t.FileModified = info.LastWriteTimeUtc; // così la scansione non lo rilegge
            }
            catch { }
        }
        return true;
    }
}
