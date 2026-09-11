using System.Windows;
using KaraokeDJ.Services;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

public partial class SupportWindow : Window
{
    private readonly MainViewModel _vm;

    public SupportWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        MachineBox.Text = LicenseService.MachineId;
        CodeBox.Text = vm.Settings.LicenseCode ?? "";
        DonateInfo.Text = string.IsNullOrEmpty(MainViewModel.DonationUrl) ? "Il link per la donazione arriverà con la prossima versione." : MainViewModel.DonationUrl;
        DonateBtn.IsEnabled = !string.IsNullOrEmpty(MainViewModel.DonationUrl);
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var lic = _vm.License;
        StatusLabel.Text = lic != null ? $"✓ Grazie {lic.Name}! Licenza attiva dal {lic.Issued:dd/MM/yyyy} su questa macchina." : "Versione gratuita (completa). Nessuna chiave attiva.";
        StatusLabel.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[lic != null ? "AccentABrush" : "MutedBrush"];
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LicenseService.MachineId); StatusLabel.Text = "ID copiato negli appunti"; } catch { }
    }

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MainViewModel.DonationUrl) { UseShellExecute = true }); } catch { }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActivateLicense(CodeBox.Text)) RefreshStatus();
        else { StatusLabel.Text = "Chiave non valida per questa macchina."; StatusLabel.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DangerBrush"]; }
    }
}
