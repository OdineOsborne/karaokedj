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
                Width = vm.Settings.WindowWidth;
                Height = vm.Settings.WindowHeight;
            }
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        PreviewKeyUp += MainWindow_PreviewKeyUp;
        Closing += MainWindow_Closing;
        Loaded += async (_, _) =>
        {
            await Task.Delay(4000);
            if (IsLoaded && Vm.ShouldShowSupportReminder()) new SupportWindow(Vm) { Owner = this }.ShowDialog();
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (Vm.DeckA.IsPlaying || Vm.DeckB.IsPlaying)
        {
            if (MessageBox.Show("C'è musica in riproduzione. Chiudere comunque?", "VOXA", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        Vm.Settings.WindowWidth = Width;
        Vm.Settings.WindowHeight = Height;
        _projector?.Close();
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsProjectorOpen))
        {
            if (Vm.IsProjectorOpen) ShowProjector();
            else HideProjector();
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
        e.Effects = e.Data.GetDataPresent(typeof(QueueEntry)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(QueueEntry)) is not QueueEntry dragged) return;
        var target = (e.OriginalSource as DependencyObject) is { } d ? FindAncestor<ListBoxItem>(d)?.DataContext as QueueEntry : null;
        int from = Vm.Queue.IndexOf(dragged);
        int to = target == null ? Vm.Queue.Count - 1 : Vm.Queue.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to) Vm.Queue.Move(from, to);
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

    private void Remote_Click(object sender, RoutedEventArgs e) => new RemoteWindow(Vm) { Owner = this }.ShowDialog();

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
