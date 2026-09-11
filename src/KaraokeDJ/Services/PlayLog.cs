using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>Una riproduzione registrata (per il borderò SIAE e lo storico serata).</summary>
public sealed class PlayLogEntry
{
    public DateTime Utc { get; set; }
    public string TrackId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    /// <summary>Autori/compositori dal tag (se presenti), altrimenti vuoto: nel borderò va compilato.</summary>
    public string Composer { get; set; } = "";
    public double DurationSec { get; set; }
    public string Deck { get; set; } = "";
    public bool Karaoke { get; set; }
}

/// <summary>Registro delle riproduzioni: un file JSON per giorno in %AppData%\KaraokeDJ\playlog.</summary>
public static class PlayLog
{
    public static string Dir => Path.Combine(AppPaths.Root, "playlog");
    private static readonly object Gate = new();

    private sealed class DayFile { public List<PlayLogEntry> Entries { get; set; } = new(); }

    private static string FileFor(DateTime localDate) => Path.Combine(Dir, localDate.ToString("yyyy-MM-dd") + ".json");

    public static void Record(Track t, string deck)
    {
        var e = new PlayLogEntry
        {
            Utc = DateTime.UtcNow, TrackId = t.Id, Title = t.Title, Artist = t.Artist, Composer = t.Composer,
            DurationSec = t.DurationSec, Deck = deck, Karaoke = t.IsKaraoke,
        };
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var path = FileFor(e.Utc.ToLocalTime().Date);
                var day = JsonStore.Load<DayFile>(path);
                day.Entries.Add(e);
                JsonStore.Save(path, day);
            }
            catch { }
        }
    }

    /// <summary>Riproduzioni fra due istanti locali (estremi inclusi), in ordine cronologico.</summary>
    public static List<PlayLogEntry> Between(DateTime fromLocal, DateTime toLocal)
    {
        var list = new List<PlayLogEntry>();
        lock (Gate)
        {
            for (var d = fromLocal.Date; d <= toLocal.Date; d = d.AddDays(1))
            {
                var path = FileFor(d);
                if (!File.Exists(path)) continue;
                try { list.AddRange(JsonStore.Load<DayFile>(path).Entries); } catch { }
            }
        }
        return list.Where(e => e.Utc.ToLocalTime() >= fromLocal && e.Utc.ToLocalTime() <= toLocal).OrderBy(e => e.Utc).ToList();
    }

    /// <summary>Giorni per cui esiste un registro (date locali), dal più recente.</summary>
    public static List<DateTime> Days()
    {
        try
        {
            if (!Directory.Exists(Dir)) return new();
            return Directory.EnumerateFiles(Dir, "????-??-??.json")
                .Select(f => DateTime.TryParse(Path.GetFileNameWithoutExtension(f), out var d) ? d : DateTime.MinValue)
                .Where(d => d != DateTime.MinValue).OrderByDescending(d => d).ToList();
        }
        catch { return new(); }
    }
}
