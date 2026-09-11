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
        DonateInfo.Text = "Si apre la pagina PayPal di VOXA con il tuo ID già inserito: dopo il pagamento la chiave arriva qui da sola.";
        Closed += (_, _) => _waitCts?.Cancel();
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

    private CancellationTokenSource? _waitCts;

    private async void Donate_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(MainViewModel.DonationUrl) { UseShellExecute = true }); } catch { }
        if (_vm.IsLicensed) return;
        _waitCts?.Cancel();
        _waitCts = new CancellationTokenSource();
        StatusLabel.Text = "In attesa della donazione… completa il pagamento nel browser: la chiave si attiva da sola.";
        StatusLabel.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["AccentBrush"];
        DonateBtn.IsEnabled = false;
        bool ok = await _vm.WaitForCloudLicenseAsync(_waitCts.Token);
        DonateBtn.IsEnabled = true;
        if (ok) { CodeBox.Text = _vm.Settings.LicenseCode ?? ""; RefreshStatus(); }
        else if (!_waitCts.IsCancellationRequested) RefreshStatus();
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActivateLicense(CodeBox.Text)) RefreshStatus();
        else { StatusLabel.Text = "Chiave non valida per questa macchina."; StatusLabel.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DangerBrush"]; }
    }
}
