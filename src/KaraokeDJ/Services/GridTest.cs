using KaraokeDJ.Audio;
using KaraokeDJ.Models;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --gridtest [quanti]</c>: controlla se la griglia dei battiti è davvero sui colpi del brano.
/// Prende i colpi di cassa dall'onda fine (banda bassa), li confronta con la griglia calcolata (BPM + aggancio)
/// e dice quanti cadono sul battito. Prova anche BPM doppio e metà: se uno dei due va molto meglio,
/// il BPM rilevato è sbagliato ed è quello a far sembrare storta la griglia.
/// </summary>
public static class GridTest
{
    public static async Task<string> RunAsync(MainViewModel vm, int max)
    {
        var tracks = vm.Tracks.Where(t => t.Analyzed && t.Bpm > 0 && !string.IsNullOrEmpty(t.FilePath) && File.Exists(t.FilePath)).Take(max).ToList();
        if (tracks.Count == 0) return "grid: nessun brano analizzato";
        var lines = new List<string>();
        double totOk = 0; int counted = 0, wrongBpm = 0;

        foreach (var t in tracks)
        {
            byte[]? fine;
            try { fine = await FineWaveform.GetOrComputeAsync(t.Id, t.FilePath, CancellationToken.None); }
            catch { continue; }
            if (fine == null || fine.Length < 400) continue;

            double off = t.BeatOffsetSec >= 0 ? t.BeatOffsetSec : BeatGrid.EstimateOffset(fine, t.Bpm);
            if (off < 0) off = 0;
            // colpi presi dall'audio vero (flusso spettrale, ~12 ms): dall'onda fine sarebbero troppo imprecisi
            List<double> onsets;
            try { onsets = await Task.Run(() => AudioAnalyzer.OnsetTimes(t.FilePath)); }
            catch { continue; }
            if (onsets.Count < 20) continue;

            // Il confronto va fatto col CASO: una griglia più fitta azzecca di più senza essere più giusta.
            // "x" = quante volte meglio del caso; sotto 1,5 la griglia non sta dicendo niente.
            var (hit, lift) = Hits(onsets, t.Bpm, off);
            var (_, liftHalf) = Hits(onsets, t.Bpm / 2, BeatGrid.EstimateOffset(fine, t.Bpm / 2));
            var (_, liftDouble) = Hits(onsets, t.Bpm * 2, BeatGrid.EstimateOffset(fine, t.Bpm * 2));
            string nota = "";
            if (liftHalf > lift * 1.25) { nota = $" ⚠ meglio a {t.Bpm / 2:0.0} BPM (x{liftHalf:0.0})"; wrongBpm++; }
            else if (liftDouble > lift * 1.25) { nota = $" ⚠ meglio a {t.Bpm * 2:0.0} BPM (x{liftDouble:0.0})"; wrongBpm++; }
            // controprova: la griglia MIGLIORE che esista per questi colpi. Se nemmeno quella batte il caso,
            // il problema non è la griglia ma il modo in cui stiamo leggendo i colpi.
            var (bestBpm, bestOff, bestLift) = BestGrid(onsets);
            totOk += lift; counted++;
            lines.Add($"  x{lift,4:0.0} ({hit,4:P0})  {t.Bpm,6:0.0} BPM  agg. {off,5:0.00}s  {t.Display}{nota}" +
                      $"   [migliore possibile: x{bestLift:0.0} a {bestBpm:0.0} BPM agg. {bestOff:0.00}s]");
        }

        if (counted == 0) return "grid: nessun brano utilizzabile";
        double avg = totOk / counted;
        lines.Sort();
        return $"grid: i colpi cadono sul battito x{avg:0.0} rispetto al caso, su {counted} brani" +
               (wrongBpm > 0 ? $" · {wrongBpm} con BPM probabilmente doppio/metà" : "") +
               (avg >= 2 ? " → griglia attendibile" : avg >= 1.4 ? " → griglia così così" : " → griglia da rivedere") +
               "\n" + string.Join("\n", lines);
    }

    /// <summary>Colpi: colonne dove l'energia dei bassi sale di scatto (picco locale sopra la media).</summary>
    private static List<double> Onsets(byte[] fine)
    {
        int cols = fine.Length / 2;
        double colSec = 1.0 / FineWaveform.ColumnsPerSec;
        var bass = new double[cols];
        for (int c = 0; c < cols; c++) bass[c] = fine[c * 2 + 1];
        double mean = bass.Average();
        var list = new List<double>();
        for (int c = 2; c < cols - 2; c++)
        {
            double rise = bass[c] - bass[c - 2];
            if (bass[c] < mean * 1.1 || rise < 12) continue;
            if (bass[c] < bass[c + 1] || bass[c] < bass[c - 1]) continue;    // solo i massimi locali
            if (list.Count > 0 && (c * colSec - list[^1]) < 0.12) continue;  // niente doppioni entro 120 ms
            list.Add(c * colSec);
        }
        return list;
    }

    /// <summary>La griglia che spiega meglio questi colpi, cercata a forza bruta: serve da metro di paragone.</summary>
    private static (double Bpm, double Offset, double Lift) BestGrid(List<double> onsets)
    {
        double bestBpm = 0, bestOff = 0, bestLift = 0;
        for (double bpm = 60; bpm <= 200.01; bpm += 0.25)
        {
            double beat = 60.0 / bpm;
            for (int s = 0; s < 40; s++)
            {
                double off = beat * s / 40;
                var (_, lift) = Hits(onsets, bpm, off);
                if (lift > bestLift) { bestLift = lift; bestBpm = bpm; bestOff = off; }
            }
        }
        return (bestBpm, bestOff, bestLift);
    }

    private const double Tol = 0.04;   // ±40 ms: quanto può stare "sul battito" un colpo suonato da persone vere

    /// <summary>Quota di colpi entro la tolleranza dal battito, e quante volte è meglio del caso.</summary>
    private static (double Rate, double Lift) Hits(List<double> onsets, double bpm, double offset)
    {
        if (bpm <= 0 || onsets.Count == 0) return (0, 0);
        if (offset < 0) offset = 0;
        double beat = 60.0 / bpm;
        int ok = 0;
        foreach (var o in onsets)
        {
            double phase = (o - offset) / beat;
            double d = Math.Abs(phase - Math.Round(phase)) * beat;
            if (d <= Tol) ok++;
        }
        double rate = (double)ok / onsets.Count;
        double chance = Math.Min(1, 2 * Tol / beat);      // probabilità di azzeccarci per caso con questa griglia
        return (rate, chance > 0 ? rate / chance : 0);
    }
}
