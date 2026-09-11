using System.Windows;

namespace KaraokeDJ;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack deve girare per primo: gestisce install / update / uninstall e poi esce se necessario
        Velopack.VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
