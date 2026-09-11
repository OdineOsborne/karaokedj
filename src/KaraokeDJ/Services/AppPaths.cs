using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaraokeDJ.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaraokeDJ");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string QueueFile => Path.Combine(Root, "queue.json");
    public static string PlaylistsFile => Path.Combine(Root, "playlists.json");
    public static string CelebrationFile => Path.Combine(Root, "celebration.json");
    public static string ToolsDir => Path.Combine(Root, "tools");
    public static string CacheDir => Path.Combine(Path.GetTempPath(), "KaraokeDJ");
    public static string DownloadsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "KaraokeDJ Downloads");

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ToolsDir);
        Directory.CreateDirectory(CacheDir);
    }
}

public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch { }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Forme d'onda pre-calcolate, un file binario per brano (id).</summary>
public static class WaveformStore
{
    public static string Dir => Path.Combine(AppPaths.Root, "waveforms");

    public static void Save(string trackId, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(Path.Combine(Dir, trackId + ".wf"), data);
        }
        catch { }
    }

    public static byte[]? Load(string trackId)
    {
        try
        {
            var p = Path.Combine(Dir, trackId + ".wf");
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }
}

/// <summary>Segreti protetti con DPAPI (solo l'utente Windows corrente può leggerli).</summary>
public static class Secret
{
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var bytes = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch { return null; }
    }
}
