using System.Windows;
using System.Windows.Media;

namespace KaraokeDJ.Views;

/// <summary>VU meter stereo (picco): verde → giallo → rosso, orizzontale o verticale.</summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty LeftProperty = DependencyProperty.Register(nameof(Left), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RightProperty = DependencyProperty.Register(nameof(Right), typeof(double), typeof(LevelMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty VerticalProperty = DependencyProperty.Register(nameof(Vertical), typeof(bool), typeof(LevelMeter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Left { get => (double)GetValue(LeftProperty); set => SetValue(LeftProperty, value); }
    public double Right { get => (double)GetValue(RightProperty); set => SetValue(RightProperty, value); }
    public bool Vertical { get => (bool)GetValue(VerticalProperty); set => SetValue(VerticalProperty, value); }

    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x12));
    private static readonly Brush Off = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x2C));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x6D));
    static LevelMeter() { Bg.Freeze(); Off.Freeze(); Green.Freeze(); Yellow.Freeze(); Red.Freeze(); }

    /// <summary>Converte un picco lineare (0..1+) in posizione 0..1 su scala dB (-60..0).</summary>
    public static double ToScale(double peak)
    {
        if (peak <= 0) return 0;
        double db = 20 * Math.Log10(peak);
        return Math.Clamp((db + 60) / 60, 0, 1);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(Bg, null, new Rect(0, 0, w, h), 3, 3);
        const int segs = 24;
        double gap = 1.5;
        for (int ch = 0; ch < 2; ch++)
        {
            double v = ch == 0 ? Left : Right;
            int lit = (int)Math.Round(Math.Clamp(v, 0, 1) * segs);
            for (int i = 0; i < segs; i++)
            {
                double frac = (double)i / segs;
                Brush b = i < lit ? (frac < 0.7 ? Green : frac < 0.9 ? Yellow : Red) : Off;
                Rect r;
                if (Vertical)
                {
                    double segH = (h - gap * (segs - 1)) / segs, segW = (w - 3) / 2;
                    r = new Rect(ch * (segW + 3), h - (i + 1) * segH - i * gap, segW, segH);
                }
                else
                {
                    double segW = (w - gap * (segs - 1)) / segs, segH = (h - 3) / 2;
                    r = new Rect(i * (segW + gap), ch * (segH + 3), segW, segH);
                }
                dc.DrawRectangle(b, null, r);
            }
        }
    }
}
