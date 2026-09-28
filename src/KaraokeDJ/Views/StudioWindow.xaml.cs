using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KaraokeDJ.Models;
using KaraokeDJ.ViewModels;
using Microsoft.Win32;

namespace KaraokeDJ.Views;

public partial class StudioWindow : Window
{
    private readonly StudioViewModel _vm;
    private readonly DispatcherTimer _timer;

    public StudioViewModel ViewModel => _vm;
    /// <summary>Per le foto automatiche: zoom su tutto il mix.</summary>
    public void FitForShot() => Timeline.ZoomToFit();

    public StudioWindow(MainViewModel main)
    {
        InitializeComponent();
        _vm = new StudioViewModel(main);
        DataContext = _vm;
        Timeline.Vm = _vm;
        NameBox.Text = _vm.Project.Name;
        _vm.PropertyChanged += Vm_PropertyChanged;
        _vm.Changed += () => { if (!NameBox.IsKeyboardFocused) NameBox.Text = _vm.Project.Name; };
        // posizione e testina: 30 volte al secondo, senza ridisegnare la timeline
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => TickUi();
        _timer.Start();
        Loaded += (_, _) => Timeline.ZoomToFit();
        PreviewKeyDown += OnKey;
        Closing += (_, _) => { _vm.Stop(); _timer.Stop(); };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(StudioViewModel.IsPlaying):
                PlayBtn.Content = _vm.IsPlaying ? "■" : "▶";
                break;
            case nameof(StudioViewModel.TransitionClip) when _vm.TransitionClip != null && _vm.SelectedClip == null:
                Tabs.SelectedIndex = 2;
                break;
            // clic su una clip della timeline: a destra le sue proprietà (non quando la si aggiunge dalla lista brani)
            case nameof(StudioViewModel.SelectedClip) when _vm.SelectedClip != null && Timeline.IsKeyboardFocusWithin:
                Tabs.SelectedIndex = 1;
                break;
        }
    }

    private void TickUi()
    {
        _vm.Tick();
        if (_vm.IsPlaying) Timeline.EnsureVisible(_vm.PositionSec);
        double x = Timeline.PlayheadX;
        Playhead.Visibility = x >= StudioTimeline.HeaderW && x <= Timeline.ActualWidth ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(Playhead, x - 1);
        var ts = TimeSpan.FromSeconds(_vm.PositionSec);
        PosText.Text = $"{(int)ts.TotalMinutes}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Space: _vm.TogglePlay(); break;
            case Key.Home: _vm.Seek(0); break;
            case Key.Delete: _vm.DeleteClipCommand.Execute(null); break;
            case Key.S when !ctrl: _vm.SplitClipCommand.Execute(null); break;
            case Key.D when ctrl: _vm.DuplicateClipCommand.Execute(null); break;
            case Key.Z when ctrl: _vm.UndoCommand.Execute(null); break;
            case Key.Y when ctrl: _vm.RedoCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Home_Click(object sender, RoutedEventArgs e) => _vm.Seek(0);
    private void Fit_Click(object sender, RoutedEventArgs e) => Timeline.ZoomToFit();

    private void Snap_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SnapBox.SelectedItem is ComboBoxItem it && Timeline != null) Timeline.Snap = (string)it.Tag;
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => _vm.Rename(NameBox.Text);
    private void NameBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { _vm.Rename(NameBox.Text); Timeline.Focus(); } }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(StudioViewModel.Dir);
        var dlg = new OpenFileDialog { Filter = "Mix dello Studio (*.mixstudio)|*.mixstudio", InitialDirectory = StudioViewModel.Dir };
        if (dlg.ShowDialog(this) == true) { _vm.OpenFile(dlg.FileName); NameBox.Text = _vm.Project.Name; Timeline.ZoomToFit(); }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Filter = "Mix dello Studio (*.mixstudio)|*.mixstudio", FileName = _vm.Project.Name + ".mixstudio", InitialDirectory = StudioViewModel.Dir };
        if (dlg.ShowDialog(this) != true) return;
        try { _vm.Project.Save(dlg.FileName); _vm.ProjectPath = dlg.FileName; _vm.Status = "Salvato: " + dlg.FileName; }
        catch (Exception ex) { _vm.Status = "Salvataggio non riuscito: " + ex.Message; }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Project.Clips.Count == 0) { _vm.Status = "Il mix è vuoto"; return; }
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Mixfonia Studio");
        Directory.CreateDirectory(dir);
        var dlg = new SaveFileDialog { Filter = "MP3 (e WAV accanto)|*.mp3|Solo WAV|*.wav", FileName = _vm.Project.Name, InitialDirectory = dir };
        if (dlg.ShowDialog(this) != true) return;
        bool mp3 = dlg.FilterIndex == 1;
        await _vm.ExportAsync(Path.ChangeExtension(dlg.FileName, ".wav"), mp3);
    }

    // ------------------------------------------------------------ libreria: doppio clic, trascinamento sulla timeline

    private Point _dragStart;
    private void Results_MouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    private void Results_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) return;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not Track t) return;
        if (e.OriginalSource is DependencyObject o && FindParent<Button>(o) != null) return;
        DragDrop.DoDragDrop(ResultsBox, new DataObject(StudioTimeline.TrackFormat, t), DragDropEffects.Copy);
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is Track t) _vm.AddToEnd(t);
    }

    private static T? FindParent<T>(DependencyObject o) where T : DependencyObject
    {
        while (o != null && o is not T) o = System.Windows.Media.VisualTreeHelper.GetParent(o);
        return o as T;
    }
}
