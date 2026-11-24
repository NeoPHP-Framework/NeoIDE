using System.Windows;
using System.Windows.Media;
using NeoIDE.Services;

namespace NeoIDE.Editor;

public readonly record struct TokenStyle(Color Color, bool Bold = false, bool Italic = false);

/// <summary>Syntax colour schemes reproducing PhpStorm's "Dark" and "Light" editor schemes.</summary>
public static class EditorPalette
{
    public static readonly string[] Names =
    {
        "Default", "Keyword", "String", "StringEscape", "Number", "Comment", "DocComment", "DocTag",
        "Variable", "Property", "Constant", "Function", "FunctionCall", "StaticCall", "Type", "Metadata",
        "Tag", "Attribute", "AttributeValue", "Entity", "Delimiter", "Selector", "CssProperty", "CssValue",
        "Key", "JsonKey", "Heading", "Bold", "Italic", "Link", "Code", "Operator", "Punctuation", "Builtin",
        "Inserted", "Deleted",
    };

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static readonly Dictionary<string, TokenStyle> Dark = new()
    {
        ["Default"] = new(C("#BCBEC4")),
        ["Keyword"] = new(C("#CF8E6D")),
        ["String"] = new(C("#6AAB73")),
        ["StringEscape"] = new(C("#CF8E6D")),
        ["Number"] = new(C("#2AACB8")),
        ["Comment"] = new(C("#7A7E85"), Italic: true),
        ["DocComment"] = new(C("#5F826B"), Italic: true),
        ["DocTag"] = new(C("#67A37C"), Bold: true, Italic: true),
        ["Variable"] = new(C("#C77DBB")),
        ["Property"] = new(C("#C77DBB")),
        ["Constant"] = new(C("#C77DBB"), Italic: true),
        ["Function"] = new(C("#56A8F5")),
        ["FunctionCall"] = new(C("#57AAF7")),
        ["StaticCall"] = new(C("#57AAF7"), Italic: true),
        ["Type"] = new(C("#BCBEC4")),
        ["Metadata"] = new(C("#B3AE60")),
        ["Tag"] = new(C("#D5B778")),
        ["Attribute"] = new(C("#BABABA")),
        ["AttributeValue"] = new(C("#6AAB73")),
        ["Entity"] = new(C("#CF8E6D")),
        ["Delimiter"] = new(C("#CF8E6D"), Bold: true),
        ["Selector"] = new(C("#D5B778")),
        ["CssProperty"] = new(C("#BCBEC4")),
        ["CssValue"] = new(C("#2AACB8")),
        ["Key"] = new(C("#CF8E6D")),
        ["JsonKey"] = new(C("#C77DBB")),
        ["Heading"] = new(C("#CF8E6D"), Bold: true),
        ["Bold"] = new(C("#BCBEC4"), Bold: true),
        ["Italic"] = new(C("#BCBEC4"), Italic: true),
        ["Link"] = new(C("#548AF7")),
        ["Code"] = new(C("#6AAB73")),
        ["Operator"] = new(C("#BCBEC4")),
        ["Punctuation"] = new(C("#BCBEC4")),
        ["Builtin"] = new(C("#56A8F5")),
        ["Inserted"] = new(C("#6AAB73")),
        ["Deleted"] = new(C("#F75464")),
    };

    private static readonly Dictionary<string, TokenStyle> Light = new()
    {
        ["Default"] = new(C("#080808")),
        ["Keyword"] = new(C("#0033B3")),
        ["String"] = new(C("#067D17")),
        ["StringEscape"] = new(C("#0037A6")),
        ["Number"] = new(C("#1750EB")),
        ["Comment"] = new(C("#8C8C8C"), Italic: true),
        ["DocComment"] = new(C("#8C8C8C"), Italic: true),
        ["DocTag"] = new(C("#8C8C8C"), Bold: true, Italic: true),
        ["Variable"] = new(C("#871094")),
        ["Property"] = new(C("#871094")),
        ["Constant"] = new(C("#871094"), Italic: true),
        ["Function"] = new(C("#00627A")),
        ["FunctionCall"] = new(C("#00627A")),
        ["StaticCall"] = new(C("#00627A"), Italic: true),
        ["Type"] = new(C("#080808")),
        ["Metadata"] = new(C("#9E880D")),
        ["Tag"] = new(C("#0033B3")),
        ["Attribute"] = new(C("#174AD4")),
        ["AttributeValue"] = new(C("#067D17")),
        ["Entity"] = new(C("#0033B3")),
        ["Delimiter"] = new(C("#0033B3"), Bold: true),
        ["Selector"] = new(C("#0033B3")),
        ["CssProperty"] = new(C("#174AD4")),
        ["CssValue"] = new(C("#1750EB")),
        ["Key"] = new(C("#0033B3")),
        ["JsonKey"] = new(C("#871094")),
        ["Heading"] = new(C("#0033B3"), Bold: true),
        ["Bold"] = new(C("#080808"), Bold: true),
        ["Italic"] = new(C("#080808"), Italic: true),
        ["Link"] = new(C("#2E55A3")),
        ["Code"] = new(C("#067D17")),
        ["Operator"] = new(C("#080808")),
        ["Punctuation"] = new(C("#080808")),
        ["Builtin"] = new(C("#00627A")),
        ["Inserted"] = new(C("#067D17")),
        ["Deleted"] = new(C("#C7222D")),
    };

    /// <summary>Base syntax maps the catalog themes start from.</summary>
    internal static Dictionary<string, TokenStyle> BaseDark => Dark;
    internal static Dictionary<string, TokenStyle> BaseLight => Light;

    private static ThemeDef T => ThemeCatalog.Current;

    public static TokenStyle Get(string name)
    {
        var map = T.Syntax;
        return map.TryGetValue(name, out var s) ? s : map["Default"];
    }

    // Editor chrome colours (from the active theme)
    public static Color Background => T.Editor.Background;
    public static Color Foreground => Get("Default").Color;
    public static Color CurrentLine => T.Editor.CurrentLine;
    public static Color Selection => T.Editor.Selection;
    public static Color LineNumbers => T.Editor.LineNumbers;
    public static Color ActiveLineNumber => T.Editor.ActiveLineNumber;
    public static Color Unused => T.Editor.Unused;
    public static Color ErrorUnderline => T.Editor.ErrorUnderline;
    public static Color BracketMatch => T.Editor.BracketMatch;
    public static Color SearchHighlight => T.Editor.SearchHighlight;
    public static Color Caret => T.Editor.Caret;
    public static Color GutterAdded => T.IsDark ? C("#549159") : C("#B5D9B7");
    public static Color GutterModified => T.IsDark ? C("#375FAD") : C("#A8C3F0");
    public static Color GutterDeleted => T.IsDark ? C("#868A91") : C("#C9CCD6");
}
