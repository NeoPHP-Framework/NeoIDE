using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;

namespace NeoIDE.Services;

/// <summary>
/// Appearance &amp; display settings applied live: IDE zoom, UI font size, compact mode, editor font and options.
/// </summary>
public static class DisplayManager
{
    public static event Action? Changed;

    public static readonly double[] ZoomLevels = { 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0 };

    private static AppSettings S => AppSettings.Current;

    /// <summary>Publishes the resources used by the styles (font size, row heights...).</summary>
    public static void ApplyResources()
    {
        var res = Application.Current.Resources;
        res["UiFontSize"] = S.UiFontSize;
        res["TreeRowHeight"] = S.CompactMode ? 20.0 : 24.0;
        res["EditorTabHeight"] = S.CompactMode ? 30.0 : 36.0;
        res["MenuItemHeight"] = S.CompactMode ? 22.0 : 26.0;
    }

    /// <summary>Applies the settings everywhere and notifies windows and editors.</summary>
    public static void Apply(bool save = true)
    {
        S.UiScale = Math.Clamp(S.UiScale, 0.8, 2.0);
        S.UiFontSize = Math.Clamp(S.UiFontSize, 10, 20);
        S.EditorFontSize = Math.Clamp(S.EditorFontSize, 8, 40);
        ApplyResources();
        if (save) S.Save();
        Changed?.Invoke();
    }

    /// <summary>Scales a window content with the IDE zoom and keeps its caption height in sync.</summary>
    public static void Attach(Window window, double captionHeight)
    {
        void Update()
        {
            if (window.Content is FrameworkElement content)
            {
                var target = content is System.Windows.Controls.Border { Child: FrameworkElement child } ? child : content;
                target.LayoutTransform = Math.Abs(S.UiScale - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(S.UiScale, S.UiScale);
            }
            var chrome = WindowChrome.GetWindowChrome(window);
            if (chrome != null) chrome.CaptionHeight = captionHeight * S.UiScale;
        }
        window.Loaded += (_, _) => Update();
        Action handler = Update;
        Changed += handler;
        window.Closed += (_, _) => Changed -= handler;
        if (window.IsLoaded) Update();
    }

    public static void Zoom(int direction)
    {
        if (direction == 0) S.UiScale = 1.0;
        else
        {
            var idx = Array.FindIndex(ZoomLevels, z => Math.Abs(z - S.UiScale) < 0.01);
            if (idx < 0) idx = Array.IndexOf(ZoomLevels, 1.0);
            S.UiScale = ZoomLevels[Math.Clamp(idx + direction, 0, ZoomLevels.Length - 1)];
        }
        Apply();
    }

    public static string ZoomLabel => $"{S.UiScale * 100:0}%";

    private static List<string>? _monospace;

    /// <summary>Installed fixed-width font families (for the editor font picker).</summary>
    public static List<string> MonospaceFonts()
    {
        if (_monospace != null) return _monospace;
        var list = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                var tf = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                double W(string s) => new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 12, Brushes.Black, 1).WidthIncludingTrailingWhitespace;
                if (Math.Abs(W("iiii") - W("WWWW")) < 0.01 && W("iiii") > 0)
                    list.Add(family.Source);
            }
            catch { }
        }
        foreach (var preferred in new[] { "JetBrains Mono", "Cascadia Mono", "Cascadia Code", "Consolas", "Fira Code", "Source Code Pro" })
            if (!list.Contains(preferred, StringComparer.OrdinalIgnoreCase) && Fonts.SystemFontFamilies.Any(f => f.Source == preferred)) list.Add(preferred);
        _monospace = list.Distinct().OrderBy(f => f).ToList();
        return _monospace;
    }

    /// <summary>Editor font with sensible fallbacks when the chosen font is not installed.</summary>
    public static FontFamily EditorFont => new($"{S.EditorFontFamily}, JetBrains Mono, Cascadia Mono, Consolas");
}
