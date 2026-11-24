using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeoIDE.Services;

/// <summary>Locates Composer and builds command lines that run it with a chosen PHP interpreter.</summary>
public static class ComposerService
{
    private static string? _pharPath;
    private static bool _searched;

    public static string? FindComposerPhar()
    {
        if (_searched) return _pharPath;
        _searched = true;
        var candidates = new List<string>
        {
            @"C:\laragon\bin\composer\composer.phar",
            @"C:\ProgramData\ComposerSetup\bin\composer.phar",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Composer", "composer.phar"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var d = dir.Trim();
                if (d.Length == 0) continue;
                candidates.Add(Path.Combine(d, "composer.phar"));
            }
            catch { }
        }
        _pharPath = candidates.FirstOrDefault(File.Exists);
        return _pharPath;
    }

    /// <summary>Returns (exe, args) running composer with the given PHP (falls back to the composer shim).</summary>
    public static (string exe, string args) Command(string? phpExe, string composerArgs)
    {
        var phar = FindComposerPhar();
        if (phar != null && phpExe != null)
            return (phpExe, $"{ProcessRunner.Quote(phar)} {composerArgs} --no-ansi");
        return ("cmd.exe", $"/c composer {composerArgs} --no-ansi");
    }

    public static async Task<List<string>> GetPackageVersionsAsync(string? phpExe, string package)
    {
        var (exe, args) = Command(phpExe, $"show {package} --all --format=json");
        var (code, stdout, _) = await ProcessRunner.RunAsync(exe, args, Path.GetTempPath(), 60000);
        if (code != 0) return new List<string>();
        try
        {
            var json = JsonNode.Parse(stdout);
            return json?["versions"]?.AsArray().Select(v => v!.GetValue<string>()).ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Updates the composer.json of a freshly created project.</summary>
    public static void CustomizeComposerJson(string projectDir, string packageName, string description,
        string phpConstraint, string platformPhp, string? authorName, string? authorEmail)
    {
        var file = Path.Combine(projectDir, "composer.json");
        if (!File.Exists(file)) return;
        var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();

        // Rebuild in Composer's usual order: name, description, type, license, authors, require...
        var ordered = new JsonObject { ["name"] = packageName, ["description"] = description };
        foreach (var kv in root)
        {
            if (kv.Key is "name" or "description" or "authors") continue;
            if (kv.Key == "require" && !string.IsNullOrWhiteSpace(authorName))
            {
                var author = new JsonObject { ["name"] = authorName };
                if (!string.IsNullOrWhiteSpace(authorEmail)) author["email"] = authorEmail;
                ordered["authors"] = new JsonArray(author);
            }
            ordered[kv.Key] = kv.Value?.DeepClone();
        }
        var require = ordered["require"] as JsonObject ?? new JsonObject();
        require["php"] = phpConstraint;
        ordered["require"] = require;

        var config = ordered["config"] as JsonObject ?? new JsonObject();
        config["platform"] = new JsonObject { ["php"] = platformPhp };
        config["sort-packages"] = true;
        ordered["config"] = config;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentSize = 4,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(file, ordered.ToJsonString(options).Replace("\r\n", "\n") + "\n");
    }

    /// <summary>Reads PSR-4 mappings (namespace prefix → directories) from composer.json.</summary>
    public static List<(string prefix, string dir)> ReadPsr4(string projectDir)
    {
        var list = new List<(string, string)>();
        var file = Path.Combine(projectDir, "composer.json");
        if (!File.Exists(file)) return list;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(file));
            foreach (var section in new[] { "autoload", "autoload-dev" })
            {
                if (root?[section]?["psr-4"] is not JsonObject psr4) continue;
                foreach (var kv in psr4)
                {
                    if (kv.Value is JsonArray arr)
                        foreach (var d in arr) list.Add((kv.Key, d!.GetValue<string>()));
                    else if (kv.Value != null)
                        list.Add((kv.Key, kv.Value.GetValue<string>()));
                }
            }
        }
        catch { }
        return list;
    }
}
