using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KaraokeDJ.Audio;

namespace KaraokeDJ.Views;

/// <summary>
/// Vista di mixaggio: le forme d'onda "fine" dei due deck scorrono sovrapposte (A sopra, B specchiato sotto)
/// centrate sulla posizione attuale, con la griglia dei battiti di ciascun deck. Serve ad allineare i beat a occhio.
/// </summary>
public sealed class MixWaveView : FrameworkElement
{
    public static readonly DependencyProperty DataAProperty = DependencyProperty.Register(nameof(DataA), typeof(byte[]), typeof(MixWaveView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty DataBProperty = DependencyProperty.Register(nameof(DataB), typeof(byte[]), typeof(MixWaveView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PosAProperty = DependencyProperty.Register(nameof(PosA), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PosBProperty = DependencyProperty.Register(nameof(PosB), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BpmAProperty = DependencyProperty.Register(nameof(BpmA), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BpmBProperty = DependencyProperty.Register(nameof(BpmB), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TempoAProperty = DependencyProperty.Register(nameof(TempoA), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TempoBProperty = DependencyProperty.Register(nameof(TempoB), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AnchorAProperty = DependencyProperty.Register(nameof(AnchorA), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AnchorBProperty = DependencyProperty.Register(nameof(AnchorB), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentAProperty = DependencyProperty.Register(nameof(AccentA), typeof(Brush), typeof(MixWaveView), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBProperty = DependencyProperty.Register(nameof(AccentB), typeof(Brush), typeof(MixWaveView), new FrameworkPropertyMetadata(Brushes.DeepPink, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WindowSecProperty = DependencyProperty.Register(nameof(WindowSec), typeof(double), typeof(MixWaveView), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public byte[]? DataA { get => (byte[]?)GetValue(DataAProperty); set => SetValue(DataAProperty, value); }
    public byte[]? DataB { get => (byte[]?)GetValue(DataBProperty); set => SetValue(DataBProperty, value); }
    /// <summary>Posizione del deck (s, nel tempo del file).</summary>
    public double PosA { get => (double)GetValue(PosAProperty); set => SetValue(PosAProperty, value); }
    public double PosB { get => (double)GetValue(PosBProperty); set => SetValue(PosBProperty, value); }
    /// <summary>BPM naturali del brano (0 = ignoti: niente griglia).</summary>
    public double BpmA { get => (double)GetValue(BpmAProperty); set => SetValue(BpmAProperty, value); }
    public double BpmB { get => (double)GetValue(BpmBProperty); set => SetValue(BpmBProperty, value); }
    /// <summary>Fattore tempo del deck (1 = normale): la forma d'onda scorre più veloce o più lenta.</summary>
    public double TempoA { get => (double)GetValue(TempoAProperty); set => SetValue(TempoAProperty, value); }
    public double TempoB { get => (double)GetValue(TempoBProperty); set => SetValue(TempoBProperty, value); }
    /// <summary>Ancora della griglia (s nel file): il punto cue se impostato su un battere, altrimenti 0.</summary>
    public double AnchorA { get => (double)GetValue(AnchorAProperty); set => SetValue(AnchorAProperty, value); }
    public double AnchorB { get => (double)GetValue(AnchorBProperty); set => SetValue(AnchorBProperty, value); }
    public Brush AccentA { get => (Brush)GetValue(AccentAProperty); set => SetValue(AccentAProperty, value); }
    public Brush AccentB { get => (Brush)GetValue(AccentBProperty); set => SetValue(AccentBProperty, value); }
    /// <summary>Secondi visibili (in tempo reale, cioè d'uscita) sull'intera larghezza.</summary>
    public double WindowSec { get => (double)GetValue(WindowSecProperty); set => SetValue(WindowSecProperty, value); }

    private static readonly Brush Bg = new SolidColorBrush(Color.FromArgb(255, 10, 10, 15));
    private static readonly Pen Center = new(new SolidColorBrush(Color.FromArgb(230, 244, 244, 248)), 1.5);
    private static readonly Pen Mid = new(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1);
    private static readonly Pen BeatA = new(new SolidColorBrush(Color.FromArgb(70, 200, 180, 255)), 1);
    private static readonly Pen BarA = new(new SolidColorBrush(Color.FromArgb(150, 220, 200, 255)), 1);
    private static readonly Pen BeatB = new(new SolidColorBrush(Color.FromArgb(70, 255, 160, 200)), 1);
    private static readonly Pen BarB = new(new SolidColorBrush(Color.FromArgb(150, 255, 190, 220)), 1);

    static MixWaveView()
    {
        Bg.Freeze(); Center.Freeze(); Mid.Freeze(); BeatA.Freeze(); BarA.Freeze(); BeatB.Freeze(); BarB.Freeze();
    }

    public MixWaveView()
    {
        ClipToBounds = true;
        MouseWheel += (_, e) => { WindowSec = Math.Clamp(WindowSec * (e.Delta > 0 ? 0.8 : 1.25), 3, 60); e.Handled = true; };
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        double mid = h / 2;
        dc.DrawLine(Mid, new Point(0, mid), new Point(w, mid));

        DrawDeck(dc, DataA, PosA, BpmA, TempoA, AnchorA, AccentA, BeatA, BarA, w, 0, mid, up: true);
        DrawDeck(dc, DataB, PosB, BpmB, TempoB, AnchorB, AccentB, BeatB, BarB, w, mid, h, up: false);

        dc.DrawLine(Center, new Point(w / 2, 0), new Point(w / 2, h));
        var ft = new FormattedText($"{WindowSec:0} s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.0);
        dc.DrawText(ft, new Point(w - ft.Width - 6, 3));
    }

    /// <summary>Disegna metà vista: onda del deck attorno alla posizione, con griglia battiti/battute.</summary>
    private void DrawDeck(DrawingContext dc, byte[]? data, double pos, double bpm, double tempo, double anchor, Brush accent,
                          Pen beatPen, Pen barPen, double w, double top, double bottom, bool up)
    {
        double hh = bottom - top;
        double baseY = up ? bottom : top;             // linea di base: al centro
        double dir = up ? -1 : 1;
        if (tempo <= 0) tempo = 1;
        // finestra in secondi del file: ai lati della posizione, scalata per il tempo del deck
        double halfFile = WindowSec / 2 * tempo;
        double pxPerFileSec = w / (2 * halfFile);

        if (data != null && data.Length >= 2)
        {
            int cols = data.Length / 2;
            double colSec = 1.0 / FineWaveform.ColumnsPerSec;
            double x0 = pos - halfFile;
            int c0 = Math.Max(0, (int)(x0 / colSec)), c1 = Math.Min(cols - 1, (int)((pos + halfFile) / colSec) + 1);
            double colW = colSec * pxPerFileSec;
            var peakBrush = accent.Clone(); peakBrush.Opacity = 0.55; peakBrush.Freeze();
            var bassBrush = accent.Clone(); bassBrush.Opacity = 1.0; bassBrush.Freeze();
            // raggruppa colonne se più fitte di un pixel
            int group = Math.Max(1, (int)Math.Ceiling(1.0 / colW));
            for (int c = c0; c <= c1; c += group)
            {
                int pk = 0, bs = 0;
                for (int k = c; k < c + group && k < cols; k++) { pk = Math.Max(pk, data[k * 2]); bs = Math.Max(bs, data[k * 2 + 1]); }
                double x = (c * colSec - x0) * pxPerFileSec;
                double ww = Math.Max(1, colW * group - 0.5);
                double hp = hh * pk / 255.0 * 0.95, hb = hh * bs / 255.0 * 0.95;
                dc.DrawRectangle(peakBrush, null, up ? new Rect(x, baseY - hp, ww, hp) : new Rect(x, baseY, ww, hp));
                if (hb > 0.5) dc.DrawRectangle(bassBrush, null, up ? new Rect(x, baseY - hb, ww, hb) : new Rect(x, baseY, ww, hb));
            }
        }

        if (bpm > 0)
        {
            double beat = 60.0 / bpm;                     // in secondi del file
            double first = anchor + Math.Floor((pos - halfFile - anchor) / beat) * beat;
            for (double t = first; t <= pos + halfFile; t += beat)
            {
                if (t < 0) continue;
                long idx = (long)Math.Round((t - anchor) / beat);
                bool bar = ((idx % 4) + 4) % 4 == 0;
                double x = (t - (pos - halfFile)) * pxPerFileSec;
                double len = bar ? hh : hh * 0.45;
                dc.DrawLine(bar ? barPen : beatPen, new Point(x, baseY), new Point(x, baseY + dir * len));
            }
        }
    }
}
