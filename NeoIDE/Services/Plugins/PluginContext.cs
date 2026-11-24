using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NeoIDE.Sdk;

namespace NeoIDE.Services.Plugins;

internal sealed class PluginContext : IPluginContext
{
    private readonly InstalledPlugin _plugin;

    public PluginContext(InstalledPlugin plugin)
    {
        _plugin = plugin;
        Settings = new PluginSettings(plugin.Id);
    }

    public string Id => _plugin.Id;
    public string Directory => _plugin.Directory;
    public IIde Ide => IdeHost.Instance;
    public IPluginSettings Settings { get; }
    public void Log(string message) => PluginManager.Log($"[{Id}] {message}");
}

/// <summary>JSON file per plugin; secrets are encrypted for the current Windows account (DPAPI).</summary>
internal sealed class PluginSettings : IPluginSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _file;
    private readonly JsonObject _data;

    public PluginSettings(string id)
    {
        _file = Path.Combine(PluginManager.DataDirectory, id + ".json");
        try { _data = File.Exists(_file) ? JsonNode.Parse(File.ReadAllText(_file)) as JsonObject ?? new() : new(); }
        catch { _data = new(); }
    }

    public T? Get<T>(string key, T? fallback = default)
    {
        try { return _data[key] is { } node ? node.Deserialize<T>() ?? fallback : fallback; }
        catch { return fallback; }
    }

    public void Set<T>(string key, T value)
    {
        _data[key] = JsonSerializer.SerializeToNode(value);
        Save();
    }

    public string? GetSecret(string key)
    {
        var enc = Get<string>("secret:" + key);
        if (string.IsNullOrEmpty(enc)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public void SetSecret(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) { _data.Remove("secret:" + key); Save(); return; }
        Set("secret:" + key, Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser)));
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, _data.ToJsonString(Options));
        }
        catch (Exception ex) { PluginManager.Log($"settings {_file}: {ex.Message}"); }
    }
}
