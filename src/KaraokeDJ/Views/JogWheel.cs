using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KaraokeDJ.Views;

/// <summary>
/// Piatto jog: trascinando col mouse si fa scratch (la velocità di lettura segue la rotazione, anche all'indietro);
/// rotella = pitch bend (nudge). Emette gli eventi JogStart / JogRate / JogEnd al deck.
/// </summary>
public sealed class JogWheel : FrameworkElement
{
    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(JogWheel), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AngleProperty =
        DependencyProperty.Register(nameof(Angle), typeof(double), typeof(JogWheel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsSpinningProperty =
        DependencyProperty.Register(nameof(IsSpinning), typeof(bool), typeof(JogWheel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    /// <summary>Angolo del marker (gradi), aggiornato dal deck in base alla posizione.</summary>
    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
    public bool IsSpinning { get => (bool)GetValue(IsSpinningProperty); set => SetValue(IsSpinningProperty, value); }

    /// <summary>Gradi al secondo a velocità normale (33⅓ giri/min).</summary>
    public const double DegPerSecAtNormal = 200.0;

    public event Action? JogStarted;
    public event Action<double>? JogRateChanged;   // velocità relativa (1 = normale, negativa = indietro)
    public event Action? JogEnded;
    public event Action<int>? Nudged;               // +1 / -1 (rotella)

    private bool _dragging;
    private double _lastAngle;
    private DateTime _lastTime;
    private double _rate;
    private System.Windows.Threading.DispatcherTimer? _decay;

    private static readonly Brush Plate = new RadialGradientBrush(Color.FromRgb(38, 38, 48), Color.FromRgb(14, 14, 20));
    private static readonly Brush Rim = new SolidColorBrush(Color.FromRgb(60, 60, 76));
    private static readonly Pen RimPen = new(Rim, 3);
    private static readonly Pen Groove = new(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1);

    static JogWheel() { Plate.Freeze(); Rim.Freeze(); RimPen.Freeze(); Groove.Freeze(); }

    public JogWheel()
    {
        Cursor = Cursors.Hand;
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) => { if (_dragging) End(); };
        MouseWheel += (_, e) => { Nudged?.Invoke(e.Delta > 0 ? 1 : -1); e.Handled = true; };
    }

    private double AngleOf(Point p)
    {
        double cx = ActualWidth / 2, cy = ActualHeight / 2;
        return Math.Atan2(p.Y - cy, p.X - cx) * 180 / Math.PI;
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _lastAngle = AngleOf(e.GetPosition(this));
        _lastTime = DateTime.UtcNow;
        _rate = 0;
        CaptureMouse();
        JogStarted?.Invoke();
        JogRateChanged?.Invoke(0); // il disco è "tenuto": fermo
        // se il mouse si ferma sul piatto, la velocità decade a 0 (come tenere fermo il vinile)
        _decay ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _decay.Tick -= DecayTick; _decay.Tick += DecayTick;
        _decay.Start();
        e.Handled = true;
    }

    private void DecayTick(object? s, EventArgs e)
    {
        if (!_dragging) { _decay?.Stop(); return; }
        if ((DateTime.UtcNow - _lastTime).TotalMilliseconds > 60 && Math.Abs(_rate) > 0.001)
        {
            _rate = 0;
            JogRateChanged?.Invoke(0);
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var now = DateTime.UtcNow;
        double a = AngleOf(e.GetPosition(this));
        double d = a - _lastAngle;
        if (d > 180) d -= 360; else if (d < -180) d += 360;
        double dt = Math.Max(0.004, (now - _lastTime).TotalSeconds);
        double rate = d / dt / DegPerSecAtNormal;              // gradi/s → velocità relativa
        _rate = _rate * 0.5 + Math.Clamp(rate, -8, 8) * 0.5;   // un po' di smorzamento
        _lastAngle = a; _lastTime = now;
        Angle += d;
        JogRateChanged?.Invoke(_rate);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        ReleaseMouseCapture();
        End();
        e.Handled = true;
    }

    private void End()
    {
        _dragging = false;
        _decay?.Stop();
        JogEnded?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Min(w, h) / 2 - 2;
        var c = new Point(w / 2, h / 2);
        dc.DrawEllipse(Plate, RimPen, c, r, r);
        for (int i = 1; i <= 3; i++) dc.DrawEllipse(null, Groove, c, r * (0.55 + i * 0.12), r * (0.55 + i * 0.12));
        // etichetta centrale
        var label = Accent.Clone(); label.Opacity = IsSpinning ? 0.9 : 0.45; label.Freeze();
        dc.DrawEllipse(label, null, c, r * 0.42, r * 0.42);
        // marker che ruota con la posizione
        double a = (Angle - 90) * Math.PI / 180;
        var m1 = new Point(c.X + Math.Cos(a) * r * 0.45, c.Y + Math.Sin(a) * r * 0.45);
        var m2 = new Point(c.X + Math.Cos(a) * r * 0.95, c.Y + Math.Sin(a) * r * 0.95);
        dc.DrawLine(new Pen(Brushes.White, 2.5), m1, m2);
    }
}
