using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;

namespace NeoIDE.Editor;

/// <summary>
/// JSON format of the NeoIDE themes (see neo-ide-marketplace/schemas/theme.schema.json):
/// { id, name, version, author, description, dark, ui: {key: "#hex"}, editor: {...}, syntax: {token: "#hex" | {color, bold, italic}} }.
/// Missing keys fall back to the defaults of the dark / light family, so a theme can stay small.
/// </summary>
public static class ThemeJson
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/NeoPHP-Framework/neo-ide-marketplace/main/schemas/theme.schema.json";

    private static readonly string[] EditorKeys =
        { "background", "currentLine", "selection", "lineNumbers", "activeLineNumber", "unused", "errorUnderline", "bracketMatch", "searchHighlight", "caret" };

    private static string Hex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

    private static Color Parse(string? s, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        try
        {
            var h = s.Trim();
            // #RRGGBBAA (CSS order) → WPF #AARRGGBB
            if (h.Length == 9 && h[0] == '#') h = "#" + h[7..9] + h[1..7];
            return (Color)ColorConverter.ConvertFromString(h);
        }
        catch { return fallback; }
    }

    public static string ToJson(ThemeDef t)
    {
        var ui = new JsonObject();
        foreach (var (k, v) in t.Ui.OrderBy(k => k.Key)) ui[k] = Hex(v);
        var e = t.Editor;
        var editor = new JsonObject
        {
            ["background"] = Hex(e.Background), ["currentLine"] = Hex(e.CurrentLine), ["selection"] = Hex(e.Selection),
            ["lineNumbers"] = Hex(e.LineNumbers), ["activeLineNumber"] = Hex(e.ActiveLineNumber), ["unused"] = Hex(e.Unused),
            ["errorUnderline"] = Hex(e.ErrorUnderline), ["bracketMatch"] = Hex(e.BracketMatch), ["searchHighlight"] = Hex(e.SearchHighlight), ["caret"] = Hex(e.Caret),
        };
        var syntax = new JsonObject();
        foreach (var name in EditorPalette.Names)
        {
            if (!t.Syntax.TryGetValue(name, out var s)) continue;
            syntax[name] = s.Bold || s.Italic
                ? new JsonObject { ["color"] = Hex(s.Color), ["bold"] = s.Bold, ["italic"] = s.Italic }
                : Hex(s.Color);
        }
        var root = new JsonObject
        {
            ["$schema"] = SchemaUrl,
            ["id"] = t.Id, ["name"] = t.Name, ["version"] = t.Version, ["author"] = t.Author, ["description"] = t.Description,
            ["dark"] = t.IsDark, ["ui"] = ui, ["editor"] = editor, ["syntax"] = syntax,
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2 }) + "\n";
    }

    /// <summary>Reads a theme; <paramref name="baseDark"/> / <paramref name="baseLight"/> provide the missing values.</summary>
    public static ThemeDef FromJson(string json, ThemeDef? baseDark, ThemeDef? baseLight, string source, string? path)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new FormatException("Empty theme file");
        string Str(string k) => root[k]?.GetValue<string>() ?? "";
        var name = Str("name");
        if (name.Length == 0) throw new FormatException("The theme has no \"name\"");
        var id = Str("id");
        if (id.Length == 0) id = string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        var dark = root["dark"]?.GetValue<bool>() ?? true;
        var b = dark ? baseDark : baseLight;

        var ui = b != null ? new Dictionary<string, Color>(b.Ui) : new Dictionary<string, Color>();
        if (root["ui"] is JsonObject u)
            foreach (var (k, v) in u) ui[k] = Parse(v?.GetValue<string>(), ui.TryGetValue(k, out var old) ? old : Colors.Magenta);

        var ed = root["editor"] as JsonObject;
        var be = b?.Editor;
        Color E(string key, Color? fallback) => Parse(ed?[key]?.GetValue<string>(), fallback ?? Colors.Magenta);
        var editor = new EditorChrome(
            E("background", be?.Background), E("currentLine", be?.CurrentLine), E("selection", be?.Selection), E("lineNumbers", be?.LineNumbers),
            E("activeLineNumber", be?.ActiveLineNumber), E("unused", be?.Unused), E("errorUnderline", be?.ErrorUnderline), E("bracketMatch", be?.BracketMatch),
            E("searchHighlight", be?.SearchHighlight), E("caret", be?.Caret));

        var syntax = new Dictionary<string, TokenStyle>(b?.Syntax ?? (dark ? EditorPalette.BaseDark : EditorPalette.BaseLight));
        if (root["syntax"] is JsonObject s)
            foreach (var (k, v) in s)
            {
                var old = syntax.TryGetValue(k, out var o) ? o : new TokenStyle(Colors.Magenta);
                if (v is JsonValue) syntax[k] = old with { Color = Parse(v.GetValue<string>(), old.Color) };
                else if (v is JsonObject so)
                    syntax[k] = new TokenStyle(Parse(so["color"]?.GetValue<string>(), old.Color),
                        so["bold"]?.GetValue<bool>() ?? old.Bold, so["italic"]?.GetValue<bool>() ?? old.Italic);
            }
        if (!syntax.ContainsKey("Default")) syntax["Default"] = new TokenStyle(dark ? Colors.Gainsboro : Colors.Black);
        // a theme only defining the editor still gets a usable UI
        foreach (var k in new[] { "EditorBg", "TabActiveBg", "TabBarBg", "ConsoleBg", "WelcomeMainBg" }) ui.TryAdd(k, editor.Background);

        return new ThemeDef(name, dark, ui, editor, syntax)
        {
            Id = id, Version = Str("version") is { Length: > 0 } v0 ? v0 : "1.0.0", Author = Str("author"), Description = Str("description"),
            Source = source, FilePath = path,
        };
    }
}
