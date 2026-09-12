using System.Security.Cryptography;
using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

public sealed class DuplicateGroup
{
    public string Reason { get; init; } = "";      // "file identici" | "stesso brano"
    public Track Keep { get; init; } = new();
    public List<Track> Remove { get; init; } = new();
    public long BytesSaved => Remove.Sum(t => t.FileSize);
}

/// <summary>Trova doppioni in libreria: file identici (hash) e stesso brano (artista+titolo normalizzati, durata simile).</summary>
public static class DuplicateFinder
{
    public static List<DuplicateGroup> Find(IEnumerable<Track> tracks, IProgress<string>? progress, CancellationToken ct)
    {
        var all = tracks.Where(t => File.Exists(t.FilePath)).ToList();
        var groups = new List<DuplicateGroup>();
        var used = new HashSet<string>();

        // 1) file identici: stessa dimensione → hash parziale (primo e ultimo MB)
        int n = 0;
        foreach (var sizeGroup in all.GroupBy(t => (t.FileSize, Path.GetExtension(t.FilePath).ToLowerInvariant())).Where(g => g.Count() > 1))
        {
            ct.ThrowIfCancellationRequested();
            var byHash = new Dictionary<string, List<Track>>();
            foreach (var t in sizeGroup)
            {
                progress?.Report($"Confronto file {++n}…");
                try
                {
                    var h = QuickHash(t.FilePath);
                    if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = new();
                    list.Add(t);
                }
                catch { }
            }
            foreach (var same in byHash.Values.Where(l => l.Count > 1))
            {
                var keep = Best(same);
                groups.Add(new DuplicateGroup { Reason = "file identici", Keep = keep, Remove = same.Where(t => t != keep).ToList() });
                foreach (var t in same) used.Add(t.Id);
            }
        }

        // 2) stesso brano: chiave artista+titolo (stesso tipo), durata entro 3 s
        foreach (var g in all.Where(t => !used.Contains(t.Id)).GroupBy(t => Key(t)).Where(g => g.Key.Length >= 4 && g.Count() > 1))
        {
            var remaining = g.OrderByDescending(Score).ToList();
            while (remaining.Count > 1)
            {
                var keep = remaining[0];
                var same = remaining.Skip(1).Where(t => keep.DurationSec <= 0 || t.DurationSec <= 0 || Math.Abs(t.DurationSec - keep.DurationSec) <= 3).ToList();
                if (same.Count > 0)
                    groups.Add(new DuplicateGroup { Reason = "stesso brano", Keep = keep, Remove = same });
                remaining.RemoveAll(t => t == keep || same.Contains(t));
            }
        }
        return groups.OrderByDescending(g => g.BytesSaved).ToList();
    }

    private static string Key(Track t)
    {
        var a = DownloadService.NormalizeForCompare(t.Artist);
        var ti = DownloadService.NormalizeForCompare(t.Title);
        // stessa estensione obbligatoria: abc.mp3 e abc.mov non sono doppioni
        return t.Kind + "|" + Path.GetExtension(t.FilePath).ToLowerInvariant() + "|" + a + "|" + ti;
    }

    /// <summary>Qualità stimata: bitrate (dimensione/durata), analisi fatta, tag presenti, non zip.</summary>
    private static double Score(Track t)
    {
        double kbps = t.DurationSec > 0 ? t.FileSize * 8.0 / t.DurationSec / 1000 : 0;
        double s = Math.Min(kbps, 320);
        if (t.Analyzed) s += 20;
        if (!string.IsNullOrEmpty(t.Artist)) s += 10;
        if (t.PlayCount > 0) s += 5;
        if (Path.GetExtension(t.FilePath).Equals(".flac", StringComparison.OrdinalIgnoreCase)) s += 100;
        return s;
    }

    private static Track Best(List<Track> same) => same.OrderByDescending(Score).ThenBy(t => t.FilePath.Length).First();

    private static string QuickHash(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var buf = new byte[1024 * 1024];
        int r = fs.Read(buf, 0, buf.Length);
        sha.TransformBlock(buf, 0, r, null, 0);
        if (fs.Length > 2 * buf.Length)
        {
            fs.Seek(-buf.Length, SeekOrigin.End);
            r = fs.Read(buf, 0, buf.Length);
            sha.TransformBlock(buf, 0, r, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!) + ":" + fs.Length;
    }

    /// <summary>Manda il file nel Cestino (recuperabile).</summary>
    public static bool RecycleFile(string path)
    {
        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            var cdg = Path.ChangeExtension(path, ".cdg");
            if (File.Exists(cdg)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(cdg, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch { return false; }
    }
}
