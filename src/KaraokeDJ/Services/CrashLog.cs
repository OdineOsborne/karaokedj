namespace KaraokeDJ.Services;

/// <summary>
/// Diario degli errori: %AppData%\KaraokeDJ\crash.log. Ci finiscono gli errori non gestiti (UI, task, thread audio),
/// i riavvii dell'uscita audio e i salvataggi falliti. Serve a capire cosa è successo in serata senza avere un debugger.
/// </summary>
public static class CrashLog
{
    public static string File => Path.Combine(AppPaths.Root, "crash.log");
    private static readonly object _gate = new();
    /// <summary>Errori registrati da quando l'app è partita (per selftest/soak).</summary>
    public static int Count { get; private set; }

    public static void Write(string what, Exception? ex = null)
    {
        lock (_gate)
        {
            Count++;
            try
            {
                Directory.CreateDirectory(AppPaths.Root);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} v{AppVersion()} {what}{(ex != null ? "\n" + ex : "")}\n";
                System.IO.File.AppendAllText(File, line);
                // il file non deve crescere per sempre: sopra 1 MB teniamo la seconda metà
                var fi = new FileInfo(File);
                if (fi.Length > 1_000_000)
                {
                    var txt = System.IO.File.ReadAllText(File);
                    System.IO.File.WriteAllText(File, txt[(txt.Length / 2)..]);
                }
            }
            catch { }
        }
    }

    private static string AppVersion()
    {
        try { return typeof(CrashLog).Assembly.GetName().Version?.ToString(3) ?? "?"; } catch { return "?"; }
    }
}
