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
                Width = vm.Settings.WindowWidth;
                Height = vm.Settings.WindowHeight;
            }
        };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += MainWindow_Closing;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (Vm.DeckA.IsPlaying || Vm.DeckB.IsPlaying)
        {
            if (MessageBox.Show("C'è musica in riproduzione. Chiudere comunque?", "KaraokeDJ", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
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

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F1..F12 → pad
        if (e.Key >= Key.F1 && e.Key <= Key.F12)
        {
            Vm.TriggerPadByIndex(e.Key - Key.F1);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.D1: Vm.DeckA.TogglePlay(); e.Handled = true; break;
                case Key.D2: Vm.DeckB.TogglePlay(); e.Handled = true; break;
                case Key.P: Vm.IsProjectorOpen = !Vm.IsProjectorOpen; e.Handled = true; break;
                case Key.N: Vm.PlayNextCommand.Execute(null); e.Handled = true; break;
                case Key.F: SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; break;
                case Key.Enter: Vm.AddToQueueCommand.Execute(null); e.Handled = true; break;
                case Key.Space: Vm.StopAllPadsCommand.Execute(null); e.Handled = true; break;
            }
        }
        else if (e.Key == Key.Escape && Vm.IsProjectorOpen && _projector != null && _projector.IsActive)
        {
            Vm.IsProjectorOpen = false;
        }
    }

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
