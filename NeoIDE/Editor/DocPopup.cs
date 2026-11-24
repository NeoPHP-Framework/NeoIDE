using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NeoIDE.Services;

namespace NeoIDE.Editor;

/// <summary>Builds the quick documentation card shown when hovering a symbol (PhpStorm style).</summary>
public static class DocPopup
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "protected", "private", "static", "function", "const", "readonly", "case", "abstract", "final",
        "class", "interface", "trait", "enum", "extends", "implements", "new", "null", "true", "false",
        "int", "float", "string", "bool", "array", "void", "mixed", "never", "object", "iterable", "callable", "self",
    };

    public static FrameworkElement Build(string description, bool isProblem)
    {
        var lines = description.Replace("\r", "").Split('\n');
        var panel = new StackPanel { MaxWidth = 640 };

        if (isProblem)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = ThemeManager.Brush("ErrorFg"), FontSize = 14, Margin = new Thickness(0, 1, 8, 0) });
            row.Children.Add(new TextBlock { Text = description.Trim(), Foreground = ThemeManager.Brush("Fg"), TextWrapping = TextWrapping.Wrap, MaxWidth = 600, FontSize = 13 });
            panel.Children.Add(row);
            return Card(panel);
        }

        // Split: header lines (before the blank line) / summary (after)
        var blank = Array.FindIndex(lines, l => l.Trim().Length == 0);
        var head = (blank < 0 ? lines : lines[..blank]).Where(l => l.Trim().Length > 0).ToList();
        var summary = blank < 0 ? "" : string.Join(" ", lines[(blank + 1)..].Select(l => l.Trim())).Trim();

        // A member description starts with the declaring class (a bare FQN line)
        if (head.Count >= 2 && Regex.IsMatch(head[0], @"^[\w\\]+$"))
        {
            var fqn = head[0];
            var shortName = fqn.Split('\\').Last();
            var ns = fqn.Contains('\\') ? fqn[..fqn.LastIndexOf('\\')] : "";
            var owner = new TextBlock { Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis };
            owner.Inlines.Add(new Run(shortName) { Foreground = ThemeManager.Brush("LinkFg"), FontWeight = FontWeights.SemiBold });
            if (ns.Length > 0) owner.Inlines.Add(new Run("   " + ns) { Foreground = ThemeManager.Brush("FgSecondary"), FontSize = 11.5 });
            panel.Children.Add(owner);
            head.RemoveAt(0);
        }

        foreach (var line in head) panel.Children.Add(CodeLine(line));

        if (summary.Length > 0)
        {
            panel.Children.Add(new Border { Height = 1, Background = ThemeManager.Brush("PopupBorder"), Margin = new Thickness(0, 8, 0, 8) });
            panel.Children.Add(new TextBlock
            {
                Text = summary, TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("Fg"), FontSize = 13, LineHeight = 19,
            });
        }
        return Card(panel);
    }

    private static Border Card(UIElement child)
    {
        var card = new Border
        {
            Background = ThemeManager.Brush("PopupBg"),
            BorderBrush = ThemeManager.Brush("PopupBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 11),
            Margin = new Thickness(8, 4, 12, 14), // room for the shadow
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = ThemeManager.IsDark ? 0.5 : 0.18, Direction = 270 },
            Child = child,
        };
        TextElement.SetFontFamily(card, new FontFamily("Segoe UI"));
        return card;
    }

    /// <summary>A signature line with light syntax colouring.</summary>
    private static TextBlock CodeLine(string line)
    {
        var tb = new TextBlock
        {
            FontFamily = DisplayManager.EditorFont, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1),
            Foreground = new SolidColorBrush(EditorPalette.Get("Default").Color),
        };
        Brush Color(string token) => new SolidColorBrush(EditorPalette.Get(token).Color);
        string? previousWord = null;
        foreach (Match m in Regex.Matches(line, @"\$\w+|[A-Za-z_\\][\w\\]*|'[^']*'|""[^""]*""|\d+|\s+|."))
        {
            var t = m.Value;
            Run run;
            if (t.StartsWith("$")) run = new Run(t) { Foreground = Color("Variable") };
            else if (t.StartsWith("'") || t.StartsWith("\"")) run = new Run(t) { Foreground = Color("String") };
            else if (char.IsDigit(t[0])) run = new Run(t) { Foreground = Color("Number") };
            else if (Keywords.Contains(t)) run = new Run(t) { Foreground = Color("Keyword") };
            else if (previousWord is "function" or "case" or "const") run = new Run(t) { Foreground = Color("Function"), FontWeight = FontWeights.SemiBold };
            else if (previousWord is "class" or "interface" or "trait" or "enum") run = new Run(t) { FontWeight = FontWeights.SemiBold };
            else run = new Run(t);
            if (!string.IsNullOrWhiteSpace(t) && (char.IsLetter(t[0]) || t[0] == '_')) previousWord = t.ToLowerInvariant();
            tb.Inlines.Add(run);
        }
        return tb;
    }
}
