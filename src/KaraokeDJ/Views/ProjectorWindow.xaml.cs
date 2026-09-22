using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KaraokeDJ.ViewModels;
using LibVLCSharp.Shared;

namespace KaraokeDJ.Views;

public partial class ProjectorWindow : Window
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mp;
    private string? _currentVideoPath;
    private DeckViewModel? _boundDeck;
    private readonly DispatcherTimer _sync = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private DateTime _lastSeekCorrection = DateTime.MinValue;

    public ProjectorWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += Vm_PropertyChanged;
                BindDeck(vm.ActiveKaraokeDeck);
            }
        };
        _sync.Tick += (_, _) => SyncVideo();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayers();
        // LibVLC impiega qualche secondo a caricare i plugin: lo facciamo fuori dal thread UI
        try
        {
            var (vlc, mp) = await Task.Run(() =>
            {
                var v = new LibVLC("--no-video-title-show", "--quiet", "--no-audio");
                var m = new MediaPlayer(v) { Mute = true, Volume = 0, EnableHardwareDecoding = true };
                return (v, m);
            });
            if (!IsLoaded) { mp.Dispose(); vlc.Dispose(); return; }
            _libVlc = vlc;
            _mp = mp;
            VideoView.MediaPlayer = _mp;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Impossibile avviare il player video: " + ex.Message, "Mixfonia", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _sync.Start();
        UpdateLayers();
        SyncVideo(force: true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _sync.Stop();
        if (Vm != null) Vm.PropertyChanged -= Vm_PropertyChanged;
        BindDeck(null);
        try
        {
            VideoView.MediaPlayer = null;
            _mp?.Stop();
            _mp?.Dispose();
            _libVlc?.Dispose();
        }
        catch { }
        _mp = null;
        _libVlc = null;
    }

    /// <summary>true: finestra "monitor" sullo schermo del DJ (piccola, sempre in primo piano) che mostra ciò che vede il pubblico.</summary>
    public bool IsMonitor { get; private set; }

    /// <summary>Apre come monitor di regia: stessa scena del proiettore, in una finestrella ridimensionabile e sempre in primo piano.</summary>
    public void ShowAsMonitor()
    {
        IsMonitor = true;
        Title = "Mixfonia – Monitor proiettore";
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        Cursor = Cursors.Arrow;
        Topmost = true;
        Width = 480; Height = 300;
        var main = Application.Current.MainWindow;
        if (main != null) { Left = main.Left + main.ActualWidth - Width - 24; Top = main.Top + 60; }
        Show();
    }

    /// <summary>Mostra la finestra a schermo intero sul monitor indicato (o in finestra se c'è un solo monitor).</summary>
    public void ShowOnScreen(int screenIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var primary = System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
        System.Windows.Forms.Screen target;
        bool fullscreen;
        if (screens.Length <= 1)
        {
            target = primary;
            fullscreen = false;
        }
        else
        {
            int idx = screenIndex < 0
                ? Array.FindIndex(screens, s => !s.Primary)
                : Math.Clamp(screenIndex, 0, screens.Length - 1);
            if (idx < 0) idx = 0;
            target = screens[idx];
            fullscreen = true;
        }

        // Conversione pixel → unità WPF (l'app è System-DPI aware: un solo fattore di scala)
        double scale = 1.0;
        var src = Application.Current.MainWindow != null ? PresentationSource.FromVisual(Application.Current.MainWindow) : null;
        if (src?.CompositionTarget != null) scale = src.CompositionTarget.TransformFromDevice.M11;

        var b = target.Bounds;
        WindowState = WindowState.Normal;
        if (fullscreen)
        {
            Left = b.Left * scale;
            Top = b.Top * scale;
            Width = b.Width * scale;
            Height = b.Height * scale;
            Topmost = true;
        }
        else
        {
            Width = 960; Height = 540;
            Left = (b.Left + 60) * scale;
            Top = (b.Top + 60) * scale;
            Topmost = false;
            Cursor = Cursors.Arrow;
        }
        Show();
        if (fullscreen) WindowState = WindowState.Maximized;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm != null) { if (IsMonitor) Vm.IsMonitorOpen = false; else Vm.IsProjectorOpen = false; }
    }

    // ------------------------------------------------------------ stato

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveKaraokeDeck))
        {
            BindDeck(Vm?.ActiveKaraokeDeck);
        }
        else if (e.PropertyName is nameof(MainViewModel.DedicationText) or nameof(MainViewModel.DedicationTitle))
        {
            UpdateLayers();
        }
    }

    private void BindDeck(DeckViewModel? deck)
    {
        if (_boundDeck == deck) { UpdateLayers(); return; }
        if (_boundDeck != null)
        {
            _boundDeck.PropertyChanged -= Deck_PropertyChanged;
            _boundDeck.Seeked -= Deck_Seeked;
        }
        _boundDeck = deck;
        if (_boundDeck != null)
        {
            _boundDeck.PropertyChanged += Deck_PropertyChanged;
            _boundDeck.Seeked += Deck_Seeked;
        }
        CdgImage.Source = deck?.CdgBitmap;
        UpdateLayers();
        SyncVideo(force: true);
    }

    private void Deck_Seeked(DeckViewModel d) => SyncVideo(force: true);

    private void Deck_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeckViewModel.IsCdg) or nameof(DeckViewModel.IsVideo) or nameof(DeckViewModel.VideoPath) or nameof(DeckViewModel.HasTrack) or nameof(DeckViewModel.IsMidiLyrics))
        {
            UpdateLayers();
            SyncVideo(force: true);
        }
    }

    private void UpdateLayers()
    {
        var d = _boundDeck;
        bool cdg = d?.IsCdg == true;
        bool video = d?.IsVideo == true && _mp != null;
        bool lyrics = d?.IsMidiLyrics == true && !cdg && !video;
        bool dedication = !cdg && !video && !lyrics && !string.IsNullOrEmpty(Vm?.DedicationText);
        CdgLayer.Visibility = cdg ? Visibility.Visible : Visibility.Collapsed;
        VideoView.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        LyricsLayer.Visibility = lyrics ? Visibility.Visible : Visibility.Collapsed;
        DedicationLayer.Visibility = dedication ? Visibility.Visible : Visibility.Collapsed;
        IdleLayer.Visibility = (cdg || video || lyrics || dedication) ? Visibility.Collapsed : Visibility.Visible;
        if (cdg) d!.ForceCdgRefresh();
    }

    // ------------------------------------------------------------ sincronizzazione video ↔ audio del deck

    private void SyncVideo(bool force = false)
    {
        if (_mp == null || _libVlc == null) return;
        var d = _boundDeck;
        var path = d?.IsVideo == true ? d.VideoPath : null;

        if (path != _currentVideoPath)
        {
            _currentVideoPath = path;
            try
            {
                if (path == null)
                {
                    _mp.Stop();
                }
                else
                {
                    using var media = new Media(_libVlc, path, FromType.FromPath);
                    _mp.Play(media);
                    _mp.SetRate((float)d!.Deck.Tempo);
                    if (!d.IsPlaying) _mp.SetPause(true);
                }
            }
            catch { }
            _lastSeekCorrection = DateTime.UtcNow;
            return;
        }
        if (path == null || d == null) return;

        try
        {
            bool deckPlaying = d.IsPlaying;
            if (deckPlaying && !_mp.IsPlaying) _mp.SetPause(false);
            else if (!deckPlaying && _mp.IsPlaying) _mp.SetPause(true);

            float rate = (float)d.Deck.Tempo;
            if (Math.Abs(_mp.Rate - rate) > 0.005f) _mp.SetRate(rate);

            long deckMs = (long)(d.Deck.PositionSec * 1000) + d.CdgOffsetMs;
            long drift = _mp.Time - deckMs;
            bool settle = (DateTime.UtcNow - _lastSeekCorrection).TotalMilliseconds > 700;
            if (force || (settle && Math.Abs(drift) > 180))
            {
                _mp.Time = Math.Max(0, deckMs);
                _lastSeekCorrection = DateTime.UtcNow;
            }
        }
        catch { }
    }
}
