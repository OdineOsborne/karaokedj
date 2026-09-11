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
        bool selfTest = e.Args.Contains("--selftest");
        DispatcherUnhandledException += (_, args) =>
        {
            if (selfTest)
            {
                Console.Error.WriteLine("SELFTEST FAIL: " + args.Exception);
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "voxa-selftest.log"), args.Exception.ToString()); } catch { }
                Environment.Exit(2);
            }
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

        if (selfTest) RunSelfTest(win);
    }

    /// <summary>--selftest: apre tutte le finestre (errori XAML/risorse vengono fuori qui), poi esce con codice 0.</summary>
    private async void RunSelfTest(MainWindow win)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(1500);
            var wins = new Window[]
            {
                new Views.RhythmWindow(Vm!.Rhythm) { Owner = win },
                new Views.DuplicatesWindow(Vm) { Owner = win },
                new Views.SettingsWindow(Vm) { Owner = win },
                new Views.AnimationWindow(Vm) { Owner = win },
                new Views.SupportWindow(Vm) { Owner = win },
                new Views.BorderoWindow(Vm) { Owner = win },
            };
            foreach (var w in wins) { w.Show(); await System.Threading.Tasks.Task.Delay(300); }
            Vm.IsProjectorOpen = true;
            await System.Threading.Tasks.Task.Delay(800);
            Vm.DeckA.FxVisible = true;
            await System.Threading.Tasks.Task.Delay(500);
            Console.Error.WriteLine("SELFTEST OK");
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "voxa-selftest.log"), "OK"); } catch { }
            Shutdown(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SELFTEST FAIL: " + ex);
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "voxa-selftest.log"), ex.ToString()); } catch { }
            Environment.Exit(2);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Vm?.Shutdown();
        base.OnExit(e);
    }
}
