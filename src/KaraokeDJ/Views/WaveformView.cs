using System.Windows;
using System.Windows.Media;

namespace KaraokeDJ.Views;

/// <summary>
/// Forma d'onda del brano (picco + RMS), con parte già suonata colorata, intro e uscita
/// evidenziate e cursore di posizione. Disegno diretto in OnRender: leggero anche a 25 fps.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(byte[]), typeof(WaveformView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IntroFractionProperty =
        DependencyProperty.Register(nameof(IntroFraction), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutroFractionProperty =
        DependencyProperty.Register(nameof(OutroFraction), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LoopStartProperty =
        DependencyProperty.Register(nameof(LoopStart), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LoopEndProperty =
        DependencyProperty.Register(nameof(LoopEnd), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BeatSecProperty =
        DependencyProperty.Register(nameof(BeatSec), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BpmProperty =
        DependencyProperty.Register(nameof(Bpm), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DurationSecProperty =
        DependencyProperty.Register(nameof(DurationSec), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CueFractionProperty =
        DependencyProperty.Register(nameof(CueFraction), typeof(double), typeof(WaveformView), new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentProperty =
        DependencyProperty.Register(nameof(Accent), typeof(Brush), typeof(WaveformView), new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush BgBrush = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x12));
    private static readonly Brush UnplayedPeak = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x3C));
    private static readonly Brush UnplayedRms = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x70));
    private static readonly Brush IntroBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0xE5, 0xFF));
    private static readonly Brush OutroBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0x2D, 0x95));
    private static readonly Pen CursorPen = new(Brushes.White, 2);
    private static readonly Pen BarPen = new(new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Pen PhrasePen = new(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly Pen CuePen = new(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00)), 2);
    private static readonly Pen MarkerPen = new(new SolidColorBrush(Color.FromArgb(0xC0, 0xFA, 0xCC, 0x15)), 1) { DashStyle = DashStyles.Dash };

    static WaveformView()
    {
        BgBrush.Freeze(); UnplayedPeak.Freeze(); UnplayedRms.Freeze(); IntroBrush.Freeze(); OutroBrush.Freeze();
        CursorPen.Freeze(); MarkerPen.Freeze(); BarPen.Freeze(); PhrasePen.Freeze(); CuePen.Freeze();
    }

    public byte[]? Data { get => (byte[]?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double IntroFraction { get => (double)GetValue(IntroFractionProperty); set => SetValue(IntroFractionProperty, value); }
    public double OutroFraction { get => (double)GetValue(OutroFractionProperty); set => SetValue(OutroFractionProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    /// <summary>Griglia dei battiti: secondi del primo "1" (-1 = nessuna), BPM e durata del brano.</summary>
    public double BeatSec { get => (double)GetValue(BeatSecProperty); set => SetValue(BeatSecProperty, value); }
    public double Bpm { get => (double)GetValue(BpmProperty); set => SetValue(BpmProperty, value); }
    public double DurationSec { get => (double)GetValue(DurationSecProperty); set => SetValue(DurationSecProperty, value); }
    /// <summary>Punto cue in frazione (-1 = nessuno).</summary>
    public double CueFraction { get => (double)GetValue(CueFractionProperty); set => SetValue(CueFractionProperty, value); }
    public double LoopStart { get => (double)GetValue(LoopStartProperty); set => SetValue(LoopStartProperty, value); }
    public double LoopEnd { get => (double)GetValue(LoopEndProperty); set => SetValue(LoopEndProperty, value); }
    private static readonly Brush LoopBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xD6, 0x0A));
    private static readonly Pen LoopPen = new(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD6, 0x0A)), 1.5);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRoundedRectangle(BgBrush, null, new Rect(0, 0, w, h), 4, 4);

        var data = Data;
        double prog = Math.Clamp(Progress, 0, 1);
        double intro = Math.Clamp(IntroFraction, 0, 1);
        double outro = Math.Clamp(OutroFraction, 0, 1);

        if (intro > 0) dc.DrawRectangle(IntroBrush, null, new Rect(0, 0, w * intro, h));
        if (outro < 1) dc.DrawRectangle(OutroBrush, null, new Rect(w * outro, 0, w * (1 - outro), h));

        if (data == null || data.Length < 4)
        {
            // nessuna analisi: barra semplice
            dc.DrawRectangle(Accent, null, new Rect(0, h * 0.4, w * prog, h * 0.2));
        }
        else
        {
            int cols = data.Length / 2;
            double mid = h / 2;
            double colW = w / cols;
            // raggruppa colonne se il controllo è più stretto dei dati
            int step = Math.Max(1, (int)Math.Ceiling(cols / w));
            var accentRms = Accent;
            var accentPeak = Accent.Clone(); accentPeak.Opacity = 0.45; accentPeak.Freeze();

            for (int c = 0; c < cols; c += step)
            {
                int peak = 0, rms = 0;
                for (int k = c; k < Math.Min(cols, c + step); k++)
                {
                    peak = Math.Max(peak, data[k * 2]);
                    rms = Math.Max(rms, data[k * 2 + 1]);
                }
                double x = c * colW;
                double bw = Math.Max(1, colW * step - 0.5);
                double ph = Math.Max(1, peak / 255.0 * (h - 4));
                double rh = Math.Max(1, rms / 255.0 * (h - 4));
                bool played = (x + bw / 2) / w <= prog;
                dc.DrawRectangle(played ? accentPeak : UnplayedPeak, null, new Rect(x, mid - ph / 2, bw, ph));
                dc.DrawRectangle(played ? accentRms : UnplayedRms, null, new Rect(x, mid - rh / 2, bw, rh));
            }
        }

        if (LoopEnd > LoopStart)
        {
            double x0 = w * Math.Clamp(LoopStart, 0, 1), x1 = w * Math.Clamp(LoopEnd, 0, 1);
            dc.DrawRectangle(LoopBrush, null, new Rect(x0, 0, Math.Max(2, x1 - x0), h));
            dc.DrawLine(LoopPen, new Point(x0, 0), new Point(x0, h));
            dc.DrawLine(LoopPen, new Point(x1, 0), new Point(x1, h));
        }
        // griglia: una tacca ogni battuta (4 battiti), più marcata ogni frase (16 battiti)
        if (Bpm > 0 && BeatSec >= 0 && DurationSec > 0)
        {
            double bar = 4 * 60.0 / Bpm;
            double pxPerBar = w * bar / DurationSec;
            int every = pxPerBar >= 3 ? 1 : pxPerBar >= 0.75 ? 4 : 16;   // se le battute sono fitte, mostra solo frasi
            double first = BeatSec - Math.Floor(BeatSec / bar) * bar;
            int idx = (int)Math.Round((first - BeatSec) / bar);
            for (double t = first; t < DurationSec; t += bar, idx++)
            {
                bool phrase = ((idx % 4) + 4) % 4 == 0;
                if (every == 16 && !phrase) continue;
                if (!phrase && every > 1 && ((idx % every) + every) % every != 0) continue;
                double x = w * t / DurationSec;
                dc.DrawLine(phrase ? PhrasePen : BarPen, new Point(x, phrase ? 0 : h * 0.25), new Point(x, phrase ? h : h * 0.75));
            }
        }
        if (CueFraction >= 0) dc.DrawLine(CuePen, new Point(w * CueFraction, 0), new Point(w * CueFraction, h));
        if (intro > 0) dc.DrawLine(MarkerPen, new Point(w * intro, 0), new Point(w * intro, h));
        if (outro < 1) dc.DrawLine(MarkerPen, new Point(w * outro, 0), new Point(w * outro, h));
        dc.DrawLine(CursorPen, new Point(w * prog, 0), new Point(w * prog, h));
    }
}
