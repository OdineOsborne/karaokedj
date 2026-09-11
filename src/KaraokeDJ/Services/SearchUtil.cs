using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KaraokeDJ.Audio;
using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>Ricerca tollerante (accenti, refusi, prefissi, BPM, tonalità) e punteggio di compatibilità tra brani.</summary>
public static class SearchUtil
{
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    public static string[] Words(string s) =>
        Regex.Split(Normalize(s), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToArray();

    /// <summary>Distanza di Levenshtein con uscita anticipata oltre <paramref name="max"/>.</summary>
    public static int Levenshtein(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > max) return max + 1;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Ogni parola della query deve trovare riscontro: sottostringa, prefisso, oppure refuso
    /// (1 errore da 5 lettere, 2 da 8). Numeri = BPM (±3 %), "8a"/"Am" = tonalità (Camelot vicina).
    /// </summary>
    public static bool Matches(Track t, string[] queryWords)
    {
        foreach (var q in queryWords)
        {
            if (q.Length == 0) continue;

            if (int.TryParse(q, out var bpm) && bpm is >= 50 and <= 220)
            {
                if (t.Bpm > 0 && BpmDistance(t.Bpm, bpm) <= 0.03) continue;
                return false;
            }
            if (Regex.IsMatch(q, @"^\d{1,2}[ab]$"))
            {
                var cam = AudioAnalyzer.CamelotOfKey(t.Key).ToLowerInvariant();
                if (cam.Length > 0 && CamelotDistance(cam, q) <= 1) continue;
                return false;
            }

            var words = t.SearchWords;
            bool ok = false;
            foreach (var w in words)
            {
                if (w.Contains(q) || (q.Length >= 3 && w.StartsWith(q))) { ok = true; break; }
                int max = q.Length >= 8 ? 2 : q.Length >= 5 ? 1 : 0;
                if (max > 0 && Levenshtein(q, w, max) <= max) { ok = true; break; }
            }
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Scarto relativo tra due BPM considerando anche tempo doppio / metà.</summary>
    public static double BpmDistance(double a, double b)
    {
        if (a <= 0 || b <= 0) return 1;
        double best = 1;
        foreach (var mult in new[] { 1.0, 2.0, 0.5 })
        {
            double r = a * mult / b;
            best = Math.Min(best, Math.Abs(r - 1));
        }
        return best;
    }

    /// <summary>0 = stessa, 1 = adiacente (±1 numero o cambio maggiore/minore), 2 = due passi, 9 = lontana.</summary>
    public static int CamelotDistance(string a, string b)
    {
        var ma = Regex.Match(a.ToLowerInvariant(), @"^(\d{1,2})([ab])$");
        var mb = Regex.Match(b.ToLowerInvariant(), @"^(\d{1,2})([ab])$");
        if (!ma.Success || !mb.Success) return 9;
        int na = int.Parse(ma.Groups[1].Value), nb = int.Parse(mb.Groups[1].Value);
        bool sameLetter = ma.Groups[2].Value == mb.Groups[2].Value;
        int diff = Math.Abs(na - nb); diff = Math.Min(diff, 12 - diff);
        if (diff == 0) return sameLetter ? 0 : 1;
        if (diff == 1) return sameLetter ? 1 : 2;
        if (diff == 2 && sameLetter) return 2;
        return 9;
    }

    /// <summary>Punteggio 0..1 di compatibilità per il mix (BPM + tonalità).</summary>
    public static double Compatibility(Track reference, Track candidate)
    {
        if (candidate.Id == reference.Id) return -1;
        double bpmScore;
        if (reference.Bpm > 0 && candidate.Bpm > 0)
        {
            double d = BpmDistance(candidate.Bpm, reference.Bpm);
            if (d > 0.08) return 0;
            bpmScore = 1 - d / 0.08;
        }
        else bpmScore = 0.4; // BPM sconosciuto: non escludiamo

        double keyScore = 0.4;
        var ca = AudioAnalyzer.CamelotOfKey(reference.Key);
        var cb = AudioAnalyzer.CamelotOfKey(candidate.Key);
        if (ca.Length > 0 && cb.Length > 0)
        {
            keyScore = CamelotDistance(ca, cb) switch { 0 => 1.0, 1 => 0.85, 2 => 0.5, _ => 0.05 };
        }
        return 0.6 * bpmScore + 0.4 * keyScore;
    }
}
