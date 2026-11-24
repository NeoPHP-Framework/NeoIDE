using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace NeoIDE.Editor;

/// <summary>Loads the embedded .xshd grammars and paints them with the current PhpStorm scheme.</summary>
public static class SyntaxRegistry
{
    private static readonly Dictionary<string, IHighlightingDefinition> Definitions = new();
    private static bool _loaded;
    public static List<string> Errors { get; } = new();

    // Order matters: definitions referenced by others are registered first.
    private static readonly string[] LoadOrder =
        { "CSS", "JavaScript", "HTML", "PHP", "Twig", "JSON", "YAML", "XML", "Markdown", "SQL", "ENV", "Shell", "Ignore" };

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        var htmlFragment = ExtractHtmlFragment(ReadResource("HTML"));

        foreach (var name in LoadOrder)
        {
            try
            {
                var xml = ReadResource(name);
                xml = xml.Replace("<!--COLORS-->", BuildColors());
                if (name == "HTML")
                    xml = xml.Replace("<!--EMBED-->", "");
                else if (name == "PHP")
                    xml = xml.Replace("<!--HTML-->", htmlFragment.Replace("<!--EMBED-->",
                        "<Span ruleSet=\"PhpCode\" multiline=\"true\"><Begin color=\"Delimiter\">&lt;\\?(php|=)?</Begin><End color=\"Delimiter\">\\?&gt;</End></Span>"));
                else if (name == "Twig")
                    xml = xml.Replace("<!--HTML-->", htmlFragment.Replace("<!--EMBED-->",
                        "<Span ruleSet=\"TwigExpr\" multiline=\"true\"><Begin color=\"Delimiter\">\\{\\{-?</Begin><End color=\"Delimiter\">-?\\}\\}</End></Span>" +
                        "<Span ruleSet=\"TwigExpr\" multiline=\"true\"><Begin color=\"Delimiter\">\\{%-?</Begin><End color=\"Delimiter\">-?%\\}</End></Span>"));

                using var reader = XmlReader.Create(new StringReader(xml));
                var xshd = HighlightingLoader.LoadXshd(reader);
                var def = HighlightingLoader.Load(xshd, HighlightingManager.Instance);
                HighlightingManager.Instance.RegisterHighlighting(name, Array.Empty<string>(), def);
                Definitions[name] = def;
            }
            catch (Exception ex)
            {
                Errors.Add($"{name}: {ex.Message}");
                try { File.AppendAllText(Path.Combine(Services.AppSettings.Directory, "errors.log"), $"[{DateTime.Now:O}] Highlighting {name}: {ex}\n\n"); } catch { }
            }
        }
        ApplyTheme();
    }

    private static string ReadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var res = asm.GetManifestResourceNames().First(r => r.EndsWith($".Highlighting.{name}.xshd"));
        using var s = asm.GetManifestResourceStream(res)!;
        using var sr = new StreamReader(s, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    private static string ExtractHtmlFragment(string html)
    {
        var m = Regex.Match(html, "<!--BEGIN-->(.*)<!--END-->", RegexOptions.Singleline);
        return m.Groups[1].Value;
    }

    private static string BuildColors()
    {
        var sb = new StringBuilder();
        foreach (var n in EditorPalette.Names) sb.Append($"<Color name=\"{n}\" foreground=\"#808080\"/>");
        return sb.ToString();
    }

    /// <summary>Repaints every named colour of every grammar with the active scheme.</summary>
    public static void ApplyTheme()
    {
        foreach (var def in Definitions.Values)
        {
            foreach (var color in def.NamedHighlightingColors)
            {
                var style = EditorPalette.Get(color.Name);
                color.Foreground = new SimpleHighlightingBrush(style.Color);
                color.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
                color.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
            }
        }
    }

    public static IHighlightingDefinition? Get(string? language) =>
        language != null && Definitions.TryGetValue(language, out var d) ? d : null;

    /// <summary>Maps a file path to a grammar name.</summary>
    public static string? DetectLanguage(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".dist")) name = name[..^5];
        if (name.StartsWith(".env") || name == "env") return "ENV";
        if (name is ".gitignore" or ".gitattributes" or ".dockerignore" or ".npmignore" or ".prettierignore" or ".eslintignore") return "Ignore";
        if (name is ".htaccess" or "dockerfile" or "makefile" || name.StartsWith("dockerfile")) return "Shell";
        if (name is "composer.lock" or ".babelrc" or ".prettierrc" or ".eslintrc") return "JSON";
        if (name == "neo" || name == "console") return "PHP";
        if (name.EndsWith(".twig")) return "Twig";

        return Path.GetExtension(name) switch
        {
            ".php" or ".phtml" or ".php8" or ".inc" or ".module" => "PHP",
            ".html" or ".htm" or ".xhtml" or ".vue" or ".svelte" => "HTML",
            ".css" or ".scss" or ".sass" or ".less" or ".pcss" => "CSS",
            ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".tsx" or ".mts" => "JavaScript",
            ".json" or ".jsonc" or ".json5" or ".webmanifest" or ".map" => "JSON",
            ".yaml" or ".yml" or ".neon" => "YAML",
            ".xml" or ".svg" or ".xsd" or ".xsl" or ".xliff" or ".xlf" or ".csproj" or ".plist" => "XML",
            ".md" or ".markdown" => "Markdown",
            ".sql" or ".mysql" or ".pgsql" => "SQL",
            ".ini" or ".conf" or ".cfg" or ".properties" or ".toml" or ".editorconfig" => "ENV",
            ".sh" or ".bash" or ".zsh" or ".bat" or ".cmd" or ".ps1" => "Shell",
            _ => null,
        };
    }

    public static string DisplayName(string? language) => language switch
    {
        null => "Plain text",
        "ENV" => ".env / INI",
        "Ignore" => "Ignore file",
        _ => language,
    };
}
