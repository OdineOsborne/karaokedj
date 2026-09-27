using System.Windows;
using System.Windows.Threading;
using KaraokeDJ.Models;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Scaletta dell'automix visibile in anticipo. Prima l'automix sceglieva il brano solo al momento del passaggio:
/// in serata il DJ non sapeva cosa sarebbe partito dopo. Ora, con automix e "mai fermarsi", la coda tiene
/// sempre i prossimi <see cref="PlanAhead"/> brani scelti in catena (ognuno dopo il precedente, con momento,
/// generi e range BPM). Il DJ li vede, li toglie (non tornano stasera) o mette i suoi, che passano davanti.
/// </summary>
public partial class MainViewModel
{
    public const int PlanAhead = 5;
    private bool _planning, _planScheduled;

    internal static bool IsAutoEntry(QueueEntry e) => e.Note.StartsWith("automix", StringComparison.OrdinalIgnoreCase);

    /// <summary>Dopo ogni cambio della coda, a lavoro finito: aggiungere dentro CollectionChanged non si può.</summary>
    private void SchedulePlanAutoMix()
    {
        if (_planning || _planScheduled || !AutoMix || !AutoMixEndless) return;
        _planScheduled = true;
        Application.Current?.Dispatcher.BeginInvoke(() => { _planScheduled = false; PlanAutoMix(); }, DispatcherPriority.Background);
    }

    /// <summary>Riempie la scaletta fino a <see cref="PlanAhead"/> proposte, in catena dall'ultimo brano in coda.</summary>
    public void PlanAutoMix()
    {
        if (_planning || !AutoMix || !AutoMixEndless) return;
        _planning = true;
        try
        {
            int auto = Queue.Count(IsAutoEntry);
            if (auto >= PlanAhead) return;
            var playing = DeckA.IsPlaying ? DeckA : DeckB.IsPlaying ? DeckB : DeckA.Track != null ? DeckA : DeckB.Track != null ? DeckB : null;
            // riferimento: l'ultimo brano in coda (la catena continua da lì), altrimenti quello che suona
            Track? refTrack = Queue.Count > 0 ? Queue[^1].Track : playing?.Track;
            double refBpm = refTrack != null && playing != null && refTrack == playing.Track ? EffectiveBpm(playing) : refTrack?.Bpm ?? 0;
            // con "aggancia BPM" il brano entrante prende il tempo di quello che esce: la catena resta al ritmo attuale
            if (BpmMatch && playing != null && EffectiveBpm(playing) > 0) refBpm = EffectiveBpm(playing);
            var exclude = QueuedAndOnDeckIds();
            int added = 0;
            while (auto < PlanAhead)
            {
                var (pick, why) = PickAutoNext(refTrack, refBpm, exclude, quiet: true);
                if (pick == null) break;
                Queue.Add(new QueueEntry { Track = pick, Note = "automix · " + why });
                exclude.Add(pick.Id);
                refTrack = pick;
                if (!BpmMatch && pick.Bpm > 0) refBpm = pick.Bpm;
                auto++; added++;
            }
            if (added > 0) StatusText = $"Automix: scaletta pronta, prossimi {auto} brani in coda (toglili se non vanno: non tornano stasera)";
        }
        finally { _planning = false; }
    }

    /// <summary>Via le proposte dell'automix ancora da suonare (senza "bocciarle": non le ha tolte il DJ).</summary>
    private void RemoveAutoEntries()
    {
        _planning = true;
        try { foreach (var e in Queue.Where(IsAutoEntry).ToList()) Queue.Remove(e); }
        finally { _planning = false; }
    }

    /// <summary>Cambiato momento, generi o range: la scaletta proposta si rifà da capo.</summary>
    private void ReplanAutoMix()
    {
        if (!AutoMix || !AutoMixEndless)
        {
            // senza "mai fermarsi" non c'è scaletta da rifare: meglio dirlo che lasciare il DJ ad aspettare
            if (AutoMix) StatusText = $"Momento {CurrentMoment.Name}: vale per le prossime scelte. Per vedere la scaletta in coda spunta «mai fermarsi»";
            return;
        }
        RemoveAutoEntries();
        PlanAutoMix();
        var first = Queue.FirstOrDefault(IsAutoEntry);
        if (first != null) StatusText = $"Scaletta rifatta per «{CurrentMoment.Name}»: dopo le tue richieste parte {first.Track.Display}";
    }
}
