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
        @"remaster(ed)?(\s*\d{4})?|\d{4}\s*remaster(ed)?|clip\s*officiel|video(clip)?\s*ufficiale(\s*\d{4})?|testo|testo\s*[/_ ]?\s*lyrics|lyrics\s*[/_ ]?\s*testo|karaoke\s*version|with\s*lyrics|visualizer|" +
        @"still\s*video|full\s*hd|upscaled?|remaster,?\s*resync\s*and\s*upscale|versione\s*karaoke[^\)\]\}]*)\s*[\)\]\}]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrailingNoise = new(
        @"\s*[-–|,]\s*(official\s*(music\s*)?(video|audio)|lyrics?\s*(video)?|full\s*hd|hd|4k|remaster(ed)?(\s*\d{4})?|video(clip)?\s*ufficiale|topic)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChannelLike = new(@"(vevo|official|music|records|tv|channel|karaoke|academy|videos?|topic|hits)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Titoli che non dicono niente ("Traccia Audio 24", "01 - Traccia 01", "Track 5"): il nome vero è nel file.</summary>
    private static readonly Regex JunkTitle = new(@"^\s*(\d{1,3}\s*[-.]\s*)?(traccia(\s*audio)?|track|audio\s*track|brano)\s*\d{0,3}\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UnknownArtist = new(@"^\s*(<?sconosciuto>?|nessun artista|no artist|unknown( artist)?|artista sconosciuto|various( artists)?|vari)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>Titolo che è solo la versione ("Original Mix", "Major Lazer Remix"): il nome della canzone è finito altrove.</summary>
    private static readonly Regex VersionOnly = new(@"^\s*[\(\[]?\s*(original\s*mix|radio\s*(edit|mix)|extended(\s*mix)?|club\s*mix|[\w .&']{0,30}\b(remix|rework|bootleg|edit|vip))\s*[\)\]]?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>"Tenco-Vedrai, vedrai(vers.piano)": artista e titolo col trattino senza spazi, tipico delle raccolte di basi.</summary>
    private static readonly Regex TightDash = new(@"^([A-Za-zÀ-ÿ'][A-Za-zÀ-ÿ'. ]{2,28})-([A-Za-zÀ-ÿ0-9'’¿¡].{1,})$", RegexOptions.Compiled);

    public static (string artist, string title) Clean(string artist, string title) => Clean(artist, title, null);

    /// <param name="fileStem">nome del file senza estensione: serve quando il titolo non dice niente</param>
    public static (string artist, string title) Clean(string artist, string title, string? fileStem)
    {
        string a = artist.Trim().Replace('⧸', '/'), t = title.Trim().Replace('⧸', '/');
        if (UnknownArtist.IsMatch(a)) a = "";
        // titolo dal nome del file: se l'artista c'era già, dal file si prende solo il titolo (non un artista nuovo)
        bool titleFromFile = false;
        if (fileStem != null && (t.Length == 0 || JunkTitle.IsMatch(t) || Regex.IsMatch(t, @"^\s*audio\s*track\s*\d*\s*$", RegexOptions.IgnoreCase)))
        {
            t = Regex.Replace(fileStem, @"^\s*\d{1,4}\s*[-.]?\s+(?=\D)|^\s*\d{1,4}\s*[-.]\s*", "").Trim();
            titleFromFile = a.Length > 0;
        }
        string artistBefore = a;
        // titolo che è solo la versione e l'artista che sembra la canzone: "Apollo" / "Original Mix"
        if (VersionOnly.IsMatch(t) && a.Length > 0)
        {
            t = $"{a} ({t.Trim(' ', '(', ')', '[', ']')})";
            a = "";
        }

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
            else if (titleFromFile) break;   // "La Canzone Degli Amanti - moderato" dal file: l'artista del tag resta
            else if (artistWrong && left.Length >= 2 && left.Length <= 40 && !Regex.IsMatch(left, @"^\d+$")) { a = left; t = right; }
            else break;
        }
        // artista mancante e "Cognome-Titolo" col trattino attaccato (raccolte di basi): "Tenco-Vedrai, vedrai"
        if (string.IsNullOrEmpty(a))
        {
            var m = TightDash.Match(t);
            if (m.Success && !Regex.IsMatch(m.Groups[1].Value, @"^(ob|la|oh|e|mr|dj|b|hip|be|re|anti|ex|non|super)$", RegexOptions.IgnoreCase))
            { a = m.Groups[1].Value.Trim(); t = m.Groups[2].Value.Trim(); }
        }
        // titolo che inizia con l'artista senza trattino: "Battisti Emozioni" (non se l'artista è appena stato ricavato
        // dal titolo: "Ferro-Ferro e cartone" deve restare "Ferro e cartone")
        if (!string.IsNullOrEmpty(a) && a == artistBefore && t.StartsWith(a + " ", StringComparison.OrdinalIgnoreCase) && t.Length > a.Length + 2)
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
        var nx = SearchUtil.NormalizeForCompare(x); var ny = SearchUtil.NormalizeForCompare(y);
        if (nx.Length < 2 || ny.Length < 2) return false;
        return nx == ny || nx.Contains(ny) || ny.Contains(nx);
    }

    /// <summary>Applica la pulizia al brano; ritorna true se qualcosa è cambiato.</summary>
    public static bool Apply(Track t, bool writeTags)
    {
        var (a, title) = Clean(t.Artist, t.Title, Path.GetFileNameWithoutExtension(t.FilePath));
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
