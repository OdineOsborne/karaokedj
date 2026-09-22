using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

public partial class DeckControl : UserControl
{
    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(DeckControl), new PropertyMetadata(Brushes.Gray));

    public DeckControl() => InitializeComponent();

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    // ------------------------------------------------------------ trascina un brano dalla libreria sul deck

    private DeckViewModel? Deck => DataContext as DeckViewModel;

    /// <summary>Brano dalla libreria o file audio/video trascinato da Esplora risorse.</summary>
    private static bool CanAccept(DragEventArgs e) =>
        e.Data.GetDataPresent(typeof(Models.Track)) || e.Data.GetDataPresent(DataFormats.FileDrop);

    private void Deck_DragOver(object sender, DragEventArgs e)
    {
        bool ok = CanAccept(e);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (Deck != null) Deck.IsDropTarget = ok;
        e.Handled = true;
    }

    private void Deck_DragLeave(object sender, DragEventArgs e)
    {
        if (Deck != null) Deck.IsDropTarget = false;
    }

    private void Deck_Drop(object sender, DragEventArgs e)
    {
        if (Deck is not { } deck) return;
        deck.IsDropTarget = false;
        e.Handled = true;
        var vm = App.Vm;
        if (vm == null) return;
        if (e.Data.GetData(typeof(Models.Track)) is Models.Track t) { vm.LoadToDeck(deck, t); return; }
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) vm.LoadFileToDeck(deck, files[0]);
    }

    private void SeekBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Seek(e.GetPosition(SeekBar).X);
        SeekBar.CaptureMouse();
    }

    private void SeekBar_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && SeekBar.IsMouseCaptured)
            Seek(e.GetPosition(SeekBar).X);
        else if (SeekBar.IsMouseCaptured)
            SeekBar.ReleaseMouseCapture();
    }

    // ---- vinile: jog e pulsanti "tieni premuto"
    private void Jog_Started() { if (DataContext is DeckViewModel vm) vm.JogStart(); }
    private void Jog_Rate(double rate) { if (DataContext is DeckViewModel vm) vm.JogRate(rate); }
    private void Jog_Ended() { if (DataContext is DeckViewModel vm) vm.JogEnd(); }
    private void Jog_Nudged(int dir) { if (DataContext is DeckViewModel vm) vm.Nudge(dir); }

    private bool _holding;
    private void Hold_Down(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button b || DataContext is not DeckViewModel vm) return;
        _holding = true;
        switch (b.Tag as string)
        {
            case "backward": vm.BackwardHoldCommand.Execute(null); break;
            case "forward": vm.ForwardHoldCommand.Execute(null); break;
            case "reverse": vm.ReverseHoldCommand.Execute(null); break;
            case "slow": vm.SlowHoldCommand.Execute(null); break;
        }
    }
    private void Hold_Up(object sender, MouseButtonEventArgs e) { if (_holding && DataContext is DeckViewModel vm) { _holding = false; vm.HoldReleaseCommand.Execute(null); } }
    private void Hold_Leave(object sender, MouseEventArgs e) { if (_holding && e.LeftButton == MouseButtonState.Pressed && DataContext is DeckViewModel vm) { _holding = false; vm.HoldReleaseCommand.Execute(null); } }

    // ---- tag genere sotto il titolo
    private void RemoveTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string g && DataContext is DeckViewModel vm) vm.RemoveGenreTag(g);
    }

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || DataContext is not DeckViewModel vm || vm.Track == null) return;
        if (vm.Track.Genres.Count() >= DeckViewModel.MaxGenreTags)
        {
            MessageBox.Show($"Massimo {DeckViewModel.MaxGenreTags} generi per brano: togline uno cliccandolo.", "Generi", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = b, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MaxHeight = 520 };
        var known = App.Vm?.GenreOptions.Select(o => o.Name) ?? Services.GenreClassifier.Genres.AsEnumerable();
        foreach (var g in known)
        {
            if (vm.Track.HasGenre(g)) continue;
            var item = new MenuItem { Header = g };
            item.Click += (_, _) => vm.AddGenreTag(g);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Nuovo genere…" };
        custom.Click += (_, _) =>
        {
            var s = InputDialog.Show("Nuovo genere", "Nome del genere (o più, separati da ;):", "");
            if (s == null) return;
            foreach (var g in Models.Track.SplitGenres(s)) if (!vm.AddGenreTag(g)) break;
        };
        menu.Items.Add(custom);
        menu.IsOpen = true;
    }

    private void Cue_Right(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DeckViewModel vm) vm.ClearCue();
    }

    private void Eq_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider s) s.Value = 0;
    }

    private void TempoReset_Click(object sender, MouseButtonEventArgs e) { if (DataContext is DeckViewModel vm) vm.TempoPercent = 0; }

    private void Pan_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DeckViewModel vm) vm.Pan = 0;
    }

    private void Gain_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DeckViewModel vm) vm.GainDb = 0;
    }

    private void HotCue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int i && DataContext is DeckViewModel vm) vm.HotCueCommand.Execute(i.ToString());
    }

    private void HotCue_Right(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button b && b.Tag is int i && DataContext is DeckViewModel vm) { vm.HotCueClearCommand.Execute(i.ToString()); e.Handled = true; }
    }

    private void Loop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string beats && DataContext is DeckViewModel vm) vm.LoopBeatsCommand.Execute(beats);
    }

    private void Loop_RightDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button b && b.Tag is string s && DataContext is DeckViewModel vm
            && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var beats))
        {
            vm.LoopRollStart(beats);
            b.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Loop_RightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button b && DataContext is DeckViewModel vm)
        {
            b.ReleaseMouseCapture();
            vm.LoopRollEnd();
            e.Handled = true;
        }
    }

    private double _rightClickFraction = -1;

    private void SeekBar_RightDown(object sender, MouseButtonEventArgs e)
    {
        _rightClickFraction = SeekBar.ActualWidth > 0 ? Math.Clamp(e.GetPosition(SeekBar).X / SeekBar.ActualWidth, 0, 1) : -1;
    }

    private DeckViewModel? Vm => DataContext as DeckViewModel;
    private void IntroHere_Click(object sender, RoutedEventArgs e) => Vm?.SetIntroAt(_rightClickFraction >= 0 ? _rightClickFraction : null);
    private void OutroHere_Click(object sender, RoutedEventArgs e) => Vm?.SetOutroAt(_rightClickFraction >= 0 ? _rightClickFraction : null);
    private void BeatHere_Click(object sender, RoutedEventArgs e) => Vm?.BeatHere(_rightClickFraction >= 0 ? _rightClickFraction : null);
    private void BeatNow_Click(object sender, RoutedEventArgs e) => Vm?.BeatHere(null);
    private void CueHere_Click(object sender, RoutedEventArgs e) => Vm?.SetCueAt(_rightClickFraction >= 0 ? _rightClickFraction : null);
    private void IntroNow_Click(object sender, RoutedEventArgs e) => Vm?.SetIntroAt(null);
    private void OutroNow_Click(object sender, RoutedEventArgs e) => Vm?.SetOutroAt(null);
    private void ClearCues_Click(object sender, RoutedEventArgs e) => Vm?.ClearCues();

    private void Seek(double x)
    {
        if (DataContext is DeckViewModel vm && SeekBar.ActualWidth > 0)
            vm.SeekFraction(x / SeekBar.ActualWidth);
    }
}
