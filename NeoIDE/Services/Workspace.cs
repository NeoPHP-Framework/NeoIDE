using System.IO;
using NeoIDE.Php;

namespace NeoIDE.Services;

/// <summary>The project currently opened in the IDE and its language services.</summary>
public static class Workspace
{
    public static string? Root { get; private set; }
    public static string? PhpExe { get; set; }
    public static string? PhpVersion { get; set; }
    public static PhpIndex Index { get; private set; } = new();
    public static ProjectSettings Settings { get; private set; } = new();

    /// <summary>Commands of bin/neo (name, description), loaded when the project opens.</summary>
    public static List<(string name, string description)> NeoCommands { get; set; } = new();

    public static string NeoScript => Root != null && File.Exists(Path.Combine(Root, "bin", "neo")) ? "bin/neo" : "vendor/bin/neo";

    public static string Name => Root != null ? Path.GetFileName(Root) : "";

    public static void Open(string root)
    {
        Root = Path.GetFullPath(root);
        Index = new PhpIndex();
        Settings = ProjectSettings.Load(Root);
    }

    public static void SaveSettings()
    {
        if (Root != null) Settings.Save(Root);
    }

    public static PhpCompletion Completion => new(Index, Root);
    public static PhpInsight Insight => new(Index);

    /// <summary>Namespace expected for a directory according to the PSR-4 rules of composer.json.</summary>
    public static string? NamespaceForDirectory(string dir)
    {
        if (Root == null) return null;
        var best = ("", "");
        var bestLen = -1;
        foreach (var (prefix, rel) in ComposerService.ReadPsr4(Root))
        {
            var full = Path.GetFullPath(Path.Combine(Root, rel)).TrimEnd('\\', '/');
            var d = Path.GetFullPath(dir).TrimEnd('\\', '/');
            if ((d.Equals(full, StringComparison.OrdinalIgnoreCase) || d.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) && full.Length > bestLen)
            {
                bestLen = full.Length;
                best = (prefix, full);
            }
        }
        if (bestLen < 0) return null;
        var sub = Path.GetRelativePath(best.Item2, dir);
        var ns = best.Item1.TrimEnd('\\');
        if (sub != ".") ns += "\\" + sub.Replace('/', '\\').Replace(Path.DirectorySeparatorChar, '\\');
        return ns.Trim('\\');
    }
}
