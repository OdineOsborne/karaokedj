using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KaraokeDJ.Views;

/// <summary>
/// Manopola da mixer: corsa 270°, arco colorato dal minimo (o dal centro se bipolare), indice bianco.
/// Trascina in verticale per girarla (Shift = fine), rotella ±2 %, doppio click = valore di default,
/// tasto destro = <see cref="RightClick"/> (usato per il kill dell'EQ).
/// </summary>
public sealed class Knob : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Knob),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(Knob), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Knob), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(nameof(DefaultValue), typeof(double), typeof(Knob), new PropertyMetadata(0.0));
    public static readonly DependencyProperty BipolarProperty = DependencyProperty.Register(nameof(Bipolar), typeof(bool), typeof(Knob), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(Knob), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(Knob), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsKilledProperty = DependencyProperty.Register(nameof(IsKilled), typeof(bool), typeof(Knob), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DiameterProperty = DependencyProperty.Register(nameof(Diameter), typeof(double), typeof(Knob), new FrameworkPropertyMetadata(34.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }
    /// <summary>Arco dal centro (EQ, filtro) invece che dal minimo (volume).</summary>
    public bool Bipolar { get => (bool)GetValue(BipolarProperty); set => SetValue(BipolarProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    /// <summary>Kill attivo: manopola spenta con anello rosso.</summary>
    public bool IsKilled { get => (bool)GetValue(IsKilledProperty); set => SetValue(IsKilledProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

    public event Action? RightClick;
    /// <summary>true se la manopola ha una sua azione col tasto destro (kill EQ): ha la precedenza sul menù scorciatoie.</summary>
    public bool HasRightAction => RightClick != null;

    private static readonly Brush Body = new SolidColorBrush(Color.FromRgb(0x2A, 0x2B, 0x31));
    private static readonly Pen BodyPen = new(new SolidColorBrush(Color.FromRgb(0x44, 0x46, 0x4E)), 1);
    private static readonly Pen TrackPen = new(new SolidColorBrush(Color.FromRgb(0x33, 0x34, 0x3B)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    private static readonly Pen KillPen = new(new SolidColorBrush(Color.FromRgb(0xF2, 0x5F, 0x5F)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    private static readonly Pen Pointer = new(Brushes.White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x90, 0x97));
    private static readonly Typeface LabelFace = new("Segoe UI");
    static Knob() { Body.Freeze(); BodyPen.Freeze(); TrackPen.Freeze(); KillPen.Freeze(); Pointer.Freeze(); LabelBrush.Freeze(); }

    private const double StartDeg = 135, Sweep = 270; // da ore 7 a ore 5 passando in alto
    private Point _dragStart; private double _dragValue; private bool _dragging;

    public Knob() { Cursor = Cursors.Hand; Focusable = false; ToolTipService.SetInitialShowDelay(this, 300); }

    protected override Size MeasureOverride(Size availableSize)
    {
        double lw = Label.Length > 0 ? new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 9, LabelBrush, 1.0).Width + 4 : 0;
        return new Size(Math.Max(Diameter + 8, lw), Diameter + (Label.Length > 0 ? 14 : 2));
    }

    private double Norm => Maximum > Minimum ? Math.Clamp((Value - Minimum) / (Maximum - Minimum), 0, 1) : 0;

    private static Point Polar(Point c, double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
    }

    private static PathGeometry Arc(Point c, double r, double fromDeg, double toDeg)
    {
        var g = new PathGeometry();
        var f = new PathFigure { StartPoint = Polar(c, r, fromDeg) };
        f.Segments.Add(new ArcSegment(Polar(c, r, toDeg), new Size(r, r), 0, Math.Abs(toDeg - fromDeg) > 180, toDeg > fromDeg ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true));
        g.Figures.Add(f);
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double d = Diameter, r = d / 2;
        var c = new Point(ActualWidth / 2, r + 1);
        double a0 = -StartDeg, a1 = -StartDeg + Sweep, av = -StartDeg + Sweep * Norm;
        dc.DrawGeometry(null, IsKilled ? KillPen : TrackPen, Arc(c, r + 2.5, a0, a1));
        if (!IsKilled)
        {
            double from = Bipolar ? 0 : a0;
            if (Math.Abs(av - from) > 0.5) dc.DrawGeometry(null, new Pen(Accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(c, r + 2.5, from, av));
        }
        dc.DrawEllipse(Body, BodyPen, c, r - 2, r - 2);
        dc.DrawLine(IsKilled ? KillPen : Pointer, Polar(c, r * 0.35, av), Polar(c, r - 5, av));
        if (Label.Length > 0)
        {
            var ft = new FormattedText(Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 9, LabelBrush, 1.0);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, d + 2));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Value = DefaultValue; e.Handled = true; return; }
        _dragStart = e.GetPosition(this); _dragValue = Value; _dragging = true; CaptureMouse(); e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        double dy = _dragStart.Y - e.GetPosition(this).Y;
        double range = Maximum - Minimum;
        double step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 600 : 150; // pixel per corsa completa
        Value = Math.Clamp(_dragValue + dy / step * range, Minimum, Maximum);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { _dragging = false; ReleaseMouseCapture(); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value = Math.Clamp(Value + Math.Sign(e.Delta) * (Maximum - Minimum) / 50, Minimum, Maximum); e.Handled = true;
    }
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) { RightClick?.Invoke(); e.Handled = true; }
}
