using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NeoIDE.Sdk;

/// <summary>
/// Small builders with the NeoIDE look. Colours are bound to the theme resources (DynamicResource), so the views
/// follow a theme change. Keys: WindowBg, PanelBg, EditorBg, Fg, FgSecondary, Accent, SubtleBorder, SeparatorBrush,
/// HoverBg, SelectedBg, InputBg, ErrorFg, SuccessFg, WarnFg, LinkFg, PopupBg…
/// </summary>
public static class Ui
{
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Mono = new("JetBrains Mono, Cascadia Mono, Consolas");

    public static T Themed<T>(this T element, DependencyProperty property, string key) where T : FrameworkElement
    {
        element.SetResourceReference(property, key);
        return element;
    }

    public static Style? Style(string key) => Application.Current?.TryFindResource(key) as Style;

    public static TextBlock Icon(string glyph, double size = 12, string? colorKey = null)
    {
        var t = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (colorKey != null) t.Themed(TextBlock.ForegroundProperty, colorKey);
        return t;
    }

    public static Button ToolButton(string glyph, string tooltip, Action onClick, double size = 12)
    {
        var b = new Button { Content = Icon(glyph, size), ToolTip = tooltip, Style = Style("ToolButton"), Margin = new Thickness(1, 0, 1, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Button(string text, Action onClick, bool primary = false)
    {
        var b = new Button { Content = text, MinWidth = 0, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 6, 0) };
        if (primary && Style("PrimaryButton") is { } s) b.Style = s;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static TextBlock Text(string text, bool secondary = false, double? size = null)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        t.Themed(TextBlock.ForegroundProperty, secondary ? "FgSecondary" : "Fg");
        if (size != null) t.FontSize = size.Value;
        return t;
    }

    public static TextBlock Title(string text) => new TextBlock
    {
        Text = text, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
    }.Themed(TextBlock.ForegroundProperty, "Fg");

    /// <summary>Header bar of a tool window: title on the left, buttons on the right.</summary>
    public static Border Header(string title, params UIElement[] buttons)
    {
        var dock = new DockPanel { Height = 34, Margin = new Thickness(10, 0, 6, 0), LastChildFill = true };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in buttons) right.Children.Add(b);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(right);
        dock.Children.Add(Title(title));
        return new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = dock }.Themed(Border.BorderBrushProperty, "SubtleBorder");
    }

    /// <summary>Horizontal bar of controls.</summary>
    public static Border Toolbar(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
        foreach (var i in items) p.Children.Add(i);
        return new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = p }.Themed(Border.BorderBrushProperty, "SubtleBorder");
    }

    public static Border Separator() => new Border { Width = 1, Margin = new Thickness(6, 4, 6, 4) }.Themed(Border.BackgroundProperty, "SubtleBorder");

    /// <summary>Coloured label (HTTP method, log level, state…).</summary>
    public static Border Badge(string text, Color color, double minWidth = 0) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center, MinWidth = minWidth,
        Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(color), FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
    };

    public static TextBlock Empty(string message) => new TextBlock
    {
        Text = message, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(20),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    }.Themed(TextBlock.ForegroundProperty, "FgSecondary");

    public static TextBox TextBox(string text = "", double width = double.NaN, string? tooltip = null) => new()
    {
        Text = text, Width = width, ToolTip = tooltip, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
    };

    /// <summary>Read-only grid of results (style of the IDE, virtualised).</summary>
    public static DataGrid DataGrid(bool autoColumns = true) => new()
    {
        AutoGenerateColumns = autoColumns, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, FontFamily = Mono, FontSize = 12.5,
    };

    /// <summary>Parses #RRGGBB.</summary>
    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
