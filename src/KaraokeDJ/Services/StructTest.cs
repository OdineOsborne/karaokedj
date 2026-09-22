using KaraokeDJ.Audio;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --structtest [quanti]</c>: mostra la struttura trovata nei brani (intro, strofe, ritornelli, finale)
/// e soprattutto dice se quella divisione vale qualcosa: i confini scelti stanno su punti di cambiamento vero
/// oppure potevano stare ovunque? Il numero è di quanti scarti tipo la novità nei confini sta sopra la media del brano.
/// Sotto 1,00 l'app non si fida della struttura e l'auto-mix continua a fare come prima.
/// </summary>
public static class StructTest
{
    public static async Task<string> RunAsync(MainViewModel vm, int max)
    {
        var tracks = vm.Tracks
            .Where(t => t.Analyzed && !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath))
            .Take(max).ToList();
        if (tracks.Count == 0) return "struttura: nessun brano analizzato";

        var lines = new List<string>();
        double totScore = 0; int counted = 0, usable = 0;
        foreach (var t in tracks)
        {
            List<TrackSection> sections; double score;
            try
            {
                var beats = t.Beats?.Select(b => (double)b).ToArray();
                (sections, score) = await Task.Run(() => StructureAnalyzer.Analyze(t.FilePath, beats, CancellationToken.None));
            }
            catch (Exception ex) { lines.Add($"  ERRORE  {t.Display}: {ex.Message}"); continue; }
            if (sections.Count < 2) { lines.Add($"  ——      {t.Display}: nessuna struttura trovata"); continue; }
            counted++; totScore += score;
            if (score >= 1.0) usable++;
            var map = string.Join(" | ", sections.Select(s => $"{Fmt(s.Start)} {s.Kind}"));
            lines.Add($"  {score,5:0.00}  {t.Display}\n          {map}");
        }
        if (counted == 0) return "struttura: nessun brano utilizzabile\n" + string.Join("\n", lines);
        double avg = totScore / counted;
        lines.Sort();
        return $"struttura: i confini stanno {avg:0.00} scarti tipo sopra la novità media, {usable}/{counted} brani sopra 1,00" +
               (avg >= 1.0 ? " → struttura utilizzabile" : " → struttura da rivedere (l'auto-mix non la usa)") +
               "\n" + string.Join("\n", lines);
    }

    private static string Fmt(double sec) => TimeSpan.FromSeconds(sec).ToString(@"m\:ss");
}
