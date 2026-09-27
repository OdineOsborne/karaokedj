using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Il cloud serve in serata: prenotazioni del pubblico col QR (/canta), scaletta dal telefono (/scaletta),
/// licenze. Il 27/9/2026 era giù senza che nessuno se ne accorgesse: il QR avrebbe mandato il pubblico su
/// una pagina inesistente. Qui lo si prova all'avvio e ogni 2 minuti, in sottofondo; il controllo
/// pre-serata legge l'ultimo esito e il QR non si mostra se il cloud non risponde.
/// </summary>
public partial class MainViewModel
{
    /// <summary>null = non ancora provato, true = risponde, false = non risponde.</summary>
    [ObservableProperty] private bool? _cloudOk;
    public string CloudDetail { get; private set; } = "";
    public DateTime? CloudCheckedAt { get; private set; }
    private DispatcherTimer? _cloudTimer;
    private bool _cloudChecking;

    private void StartCloudWatch()
    {
        _ = CheckCloudAsync();
        _cloudTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _cloudTimer.Tick += (_, _) => _ = CheckCloudAsync();
        _cloudTimer.Start();
    }

    public async Task CheckCloudAsync()
    {
        if (_cloudChecking) return;
        _cloudChecking = true;
        bool ok; string detail;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var r = await CloudHttp.GetAsync($"{CloudBaseUrl}/api/config", cts.Token);
            ok = r.IsSuccessStatusCode;
            detail = ok ? "raggiungibile" : $"il servizio risponde {(int)r.StatusCode}";
        }
        catch (Exception ex)
        {
            ok = false;
            detail = ex is TaskCanceledException or OperationCanceledException ? "non risponde (internet assente o lento?)" : "non raggiungibile (internet assente?)";
        }
        finally { _cloudChecking = false; }
        var was = CloudOk;
        CloudDetail = detail;
        CloudCheckedAt = DateTime.Now;
        CloudOk = ok;
        if (was == true && !ok) StatusText = "Cloud non raggiungibile: QR delle prenotazioni sospeso, si prenota a voce";
        else if (was == false && ok) StatusText = "Cloud di nuovo raggiungibile: prenotazioni col QR disponibili";
    }

    /// <summary>
    /// Il QR di una pagina del cloud si mostra solo se il cloud risponde: altrimenti il pubblico inquadra e
    /// trova una pagina d'errore. Ritorna true se si può mostrare.
    /// </summary>
    private bool CloudQrAllowed(string url)
    {
        if (!url.StartsWith(CloudBaseUrl, StringComparison.OrdinalIgnoreCase) || CloudOk != false) return true;
        StatusText = $"QR non mostrato: il cloud {CloudDetail}, la pagina sul telefono non si aprirebbe. Prenotazioni a voce.";
        _ = CheckCloudAsync();
        return false;
    }
}
