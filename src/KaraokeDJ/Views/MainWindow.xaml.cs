using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KaraokeDJ.Models;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

public partial class MainWindow : Window
{
    private ProjectorWindow? _projector;
    private ProjectorWindow? _monitor;
    private Point _dragStart;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += Vm_PropertyChanged;
                vm.SearchFocusRequested += FocusSearch;
                // non aprire più grande dello schermo (portatili piccoli, schermo cambiato dall'ultima volta)
                var wa = SystemParameters.WorkArea;
                Width = Math.Min(vm.Settings.WindowWidth, wa.Width);
                Height = Math.Min(vm.Settings.WindowHeight, wa.Height);
                ApplyUiScale();
            }
        };
        SizeChanged += (_, _) => ApplyUiScale();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += (_, _) => { if (Vm.Settings.GlassEffect) WindowBackdrop.Apply(this); };
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        Closing += MainWindow_Closing;
        Loaded += async (_, _) =>
        {
            await Task.Delay(4000);
            if (IsLoaded && Vm.ShouldShowSupportReminder()) new SupportWindow(Vm) { Owner = this }.ShowDialog();
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    // ---------------------------------------------------------------- dimensione dell'interfaccia
    // Formato di riferimento del layout: sotto questa misura l'interfaccia viene rimpicciolita invece di essere tagliata.
    private const double DesignWidth = 1180, DesignHeight = 944;
    // altezze misurate del layout: fisse (barre, deck, mixer, stato) + libreria minima + i due pannelli facoltativi
    private const double MixViewHeight = 78, StripHeight = 133, LibraryMin = 170, NeedFull = 944;
    /// <summary>Sotto questa scala si preferisce chiudere un pannello invece di continuare a rimpicciolire i testi.</summary>
    private const double ComfortScale = 0.8;
    /// <summary>Sotto questo fattore i testi diventano illeggibili: meglio fermarsi e lasciare che l'utente chiuda qualche pannello.</summary>
    private const double MinScale = 0.6;

    /// <summary>
    /// Adatta l'interfaccia alla finestra: automatica (rimpicciolisce quanto basta perché non venga tagliato niente)
    /// oppure fissa se l'utente ha scelto una percentuale (Impostazioni → Aspetto, o Ctrl + / Ctrl − / Ctrl 0).
    /// </summary>
    public double CurrentUiScale => UiScale?.ScaleX ?? 1;
    /// <summary>Griglia principale (per il test di layout).</summary>
    public System.Windows.Controls.Grid RootGrid => Root;

    public void ApplyUiScale()
    {
        if (DataContext is not MainViewModel vm || UiScale == null) return;
        double s;
        if (vm.Settings.UiScale > 0.05) s = Math.Clamp(vm.Settings.UiScale, MinScale, 2);
        else
        {
            double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
            double Fit(double need) => Math.Min(1, Math.Min((w - 4) / DesignWidth, (h - 4) / need));
            // Prima si rimpicciolisce un po' (fino all'80 %); se non basta si chiudono i pannelli meno importanti
            // — prima la striscia jingle/importazione, poi la vista mix — così la libreria resta sempre utilizzabile.
            s = Fit(NeedFull);
            bool hideStrip = false, hideMix = false;
            if (s < ComfortScale) { hideStrip = true; s = Fit(NeedFull - StripHeight); }
            if (s < ComfortScale && hideStrip) { hideMix = true; s = Fit(NeedFull - StripHeight - MixViewHeight); }
            s = Math.Clamp(s, MinScale, 1);
            AutoHide(vm, hideStrip, hideMix);
        }
        if (Math.Abs(UiScale.ScaleX - s) < 0.005) return;
        UiScale.ScaleX = UiScale.ScaleY = s;
        vm.UiScaleLabel = vm.Settings.UiScale > 0.05 ? $"Interfaccia {s * 100:0} %" : $"Interfaccia {s * 100:0} % (automatica)";
    }

    private bool _autoHidStrip, _autoHidMix, _stripByUser, _mixByUser, _applyingAutoHide;

    /// <summary>Chiude (o riapre) i pannelli facoltativi per far stare la libreria; se li tocca l'utente non ci mettiamo più mano.</summary>
    private void AutoHide(MainViewModel vm, bool hideStrip, bool hideMix)
    {
        _applyingAutoHide = true;
        try { AutoHideCore(vm, hideStrip, hideMix); } finally { _applyingAutoHide = false; }
    }

    private void AutoHideCore(MainViewModel vm, bool hideStrip, bool hideMix)
    {
        if (!_stripByUser)
        {
            if (hideStrip && vm.BottomStripVisible) { vm.BottomStripVisible = false; _autoHidStrip = true; vm.StatusText = "Schermo piccolo: nascosta la striscia jingle/importazione (tasto 🎛 per riaprirla)"; }
            else if (!hideStrip && _autoHidStrip) { vm.BottomStripVisible = true; _autoHidStrip = false; }
        }
        if (!_mixByUser)
        {
            if (hideMix && vm.MixViewVisible) { vm.MixViewVisible = false; _autoHidMix = true; vm.StatusText = "Schermo piccolo: nascosta la vista mix (tasto 〰 per riaprirla)"; }
            else if (!hideMix && _autoHidMix) { vm.MixViewVisible = true; _autoHidMix = false; }
        }
    }

    /// <summary>L'utente ha riaperto a mano un pannello: da qui in poi comanda lui.</summary>
    public void PanelToggledByUser(string which)
    {
        if (which == "strip") { _stripByUser = true; _autoHidStrip = false; }
        else { _mixByUser = true; _autoHidMix = false; }
    }

    /// <summary>Ctrl + / Ctrl − cambiano la dimensione a mano, Ctrl 0 torna automatica.</summary>
    private void ChangeUiScale(int dir)
    {
        var vm = Vm;
        double cur = vm.Settings.UiScale > 0.05 ? vm.Settings.UiScale : UiScale.ScaleX;
        vm.Settings.UiScale = dir == 0 ? 0 : Math.Clamp(Math.Round((cur + dir * 0.05) * 100) / 100, MinScale, 1.5);
        ApplyUiScale();
        vm.StatusText = vm.Settings.UiScale > 0.05 ? $"Interfaccia al {UiScale.ScaleX * 100:0} % (Ctrl+0 = automatica)" : "Interfaccia: dimensione automatica";
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (Vm.DeckA.IsPlaying || Vm.DeckB.IsPlaying)
        {
            if (MessageBox.Show("C'è musica in riproduzione. Chiudere comunque?", "Mixfonia", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        Vm.Settings.WindowWidth = Width;
        Vm.Settings.WindowHeight = Height;
        _projector?.Close();
        _monitor?.Close();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // se i pannelli li apre/chiude l'utente, l'adattamento automatico non ci mette più mano
        if (!_applyingAutoHide && e.PropertyName == nameof(MainViewModel.BottomStripVisible)) PanelToggledByUser("strip");
        if (!_applyingAutoHide && e.PropertyName == nameof(MainViewModel.MixViewVisible)) PanelToggledByUser("mix");
        if (e.PropertyName == nameof(MainViewModel.IsProjectorOpen))
        {
            if (Vm.IsProjectorOpen) ShowProjector();
            else HideProjector();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsMonitorOpen))
        {
            if (Vm.IsMonitorOpen)
            {
                if (_monitor == null)
                {
                    _monitor = new ProjectorWindow { DataContext = Vm, Owner = this };
                    _monitor.Closed += (_, _) => { _monitor = null; Vm.IsMonitorOpen = false; };
                }
                _monitor.ShowAsMonitor();
            }
            else { _monitor?.Close(); _monitor = null; }
        }
    }

    private void ShowProjector()
    {
        if (_projector == null)
        {
            _projector = new ProjectorWindow { DataContext = Vm, Owner = null };
            _projector.Closed += (_, _) => { _projector = null; Vm.IsProjectorOpen = false; };
        }
        _projector.ShowOnScreen(Vm.Settings.ProjectorScreenIndex);
        Activate();
    }

    private void HideProjector()
    {
        _projector?.Close();
        _projector = null;
    }

    // ------------------------------------------------------------ tastiera

    // Scorciatoie configurabili (tasto destro su un comando → assegna). Nelle caselle di testo i tasti senza modificatori restano per scrivere.
    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm.IsProjectorOpen && _projector != null && _projector.IsActive) { Vm.IsProjectorOpen = false; return; }
        // dimensione dell'interfaccia: Ctrl + / Ctrl − / Ctrl 0 (come nei browser)
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key is Key.OemPlus or Key.Add) { ChangeUiScale(+1); e.Handled = true; return; }
            if (e.Key is Key.OemMinus or Key.Subtract) { ChangeUiScale(-1); e.Handled = true; return; }
            if (e.Key is Key.D0 or Key.NumPad0) { ChangeUiScale(0); e.Handled = true; return; }
        }
        if (e.IsRepeat) { if (IsMappedNow(e)) e.Handled = true; return; }
        var g = Services.KeyboardService.GestureText(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        if (g == null) return;
        if (TypingInTextBox() && !g.Contains("Ctrl") && !g.Contains("Alt") && !g.StartsWith("F")) return;
        if (Vm.HandleKey(g, pressed: true)) e.Handled = true;
    }

    private void MainWindow_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var g = Services.KeyboardService.GestureText(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        if (g == null) return;
        if (Vm.HandleKey(g, pressed: false)) e.Handled = true;
    }

    private bool IsMappedNow(KeyEventArgs e)
    {
        var g = Services.KeyboardService.GestureText(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        return g != null && Vm.Keys.ActionFor(g) != null && !(TypingInTextBox() && !g.Contains("Ctrl") && !g.Contains("Alt"));
    }

    private static bool TypingInTextBox() => Keyboard.FocusedElement is TextBox or System.Windows.Controls.Primitives.TextBoxBase or PasswordBox;

    private void FocusSearch() { SearchBox.Focus(); SearchBox.SelectAll(); }

    /// <summary>Tasto destro sul nome cantante: menù con i nomi già visti (stasera e nelle serate passate).</summary>
    private void SingerBox_Right(object sender, MouseButtonEventArgs e)
    {
        if (Vm.KnownSingers.Count == 0) { Vm.StatusText = "Nessun cantante ancora memorizzato"; return; }
        var menu = new ContextMenu();
        foreach (var name in Vm.KnownSingers)
        {
            var n = name;
            var item = new MenuItem { Header = n + (Vm.SingerCountTonight(n) > 0 ? $"   ({Vm.SingerCountTonight(n)} stasera)" : "") };
            item.Click += (_, _) => { Vm.SingerName = n; SingerBox.CaretIndex = n.Length; SingerBox.Focus(); };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = SingerBox; menu.IsOpen = true;
        e.Handled = true;
    }

    // kill EQ dal tasto destro sulle manopole del mixer
    private void KillHighA() => Vm.DeckA.EqHighKill = !Vm.DeckA.EqHighKill;
    private void KillMidA() => Vm.DeckA.EqMidKill = !Vm.DeckA.EqMidKill;
    private void KillLowA() => Vm.DeckA.EqLowKill = !Vm.DeckA.EqLowKill;
    private void KillHighB() => Vm.DeckB.EqHighKill = !Vm.DeckB.EqHighKill;
    private void KillMidB() => Vm.DeckB.EqMidKill = !Vm.DeckB.EqMidKill;
    private void KillLowB() => Vm.DeckB.EqLowKill = !Vm.DeckB.EqLowKill;

    private void Talk_Down(object sender, MouseButtonEventArgs e) { Vm.TalkOver = true; e.Handled = true; }
    private void Talk_Up(object sender, MouseButtonEventArgs e) { Vm.TalkOver = false; e.Handled = true; }
    private void MicGain_Reset(object sender, MouseButtonEventArgs e) => Vm.MicGainDb = 0;

    private void SingerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm.AddToQueueCommand.Execute(null); e.Handled = true; }
    }

    private void DownloadBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm.DownloadCommand.Execute(null); e.Handled = true; }
    }

    private void AllTracks_Click(object sender, RoutedEventArgs e) => Vm.SelectedPlaylist = null;

    private void QueueKeyDown_Click(object sender, RoutedEventArgs e) => Vm.QueueKeyShift = Math.Max(-12, Vm.QueueKeyShift - 1);
    private void QueueKeyUp_Click(object sender, RoutedEventArgs e) => Vm.QueueKeyShift = Math.Min(12, Vm.QueueKeyShift + 1);

    // ------------------------------------------------------------ libreria / coda

    private void LibraryGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedTrack != null) Vm.AddToQueueCommand.Execute(null);
    }

    private void QueueList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm.SelectedQueueEntry is { } entry)
        {
            var deck = !Vm.DeckA.IsPlaying ? Vm.DeckA : Vm.DeckB;
            if (deck == Vm.DeckA) Vm.QueueEntryToACommand.Execute(entry); else Vm.QueueEntryToBCommand.Execute(entry);
        }
    }

    // ------------------------------------------------------------ trascina dalla libreria ai deck / alla coda

    private Point _libDragStart;

    private void LibraryGrid_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _libDragStart = e.GetPosition(null);

    private void LibraryGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _libDragStart.X) < 8 && Math.Abs(pos.Y - _libDragStart.Y) < 8) return;
        if (e.OriginalSource is DependencyObject src && (FindAncestor<Button>(src) != null || FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(src) != null)) return;
        // il brano sotto il puntatore (non solo quello selezionato: così si trascina anche senza selezionare prima)
        var row = e.OriginalSource is DependencyObject d ? FindAncestor<DataGridRow>(d) : null;
        if ((row?.DataContext ?? Vm.SelectedTrack) is not Track t) return;
        DragDrop.DoDragDrop(LibraryGrid, new DataObject(typeof(Track), t), DragDropEffects.Copy);
    }

    /// <summary>File o cartelle trascinati da Esplora risorse dentro la libreria: vengono aggiunti.</summary>
    private void LibraryGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void LibraryGrid_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        e.Handled = true;
        Vm.AddPathsToLibrary(paths);
    }

    // riordino coda con drag & drop
    private void QueueList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _dragStart = e.GetPosition(null); return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < 8 && Math.Abs(pos.Y - _dragStart.Y) < 8) return;
        if (e.OriginalSource is DependencyObject src && FindAncestor<Button>(src) != null) return;
        if (QueueList.SelectedItem is QueueEntry entry)
        {
            DragDrop.DoDragDrop(QueueList, entry, DragDropEffects.Move);
        }
    }

    private void QueueList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(QueueEntry)) ? DragDropEffects.Move
                  : e.Data.GetDataPresent(typeof(Track)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        // brano trascinato dalla libreria: va in coda nel punto dove lo lasci
        if (e.Data.GetData(typeof(Track)) is Track track)
        {
            var before = (e.OriginalSource as DependencyObject) is { } od ? FindAncestor<ListBoxItem>(od)?.DataContext as QueueEntry : null;
            Vm.AddTrackToQueue(track, before);
            return;
        }
        if (e.Data.GetData(typeof(QueueEntry)) is not QueueEntry dragged) return;
        var target = (e.OriginalSource as DependencyObject) is { } d ? FindAncestor<ListBoxItem>(d)?.DataContext as QueueEntry : null;
        int from = Vm.Queue.IndexOf(dragged);
        int to = target == null ? Vm.Queue.Count - 1 : Vm.Queue.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) Vm.Queue.Move(from, to);
    }

    // ------------------------------------------------------------ tag del brano selezionato (senza caricarlo su un deck)

    private void SelectedAddTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || Vm.SelectedTrack is not { } t) return;
        if (t.Genres.Count() >= DeckViewModel.MaxGenreTags)
        {
            MessageBox.Show($"Massimo {DeckViewModel.MaxGenreTags} generi per brano: togline uno cliccandolo.", "Generi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Top, MaxHeight = 520 };
        foreach (var g in Vm.GenreOptions.Where(o => !o.IsChecked).Select(o => o.Name))
        {
            var item = new MenuItem { Header = g };
            item.Click += (_, _) => Vm.ToggleGenreCommand.Execute(g);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Nuovo genere…" };
        custom.Click += (_, _) =>
        {
            var s = InputDialog.Show("Nuovo genere", "Nome del genere:", "");
            if (!string.IsNullOrWhiteSpace(s)) Vm.ToggleGenreCommand.Execute(s.Trim());
        };
        menu.Items.Add(custom);
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------ pad

    private void Pad_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Pad_Drop(object sender, DragEventArgs e)
    {
        if (sender is Button { DataContext: PadItem pad } && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            Vm.SetPadFile(pad, files[0]);
    }

    // ------------------------------------------------------------ impostazioni

    private void Duplicates_Click(object sender, RoutedEventArgs e) => new DuplicatesWindow(Vm) { Owner = this }.ShowDialog();

    private void Remote_Click(object sender, RoutedEventArgs e) { if (Vm.RequireLicense("Scaletta remota")) new RemoteWindow(Vm) { Owner = this }.ShowDialog(); }

    private void Renew_Click(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Vm.RenewUrl) { UseShellExecute = true }); } catch { }
        new SupportWindow(Vm) { Owner = this }.ShowDialog();
    }

    private void Bordero_Click(object sender, RoutedEventArgs e) => new BorderoWindow(Vm) { Owner = this }.ShowDialog();

    private void Support_Click(object sender, RoutedEventArgs e) => new SupportWindow(Vm) { Owner = this }.ShowDialog();

    private RhythmWindow? _rhythm;

    private void Rhythm_Click(object sender, RoutedEventArgs e)
    {
        if (_rhythm == null || !_rhythm.IsLoaded)
        {
            _rhythm = new RhythmWindow(Vm.Rhythm) { Owner = this };
            _rhythm.Closed += (_, _) => _rhythm = null;
            _rhythm.Show();
        }
        else _rhythm.Activate();
    }

    private AnimationWindow? _animation;

    private void Animation_Click(object sender, RoutedEventArgs e)
    {
        if (_animation == null || !_animation.IsLoaded)
        {
            _animation = new AnimationWindow(Vm) { Owner = this };
            _animation.Closed += (_, _) => _animation = null;
            _animation.Show();
        }
        else _animation.Activate();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(Vm) { Owner = this };
        dlg.ShowDialog();
        if (Vm.IsProjectorOpen) ShowProjector();
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
