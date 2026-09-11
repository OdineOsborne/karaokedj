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

    private void Cue_Right(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DeckViewModel vm) vm.ClearCue();
    }

    private void Eq_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider s) s.Value = 0;
    }

    private void Gain_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DeckViewModel vm) vm.GainDb = 0;
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
