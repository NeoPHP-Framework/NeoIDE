using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using NeoIDE.Sdk;

namespace NeoIDE.Services.Plugins;

public sealed class PluginManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>Segoe Fluent Icons glyph.</summary>
    [JsonPropertyName("icon")] public string Icon { get; set; } = "";
    /// <summary>Main assembly, e.g. NeoIDE.Plugin.Database.dll.</summary>
    [JsonPropertyName("assembly")] public string Assembly { get; set; } = "";
    /// <summary>Full name of the IPlugin class (optional: the first IPlugin of the assembly).</summary>
    [JsonPropertyName("entry")] public string? Entry { get; set; }
    [JsonPropertyName("sdk")] public int Sdk { get; set; } = 1;
    [JsonPropertyName("minIdeVersion")] public string? MinIdeVersion { get; set; }
}

public enum PluginState { Active, Disabled, Failed, Incompatible, RestartRequired }

public sealed class InstalledPlugin
{
    public required PluginManifest Manifest { get; init; }
    public required string Directory { get; init; }
    public PluginState State { get; set; }
    public string? Error { get; set; }
    public IPlugin? Instance { get; set; }
    /// <summary>Version waiting in the staging folder (applied at the next start).</summary>
    public string? PendingVersion { get; set; }
    public bool PendingRemoval { get; set; }
    public string Id => Manifest.Id;
}

/// <summary>
/// Installs and loads the plugins (%APPDATA%\NeoIDE\plugins\&lt;id&gt;). Each plugin gets its own AssemblyLoadContext;
/// NeoIDE.Sdk, WPF, AvalonEdit… are shared with the IDE. A loaded assembly cannot be unloaded: an update or a removal
/// of an active plugin is staged in plugins\.pending and applied at the next start.
/// </summary>
public static class PluginManager
{
    public static string Root => Path.Combine(AppSettings.Directory, "plugins");
    private static string Pending => Path.Combine(Root, ".pending");
    public static string DataDirectory => Path.Combine(AppSettings.Directory, "plugin-data");

    private static readonly List<InstalledPlugin> _plugins = new();
    public static IReadOnlyList<InstalledPlugin> Installed => _plugins;
    public static event Action? Changed;

    public static InstalledPlugin? Find(string id) => _plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>At startup, before loading: applies the staged updates and removals.</summary>
    public static void ApplyPending()
    {
        if (!System.IO.Directory.Exists(Pending)) return;
        foreach (var marker in System.IO.Directory.GetFiles(Pending, "*.remove"))
        {
            var id = Path.GetFileNameWithoutExtension(marker);
            TryDelete(Path.Combine(Root, id));
            TryDelete(Path.Combine(DataDirectory, id + ".json"), file: true);
            File.Delete(marker);
        }
        foreach (var dir in System.IO.Directory.GetDirectories(Pending))
        {
            var target = Path.Combine(Root, Path.GetFileName(dir));
            TryDelete(target);
            try { System.IO.Directory.Move(dir, target); } catch (Exception ex) { Log($"apply update {dir}: {ex.Message}"); }
        }
    }

    public static void LoadAll()
    {
        System.IO.Directory.CreateDirectory(Root);
        foreach (var dir in System.IO.Directory.GetDirectories(Root).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            var manifest = ReadManifest(dir);
            if (manifest == null) continue;
            var plugin = new InstalledPlugin { Manifest = manifest, Directory = dir };
            _plugins.Add(plugin);
            if (AppSettings.Current.DisabledPlugins.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase)) plugin.State = PluginState.Disabled;
            else Activate(plugin);
        }
        Changed?.Invoke();
    }

    public static void DeactivateAll()
    {
        foreach (var p in _plugins.Where(p => p.Instance != null))
            try { p.Instance!.Deactivate(); } catch (Exception ex) { Log($"{p.Id} deactivate: {ex.Message}"); }
    }

    private static void Activate(InstalledPlugin plugin)
    {
        var m = plugin.Manifest;
        if (m.Sdk != SdkInfo.Version)
        {
            plugin.State = PluginState.Incompatible;
            plugin.Error = $"Built for plugin API v{m.Sdk}, NeoIDE provides v{SdkInfo.Version}.";
            return;
        }
        if (m.MinIdeVersion != null && Marketplace.IsNewer(m.MinIdeVersion, UpdateService.CurrentVersion) && UpdateService.IsInstalled)
        {
            plugin.State = PluginState.Incompatible;
            plugin.Error = $"Requires NeoIDE {m.MinIdeVersion} or newer.";
            return;
        }
        try
        {
            var path = Path.Combine(plugin.Directory, m.Assembly);
            var context = new PluginLoadContext(path);
            var assembly = context.LoadFromAssemblyPath(path);
            var type = m.Entry != null ? assembly.GetType(m.Entry, throwOnError: true)!
                : assembly.GetTypes().FirstOrDefault(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract)
                  ?? throw new InvalidOperationException("No IPlugin class in " + m.Assembly);
            var instance = (IPlugin)Activator.CreateInstance(type)!;
            instance.Activate(new PluginContext(plugin));
            plugin.Instance = instance;
            plugin.State = PluginState.Active;
            plugin.Error = null;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            plugin.State = PluginState.Failed;
            plugin.Error = inner.Message;
            Log($"{m.Id} activate: {inner}");
        }
    }

    /// <summary>Downloads the zip of the marketplace and installs (or stages) it.</summary>
    public static async Task<InstalledPlugin> InstallAsync(MarketplacePlugin item, IProgress<int>? progress = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), "neoide-plugin-" + Guid.NewGuid().ToString("N"));
        var zip = temp + ".zip";
        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("NeoIDE");
                using var response = await http.GetAsync(item.Download, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync();
                await using var output = File.Create(zip);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    if (total > 0) progress?.Report((int)(done * 100 / total));
                }
            }
            ZipFile.ExtractToDirectory(zip, temp);
            return InstallFromFolder(temp, item.Id);
        }
        finally
        {
            TryDelete(temp);
            TryDelete(zip, file: true);
        }
    }

    /// <summary>Installs an extracted plugin folder (also used for a local .zip).</summary>
    public static InstalledPlugin InstallFromFolder(string folder, string? expectedId = null)
    {
        var manifest = ReadManifest(folder) ?? throw new InvalidDataException("plugin.json not found or invalid.");
        if (expectedId != null && !string.Equals(manifest.Id, expectedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The package contains '{manifest.Id}' instead of '{expectedId}'.");
        if (!File.Exists(Path.Combine(folder, manifest.Assembly))) throw new InvalidDataException($"{manifest.Assembly} is missing from the package.");

        var existing = Find(manifest.Id);
        if (existing?.Instance != null)
        {
            // loaded: its DLLs are locked until NeoIDE exits
            var staged = Path.Combine(Pending, manifest.Id);
            TryDelete(staged);
            CopyDirectory(folder, staged);
            existing.PendingVersion = manifest.Version;
            existing.PendingRemoval = false;
            TryDelete(Path.Combine(Pending, manifest.Id + ".remove"), file: true);
            existing.State = PluginState.RestartRequired;
            Changed?.Invoke();
            return existing;
        }

        var target = Path.Combine(Root, manifest.Id);
        TryDelete(target);
        CopyDirectory(folder, target);
        if (existing != null) _plugins.Remove(existing);
        var plugin = new InstalledPlugin { Manifest = manifest, Directory = target };
        _plugins.Add(plugin);
        AppSettings.Current.DisabledPlugins.RemoveAll(d => string.Equals(d, manifest.Id, StringComparison.OrdinalIgnoreCase));
        AppSettings.Current.Save();
        Activate(plugin);
        Changed?.Invoke();
        return plugin;
    }

    public static void Uninstall(string id)
    {
        var p = Find(id);
        if (p == null) return;
        if (p.Instance != null)
        {
            System.IO.Directory.CreateDirectory(Pending);
            File.WriteAllText(Path.Combine(Pending, id + ".remove"), "");
            TryDelete(Path.Combine(Pending, id));
            p.PendingRemoval = true;
            p.PendingVersion = null;
            p.State = PluginState.RestartRequired;
        }
        else
        {
            TryDelete(p.Directory);
            TryDelete(Path.Combine(DataDirectory, id + ".json"), file: true);
            _plugins.Remove(p);
        }
        Changed?.Invoke();
    }

    /// <summary>Disabling an active plugin takes effect at the next start; enabling loads it now when possible.</summary>
    public static void SetEnabled(string id, bool enabled)
    {
        var list = AppSettings.Current.DisabledPlugins;
        list.RemoveAll(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase));
        if (!enabled) list.Add(id);
        AppSettings.Current.Save();
        var p = Find(id);
        if (p == null) return;
        if (enabled && p.Instance == null) Activate(p);
        else if (!enabled && p.Instance != null) p.State = PluginState.RestartRequired;
        else if (!enabled) p.State = PluginState.Disabled;
        Changed?.Invoke();
    }

    public static bool IsEnabled(string id) => !AppSettings.Current.DisabledPlugins.Contains(id, StringComparer.OrdinalIgnoreCase);

    public static bool RestartRequired => _plugins.Any(p => p.State == PluginState.RestartRequired);

    private static PluginManifest? ReadManifest(string dir)
    {
        var file = Path.Combine(dir, "plugin.json");
        if (!File.Exists(file)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file));
            return m == null || string.IsNullOrWhiteSpace(m.Id) || string.IsNullOrWhiteSpace(m.Assembly) ? null : m;
        }
        catch (Exception ex) { Log($"{file}: {ex.Message}"); return null; }
    }

    private static void CopyDirectory(string source, string target)
    {
        System.IO.Directory.CreateDirectory(target);
        foreach (var f in System.IO.Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, f));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    private static void TryDelete(string path, bool file = false)
    {
        try
        {
            if (file) { if (File.Exists(path)) File.Delete(path); }
            else if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
        }
        catch (Exception ex) { Log($"delete {path}: {ex.Message}"); }
    }

    internal static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(AppSettings.Directory, "plugins.log"), $"[{DateTime.Now:O}] {message}\n"); } catch { }
    }
}

/// <summary>Resolves the plugin's own dependencies from its folder; everything the IDE already has is shared.</summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string mainAssembly) : base(Path.GetFileNameWithoutExtension(mainAssembly))
    {
        _resolver = new AssemblyDependencyResolver(mainAssembly);
    }

    protected override Assembly? Load(AssemblyName name)
    {
        // the SDK of the IDE, whatever version the plugin was compiled against (compatibility is checked with "sdk" in plugin.json)
        if (name.Name == "NeoIDE.Sdk") return typeof(IPlugin).Assembly;
        if (name.Name == "NeoIDE") return typeof(PluginManager).Assembly;
        // shared with the IDE: every assembly the IDE ships (WPF, AvalonEdit…)
        if (Default.Assemblies.Any(a => a.GetName().Name == name.Name)) return null;
        try { if (Default.LoadFromAssemblyName(name) is { } shared) return shared; } catch { }
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
