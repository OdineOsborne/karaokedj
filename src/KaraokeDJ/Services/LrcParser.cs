using System.Globalization;
using System.Text.RegularExpressions;

namespace KaraokeDJ.Services;

/// <summary>
/// Testi sincronizzati .lrc (una riga = "[mm:ss.xx]testo", anche più tempi per riga) ed enhanced LRC
/// ("[mm:ss.xx]&lt;mm:ss.xx&gt;parola &lt;mm:ss.xx&gt;parola"): diventano gli stessi LyricEvent dei file KAR,
/// così proiettore e deck li mostrano allo stesso modo. Un .txt con gli stessi marcatori va bene uguale.
/// </summary>
public static class LrcParser
{
    private static readonly Regex LineTime = new(@"^\s*(?:\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\])+", RegexOptions.Compiled);
    private static readonly Regex AnyTime = new(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex WordTime = new(@"<(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled);
    private static readonly Regex WordTimeSplit = new(@"<\d{1,2}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);
    private static readonly Regex MetaTag = new(@"^\s*\[(ar|ti|al|by|offset|length|re|ve|la):(.*)\]\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsLrc(string path) => Path.GetExtension(path).Equals(".lrc", StringComparison.OrdinalIgnoreCase);

    /// <summary>File di testo accanto al brano: stesso nome con .lrc (o .txt che contiene marcatori [mm:ss]).</summary>
    public static string? FindSidecar(string audioPath)
    {
        var lrc = Path.ChangeExtension(audioPath, ".lrc");
        if (File.Exists(lrc)) return lrc;
        var txt = Path.ChangeExtension(audioPath, ".txt");
        try
        {
            if (File.Exists(txt) && new FileInfo(txt).Length < 200_000)
            {
                using var r = new StreamReader(txt);
                for (int i = 0; i < 40 && r.ReadLine() is { } line; i++) if (AnyTime.IsMatch(line)) return txt;
            }
        }
        catch { }
        return null;
    }

    private static double Sec(Match m, int g0) =>
        int.Parse(m.Groups[g0].Value) * 60 + int.Parse(m.Groups[g0 + 1].Value) + (m.Groups[g0 + 2].Success ? int.Parse(m.Groups[g0 + 2].Value.PadRight(3, '0')) / 1000.0 : 0);

    public static List<LyricEvent> Parse(string path)
    {
        var list = new List<LyricEvent>();
        double offset = 0;
        string[] lines;
        try { lines = File.ReadAllLines(path); } catch { return list; }
        foreach (var raw in lines)
        {
            var mm = MetaTag.Match(raw);
            if (mm.Success)
            {
                if (mm.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase) && double.TryParse(mm.Groups[2].Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var off)) offset = off / 1000.0;
                continue;
            }
            var lt = LineTime.Match(raw);
            if (!lt.Success) continue;
            var times = AnyTime.Matches(raw).Select(x => Sec(x, 1)).ToList();
            var text = raw[lt.Length..];
            foreach (var t0 in times)
            {
                var words = WordTimeSplit.Split(text);
                var wtimes = WordTime.Matches(text);
                if (wtimes.Count == 0)
                {
                    var txt = text.Trim();
                    if (txt.Length > 0) list.Add(new LyricEvent(t0 - offset, txt, true));
                    continue;
                }
                // enhanced: il primo pezzo (prima del primo <tempo>) va al tempo di riga
                bool first = true;
                for (int i = 0; i < words.Length; i++)
                {
                    var piece = words[i];
                    if (piece.Length == 0) continue;
                    double at = i == 0 ? t0 : Sec(wtimes[i - 1], 1);
                    if (!piece.EndsWith(' ')) piece += " ";
                    list.Add(new LyricEvent(at - offset, piece, first));
                    first = false;
                }
            }
        }
        list.Sort((a, b) => a.Sec.CompareTo(b.Sec));
        return list;
    }
}
