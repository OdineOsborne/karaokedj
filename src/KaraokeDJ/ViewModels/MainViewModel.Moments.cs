using CommunityToolkit.Mvvm.ComponentModel;
using KaraokeDJ.Services;

namespace KaraokeDJ.ViewModels;

/// <summary>Un momento della serata: generi, fascia di BPM (0 = qualsiasi) e intenzione per l'automix.</summary>
public sealed record SetMoment(string Id, string Name, string Genres, double MinBpm, double MaxBpm, FlowIntent Intent)
{
    public bool HasWindow => MaxBpm > 0;
    /// <summary>Il BPM (o la sua metà/doppio) cade nella fascia del momento.</summary>
    public bool InWindow(double bpm) => !HasWindow || (bpm > 0 && new[] { 1.0, 2.0, 0.5 }.Any(m => bpm * m >= MinBpm && bpm * m <= MaxBpm));
}

/// <summary>
/// «Generi serata» era un campo di testo libero: in serata nessuno si ferma a scrivere "Dance, House". Il DJ
/// sceglie il momento (aperitivo, apertura pista, latino, lenti…) e l'automix sa cosa pescare e a che ritmo.
/// I generi sono quelli che la libreria ha davvero (riordino del 25/9/2026).
/// </summary>
public partial class MainViewModel
{
    public const string CustomMomentId = "personalizzato";

    public static IReadOnlyList<SetMoment> Moments { get; } = new SetMoment[]
    {
        new("libero", "Libero", "", 0, 0, FlowIntent.Auto),
        new("aperitivo", "Aperitivo", "Lounge, Jazz, Electro swing, Soul, R&B", 80, 118, FlowIntent.Keep),
        new("cena", "Cena", "Lounge, Jazz, Pop, Pop italiano", 70, 110, FlowIntent.Down),
        new("apertura", "Apertura pista", "Pop, Pop italiano, Dance", 110, 124, FlowIntent.Up),
        new("pista", "Pista piena", "Dance, Elettronica, Techno", 122, 132, FlowIntent.Keep),
        new("latino", "Latino", "Musica latina, Reggaeton, Salsa, Bachata, Kizomba", 0, 0, FlowIntent.Keep),
        new("bachata", "Bachata", "Bachata", 0, 0, FlowIntent.Keep),
        new("liscio", "Liscio", "Liscio, Balli di gruppo", 0, 0, FlowIntent.Keep),
        new("balli", "Balli di gruppo", "Balli di gruppo", 0, 0, FlowIntent.Keep),
        new("italiana", "Italiana", "Pop italiano, Rock italiano, Rap italiano, Canzone napoletana", 0, 0, FlowIntent.Keep),
        new("napoletana", "Napoletana", "Canzone napoletana, Neomelodico", 0, 0, FlowIntent.Keep),
        new("rock", "Rock", "Rock, Rock italiano, Metal", 0, 0, FlowIntent.Keep),
        new("bambini", "Bambini", "Cartoni animati", 0, 0, FlowIntent.Keep),
        new("lenti", "Lenti", "Pop italiano, Pop, Soul, R&B", 55, 90, FlowIntent.Down),
        new("chiusura", "Chiusura", "Pop italiano, Pop, Lounge", 70, 105, FlowIntent.Down),
        new(CustomMomentId, "Personalizzato", "", 0, 0, FlowIntent.Auto),
    };

    /// <summary>Per l'interfaccia (i binding WPF non vedono le proprietà statiche attraverso il DataContext).</summary>
    public IReadOnlyList<SetMoment> MomentList => Moments;

    [ObservableProperty] private string _momentId = "libero";
    private bool _applyingMoment;

    public SetMoment CurrentMoment => Moments.FirstOrDefault(m => m.Id == MomentId) ?? Moments[0];

    partial void OnMomentIdChanged(string value)
    {
        Settings.AutoMixMoment = value;
        var m = CurrentMoment;
        if (m.Id != CustomMomentId)
        {
            _applyingMoment = true;
            try { SetGenres = m.Genres; FlowIntentNow = m.Intent; }
            finally { _applyingMoment = false; }
            StatusText = m.HasWindow
                ? $"Momento: {m.Name} · {m.MinBpm:0}–{m.MaxBpm:0} BPM" + (m.Genres.Length > 0 ? $" · {m.Genres}" : "")
                : $"Momento: {m.Name}" + (m.Genres.Length > 0 ? $" · {m.Genres}" : "");
        }
        OnPropertyChanged(nameof(CurrentMoment));
    }

    /// <summary>Generi cambiati a mano: il momento diventa "personalizzato" (i BPM del momento non valgono più).</summary>
    private void OnSetGenresEditedByHand()
    {
        if (_applyingMoment || MomentId == CustomMomentId || MomentId == "libero" && string.IsNullOrWhiteSpace(SetGenres)) return;
        if (SetGenres != CurrentMoment.Genres) MomentId = CustomMomentId;
    }
}
