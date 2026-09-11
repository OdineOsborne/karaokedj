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
    private void IntroNow_Click(object sender, RoutedEventArgs e) => Vm?.SetIntroAt(null);
    private void OutroNow_Click(object sender, RoutedEventArgs e) => Vm?.SetOutroAt(null);
    private void ClearCues_Click(object sender, RoutedEventArgs e) => Vm?.ClearCues();

    private void Seek(double x)
    {
        if (DataContext is DeckViewModel vm && SeekBar.ActualWidth > 0)
            vm.SeekFraction(x / SeekBar.ActualWidth);
    }
}
