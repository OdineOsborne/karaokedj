using System.Windows;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

/// <summary>
/// Richiesta di consenso alle statistiche d'uso: si fa una volta sola, la risposta viene salvata,
/// e in ogni caso l'app funziona uguale. Senza un "sì" esplicito non parte nessun dato.
/// </summary>
public partial class UsageConsentWindow : Window
{
    private readonly MainViewModel _vm;

    public UsageConsentWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => Answer(true);
    private void No_Click(object sender, RoutedEventArgs e) => Answer(false);

    private void Answer(bool yes)
    {
        _vm.Settings.UsageStatsOptIn = yes;
        _vm.Settings.UsageStatsAsked = true;
        _vm.SaveSettings();
        _vm.StatusText = yes
            ? "Grazie: i passaggi della serata aiuteranno a migliorare i suggerimenti (Impostazioni → Generale per spegnerlo)"
            : "Statistiche d'uso disattivate";
        DialogResult = yes;
        Close();
    }
}
