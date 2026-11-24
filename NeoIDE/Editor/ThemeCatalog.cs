using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Media;
using NeoIDE.Services;

namespace NeoIDE.Editor;

public sealed record EditorChrome(Color Background, Color CurrentLine, Color Selection, Color LineNumbers, Color ActiveLineNumber,
    Color Unused, Color ErrorUnderline, Color BracketMatch, Color SearchHighlight, Color Caret);

/// <summary>A complete theme: interface colours (brush keys used by the XAML styles), editor chrome and syntax colours.</summary>
public sealed record ThemeDef(string Name, bool IsDark, Dictionary<string, Color> Ui, EditorChrome Editor, Dictionary<string, TokenStyle> Syntax)
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "1.0.0";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>"bundled" (shipped with NeoIDE) or "installed" (from the marketplace / a file).</summary>
    public string Source { get; init; } = "bundled";
    public string? FilePath { get; init; }
    public bool IsBundled => Source == "bundled";

    /// <summary>Colours shown in the theme picker.</summary>
    public Color[] Swatch => new[] { Editor.Background, Syntax["Keyword"].Color, Syntax["String"].Color, Syntax["Function"].Color, Syntax["Number"].Color, Ui["Accent"] };
}

/// <summary>
/// Themes are JSON files: the two defaults (Neo Dark / Neo Light) are shipped inside NeoIDE,
/// the others are installed from the marketplace (or a file) into %APPDATA%\NeoIDE\themes.
/// </summary>
public static class ThemeCatalog
{
    public static string InstallDirectory
    {
        get
        {
            var d = Path.Combine(AppSettings.Directory, "themes");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static IReadOnlyList<ThemeDef> All { get; private set; } = Array.Empty<ThemeDef>();
    public static ThemeDef Current { get; private set; } = null!;
    public static event Action? Changed;

    public const string DefaultDark = "neo-dark";
    public const string DefaultLight = "neo-light";

    static ThemeCatalog() => Reload();

    /// <summary>Theme by id or name (legacy settings stored "Dark" / "Light" / the name).</summary>
    public static ThemeDef Get(string? key)
    {
        if (key == "Light") key = DefaultLight;
        if (key == "Dark") key = DefaultDark;
        return All.FirstOrDefault(t => string.Equals(t.Id, key, StringComparison.OrdinalIgnoreCase))
               ?? All.FirstOrDefault(t => string.Equals(t.Name, key, StringComparison.OrdinalIgnoreCase))
               ?? All.First(t => t.Id == DefaultDark);
    }

    public static ThemeDef? Find(string id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public static void SetCurrent(ThemeDef theme) => Current = theme;

    public static void Reload()
    {
        var list = new List<ThemeDef>();
        ThemeDef? dark = null, light = null;
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(r => r.Contains(".Themes.Bundled.") && r.EndsWith(".json")).OrderBy(r => r.Contains("light")))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var reader = new StreamReader(s, Encoding.UTF8);
            var t = ThemeJson.FromJson(reader.ReadToEnd(), dark, light, "bundled", null);
            if (t.Id == DefaultDark) dark = t;
            if (t.Id == DefaultLight) light = t;
            list.Add(t);
        }
        foreach (var file in Directory.EnumerateFiles(InstallDirectory, "*.json").OrderBy(f => f))
        {
            try
            {
                var t = ThemeJson.FromJson(File.ReadAllText(file), dark, light, "installed", file);
                list.RemoveAll(x => string.Equals(x.Id, t.Id, StringComparison.OrdinalIgnoreCase));
                list.Add(t);
            }
            catch { /* invalid theme file: ignored */ }
        }
        All = list.OrderBy(t => t.IsBundled ? 0 : 1).ThenBy(t => t.IsDark ? 0 : 1).ThenBy(t => t.Name).ToList();
        Current = Current == null ? Get(DefaultDark) : Get(Current.Id);
        Changed?.Invoke();
    }

    /// <summary>Validates and saves a theme JSON (marketplace download / imported file), then reloads the catalog.</summary>
    public static ThemeDef Install(string json)
    {
        var parsed = ThemeJson.FromJson(json, Find(DefaultDark), Find(DefaultLight), "installed", null);
        var file = Path.Combine(InstallDirectory, Sanitize(parsed.Id) + ".json");
        File.WriteAllText(file, json, new UTF8Encoding(false));
        Reload();
        return Find(parsed.Id)!;
    }

    public static void Uninstall(string id)
    {
        var t = Find(id);
        if (t?.FilePath == null) return;
        try { File.Delete(t.FilePath); } catch { }
        Reload();
    }

    private static string Sanitize(string id) => string.Concat(id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
