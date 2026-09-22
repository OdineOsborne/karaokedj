using System.Windows;
using System.Windows.Media;
using KaraokeDJ.Services;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

public partial class SupportWindow : Window
{
    private readonly MainViewModel _vm;
    private CancellationTokenSource? _waitCts;

    public SupportWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        MachineBox.Text = LicenseService.MachineId;
        CodeBox.Text = "";
        Closed += (_, _) => _waitCts?.Cancel();
        RefreshStatus();
    }

    private Brush Res(string key) => (Brush)Application.Current.Resources[key];

    private void RefreshStatus()
    {
        var lic = _vm.License;
        if (lic != null)
        {
            Headline.Text = " attiva";
            var until = _vm.UpdatesUntil;
            StatusLabel.Text = $"✓ Licenza di {lic.Name} su questo PC (dal {lic.Issued:dd/MM/yyyy}).";
            StatusLabel.Foreground = Res("AccentABrush");
            StatusDetail.Text = (lic.IsLegacy ? "Chiave della fase donationware, grazie! " : $"Account {lic.Account}. ")
                + (_vm.UpdatesActive ? $"Aggiornamenti inclusi fino al {until:dd/MM/yyyy}." : $"Aggiornamenti scaduti il {until:dd/MM/yyyy}: la versione installata resta tua, le nuove release richiedono il rinnovo.");
            BuyBtn.Visibility = Visibility.Collapsed;
            RenewBtn.Visibility = _vm.UpdatesActive && (until - DateTime.UtcNow)?.TotalDays > 45 ? Visibility.Collapsed : Visibility.Visible;
            ManageBtn.Visibility = Visibility.Visible;
        }
        else if (_vm.IsTrial)
        {
            Headline.Text = " in prova";
            StatusLabel.Text = $"Prova gratuita: {_vm.TrialDaysLeft} giorni rimasti, tutte le funzioni.";
            StatusLabel.Foreground = Res("AccentBrush");
            StatusDetail.Text = "Alla fine della prova Mixfonia continua a suonare, ma il proiettore mostra una scritta e scaletta remota e funzioni AI si fermano.";
            BuyBtn.Visibility = Visibility.Visible; RenewBtn.Visibility = Visibility.Collapsed; ManageBtn.Visibility = Visibility.Visible;
        }
        else
        {
            Headline.Text = " dimostrativa";
            StatusLabel.Text = "Prova finita: versione dimostrativa.";
            StatusLabel.Foreground = Res("DangerBrush");
            StatusDetail.Text = "Il proiettore mostra una scritta; scaletta remota e funzioni AI sono ferme. La licenza costa 20 € una volta sola.";
            BuyBtn.Visibility = Visibility.Visible; RenewBtn.Visibility = Visibility.Collapsed; ManageBtn.Visibility = Visibility.Visible;
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(LicenseService.MachineId); WaitLabel.Text = "ID copiato negli appunti"; } catch { }
    }

    private async void Buy_Click(object sender, RoutedEventArgs e)
    {
        var url = ReferenceEquals(sender, BuyBtn) ? MainViewModel.PurchaseUrl : _vm.RenewUrl;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        await WaitCloudAsync("Completa il pagamento nel browser: chiave e rinnovo arrivano qui da soli.");
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshBtn.IsEnabled = false;
        WaitLabel.Text = "Chiedo al cloud…";
        var c = await _vm.FetchLicenseFromCloudAsync();
        if (c != null)
        {
            bool changed;
            if (c.Revoked) WaitLabel.Text = "La licenza di questo PC è stata trasferita su un altro PC.";
            else
            {
                var before = (_vm.Settings.LicenseCode, _vm.Settings.UpdatesToken);
                _vm.ApplyCloudLicense(c);
                changed = (_vm.Settings.LicenseCode, _vm.Settings.UpdatesToken) != before;
                WaitLabel.Text = changed ? "Aggiornato dal cloud." : "Nessuna novità per questo PC.";
            }
        }
        else WaitLabel.Text = "Nessuna licenza registrata per questo PC (o cloud non raggiungibile).";
        RefreshBtn.IsEnabled = true;
        RefreshStatus();
    }

    private async Task WaitCloudAsync(string msg)
    {
        _waitCts?.Cancel();
        _waitCts = new CancellationTokenSource();
        WaitLabel.Text = msg;
        BuyBtn.IsEnabled = RenewBtn.IsEnabled = false;
        bool ok = await _vm.WaitForCloudLicenseAsync(_waitCts.Token);
        BuyBtn.IsEnabled = RenewBtn.IsEnabled = true;
        if (ok) WaitLabel.Text = "✓ Ricevuto dal cloud.";
        else if (!_waitCts.IsCancellationRequested) WaitLabel.Text = "";
        RefreshStatus();
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.ActivateLicense(CodeBox.Text)) { ActivateLabel.Text = "✓ Attivato."; ActivateLabel.Foreground = Res("AccentABrush"); CodeBox.Text = ""; RefreshStatus(); }
        else { ActivateLabel.Text = LicenseService.VerifyToken(CodeBox.Text) != null ? "Il token è di un altro account (o manca la licenza)." : "Chiave non valida per questo PC."; ActivateLabel.Foreground = Res("DangerBrush"); }
    }
}
