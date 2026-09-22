using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using Mixfonia.Plugins;

namespace KaraokeDJ.Services;

/// <summary>Un plugin caricato (o fallito) con le sue informazioni per la UI.</summary>
public sealed class LoadedPlugin
{
    public string Folder { get; init; } = "";
    public IMixfoniaPlugin? Plugin { get; init; }
    public string? Error { get; init; }
    public string Name => Plugin?.Name ?? Path.GetFileName(Folder);
    public string Version => Plugin?.Version ?? "";
    public string Description => Plugin?.Description ?? (Error ?? "");
    public bool Ok => Plugin != null;
}

/// <summary>
/// Carica i plugin da %AppData%\KaraokeDJ\plugins\&lt;cartella&gt;\*.dll (classi che implementano IMixfoniaPlugin)
/// e raccoglie le sorgenti di importazione: quelle integrate (lecite e gratuite) più quelle dei plugin.
/// </summary>
public sealed class PluginManager
{
    public static string PluginsDir => Path.Combine(AppPaths.Root, "plugins");
    public List<LoadedPlugin> Plugins { get; } = new();
    public List<IImportSource> ImportSources { get; } = new();

    private sealed class Host : IPluginHost
    {
        public string DataDir { get; init; } = "";
        public string ToolsDir => AppPaths.ToolsDir;
        public Func<string, string, bool> TrackExists { get; init; } = (_, _) => false;
        public Action<string> Status { get; init; } = _ => { };
        public void SetStatus(string text) => Status(text);
    }

    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        public PluginLoadContext(string mainDll) : base(isCollectible: false) => _resolver = new AssemblyDependencyResolver(mainDll);
        protected override Assembly? Load(AssemblyName name)
        {
            // il contratto (e tutto ciò che l'app ha già) resta condiviso con l'app; le altre dipendenze vengono dalla cartella del plugin
            if (name.Name == "Mixfonia.Plugins") return null;
            var already = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
            if (already != null) return already;
            var path = _resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }
    }

    public void Load(Func<string, string, bool> trackExists, Action<string> status)
    {
        Plugins.Clear();
        ImportSources.Clear();
        foreach (var s in LegalSources.All) ImportSources.Add(s);
        try { Directory.CreateDirectory(PluginsDir); } catch { }
        foreach (var dir in Directory.Exists(PluginsDir) ? Directory.EnumerateDirectories(PluginsDir) : Array.Empty<string>())
        {
            var dll = Directory.EnumerateFiles(dir, "Mixfonia.Plugin.*.dll").FirstOrDefault() ?? Directory.EnumerateFiles(dir, "*.dll").FirstOrDefault();
            if (dll == null) continue;
            try
            {
                var ctx = new PluginLoadContext(dll);
                var asm = ctx.LoadFromAssemblyPath(dll);
                var type = asm.GetTypes().FirstOrDefault(t => typeof(IMixfoniaPlugin).IsAssignableFrom(t) && !t.IsAbstract);
                if (type == null) { Plugins.Add(new LoadedPlugin { Folder = dir, Error = "nessuna classe IMixfoniaPlugin" }); continue; }
                var plugin = (IMixfoniaPlugin)Activator.CreateInstance(type)!;
                var data = Path.Combine(PluginsDir, "data", plugin.Id);
                Directory.CreateDirectory(data);
                plugin.Initialize(new Host { DataDir = data, TrackExists = trackExists, Status = status });
                Plugins.Add(new LoadedPlugin { Folder = dir, Plugin = plugin });
                foreach (var src in plugin.ImportSources) ImportSources.Insert(0, src); // i plugin hanno precedenza nell'elenco
            }
            catch (Exception ex) { Plugins.Add(new LoadedPlugin { Folder = dir, Error = ex.GetBaseException().Message }); }
        }
    }

    /// <summary>Installa un plugin da uno zip (una cartella con le DLL) o da una DLL singola. Serve riavviare l'app.</summary>
    public static string Install(string path)
    {
        Directory.CreateDirectory(PluginsDir);
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var dest = Path.Combine(PluginsDir, name);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            ZipFile.ExtractToDirectory(path, dest);
            // zip con una sottocartella unica: appiattisci
            var subs = Directory.GetDirectories(dest);
            if (subs.Length == 1 && Directory.GetFiles(dest).Length == 0)
            {
                var inner = subs[0];
                foreach (var f in Directory.GetFiles(inner)) File.Move(f, Path.Combine(dest, Path.GetFileName(f)), true);
                Directory.Delete(inner, true);
            }
            return dest;
        }
        else
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var dest = Path.Combine(PluginsDir, name);
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, name + ".*")) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
            return dest;
        }
    }
}
