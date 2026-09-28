using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KaraokeDJ.Audio;
using KaraokeDJ.Models;
using KaraokeDJ.Models.Studio;
using KaraokeDJ.Services;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

/// <summary>
/// La timeline dello Studio, disegnata a mano (come la Playlist di FL Studio): righello in battute, corsie con
/// intestazione (muto/solo), clip con forma d'onda e struttura, loop, dissolvenze, passaggi e la curva del
/// parametro scelto. Si trascina, si taglia sulle battute, si zooma con Ctrl+rotella.
/// </summary>
public sealed class StudioTimeline : FrameworkElement
{
    public const double HeaderW = 150, RulerH = 30;
    /// <summary>Le corsie riempiono l'altezza disponibile (sul portatile da 15,6" metà schermo restava vuota).</summary>
    public double LaneH => Math.Clamp((ActualHeight - RulerH - 4) / Math.Max(1, Vm?.Project.Lanes.Count ?? 4), 72, 180);
    private const double EdgePx = 7, PointR = 5;

    private StudioViewModel? _vm;
    public StudioViewModel? Vm
    {
        get => _vm;
        set { if (_vm != null) _vm.Changed -= OnChanged; _vm = value; if (_vm != null) _vm.Changed += OnChanged; InvalidateMeasure(); InvalidateVisual(); }
    }
    private void OnChanged() { InvalidateMeasure(); InvalidateVisual(); }

    /// <summary>Pixel per secondo (zoom) e secondo al bordo sinistro (scorrimento).</summary>
    public double PxPerSec { get; private set; } = 8;
    public double ScrollSec { get; private set; }
    /// <summary>Aggancio: "bar" (battute), "beat" (battiti), "off".</summary>
    public string Snap { get; set; } = "bar";

    public StudioTimeline()
    {
        Focusable = true;
        ClipToBounds = true;
        AllowDrop = true;
        SnapsToDevicePixels = true;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info) { base.OnRenderSizeChanged(info); InvalidateVisual(); }

    protected override Size MeasureOverride(Size available)
    {
        int lanes = Vm?.Project.Lanes.Count ?? 4;
        return new Size(double.IsInfinity(available.Width) ? 800 : available.Width, double.IsInfinity(available.Height) ? RulerH + lanes * 96 + 10 : available.Height);
    }

    // ------------------------------------------------------------ coordinate

    private double X(double sec) => HeaderW + (sec - ScrollSec) * PxPerSec;
    private double Sec(double x) => ScrollSec + (x - HeaderW) / PxPerSec;
    private double LaneTop(int i) => RulerH + i * LaneH;
    private int LaneAt(double y) => (int)Math.Floor((y - RulerH) / LaneH);

    public void ZoomToFit()
    {
        if (Vm == null) return;
        double end = Math.Max(60, Vm.Project.EndSec + 10);
        PxPerSec = Math.Clamp((ActualWidth - HeaderW - 20) / end, 0.5, 400);
        ScrollSec = 0;
        InvalidateVisual();
    }

    public void EnsureVisible(double sec)
    {
        double w = (ActualWidth - HeaderW) / PxPerSec;
        if (sec < ScrollSec || sec > ScrollSec + w * 0.92) { ScrollSec = Math.Max(0, sec - w * 0.1); InvalidateVisual(); }
    }

    private double SnapTimeline(double sec)
    {
        if (Vm == null || Snap == "off" || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return sec;
        double step = Vm.Project.BeatSec * (Snap == "bar" ? 4 : 1);
        return Vm.Project.GridOffsetSec + Math.Round((sec - Vm.Project.GridOffsetSec) / step) * step;
    }

    private double SnapFile(StudioClip c, double fileSec)
    {
        if (Snap == "off" || c.Bpm <= 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return fileSec;
        return Services.Studio.StudioDj.SnapBeat(c, fileSec, Snap == "bar" ? 4 : 1);
    }

    // ------------------------------------------------------------ forme d'onda (cache)

    private readonly Dictionary<string, byte[]?> _fine = new();
    private readonly HashSet<string> _loading = new();

    private byte[]? FineFor(StudioClip c)
    {
        string key = c.TrackId ?? "file_" + (c.FilePath ?? "").GetHashCode().ToString("x");
        if (_fine.TryGetValue(key, out var f)) return f;
        f = FineWaveform.Load(key);
        if (f != null) { _fine[key] = f; return f; }
        if (_loading.Add(key))
        {
            var path = Vm?.PathOf(c);
            if (path != null)
                _ = Task.Run(async () =>
                {
                    var data = await FineWaveform.GetOrComputeAsync(key, path, CancellationToken.None);
                    Dispatcher.BeginInvoke(() => { _fine[key] = data; InvalidateVisual(); });
                });
        }
        return null;
    }

    // ------------------------------------------------------------ disegno

    private static readonly Brush Bg = Frozen(Color.FromRgb(0x14, 0x15, 0x1B));
    private static readonly Brush LaneBg1 = Frozen(Color.FromRgb(0x1A, 0x1C, 0x24));
    private static readonly Brush LaneBg2 = Frozen(Color.FromRgb(0x17, 0x19, 0x20));
    private static readonly Brush HeaderBg = Frozen(Color.FromRgb(0x21, 0x23, 0x2D));
    private static readonly Brush RulerBg = Frozen(Color.FromRgb(0x1E, 0x20, 0x29));
    private static readonly Brush TextBrush = Frozen(Color.FromRgb(0xE6, 0xE8, 0xEF));
    private static readonly Brush MutedText = Frozen(Color.FromRgb(0x8A, 0x8F, 0xA3));
    private static readonly Pen BarPen = new Pen(Frozen(Color.FromArgb(0x55, 0x80, 0x88, 0xA0)), 1).Frz();
    private static readonly Pen BeatPen = new Pen(Frozen(Color.FromArgb(0x22, 0x80, 0x88, 0xA0)), 1).Frz();
    private static readonly Pen AutoPen = new Pen(Frozen(Color.FromRgb(0xFF, 0xD3, 0x4D)), 2).Frz();
    private static readonly Brush AutoDot = Frozen(Color.FromRgb(0xFF, 0xD3, 0x4D));
    private static readonly Pen PlayPen = new Pen(Frozen(Color.FromRgb(0xFF, 0x4D, 0x6D)), 2).Frz();
    private static readonly Pen SelPen = new Pen(Brushes.White, 2).Frz();
    private static readonly Brush TransBrush = Frozen(Color.FromArgb(0x38, 0xB0, 0x7C, 0xFF));
    private static readonly Pen TransPen = new Pen(Frozen(Color.FromArgb(0xC0, 0xB0, 0x7C, 0xFF)), 1.5).Frz();
    private static readonly Brush LoopBrush = Frozen(Color.FromArgb(0x50, 0x3D, 0xDC, 0x84));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private static Color LaneColor(StudioLane l, int index) => l.Kind switch
    {
        LaneKind.Voice => Color.FromRgb(0x3D, 0xDC, 0x84),
        LaneKind.Fx => Color.FromRgb(0xB0, 0x7C, 0xFF),
        _ => index % 2 == 0 ? Color.FromRgb(0x29, 0xB6, 0xF6) : Color.FromRgb(0xFF, 0x9F, 0x2E),
    };

    private double Dpi => VisualTreeHelper.GetDpi(this).PixelsPerDip;
    private FormattedText Txt(string s, double size, Brush b, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal), size, b, Dpi);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Bg, null, new Rect(0, 0, w, h));
        if (Vm == null) return;
        var p = Vm.Project;

        // corsie
        for (int i = 0; i < p.Lanes.Count; i++)
            dc.DrawRectangle(i % 2 == 0 ? LaneBg1 : LaneBg2, null, new Rect(HeaderW, LaneTop(i), w - HeaderW, LaneH));

        DrawGrid(dc, p, w, h);

        dc.PushClip(new RectangleGeometry(new Rect(HeaderW, RulerH, Math.Max(0, w - HeaderW), Math.Max(0, h - RulerH))));
        DrawTransitions(dc, p);
        for (int i = 0; i < p.Lanes.Count; i++)
            foreach (var c in p.Clips.Where(c => c.LaneId == p.Lanes[i].Id))
                DrawClip(dc, p, c, i);
        dc.Pop();

        DrawRuler(dc, p, w);
        DrawHeaders(dc, p);
    }

    /// <summary>Posizione della testina in pixel (la disegna un livello sopra, che si muove senza ridisegnare tutto).</summary>
    public double PlayheadX => Vm == null ? -1 : X(Vm.PositionSec);

    private void DrawGrid(DrawingContext dc, StudioProject p, double w, double h)
    {
        double beat = p.BeatSec, bar = beat * 4;
        bool showBeats = beat * PxPerSec > 10;
        int barStep = 1; while (bar * barStep * PxPerSec < 14) barStep *= 2;
        double s0 = Sec(HeaderW), s1 = Sec(w);
        long k0 = (long)Math.Floor((s0 - p.GridOffsetSec) / beat), k1 = (long)Math.Ceiling((s1 - p.GridOffsetSec) / beat);
        for (long k = Math.Max(0, k0); k <= k1; k++)
        {
            bool isBar = k % 4 == 0;
            if (!isBar && !showBeats) continue;
            if (isBar && (k / 4) % barStep != 0) continue;
            double x = X(p.GridOffsetSec + k * beat);
            dc.DrawLine(isBar ? BarPen : BeatPen, new Point(x, RulerH), new Point(x, h));
        }
    }

    private void DrawRuler(DrawingContext dc, StudioProject p, double w)
    {
        dc.DrawRectangle(RulerBg, null, new Rect(0, 0, w, RulerH));
        double bar = p.BeatSec * 4;
        int step = 1; while (bar * step * PxPerSec < 46) step *= 2;
        double s0 = Sec(HeaderW), s1 = Sec(w);
        for (long b = Math.Max(0, (long)Math.Floor((s0 - p.GridOffsetSec) / bar)); b * bar + p.GridOffsetSec <= s1; b++)
        {
            if (b % step != 0) continue;
            double t = p.GridOffsetSec + b * bar, x = X(t);
            if (x < HeaderW) continue;
            dc.DrawLine(BarPen, new Point(x, RulerH - 10), new Point(x, RulerH));
            dc.DrawText(Txt((b + 1).ToString(), 10, TextBrush, true), new Point(x + 3, 2));
            dc.DrawText(Txt(StudioViewModel.Fmt(t), 9, MutedText), new Point(x + 3, 15));
        }
        dc.DrawRectangle(HeaderBg, null, new Rect(0, 0, HeaderW, RulerH));
        dc.DrawText(Txt($"{p.Bpm:0.##} BPM", 12, TextBrush, true), new Point(10, 7));
    }

    private void DrawHeaders(DrawingContext dc, StudioProject p)
    {
        for (int i = 0; i < p.Lanes.Count; i++)
        {
            var l = p.Lanes[i];
            double y = LaneTop(i);
            dc.DrawRectangle(HeaderBg, null, new Rect(0, y, HeaderW, LaneH - 1));
            dc.DrawRectangle(new SolidColorBrush(LaneColor(l, i)), null, new Rect(0, y, 4, LaneH - 1));
            dc.DrawText(Txt(l.Name, 13, TextBrush, true), new Point(12, y + 8));
            string kind = l.Kind switch { LaneKind.Voice => "voce · abbassa la musica", LaneKind.Fx => "effetti", _ => "musica" };
            dc.DrawText(Txt(kind, 9.5, MutedText), new Point(12, y + 27));
            DrawToggle(dc, new Rect(12, y + LaneH - 32, 30, 22), "M", l.Mute, Color.FromRgb(0xFF, 0x4D, 0x6D));
            DrawToggle(dc, new Rect(48, y + LaneH - 32, 30, 22), "S", l.Solo, Color.FromRgb(0xFF, 0xD3, 0x4D));
            if (Math.Abs(l.GainDb) > 0.05) dc.DrawText(Txt($"{l.GainDb:+0.#;-0.#} dB", 10, MutedText), new Point(86, y + LaneH - 29));
        }
    }

    private void DrawToggle(DrawingContext dc, Rect r, string s, bool on, Color c)
    {
        dc.DrawRoundedRectangle(on ? new SolidColorBrush(c) : Frozen(Color.FromRgb(0x2C, 0x2F, 0x3B)), null, r, 4, 4);
        var t = Txt(s, 11, on ? Brushes.Black : TextBrush, true);
        dc.DrawText(t, new Point(r.X + (r.Width - t.Width) / 2, r.Y + (r.Height - t.Height) / 2));
    }

    private void DrawTransitions(DrawingContext dc, StudioProject p)
    {
        if (Vm == null) return;
        foreach (var c in p.Clips.Where(c => c.TransitionIn != null))
        {
            var prev = Vm.PrevInChain(c);
            if (prev == null) continue;
            int li = p.Lanes.FindIndex(l => l.Id == c.LaneId), lp = p.Lanes.FindIndex(l => l.Id == prev.LaneId);
            if (li < 0 || lp < 0) continue;
            double x0 = X(c.StartSec), x1 = X(Math.Max(prev.EndSec, c.StartSec + 0.3));
            double y0 = LaneTop(Math.Min(li, lp)) + 2, y1 = LaneTop(Math.Max(li, lp)) + LaneH - 2;
            var r = new Rect(x0, y0, Math.Max(4, x1 - x0), y1 - y0);
            bool sel = Vm.TransitionClip == c;
            dc.DrawRoundedRectangle(TransBrush, sel ? SelPen : TransPen, r, 4, 4);
            var name = Services.Studio.StudioDj.GetPreset(c.TransitionIn!).Name;
            var t = Txt("⇄ " + name, 10.5, TextBrush, true);
            if (r.Width > t.Width + 6) dc.DrawText(t, new Point(r.X + 4, (y0 + y1) / 2 - t.Height / 2));
            else dc.DrawText(Txt("⇄", 12, TextBrush, true), new Point(r.X + 2, (y0 + y1) / 2 - 8));
        }
    }

    private void DrawClip(DrawingContext dc, StudioProject p, StudioClip c, int laneIndex)
    {
        double x0 = X(c.StartSec), x1 = X(c.EndSec);
        if (x1 < HeaderW || x0 > ActualWidth) return;
        double y = LaneTop(laneIndex) + 4, hgt = LaneH - 8;
        var col = LaneColor(p.Lanes[laneIndex], laneIndex);
        var rect = new Rect(x0, y, Math.Max(3, x1 - x0), hgt);
        bool sel = Vm!.SelectedClip == c;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x40, col.R, col.G, col.B)), sel ? SelPen : new Pen(new SolidColorBrush(col), 1), rect, 4, 4);

        dc.PushClip(new RectangleGeometry(rect, 4, 4));
        var t = Vm.TrackOf(c);
        // sezioni del brano (ritornelli in evidenza) in fondo alla clip
        if (t?.Sections is { Count: > 1 } secs && t.SectionsScore >= 1.0)
        {
            for (int i = 0; i < secs.Count; i++)
            {
                double f0 = secs[i].Start, f1 = i + 1 < secs.Count ? secs[i + 1].Start : t.DurationSec;
                var kindCol = secs[i].Kind switch { "ritornello" => Color.FromArgb(0x70, 0xFF, 0x4D, 0x9A), "pieno" => Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF), "intro" or "finale" => Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF), _ => Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) };
                foreach (var (a, b) in LocalRanges(c, f0, f1))
                    dc.DrawRectangle(new SolidColorBrush(kindCol), null, new Rect(X(c.StartSec + a), y + hgt - 6, Math.Max(1, (b - a) * PxPerSec), 6));
            }
        }
        // forma d'onda
        var fine = FineFor(c);
        if (fine != null) DrawWave(dc, c, fine, rect, col);
        // loop (zone ripetute)
        double local = 0;
        foreach (var (from, to) in c.Segments())
        {
            double len = (to - from) / c.Tempo;
            bool repeated = c.Loops.Any(l => Math.Abs(l.AtSec - from) < 1e-6 && Math.Abs(l.AtSec + l.LengthSec - to) < 1e-6);
            if (repeated) dc.DrawRectangle(LoopBrush, null, new Rect(X(c.StartSec + local), y, Math.Max(1, len * PxPerSec), 10));
            local += len;
        }
        // dissolvenze
        if (c.FadeInSec > 0) DrawFade(dc, x0, y, c.FadeInSec * PxPerSec, hgt, true);
        if (c.FadeOutSec > 0) DrawFade(dc, x1, y, c.FadeOutSec * PxPerSec, hgt, false);
        // curva di automazione del parametro scelto
        DrawAutomation(dc, c, rect, sel);
        dc.Pop();

        // titolo
        var title = Txt(c.Label, 11, TextBrush, true);
        title.MaxTextWidth = Math.Max(10, rect.Width - 8); title.MaxLineCount = 1; title.Trimming = TextTrimming.CharacterEllipsis;
        if (rect.Width > 30) dc.DrawText(title, new Point(Math.Max(x0, HeaderW) + 4, y + 2));
        if (rect.Width > 80 && Math.Abs(c.Tempo - 1) > 0.0005 || c.KeyShift != 0)
        {
            string info = (Math.Abs(c.Tempo - 1) > 0.0005 ? $"{(c.Tempo - 1) * 100:+0.#;-0.#} %" : "") + (c.KeyShift != 0 ? $"  {c.KeyShift:+0;-0} st" : "");
            if (rect.Width > 120) dc.DrawText(Txt(info, 9.5, MutedText), new Point(Math.Max(x0, HeaderW) + 4, y + 17));
        }
    }

    /// <summary>Dove un pezzo di file (from–to) cade dentro la clip, in secondi dall'inizio della clip (i loop lo ripetono).</summary>
    private static IEnumerable<(double a, double b)> LocalRanges(StudioClip c, double f0, double f1)
    {
        double local = 0;
        foreach (var (from, to) in c.Segments())
        {
            double lo = Math.Max(from, f0), hi = Math.Min(to, f1);
            if (hi > lo) yield return (local + (lo - from) / c.Tempo, local + (hi - from) / c.Tempo);
            local += (to - from) / c.Tempo;
        }
    }

    private void DrawWave(DrawingContext dc, StudioClip c, byte[] fine, Rect rect, Color col)
    {
        int cols = fine.Length / 2, cps = FineWaveform.ColumnsPerSec;
        double mid = rect.Y + rect.Height / 2 + 4, amp = rect.Height / 2 - 12;
        double xa = Math.Max(rect.X, HeaderW), xb = Math.Min(rect.Right, ActualWidth);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            bool first = true;
            var bottom = new List<Point>();
            // pezzi del file calcolati una volta (i loop li ripetono): da tempo della clip a secondo del file
            var segs = c.Segments();
            var starts = new double[segs.Count];
            double acc = 0;
            for (int k = 0; k < segs.Count; k++) { starts[k] = acc; acc += (segs[k].To - segs[k].From) / c.Tempo; }
            double FileAt(double local)
            {
                for (int k = segs.Count - 1; k >= 0; k--)
                    if (local >= starts[k]) return Math.Min(segs[k].To, segs[k].From + (local - starts[k]) * c.Tempo);
                return c.InSec;
            }
            for (double x = xa; x <= xb; x += 1.5)
            {
                double local = (x - rect.X) / PxPerSec;
                double ft0 = FileAt(local), ft1 = FileAt(local + 1.5 / PxPerSec);
                int i0 = (int)(ft0 * cps), i1 = Math.Max(i0 + 1, (int)(ft1 * cps));
                byte pk = 0;
                for (int i = Math.Max(0, i0); i < Math.Min(cols, i1); i++) pk = Math.Max(pk, fine[i * 2]);
                double a = pk / 255.0 * amp;
                var top = new Point(x, mid - a);
                if (first) { g.BeginFigure(top, true, true); first = false; } else g.LineTo(top, true, false);
                bottom.Add(new Point(x, mid + a));
            }
            for (int i = bottom.Count - 1; i >= 0; i--) g.LineTo(bottom[i], true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0xB0, col.R, col.G, col.B)), null, geo);
    }

    private static void DrawFade(DrawingContext dc, double x, double y, double w, double h, bool fadeIn)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            if (fadeIn) { g.BeginFigure(new Point(x, y), true, true); g.LineTo(new Point(x + w, y), true, false); g.LineTo(new Point(x, y + h), true, false); }
            else { g.BeginFigure(new Point(x, y), true, true); g.LineTo(new Point(x - w, y), true, false); g.LineTo(new Point(x, y + h), true, false); }
        }
        geo.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), null, geo);
    }

    private (double y0, double y1) AutoBand(Rect r) => (r.Y + 16, r.Bottom - 10);

    private void DrawAutomation(DrawingContext dc, StudioClip c, Rect r, bool sel)
    {
        var info = AutoParams.Get(Vm!.AutomationParam);
        var a = c.Autos.FirstOrDefault(x => x.Param == info.Id);
        if (a == null || a.Points.Count == 0) { if (!sel) return; }
        var (y0, y1) = AutoBand(r);
        double Y(double v) => y1 - (v - info.Min) / (info.Max - info.Min) * (y1 - y0);
        var pen = sel ? AutoPen : new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xD3, 0x4D)), 1.2);
        if (a == null || a.Points.Count == 0)
        {
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xD3, 0x4D)), 1) { DashStyle = DashStyles.Dash }, new Point(r.X, Y(info.Neutral)), new Point(r.Right, Y(info.Neutral)));
            return;
        }
        var pts = a.Points;
        Point prev = new(r.X, Y(pts[0].V));
        foreach (var pt in pts)
        {
            var cur = new Point(r.X + pt.T * PxPerSec, Y(pt.V));
            if (pt.Step) { dc.DrawLine(pen, prev, new Point(cur.X, prev.Y)); dc.DrawLine(pen, new Point(cur.X, prev.Y), cur); }
            else dc.DrawLine(pen, prev, cur);
            prev = cur;
        }
        dc.DrawLine(pen, prev, new Point(r.Right, prev.Y));
        if (sel) foreach (var pt in pts) dc.DrawEllipse(AutoDot, null, new Point(r.X + pt.T * PxPerSec, Y(pt.V)), PointR, PointR);
    }

    // ------------------------------------------------------------ mouse

    private enum DragKind { None, Move, TrimLeft, TrimRight, Point, Seek, Pan }
    private DragKind _drag;
    private StudioClip? _dragClip;
    private AutoPoint? _dragPoint;
    private Point _down;
    private double _origStart, _origIn, _origOut, _origScroll;
    private int _origLane;
    private bool _moved;

    private (StudioClip? clip, int lane) ClipAt(Point pt)
    {
        if (Vm == null) return (null, -1);
        int li = LaneAt(pt.Y);
        if (li < 0 || li >= Vm.Project.Lanes.Count) return (null, li);
        double s = Sec(pt.X);
        var lane = Vm.Project.Lanes[li];
        var c = Vm.Project.Clips.Where(c => c.LaneId == lane.Id && s >= c.StartSec && s <= c.EndSec).OrderByDescending(c => c.StartSec).FirstOrDefault();
        return (c, li);
    }

    private StudioClip? TransitionAt(Point pt)
    {
        if (Vm == null) return null;
        double s = Sec(pt.X);
        foreach (var c in Vm.Project.Clips.Where(c => c.TransitionIn != null))
        {
            var prev = Vm.PrevInChain(c);
            if (prev == null) continue;
            int li = Vm.Project.Lanes.FindIndex(l => l.Id == c.LaneId), lp = Vm.Project.Lanes.FindIndex(l => l.Id == prev.LaneId);
            double y0 = LaneTop(Math.Min(li, lp)), y1 = LaneTop(Math.Max(li, lp)) + LaneH;
            // la fascia del passaggio si prende nella parte bassa della corsia sopra e alta di quella sotto
            if (s >= c.StartSec && s <= Math.Max(prev.EndSec, c.StartSec + 0.3) && pt.Y >= y0 && pt.Y <= y1)
            {
                double yc = LaneTop(Math.Max(li, lp));
                if (Math.Abs(pt.Y - yc) < 24) return c;
            }
        }
        return null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Vm == null) return;
        var pt = e.GetPosition(this);
        _down = pt; _moved = false; _drag = DragKind.None;

        if (pt.X < HeaderW) { HeaderClick(pt); return; }
        if (pt.Y < RulerH)
        {
            _drag = DragKind.Seek;
            Vm.Seek(Math.Max(0, Sec(pt.X)));
            CaptureMouse();
            return;
        }
        if (e.ClickCount == 2)
        {
            Vm.Seek(Math.Max(0, Sec(pt.X)));
            if (!Vm.IsPlaying) Vm.TogglePlay();
            return;
        }
        var tr = TransitionAt(pt);
        if (tr != null) { Vm.TransitionClip = tr; Vm.SelectedClip = null; return; }

        var (c, li) = ClipAt(pt);
        if (c == null)
        {
            Vm.SelectedClip = null; Vm.TransitionClip = null;
            _drag = DragKind.Pan; _origScroll = ScrollSec; CaptureMouse();
            return;
        }
        Vm.SelectedClip = c;
        if (c.TransitionIn != null) Vm.TransitionClip = c;
        _dragClip = c; _origStart = c.StartSec; _origIn = c.InSec; _origOut = c.OutSec; _origLane = li;

        // punto di automazione (solo sulla clip selezionata)
        var hitPoint = PointAt(c, li, pt);
        if (hitPoint != null) { _drag = DragKind.Point; _dragPoint = hitPoint; Vm.BeginDrag(); CaptureMouse(); return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            // Ctrl+clic: nuovo punto di automazione qui
            AddPoint(c, li, pt);
            return;
        }
        double x0 = X(c.StartSec), x1 = X(c.EndSec);
        _drag = pt.X - x0 < EdgePx ? DragKind.TrimLeft : x1 - pt.X < EdgePx ? DragKind.TrimRight : DragKind.Move;
        Vm.BeginDrag();
        CaptureMouse();
    }

    private void HeaderClick(Point pt)
    {
        int li = LaneAt(pt.Y);
        if (Vm == null || li < 0 || li >= Vm.Project.Lanes.Count) return;
        var l = Vm.Project.Lanes[li];
        double y = LaneTop(li);
        if (new Rect(12, y + LaneH - 32, 30, 22).Contains(pt)) Vm.Edit(l.Mute ? $"{l.Name}: si sente" : $"{l.Name}: muta", () => l.Mute = !l.Mute);
        else if (new Rect(48, y + LaneH - 32, 30, 22).Contains(pt)) Vm.Edit(l.Solo ? $"{l.Name}: solo tolto" : $"{l.Name}: solo lei", () => l.Solo = !l.Solo);
    }

    private AutoPoint? PointAt(StudioClip c, int li, Point pt)
    {
        if (Vm?.SelectedClip != c) return null;
        var a = c.Autos.FirstOrDefault(x => x.Param == Vm.AutomationParam);
        if (a == null) return null;
        var info = AutoParams.Get(a.Param);
        var r = new Rect(X(c.StartSec), LaneTop(li) + 4, c.LengthSec * PxPerSec, LaneH - 8);
        var (y0, y1) = AutoBand(r);
        foreach (var p in a.Points)
        {
            double px = r.X + p.T * PxPerSec, py = y1 - (p.V - info.Min) / (info.Max - info.Min) * (y1 - y0);
            if (Math.Abs(px - pt.X) <= PointR + 3 && Math.Abs(py - pt.Y) <= PointR + 3) return p;
        }
        return null;
    }

    private double ValueAtY(StudioClip c, int li, double y)
    {
        var info = AutoParams.Get(Vm!.AutomationParam);
        var r = new Rect(X(c.StartSec), LaneTop(li) + 4, c.LengthSec * PxPerSec, LaneH - 8);
        var (y0, y1) = AutoBand(r);
        return Math.Clamp(info.Min + (y1 - y) / (y1 - y0) * (info.Max - info.Min), info.Min, info.Max);
    }

    private void AddPoint(StudioClip c, int li, Point pt)
    {
        double t = Math.Clamp(Sec(pt.X) - c.StartSec, 0, c.LengthSec);
        double v = ValueAtY(c, li, pt.Y);
        var info = AutoParams.Get(Vm!.AutomationParam);
        Vm.Edit($"Punto {info.Name}", () =>
        {
            var a = c.Auto(info.Id);
            a.Points.Add(new AutoPoint(t, v));
            a.Points.Sort((x, y) => x.T.CompareTo(y.T));
        });
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (Vm == null) return;
        var pt = e.GetPosition(this);
        var (c, li) = ClipAt(pt);
        if (c == null) return;
        var p = PointAt(c, li, pt);
        if (p != null)
        {
            Vm.Edit("Punto tolto", () => { foreach (var a in c.Autos) a.Points.Remove(p); c.Autos.RemoveAll(a => a.Points.Count == 0); });
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Vm == null) return;
        var pt = e.GetPosition(this);
        if (_drag == DragKind.None)
        {
            UpdateCursor(pt);
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        double dx = pt.X - _down.X;
        if (!_moved && Math.Abs(dx) < 3 && Math.Abs(pt.Y - _down.Y) < 3) return;
        _moved = true;
        double dSec = dx / PxPerSec;
        var c = _dragClip;
        switch (_drag)
        {
            case DragKind.Seek:
                Vm.PositionSec = Math.Max(0, Sec(pt.X));
                return;
            case DragKind.Pan:
                ScrollSec = Math.Max(0, _origScroll - dSec);
                InvalidateVisual();
                return;
            case DragKind.Move when c != null:
                c.StartSec = Math.Max(0, SnapTimeline(_origStart + dSec));
                int li = Math.Clamp(LaneAt(pt.Y), 0, Vm.Project.Lanes.Count - 1);
                c.LaneId = Vm.Project.Lanes[li].Id;
                break;
            case DragKind.TrimLeft when c != null:
            {
                double fileIn = SnapFile(c, Math.Clamp(_origIn + dSec * c.Tempo, 0, c.OutSec - 0.5));
                c.InSec = fileIn;
                c.StartSec = Math.Max(0, _origStart + (fileIn - _origIn) / c.Tempo);
                c.AutoRange = false;
                break;
            }
            case DragKind.TrimRight when c != null:
            {
                double max = c.FileDurationSec > 0 ? c.FileDurationSec : double.MaxValue;
                c.OutSec = SnapFile(c, Math.Clamp(_origOut + dSec * c.Tempo, c.InSec + 0.5, max));
                c.AutoRange = false;
                break;
            }
            case DragKind.Point when c != null && _dragPoint != null:
            {
                int lane = Vm.Project.Lanes.FindIndex(l => l.Id == c.LaneId);
                _dragPoint.T = Math.Clamp(Sec(pt.X) - c.StartSec, 0, c.LengthSec);
                _dragPoint.V = ValueAtY(c, lane, pt.Y);
                foreach (var a in c.Autos) a.Points.Sort((x, y) => x.T.CompareTo(y.T));
                break;
            }
        }
        Vm.DragMoved();
    }

    private void UpdateCursor(Point pt)
    {
        if (pt.X < HeaderW || pt.Y < RulerH) { Cursor = pt.Y < RulerH ? Cursors.IBeam : Cursors.Arrow; return; }
        if (TransitionAt(pt) != null) { Cursor = Cursors.Hand; return; }
        var (c, li) = ClipAt(pt);
        if (c == null) { Cursor = Cursors.Arrow; return; }
        if (PointAt(c, li, pt) != null) { Cursor = Cursors.Cross; return; }
        double x0 = X(c.StartSec), x1 = X(c.EndSec);
        Cursor = pt.X - x0 < EdgePx || x1 - pt.X < EdgePx ? Cursors.SizeWE : Cursors.SizeAll;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (Vm == null) { _drag = DragKind.None; return; }
        var kind = _drag;
        _drag = DragKind.None;
        ReleaseMouseCapture();
        if (kind == DragKind.Seek) { Vm.Seek(Vm.PositionSec); return; }
        if (kind == DragKind.Pan) return;
        if (kind is DragKind.Move or DragKind.TrimLeft or DragKind.TrimRight or DragKind.Point)
        {
            if (!_moved) { Vm.EndDrag(""); return; }
            Vm.EndDrag(kind switch
            {
                DragKind.Move => $"Spostata: {_dragClip?.Label} a {StudioViewModel.Fmt(_dragClip?.StartSec ?? 0)}",
                DragKind.TrimLeft or DragKind.TrimRight => $"Tagliata: {_dragClip?.Label}",
                _ => "Automazione modificata",
            });
        }
        _dragClip = null; _dragPoint = null;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var pt = e.GetPosition(this);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            double at = Sec(pt.X);
            PxPerSec = Math.Clamp(PxPerSec * (e.Delta > 0 ? 1.25 : 0.8), 0.3, 600);
            ScrollSec = Math.Max(0, at - (pt.X - HeaderW) / PxPerSec);
        }
        else ScrollSec = Math.Max(0, ScrollSec - e.Delta / 120.0 * 80 / PxPerSec);
        InvalidateVisual();
        e.Handled = true;
    }

    // ------------------------------------------------------------ trascinare dentro brani e file

    public const string TrackFormat = "MixfoniaTrack";

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(TrackFormat) || e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        if (Vm == null) return;
        var pt = e.GetPosition(this);
        int li = Math.Clamp(LaneAt(pt.Y), 0, Math.Max(0, Vm.Project.Lanes.Count - 1));
        if (Vm.Project.Lanes.Count == 0) return;
        double at = Math.Max(0, SnapTimeline(Sec(pt.X)));
        var laneId = Vm.Project.Lanes[li].Id;
        if (e.Data.GetData(TrackFormat) is Track t) Vm.AddAt(t, laneId, at);
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            foreach (var f in files.Where(f => SourceFactory.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                Vm.AddFileAt(f, laneId, at);
                at += 1;
            }
    }
}

internal static class PenExt
{
    public static Pen Frz(this Pen p) { p.Freeze(); return p; }
}
