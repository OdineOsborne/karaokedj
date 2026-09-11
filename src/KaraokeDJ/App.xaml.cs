using System.Windows;
using KaraokeDJ.ViewModels;
using KaraokeDJ.Views;

namespace KaraokeDJ;

public partial class App : Application
{
    public static MainViewModel? Vm { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "Errore imprevisto", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try { LibVLCSharp.Shared.Core.Initialize(); }
        catch (Exception ex)
        {
            MessageBox.Show("LibVLC non inizializzato (i video karaoke non funzioneranno):\n" + ex.Message, "Avviso", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Vm = new MainViewModel();
        var win = new MainWindow { DataContext = Vm };
        MainWindow = win;
        win.Show();
        Vm.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Vm?.Shutdown();
        base.OnExit(e);
    }
}
