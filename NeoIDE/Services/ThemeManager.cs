using System.Windows;
using System.Windows.Media;
using NeoIDE.Editor;

namespace NeoIDE.Services;

/// <summary>Light/dark family of a theme (used by the quick light/dark toggle).</summary>
public enum IdeTheme { Dark, Light }

/// <summary>Applies a theme of the <see cref="ThemeCatalog"/> at runtime and notifies the editors.</summary>
public static class ThemeManager
{
    public static event Action? ThemeChanged;

    public static ThemeDef CurrentTheme => ThemeCatalog.Current;
    public static string CurrentName => ThemeCatalog.Current.Name;
    public static IdeTheme Current => ThemeCatalog.Current.IsDark ? IdeTheme.Dark : IdeTheme.Light;
    public static bool IsDark => ThemeCatalog.Current.IsDark;

    private const string MarkerKey = "__NeoThemeName";

    /// <summary>Quick toggle: switches to the last used theme of the other family.</summary>
    public static void Apply(IdeTheme family) =>
        Apply(family == IdeTheme.Dark ? AppSettings.Current.LastDarkTheme : AppSettings.Current.LastLightTheme);

    public static void Apply(string name)
    {
        var theme = ThemeCatalog.Get(name);
        ThemeCatalog.SetCurrent(theme);

        // Live brushes: the same instances are kept for the whole session and only their colour changes,
        // so UI built in code with ThemeManager.Brush(...) follows the theme without being rebuilt.
        // (a brush that got frozen — e.g. used in a code-built Style — is replaced: only that consumer stays stale)
        foreach (var (key, color) in theme.Ui)
        {
            if (LiveBrushes.TryGetValue(key, out var brush) && !brush.IsFrozen) brush.Color = color;
            else LiveBrushes[key] = new SolidColorBrush(color);
        }

        // XAML styles use DynamicResource on their own (frozen) copies: replace the dictionary
        var dict = new ResourceDictionary { [MarkerKey] = theme.Name };
        foreach (var (key, color) in theme.Ui)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            dict[key] = b;
        }
        // the scrollbar corner of ScrollViewers uses the system "control" colour (white square otherwise)
        var corner = new SolidColorBrush(theme.Ui["PanelBg"]);
        corner.Freeze();
        dict[SystemColors.ControlBrushKey] = corner;
        var dicts = Application.Current.Resources.MergedDictionaries;
        var existing = dicts.FirstOrDefault(d => d.Contains(MarkerKey) ||
            (d.Source != null && (d.Source.OriginalString.EndsWith("Dark.xaml") || d.Source.OriginalString.EndsWith("Light.xaml"))));
        if (existing != null) dicts[dicts.IndexOf(existing)] = dict;
        else dicts.Insert(0, dict);

        var s = AppSettings.Current;
        s.Theme = theme.Id;
        if (theme.IsDark) s.LastDarkTheme = theme.Id; else s.LastLightTheme = theme.Id;
        s.Save();
        SyntaxRegistry.ApplyTheme();
        ThemeChanged?.Invoke();
    }

    private static readonly Dictionary<string, SolidColorBrush> LiveBrushes = new();

    public static Brush Brush(string key) =>
        LiveBrushes.TryGetValue(key, out var live) ? live : Application.Current.TryFindResource(key) as Brush ?? Brushes.Magenta;

    public static Color Color(string key) => (Brush(key) as SolidColorBrush)?.Color ?? Colors.Magenta;
}
