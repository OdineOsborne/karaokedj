using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KaraokeDJ.ViewModels;
using QRCoder;

namespace KaraokeDJ.Views;

/// <summary>QR + PIN per controllare la scaletta dal telefono.</summary>
public partial class RemoteWindow : Window
{
    private readonly MainViewModel _vm;

    public RemoteWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        if (!vm.Remote.IsRunning) vm.Remote.Start();
        vm.Remote.StatusChanged += OnStatus;
        Closed += (_, _) => vm.Remote.StatusChanged -= OnStatus;
        Refresh();
    }

    public static BitmapImage MakeQr(string text, int pixelsPerModule = 10)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);
        var img = new BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit(); img.CacheOption = BitmapCacheOption.OnLoad; img.StreamSource = ms; img.EndInit(); img.Freeze();
        return img;
    }

    private void Refresh()
    {
        Qr.Source = MakeQr(_vm.Remote.PageUrl);
        PinText.Text = _vm.Remote.Pin;
        UrlText.Text = _vm.Remote.PageUrl;
        OnStatus();
    }

    private void OnStatus() => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = _vm.Remote.IsRunning ? _vm.Remote.Status : "Disattivata";
        StopBtn.Content = _vm.Remote.IsRunning ? "⏹ Disattiva" : "▶ Attiva";
    });

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Remote.IsRunning) _vm.Remote.Stop(); else _vm.Remote.Start();
        OnStatus();
    }

    private void NewPin_Click(object sender, RoutedEventArgs e) { _vm.Remote.NewPin(); Refresh(); }

    private void Url_Click(object sender, MouseButtonEventArgs e)
    {
        try { Clipboard.SetText(_vm.Remote.PageUrl); StatusText.Text = "Link copiato"; } catch { }
    }

    private void Projector_Click(object sender, RoutedEventArgs e) => _vm.ShowQrOnProjector(_vm.Remote.PageUrl, $"Scaletta remota · PIN {_vm.Remote.Pin}", 20);
}
