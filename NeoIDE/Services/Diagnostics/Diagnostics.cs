using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NeoIDE.Services.Diagnostics;

public enum Severity { Error, Warning, Info }

/// <summary>
/// A problem of a file. <see cref="Line"/> / <see cref="Column"/> are 1-based; when <see cref="Start"/> is set
/// (analysis of the editor buffer) it wins over the line / column (results of external tools on the saved file).
/// </summary>
public sealed record Diagnostic(string File, int Line, int Column, Severity Severity, string Message, string Source)
{
    public int Start { get; init; } = -1;
    public int Length { get; init; }
    /// <summary>PHPStan identifier, NeoPHP rule... (shown in the Problems panel).</summary>
    public string? Code { get; init; }
}

/// <summary>
/// Problems of the project, by file and by source ("php" lint, "phpstan", "neophp" checks, "ide" analysis).
/// The editors and the Problems tool window listen to <see cref="Changed"/>.
/// </summary>
public static class DiagnosticsStore
{
    private static readonly Dictionary<string, Dictionary<string, List<Diagnostic>>> Files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>A file's problems changed (path, or null when everything was cleared).</summary>
    public static event Action<string?>? Changed;

    public static void Set(string file, string source, IReadOnlyList<Diagnostic> list)
    {
        file = Normalize(file);
        lock (Gate)
        {
            if (!Files.TryGetValue(file, out var bySource))
            {
                if (list.Count == 0) return;
                Files[file] = bySource = new Dictionary<string, List<Diagnostic>>();
            }
            if (bySource.TryGetValue(source, out var old) && old.SequenceEqual(list)) return;
            if (list.Count == 0) bySource.Remove(source); else bySource[source] = list.ToList();
            if (bySource.Count == 0) Files.Remove(file);
        }
        Changed?.Invoke(file);
    }

    /// <summary>Replaces the problems of a source for a whole set of files (project inspection).</summary>
    public static void SetProject(string source, IEnumerable<Diagnostic> all)
    {
        var groups = all.GroupBy(d => Normalize(d.File), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        List<string> touched;
        lock (Gate)
        {
            touched = Files.Where(f => f.Value.ContainsKey(source)).Select(f => f.Key).Union(groups.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        }
        foreach (var f in touched) Set(f, source, groups.TryGetValue(f, out var l) ? l : new List<Diagnostic>());
    }

    public static List<Diagnostic> For(string file)
    {
        file = Normalize(file);
        lock (Gate)
            return Files.TryGetValue(file, out var bySource) ? bySource.Values.SelectMany(l => l).ToList() : new List<Diagnostic>();
    }

    public static List<Diagnostic> All()
    {
        lock (Gate) return Files.Values.SelectMany(s => s.Values).SelectMany(l => l).ToList();
    }

    public static (int errors, int warnings) Count(string? file = null)
    {
        var list = file == null ? All() : For(file);
        return (list.Count(d => d.Severity == Severity.Error), list.Count(d => d.Severity == Severity.Warning));
    }

    public static void Clear()
    {
        lock (Gate) Files.Clear();
        Changed?.Invoke(null);
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); } catch { return path; }
    }
}

/// <summary>External checkers: <c>php -l</c> on the editor buffer, PHPStan on saved files or the whole project.</summary>
public static class Checkers
{
    private static readonly string LintDir = Path.Combine(Path.GetTempPath(), "neoide-lint");

    /// <summary>Syntax check of a PHP buffer (not saved yet): <c>php -l</c> on a temporary copy.</summary>
    public static async Task<List<Diagnostic>> LintAsync(string file, string text)
    {
        var list = new List<Diagnostic>();
        if (Workspace.PhpExe == null) return list;
        Directory.CreateDirectory(LintDir);
        var tmp = Path.Combine(LintDir, $"{Environment.ProcessId}-{(uint)file.ToLowerInvariant().GetHashCode():x8}.php");
        await File.WriteAllTextAsync(tmp, text);
        var (code, stdout, stderr) = await ProcessRunner.RunAsync(Workspace.PhpExe,
            $"-n -l -d display_errors=1 -d error_reporting=-1 -d log_errors=0 \"{tmp}\"", Path.GetDirectoryName(file), 10000);
        if (code == 0) return list;
        foreach (Match m in Regex.Matches(stdout + "\n" + stderr, @"(?:PHP\s+)?(?<kind>Parse error|Fatal error|Deprecated|Warning):\s*(?<msg>.+?) in .+? on line (?<line>\d+)"))
        {
            var severity = m.Groups["kind"].Value is "Deprecated" or "Warning" ? Severity.Warning : Severity.Error;
            var msg = m.Groups["msg"].Value.Trim();
            if (list.Any(d => d.Message == msg)) continue;
            list.Add(new Diagnostic(file, int.Parse(m.Groups["line"].Value), 0, severity, msg, "php"));
        }
        return list;
    }

    public static string? PhpStanBinary(string root)
    {
        var p = Path.Combine(root, "vendor", "phpstan", "phpstan", "phpstan");
        return File.Exists(p) ? p : null;
    }

    public static string? PhpStanConfig(string root) =>
        new[] { "phpstan.neon", "phpstan.neon.dist", "phpstan.dist.neon" }.Select(f => Path.Combine(root, f)).FirstOrDefault(File.Exists);

    /// <summary>
    /// PHPStan on <paramref name="paths"/> (files or directories; the project paths of phpstan.neon when empty).
    /// Without a configuration file the level of the settings is used.
    /// </summary>
    public static async Task<(List<Diagnostic> list, string? error)> PhpStanAsync(IReadOnlyList<string> paths, int timeoutMs = 180000)
    {
        var root = Workspace.Root;
        var list = new List<Diagnostic>();
        if (root == null || Workspace.PhpExe == null) return (list, "No PHP interpreter");
        var bin = PhpStanBinary(root);
        if (bin == null) return (list, "PHPStan is not installed (composer require --dev phpstan/phpstan)");
        var config = PhpStanConfig(root);
        var targets = paths.Count > 0 ? paths : config == null ? new[] { Path.Combine(root, "src") } : Array.Empty<string>();
        var args = $"-d memory_limit=1G \"{bin}\" analyse --error-format=json --no-progress --no-interaction";
        if (config == null) args += $" --level={AppSettings.Current.PhpStanLevel}";
        foreach (var t in targets) args += $" \"{t}\"";
        var (code, stdout, stderr) = await ProcessRunner.RunAsync(Workspace.PhpExe, args, root, timeoutMs);
        var json = stdout.Trim();
        var brace = json.IndexOf('{');
        if (brace < 0) return (list, (stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim()).Split('\n').FirstOrDefault() ?? $"PHPStan exited with {code}");
        try
        {
            using var doc = JsonDocument.Parse(json[brace..]);
            if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
                foreach (var f in files.EnumerateObject())
                {
                    if (!f.Value.TryGetProperty("messages", out var messages)) continue;
                    foreach (var m in messages.EnumerateArray())
                    {
                        var line = m.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 1;
                        var text = m.TryGetProperty("message", out var t) ? t.GetString() ?? "" : "";
                        var tip = m.TryGetProperty("tip", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;
                        var id = m.TryGetProperty("identifier", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                        var path = Regex.Replace(f.Name, @"\s*\(in context of .*\)$", "");
                        list.Add(new Diagnostic(path, line, 0, Severity.Error, text + (tip != null ? "\n💡 " + Regex.Replace(tip, "<[^>]+>", "") : ""), "phpstan") { Code = id });
                    }
                }
            string? error = null;
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                error = errors[0].GetString();
            return (list, error);
        }
        catch (Exception ex) { return (list, "Cannot read the PHPStan output: " + ex.Message); }
    }
}
