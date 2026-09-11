using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KaraokeDJ.Models;

/// <summary>Un messaggio degli invitati sul festeggiato ("Parlaci di lui/lei/loro").</summary>
public sealed partial class GuestMessage : ObservableObject
{
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _text = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "manuale"; // manuale | web
}

/// <summary>Serata "Animazione": festeggiato, messaggi, testo generato per Suno, dedica.</summary>
public sealed partial class Celebration : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _pronoun = "lui";      // lui | lei | loro
    [ObservableProperty] private string _occasion = "compleanno";
    [ObservableProperty] private string _style = "pop italiano allegro, cantabile, festa";
    [ObservableProperty] private string _lyrics = "";
    [ObservableProperty] private string _songTitle = "";
    public ObservableCollection<GuestMessage> Messages { get; set; } = new();

    /// <summary>Testo breve mostrato sul proiettore mentre suona il brano dedicato.</summary>
    public string DedicationText =>
        Messages.Count == 0 ? $"Per {Name}" :
        $"Per {Name} — " + string.Join("  ·  ", Messages.Take(6).Select(m => string.IsNullOrWhiteSpace(m.From) ? m.Text : $"{m.Text} ({m.From})"));
}
