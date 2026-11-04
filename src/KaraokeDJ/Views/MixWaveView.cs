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

    public static readonly DependencyProperty BandsAProperty = DependencyProperty.Register(nameof(BandsA), typeof(byte[]), typeof(MixWaveView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BandsBProperty = DependencyProperty.Register(nameof(BandsB), typeof(byte[]), typeof(MixWaveView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public byte[]? DataA { get => (byte[]?)GetValue(DataAProperty); set => SetValue(DataAProperty, value); }
    public byte[]? DataB { get => (byte[]?)GetValue(DataBProperty); set => SetValue(DataBProperty, value); }
    /// <summary>Le tre bande dei due deck: l'onda prende il colore di quello che suona invece della tinta del deck.</summary>
    public byte[]? BandsA { get => (byte[]?)GetValue(BandsAProperty); set => SetValue(BandsAProperty, value); }
    public byte[]? BandsB { get => (byte[]?)GetValue(BandsBProperty); set => SetValue(BandsBProperty, value); }
    /// <summary>Posizione del deck (s, nel tempo del file).</summary>
    public double PosA { get => (double)GetValue(PosAProperty); set => SetValue(PosAProperty, value); }
    public double PosB { get => (double)GetValue(PosBProperty); set => SetValue(PosBProperty, value); }
    /// <summary>BPM naturali del brano (0 = ignoti: niente griglia).</summary>
    public static readonly DependencyProperty BeatsAProperty = DependencyProperty.Register(nameof(BeatsA), typeof(float[]), typeof(MixWaveView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BeatsBProperty = DependencyProperty.Register(nameof(BeatsB), typeof(float[]), typeof(MixWaveView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Griglia fluida dei due deck (istanti dei battiti veri).</summary>
    public float[]? BeatsA { get => (float[]?)GetValue(BeatsAProperty); set => SetValue(BeatsAProperty, value); }
    public float[]? BeatsB { get => (float[]?)GetValue(BeatsBProperty); set => SetValue(BeatsBProperty, value); }

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
    // La griglia si disegna SOPRA l'onda: con poca opacità spariva dentro le parti piene (il deck B non si vedeva più).
    private static readonly Pen BeatA = new(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);
    private static readonly Pen BarA = new(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1.6);
    private static readonly Pen BeatB = new(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1);
    private static readonly Pen BarB = new(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1.6);
    /// <summary>Ombra sotto le linee, così restano leggibili anche sulle creste chiare dell'onda.</summary>
    private static readonly Pen BeatShadow = new(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 2.6);

    static MixWaveView()
    {
        Bg.Freeze(); Center.Freeze(); Mid.Freeze(); BeatA.Freeze(); BarA.Freeze(); BeatB.Freeze(); BarB.Freeze(); BeatShadow.Freeze();
        Low.Freeze(); MidBand.Freeze(); HiBand.Freeze();
    }

    // le stesse tinte dell'onda del deck: una banda deve avere lo stesso colore ovunque, altrimenti non è un codice
    private static readonly Brush Low = new SolidColorBrush(Color.FromRgb(0xF2, 0x4B, 0x3A));
    private static readonly Brush MidBand = new SolidColorBrush(Color.FromRgb(0x3C, 0xC9, 0x5E));
    private static readonly Brush HiBand = new SolidColorBrush(Color.FromRgb(0x56, 0xAE, 0xFF));

    /// <summary>Le tre bande da una linea di base verso l'alto o il basso, dalla più alta alla più bassa.</summary>
    private static void DrawFromBase(DrawingContext dc, double x, double ww, double baseY, bool up,
                                     double h1, Brush b1, double h2, Brush b2, double h3, Brush b3)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            double best = 0; Brush? brush = null; int which = 0;
            if (h1 > best) { best = h1; brush = b1; which = 1; }
            if (h2 > best) { best = h2; brush = b2; which = 2; }
            if (h3 > best) { best = h3; brush = b3; which = 3; }
            if (brush == null || best < 0.5) return;
            dc.DrawRectangle(brush, null, up ? new Rect(x, baseY - best, ww, best) : new Rect(x, baseY, ww, best));
            if (which == 1) h1 = 0; else if (which == 2) h2 = 0; else h3 = 0;
        }
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

        DrawDeck(dc, DataA, BandsA, PosA, BpmA, TempoA, AnchorA, AccentA, BeatA, BarA, w, 0, mid, up: true, beats: BeatsA);
        DrawDeck(dc, DataB, BandsB, PosB, BpmB, TempoB, AnchorB, AccentB, BeatB, BarB, w, mid, h, up: false, beats: BeatsB);

        dc.DrawLine(Center, new Point(w / 2, 0), new Point(w / 2, h));
        var ft = new FormattedText($"{WindowSec:0} s", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.0);
        dc.DrawText(ft, new Point(w - ft.Width - 6, 3));
    }

    /// <summary>Disegna metà vista: onda del deck attorno alla posizione, con griglia battiti/battute.</summary>
    private void DrawDeck(DrawingContext dc, byte[]? data, byte[]? bands, double pos, double bpm, double tempo, double anchor, Brush accent,
                          Pen beatPen, Pen barPen, double w, double top, double bottom, bool up, float[]? beats = null)
    {
        double hh = bottom - top;
        double baseY = up ? bottom : top;             // linea di base: al centro
        double dir = up ? -1 : 1;
        if (tempo <= 0) tempo = 1;
        // finestra in secondi del file: ai lati della posizione, scalata per il tempo del deck
        double halfFile = WindowSec / 2 * tempo;
        double pxPerFileSec = w / (2 * halfFile);

        if (bands != null && bands.Length >= 3)
        {
            // stesso colore per banda dell'onda del deck. Qui serve il doppio: oltre a leggere il brano,
            // allineando due righe di rosso si allineano le casse, che è quello che si guarda mentre si mixa.
            int cols = bands.Length / 3;
            double colSec = 1.0 / FineWaveform.ColumnsPerSec;
            double x0 = pos - halfFile;
            int c0 = Math.Max(0, (int)(x0 / colSec)), c1 = Math.Min(cols - 1, (int)((pos + halfFile) / colSec) + 1);
            double colW = colSec * pxPerFileSec;
            int group = Math.Max(1, (int)Math.Ceiling(1.0 / colW));
            for (int c = c0; c <= c1; c += group)
            {
                int lo = 0, md = 0, hi = 0;
                for (int k = c; k < c + group && k < cols; k++)
                {
                    if (bands[k * 3] > lo) lo = bands[k * 3];
                    if (bands[k * 3 + 1] > md) md = bands[k * 3 + 1];
                    if (bands[k * 3 + 2] > hi) hi = bands[k * 3 + 2];
                }
                double x = (c * colSec - x0) * pxPerFileSec;
                double ww = Math.Max(1, colW * group - 0.5);
                double f = hh / 255.0 * 0.95;
                DrawFromBase(dc, x, ww, baseY, up, lo * f, Low, md * f, MidBand, hi * f, HiBand);
            }
        }
        else if (data != null && data.Length >= 2)
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

        // griglia fluida: si disegnano i battiti veri che cadono nella finestra
        if (beats is { Length: > 8 })
        {
            double from = pos - halfFile, to = pos + halfFile;
            for (int i = 0; i < beats.Length; i++)
            {
                double t = beats[i];
                if (t < from) continue;
                if (t > to) break;
                bool bar = i % 4 == 0;
                double x = (t - from) * pxPerFileSec;
                double len = bar ? hh : hh * 0.45;
                var q1 = new Point(x, baseY); var q2 = new Point(x, baseY + dir * len);
                dc.DrawLine(BeatShadow, q1, q2);
                dc.DrawLine(bar ? barPen : beatPen, q1, q2);
            }
        }
        else if (bpm > 0)
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
                var p1 = new Point(x, baseY); var p2 = new Point(x, baseY + dir * len);
                dc.DrawLine(BeatShadow, p1, p2);
                dc.DrawLine(bar ? barPen : beatPen, p1, p2);
            }
        }
    }
}
