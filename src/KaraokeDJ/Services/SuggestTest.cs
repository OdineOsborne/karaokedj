using KaraokeDJ.Models;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --suggesttest "hells bells"</c>: stampa cosa l'app proporrebbe dopo quel brano, col motivo.
/// Serve a controllare sui dati veri che non escano accostamenti assurdi (il caso da cui è nato: Battisti dopo gli AC/DC).
/// Analizza al volo i candidati che non hanno ancora energia/brillantezza, altrimenti il confronto non avrebbe dati.
/// </summary>
public static class SuggestTest
{
    public static async Task<string> RunAsync(MainViewModel vm, string query, int analyzeMax = 25)
    {
        var norm = SearchUtil.NormalizeForCompare(query);
        var reference = vm.Tracks.FirstOrDefault(t => SearchUtil.NormalizeForCompare(t.Title).Contains(norm))
                     ?? vm.Tracks.FirstOrDefault(t => SearchUtil.NormalizeForCompare(t.Display).Contains(norm));
        if (reference == null) return $"suggest: brano non trovato per \"{query}\" fra {vm.Tracks.Count} in libreria";

        // i candidati mixabili per BPM/tonalità: sono quelli che possono comparire fra i suggeriti
        var candidates = vm.Tracks.Where(t => t.Id != reference.Id && !t.IsKaraoke && SearchUtil.Compatibility(reference, t) > 0)
                                  .OrderByDescending(t => SearchUtil.Compatibility(reference, t)).Take(analyzeMax).ToList();
        int analysed = 0;
        foreach (var t in candidates.Prepend(reference))
        {
            if (t.Energy > 0 || string.IsNullOrEmpty(t.FilePath) || !File.Exists(t.FilePath)) continue;
            try
            {
                var r = await Task.Run(() => Audio.AudioAnalyzer.Analyze(t.FilePath));
                t.Bpm = r.Bpm; t.Key = r.Key; t.Energy = r.Energy; t.Brightness = r.Brightness; t.Analyzed = true;
                vm.Library.Save(t);
                analysed++;
            }
            catch { }
        }

        vm.SelectedTrack = reference;
        vm.UpdateSuggestionsFor(reference);
        var lines = vm.Suggestions.Select(s => $"  {s.MatchLabel,5}  {s.Display}  [{s.MatchWhy}]  {s.Bpm:0} BPM, {s.Key}, {(s.Year > 0 ? s.Year.ToString() : "?")}, energia {s.Energy:0.00}, brillantezza {s.Brightness:0.00}");
        return $"suggest dopo: {reference.Display} ({reference.Bpm:0} BPM, {reference.Key}, energia {reference.Energy:0.00}, brillantezza {reference.Brightness:0.00})"
             + $"\n  analizzati al volo: {analysed} · candidati mixabili: {candidates.Count} · {vm.SuggestionsLabel}\n"
             + (vm.Suggestions.Count == 0 ? "  (nessun suggerimento: meglio nessuno che uno sbagliato)" : string.Join("\n", lines));
    }
}
