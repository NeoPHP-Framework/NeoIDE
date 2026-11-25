using System.IO;
using System.Text.RegularExpressions;

namespace NeoIDE.Services;

/// <summary>Indentation of a language (Settings › Code Style).</summary>
public sealed class IndentSetting
{
    public int Size { get; set; } = 4;
    public bool UseTabs { get; set; }
}

/// <summary>The indentation used for a file and where it comes from.</summary>
public sealed record IndentResolved(int Size, bool UseTabs, string Source)
{
    public string Unit => UseTabs ? "\t" : new string(' ', Size);
    public string Label => UseTabs ? $"Tab ({Size})" : $"{Size} spaces";
}

/// <summary>
/// Indentation of a file, by priority: .editorconfig of the project, the indentation already used by the file
/// (detected), the setting of its language, the general setting.
/// </summary>
public static class IndentStyle
{
    public static readonly string[] Languages = { "PHP", "Twig", "HTML", "CSS", "JavaScript", "TypeScript", "JSON", "YAML", "XML", "Markdown", "SQL", "Shell" };

    public static IndentResolved For(string? language, string? filePath, string? text, AppSettings? settings = null)
    {
        var s = settings ?? AppSettings.Current;
        IndentSetting? lang = language != null && s.LanguageIndent != null && s.LanguageIndent.TryGetValue(language, out var l) ? l : null;
        var fallbackSize = lang?.Size ?? s.IndentSize;
        if (s.UseEditorConfig && filePath != null && EditorConfig.IndentFor(filePath, fallbackSize) is { } ec) return ec;
        if (s.DetectIndent && text != null && Detect(text, fallbackSize) is { } detected) return detected;
        if (lang != null) return new IndentResolved(Math.Clamp(lang.Size, 1, 16), lang.UseTabs, language!);
        return new IndentResolved(Math.Clamp(s.IndentSize, 1, 16), s.UseTabs, "general");
    }

    /// <summary>
    /// The indentation already used by a text: tabs when most indented lines start with one, else the most common
    /// difference of indentation between a line and the next one (2, 3, 4, 8). Null when there are too few
    /// indented lines to tell.
    /// </summary>
    public static IndentResolved? Detect(string text, int tabSize = 4)
    {
        int tabs = 0, spaces = 0;
        var steps = new Dictionary<int, int>();
        int previous = 0;
        bool inBlockComment = false;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n').Take(4000))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;
            var trimmed = line.TrimStart();
            // doc comment lines (" * ") have one more space: not an indentation
            if (trimmed.StartsWith("*") || inBlockComment) { inBlockComment = !trimmed.Contains("*/") && (inBlockComment || trimmed.StartsWith("/*")); continue; }
            if (trimmed.StartsWith("/*") && !trimmed.Contains("*/")) inBlockComment = true;
            if (line[0] == '\t') { tabs++; continue; }
            var indent = line.Length - trimmed.Length;
            if (indent > 0) spaces++;
            var diff = indent - previous;
            if (diff is 2 or 3 or 4 or 8) steps[diff] = steps.GetValueOrDefault(diff) + 1;
            previous = indent;
        }
        if (tabs + spaces < 3) return null;
        if (tabs > spaces) return new IndentResolved(tabSize, true, "detected");
        if (steps.Count == 0) return null;
        var best = steps.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
        // 8 = two levels of 4 most of the time
        var size = best.Key == 8 && steps.GetValueOrDefault(4) > 0 ? 4 : best.Key;
        return new IndentResolved(size, false, "detected");
    }
}

/// <summary>The indent_style / indent_size / tab_width of the .editorconfig files above a file (https://editorconfig.org).</summary>
public static class EditorConfig
{
    private static readonly Dictionary<string, (DateTime time, List<(string glob, Dictionary<string, string> props)> sections, bool root)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IndentResolved? IndentFor(string filePath, int fallbackSize = 4)
    {
        var props = PropertiesFor(filePath);
        if (props.Count == 0) return null;
        var style = props.GetValueOrDefault("indent_style");
        var sizeText = props.GetValueOrDefault("indent_size");
        var tabWidthText = props.GetValueOrDefault("tab_width");
        if (style == null && sizeText == null) return null;
        int.TryParse(tabWidthText, out var tabWidth);
        int size = int.TryParse(sizeText, out var n) ? n : sizeText == "tab" && tabWidth > 0 ? tabWidth : tabWidth > 0 ? tabWidth : fallbackSize;
        return new IndentResolved(Math.Clamp(size, 1, 16), style == "tab", ".editorconfig");
    }

    /// <summary>The properties that apply to a file (the closest .editorconfig wins, then the last matching section).</summary>
    public static Dictionary<string, string> PropertiesFor(string filePath)
    {
        filePath = Path.GetFullPath(filePath);
        var files = new List<string>();
        for (var dir = Path.GetDirectoryName(filePath); dir != null; dir = Path.GetDirectoryName(dir))
        {
            var ec = Path.Combine(dir, ".editorconfig");
            if (!File.Exists(ec)) continue;
            files.Add(ec);
            if (Read(ec).root) break;
        }
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // from the farthest to the closest: the closest overrides
        foreach (var ec in Enumerable.Reverse(files))
        {
            var baseDir = Path.GetDirectoryName(ec)!;
            var rel = Path.GetRelativePath(baseDir, filePath).Replace('\\', '/');
            foreach (var (glob, props) in Read(ec).sections)
                if (Matches(glob, rel))
                    foreach (var (k, v) in props) result[k] = v;
        }
        return result;
    }

    private static (List<(string glob, Dictionary<string, string> props)> sections, bool root) Read(string path)
    {
        var time = File.GetLastWriteTimeUtc(path);
        lock (Cache)
            if (Cache.TryGetValue(path, out var c) && c.time == time) return (c.sections, c.root);
        var sections = new List<(string, Dictionary<string, string>)>();
        bool root = false;
        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections.Add((line[1..^1], current));
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim().ToLowerInvariant();
            if (current == null) { if (key == "root") root = value == "true"; continue; }
            current[key] = value;
        }
        lock (Cache) Cache[path] = (time, sections, root);
        return (sections, root);
    }

    /// <summary>EditorConfig globs: *, **, ?, [abc], {a,b}, {1..3}; without "/" they match the file name at any depth.</summary>
    public static bool Matches(string glob, string relativePath)
    {
        var anyDir = !glob.Contains('/');
        if (glob.StartsWith('/')) glob = glob[1..];
        var re = new System.Text.StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*': re.Append(".*"); i++; break;
                case '*': re.Append("[^/]*"); break;
                case '?': re.Append("[^/]"); break;
                case '[':
                    var end = glob.IndexOf(']', i);
                    if (end < 0) { re.Append("\\["); break; }
                    re.Append('[').Append(glob[(i + 1)..end].Replace("!", "^")).Append(']');
                    i = end;
                    break;
                case '{':
                    var close = glob.IndexOf('}', i);
                    if (close < 0) { re.Append("\\{"); break; }
                    var inner = glob[(i + 1)..close];
                    var range = Regex.Match(inner, @"^(-?\d+)\.\.(-?\d+)$");
                    if (range.Success)
                    {
                        int a = int.Parse(range.Groups[1].Value), b = int.Parse(range.Groups[2].Value);
                        re.Append('(').Append(string.Join("|", Enumerable.Range(Math.Min(a, b), Math.Abs(b - a) + 1))).Append(')');
                    }
                    else re.Append('(').Append(string.Join("|", inner.Split(',').Select(p => Regex.Escape(p).Replace("\\*", "[^/]*")))).Append(')');
                    i = close;
                    break;
                default: re.Append(Regex.Escape(c.ToString())); break;
            }
        }
        var pattern = (anyDir ? "(^|.*/)" : "^") + re + "$";
        return Regex.IsMatch(relativePath, pattern, RegexOptions.IgnoreCase);
    }
}
