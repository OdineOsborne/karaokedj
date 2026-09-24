using KaraokeDJ.Audio;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --jogtest</c>: il piatto va davvero avanti E indietro?
///
/// Rifà quello che fa la console vera, con i numeri presi dal registratore MIDI di una Hercules Instinct P8:
/// un messaggio per ogni scatto, valore 1 = avanti e 127 = indietro, a 200 scatti al secondo.
/// Poi misura dove è finita la puntina. Gira col volume master a zero: si può lanciare con le casse accese.
/// </summary>
public static class JogTest
{
    /// <summary>Scatti al secondo della prova: una girata decisa, come quelle registrate (280–345/s di picco).</summary>
    private const int TicksPerSecond = 200;

    public static async Task<string> RunAsync(MainViewModel vm)
    {
        var t = vm.Tracks.FirstOrDefault(x => x.DurationSec > 60 && !string.IsNullOrEmpty(x.FilePath) && File.Exists(x.FilePath));
        if (t == null) return "jog: saltato (serve un brano di almeno un minuto in libreria)";

        var errors = new List<string>();
        var lines = new List<string>();
        float volWas = vm.Engine.MasterVolume;
        double crossWas = vm.Crossfader;
        vm.Engine.MasterVolume = 0;
        vm.Crossfader = -1;
        var deck = vm.DeckA;
        try
        {
            if (!vm.LoadToDeck(deck, t, confirmIfPlaying: false)) return "jog: FAIL (brano non caricato)";
            deck.Deck.Seek(30);
            deck.Deck.Play();
            await Task.Delay(400);

            // 1) mano sul piatto, indietro: la puntina deve TORNARE INDIETRO
            double before = deck.Deck.PositionSec;
            await SpinAsync(vm, "a.jogscratch", 127, 0.5);
            double after = deck.Deck.PositionSec;
            lines.Add($"scratch indietro: {before:0.00}s → {after:0.00}s");
            if (after >= before) errors.Add($"indietro non torna indietro ({before:0.00}s → {after:0.00}s)");

            await Task.Delay(500);   // lascia riagganciare il deck

            // 2) mano sul piatto, avanti: deve correre più della riproduzione normale
            before = deck.Deck.PositionSec;
            await SpinAsync(vm, "a.jogscratch", 1, 0.5);
            after = deck.Deck.PositionSec;
            lines.Add($"scratch avanti:   {before:0.00}s → {after:0.00}s");
            if (after - before < 0.6) errors.Add($"avanti non accelera ({after - before:0.00}s in mezzo secondo)");

            await Task.Delay(500);

            // 3) piatto senza mano: pitch bend, il brano continua ma cambia passo
            before = deck.Deck.PositionSec;
            await SpinAsync(vm, "a.jog", 1, 0.5);
            double bendFwd = deck.Deck.PositionSec - before;
            await Task.Delay(400);
            before = deck.Deck.PositionSec;
            await SpinAsync(vm, "a.jog", 127, 0.5);
            double bendBack = deck.Deck.PositionSec - before;
            lines.Add($"bend: in {_lastSpinSec:0.00}s di girata il brano avanza {bendFwd:0.00}s girando avanti e {bendBack:0.00}s girando indietro");
            if (bendFwd <= bendBack) errors.Add("il pitch bend non distingue avanti e indietro");
            if (bendBack < 0) errors.Add("il pitch bend manda la traccia all'indietro (deve solo rallentare)");

            // 4) deck fermo: il piatto cerca il punto, ma NON fa partire la musica
            deck.Deck.Pause();
            await Task.Delay(200);
            before = deck.Deck.PositionSec;
            await SpinAsync(vm, "a.jogscratch", 127, 0.4);
            after = deck.Deck.PositionSec;
            lines.Add($"deck fermo, ricerca indietro: {before:0.00}s → {after:0.00}s");
            if (deck.Deck.IsPlaying) errors.Add("il piatto ha fatto partire un deck fermo");
            if (after >= before) errors.Add("a deck fermo il piatto non cerca all'indietro");
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        finally
        {
            try { vm.Panic(); deck.Eject(); } catch { }
            vm.Engine.MasterVolume = volWas;
            vm.Crossfader = crossWas;
        }

        return "jog: " + (errors.Count == 0
            ? "OK (scratch avanti e indietro, bend che distingue il verso, deck fermo che non parte)"
            : "ERRORI → " + string.Join("; ", errors)) + "\n  " + string.Join("\n  ", lines);
    }

    /// <summary>Quanto e durata davvero l'ultima girata (Windows non sa dormire 5 ms precisi).</summary>
    private static double _lastSpinSec;

    /// <summary>Manda scatti come la console vera: uno dietro l'altro, tutti nello stesso verso.</summary>
    private static async Task SpinAsync(MainViewModel vm, string action, int value, double seconds)
    {
        var t0 = DateTime.UtcNow;
        int n = (int)(seconds * TicksPerSecond);
        int stepMs = Math.Max(1, 1000 / TicksPerSecond);
        for (int i = 0; i < n; i++)
        {
            vm.SimulateMidi(action, value);
            await Task.Delay(stepMs);
        }
        _lastSpinSec = (DateTime.UtcNow - t0).TotalSeconds;
    }
}
