using System.Windows;
using KaraokeDJ.Services;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

/// <summary>Finestra del controllo pre-serata: elenco verde/giallo/rosso, con il consiglio accanto a ogni riga.</summary>
public partial class PreflightWindow : Window
{
    private readonly MainViewModel _vm;

    public PreflightWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Refresh();
    }

    private void Refresh()
    {
        var checks = Preflight.Run(_vm);
        List.ItemsSource = checks;
        SummaryText.Text = Preflight.Summary(checks);
    }

    private void Recheck_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(Preflight.AsText(Preflight.Run(_vm))); _vm.StatusText = "Controllo pre-serata copiato negli appunti"; }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
