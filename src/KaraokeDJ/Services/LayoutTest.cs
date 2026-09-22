using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KaraokeDJ.Services;

/// <summary>
/// <c>KaraokeDJ.exe --layouttest</c>: mette la finestra alle misure dei portatili veri (compresi gli schermi scalati
/// al 125/150 % di Windows, che riducono lo spazio utile) e verifica che nessun pezzo dell'interfaccia finisca fuori.
/// Serve perché i DJ lavorano su schermi da 13": qui si misura, non si guarda a occhio.
/// </summary>
public static class LayoutTest
{
    /// <summary>Misure in punti WPF (già divise per il fattore di Windows): come le vede l'app.</summary>
    public static readonly (string Name, double W, double H)[] Sizes =
    {
        ("1920x1080 al 100%", 1920, 1040),
        ("1920x1080 al 150% (13\" moderno)", 1280, 680),
        ("1600x900 al 100%", 1600, 860),
        ("1366x768 al 100% (13\" classico)", 1366, 728),
        ("1366x768 al 125%", 1093, 574),
        ("1280x800 al 100% (MacBook 13\")", 1280, 760),
    };

    public static string Run(Window win)
    {
        var sb = new StringBuilder();
        bool allOk = true;
        foreach (var (name, w, h) in Sizes)
        {
            win.Width = w; win.Height = h;
            win.UpdateLayout();
            // due giri: il primo applica la scala, il secondo rimisura con la scala nuova
            win.UpdateLayout();
            double scale = (win as Views.MainWindow)?.CurrentUiScale ?? 1;
            var outside = Clipped(win).Take(4).ToList();
            double lib = (win as Views.MainWindow)?.RootGrid is { } g2 && g2.RowDefinitions.Count > 4 ? g2.RowDefinitions[4].ActualHeight : 0;
            if (lib < 100) outside.Add($"libreria alta solo {lib:0} px");
            bool ok = outside.Count == 0;
            allOk &= ok;
            var rows = (win as Views.MainWindow)?.RootGrid is { } g
                ? " righe " + string.Join("/", g.RowDefinitions.Select(r => r.ActualHeight.ToString("0"))) : "";
            sb.Append($"\n  {name}: scala {scale * 100:0}%{rows} → {(ok ? "OK" : "PROBLEMI: " + string.Join(", ", outside))}");
        }
        return "layout: " + (allOk ? "OK (niente fuori dalla finestra su tutti i formati provati)" : "ERRORI") + sb;
    }

    /// <summary>Elementi che escono dai bordi della finestra (esclusi quelli dentro a un elenco scorrevole). Riporta solo il più esterno.</summary>
    private static List<string> Clipped(Window win)
    {
        var bad = new List<string>();
        var area = new Rect(0, 0, win.ActualWidth, win.ActualHeight);
        void Walk(DependencyObject d, bool inScroll, int depth)
        {
            if (depth > 16 || bad.Count > 6) return;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                bool scroll = inScroll || c is ScrollViewer or ScrollContentPresenter;
                if (!scroll && c is FrameworkElement fe && fe.IsVisible && fe.ActualWidth > 12 && fe.ActualHeight > 12)
                {
                    Rect r;
                    try { r = fe.TransformToAncestor(win).TransformBounds(new Rect(fe.RenderSize)); }
                    catch { Walk(c, scroll, depth + 1); continue; }
                    if (r.Right > area.Right + 1.5 || r.Bottom > area.Bottom + 1.5 || r.Left < -1.5 || r.Top < -1.5)
                    {
                        bad.Add($"{Describe(fe)} a {r.Left:0},{r.Top:0} di {r.Width:0}x{r.Height:0}");
                        continue;  // i figli escono di conseguenza: non li elenchiamo
                    }
                }
                Walk(c, scroll, depth + 1);
            }
        }
        Walk(win, false, 0);
        return bad;
    }

    private static string Describe(FrameworkElement fe)
    {
        if (!string.IsNullOrEmpty(fe.Name)) return fe.Name;
        var t = fe.GetType().Name;
        string? txt = fe switch
        {
            TextBlock tb => tb.Text,
            ContentControl cc => cc.Content as string,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(txt)) txt = fe.ToolTip as string;
        txt = txt?.Split('\n')[0];
        var label = txt is { Length: > 0 } ? $"{t} \"{(txt.Length > 24 ? txt[..24] : txt)}\"" : t;
        // primo testo trovato fra i figli, per capire di che pezzo si tratta
        string inner = FirstText(fe, 0) ?? "";
        // catena dei genitori, per ritrovarlo nello XAML
        var chain = new List<string>();
        DependencyObject? cur = VisualTreeHelper.GetParent(fe);
        while (cur != null && chain.Count < 3) { if (cur is FrameworkElement p2) chain.Add(string.IsNullOrEmpty(p2.Name) ? p2.GetType().Name : p2.Name); cur = VisualTreeHelper.GetParent(cur); }
        return label + (inner.Length > 0 ? " [" + inner + "]" : "") + " in " + string.Join("<", chain);
    }

    private static string? FirstText(DependencyObject d, int depth)
    {
        if (depth > 4) return null;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text)) return tb.Text.Length > 20 ? tb.Text[..20] : tb.Text;
            if (c is ContentControl cc && cc.Content is string s2 && s2.Length > 0) return s2.Length > 20 ? s2[..20] : s2;
            var r = FirstText(c, depth + 1);
            if (r != null) return r;
        }
        return null;
    }
}
