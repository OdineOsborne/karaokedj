using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KaraokeDJ.Views;

/// <summary>
/// Effetto "vetro" di Windows 11 (Mica/Acrylic) dietro la finestra: i pannelli semitrasparenti lasciano vedere lo sfondo.
/// Su Windows 10 o se il compositore non lo supporta non fa nulla e resta lo sfondo sfumato dell'app.
/// </summary>
public static class WindowBackdrop
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;   // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    public static bool IsSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Attiva il backdrop sulla finestra (chiamare da SourceInitialized/Loaded). Ritorna true se applicato.</summary>
    public static bool Apply(Window w, bool acrylic = false)
    {
        if (!IsSupported) return false;
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            int type = acrylic ? DWMSBT_TRANSIENTWINDOW : DWMSBT_MAINWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) != 0) return false;
            // il frame (con il backdrop) copre tutta l'area client; WPF non deve dipingere lo sfondo
            var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref m);
            var src = HwndSource.FromHwnd(hwnd);
            if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
            w.Background = Brushes.Transparent;
            return true;
        }
        catch { return false; }
    }
}
