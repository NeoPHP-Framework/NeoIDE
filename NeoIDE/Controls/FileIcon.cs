using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using NeoIDE.Services;

namespace NeoIDE.Controls;

/// <summary>Vector file/folder icon in the spirit of PhpStorm's project view icons.</summary>
public sealed class FileIcon : FrameworkElement
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(FileIcon), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsDirectoryProperty = DependencyProperty.Register(
        nameof(IsDirectory), typeof(bool), typeof(FileIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(FileIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsRootProperty = DependencyProperty.Register(
        nameof(IsRoot), typeof(bool), typeof(FileIcon), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PhpKindProperty = DependencyProperty.Register(
        nameof(PhpKind), typeof(string), typeof(FileIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Kind of the PHP symbol declared in the file (class, interface, trait, enum, abstract) when known by the index.</summary>
    public string? PhpKind { get => (string?)GetValue(PhpKindProperty); set => SetValue(PhpKindProperty, value); }

    public string Path { get => (string)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public bool IsDirectory { get => (bool)GetValue(IsDirectoryProperty); set => SetValue(IsDirectoryProperty, value); }
    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
    public bool IsRoot { get => (bool)GetValue(IsRootProperty); set => SetValue(IsRootProperty, value); }

    public FileIcon()
    {
        Width = 16;
        Height = 16;
        ThemeManager.ThemeChanged += InvalidateVisual;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= InvalidateVisual;
        Loaded += (_, _) => { ThemeManager.ThemeChanged -= InvalidateVisual; ThemeManager.ThemeChanged += InvalidateVisual; };
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private enum Shape { Badge, Circle, Page }

    private record IconSpec(string Text, Color Bg, Color Fg, Shape Shape = Shape.Badge, bool Abstract = false);

    public static Color FolderColor(string path, bool isRoot)
    {
        var dark = ThemeManager.IsDark;
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')).ToLowerInvariant();
        if (isRoot) return dark ? C("#A1A9B7") : C("#6C707E");
        return name switch
        {
            "src" or "lib" or "app" => C("#548AF7"),
            "tests" or "test" or "spec" => C("#5FB865"),
            "vendor" or "var" or "node_modules" or "cache" or "builds" or ".git" or ".idea" or ".neoide" => C("#E08855"),
            "public" or "assets" or "resources" or "translations" => C("#B189F5"),
            "templates" or "views" => C("#D5B778"),
            _ => dark ? C("#8C96A3") : C("#7F8B9B"),
        };
    }

    private static IconSpec? FileSpec(string path, string? phpKind)
    {
        var w = Colors.White;
        switch (phpKind)
        {
            case "class": return new("C", C("#3D8EDB"), w, Shape.Circle);
            case "abstract": return new("C", C("#3D8EDB"), w, Shape.Circle, true);
            case "interface": return new("I", C("#5FB865"), w, Shape.Circle);
            case "trait": return new("T", C("#E0A44C"), w, Shape.Circle);
            case "enum": return new("E", C("#B189F5"), w, Shape.Circle);
        }
        var name = System.IO.Path.GetFileName(path);
        var lower = name.ToLowerInvariant();
        var ext = System.IO.Path.GetExtension(lower);
        var white = Colors.White;
        var black = C("#1E1F22");

        if (lower == "composer.json") return new("C", C("#885630"), white, Shape.Circle);
        if (lower == "composer.lock") return new("lck", C("#7A6A5A"), white);
        if (lower == "package.json") return new("npm", C("#CB3837"), white);
        if (lower.StartsWith(".env")) return new("env", C("#E8C547"), black);
        if (lower is ".gitignore" or ".gitattributes" or ".gitkeep") return new("git", C("#F05033"), white);
        if (lower == ".htaccess") return new("ht", C("#D22128"), white);
        if (lower.StartsWith("dockerfile") || lower.StartsWith("docker-compose")) return new("D", C("#2496ED"), white);
        if (lower is "neo" or "console") return new("N", C("#3574F0"), white, Shape.Circle);
        if (lower.StartsWith("readme")) return new("i", C("#548AF7"), white, Shape.Circle);
        if (lower.StartsWith("license")) return new("§", C("#8C96A3"), white);
        if (lower.StartsWith("phpunit")) return new("PU", C("#3C9CD7"), white);
        if (lower.EndsWith(".twig")) return new("Tw", C("#8FBF3F"), black);
        if (lower.EndsWith(".blade.php")) return new("bl", C("#F55247"), white);

        switch (ext)
        {
            case ".php" or ".phtml" or ".inc":
            {
                var bare = System.IO.Path.GetFileNameWithoutExtension(name);
                var inTemplates = path.Replace('/', '\\').Contains("\\templates\\", StringComparison.OrdinalIgnoreCase);
                if (!inTemplates && bare.Length > 0 && char.IsUpper(bare[0]))
                {
                    if (bare.EndsWith("Interface") || bare.EndsWith("Contract")) return new("I", C("#5FB865"), white, Shape.Circle);
                    if (bare.EndsWith("Trait")) return new("T", C("#E0A44C"), white, Shape.Circle);
                    if (bare.EndsWith("Enum")) return new("E", C("#B189F5"), white, Shape.Circle);
                    var isAbstract = bare.StartsWith("Abstract");
                    return new("C", C("#3D8EDB"), white, Shape.Circle, isAbstract);
                }
                return new("php", C("#7A86B8"), white);
            }
            case ".js" or ".mjs" or ".cjs": return new("JS", C("#E8C547"), black);
            case ".jsx": return new("JX", C("#61DAFB"), black);
            case ".ts" or ".mts": return new("TS", C("#3178C6"), white);
            case ".tsx": return new("TX", C("#3178C6"), white);
            case ".vue": return new("V", C("#41B883"), white);
            case ".json" or ".jsonc" or ".webmanifest": return new("{}", C("#E59E3D"), white);
            case ".yaml" or ".yml" or ".neon": return new("yml", C("#C8553D"), white);
            case ".html" or ".htm" or ".xhtml": return new("<>", C("#E44D26"), white);
            case ".css": return new("#", C("#2965F1"), white);
            case ".scss" or ".sass": return new("S", C("#CD6799"), white);
            case ".less": return new("L", C("#1D365D"), white);
            case ".md" or ".markdown": return new("M", C("#548AF7"), white);
            case ".xml" or ".xsd" or ".xlf" or ".xliff": return new("</>", C("#E08855"), white);
            case ".svg": return new("svg", C("#F2A93B"), black);
            case ".sql": return new("SQL", C("#D4A72C"), black);
            case ".sh" or ".bash" or ".zsh": return new(">_", C("#4EAA25"), white);
            case ".bat" or ".cmd" or ".ps1": return new(">_", C("#3D6FB4"), white);
            case ".ini" or ".conf" or ".cfg" or ".toml" or ".editorconfig": return new("ini", C("#8C96A3"), white);
            case ".log": return new("log", C("#8C96A3"), white);
            case ".txt": return new("txt", C("#8C96A3"), white);
            case ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".ico" or ".bmp" or ".avif": return new("img", C("#59A869"), white);
            case ".woff" or ".woff2" or ".ttf" or ".otf" or ".eot": return new("Aa", C("#8C96A3"), white);
            case ".zip" or ".gz" or ".tar" or ".rar" or ".7z" or ".phar": return new("zip", C("#9C7A4C"), white);
            case ".pdf": return new("pdf", C("#E1433A"), white);
            case ".csv": return new("csv", C("#4EAA25"), white);
            case ".map": return new("map", C("#8C96A3"), white);
        }
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (IsDirectory) { DrawFolder(dc); return; }

        var spec = FileSpec(Path, PhpKind);
        if (spec == null) { DrawPage(dc); return; }

        var bg = new SolidColorBrush(spec.Bg);
        switch (spec.Shape)
        {
            case Shape.Circle:
                if (spec.Abstract)
                {
                    // Abstract class: half-filled circle, like PhpStorm's abstract class icon.
                    dc.DrawEllipse(Brushes.Transparent, new Pen(bg, 1.4), new Point(8, 8), 6.6, 6.6);
                    var clip = new RectangleGeometry(new Rect(0, 8, 16, 8));
                    dc.PushClip(clip);
                    dc.DrawEllipse(bg, null, new Point(8, 8), 6.6, 6.6);
                    dc.Pop();
                    DrawText(dc, spec.Text, ThemeManager.IsDark ? Colors.White : C("#1E1F22"), dpi, 9, true);
                }
                else
                {
                    dc.DrawEllipse(bg, null, new Point(8, 8), 7, 7);
                    DrawText(dc, spec.Text, spec.Fg, dpi, 9, true);
                }
                break;
            default:
                dc.DrawRoundedRectangle(bg, null, new Rect(0.5, 2, 15, 12), 2.5, 2.5);
                var size = spec.Text.Length switch { 1 => 9.0, 2 => 7.5, _ => 6.2 };
                DrawText(dc, spec.Text, spec.Fg, dpi, size, true);
                break;
        }
    }

    private static void DrawText(DrawingContext dc, string text, Color color, double dpi, double size, bool bold)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Condensed),
            size, new SolidColorBrush(color), dpi);
        dc.DrawText(ft, new Point(8 - ft.Width / 2, 8 - ft.Height / 2));
    }

    private void DrawPage(DrawingContext dc)
    {
        var stroke = new Pen(new SolidColorBrush(ThemeManager.IsDark ? C("#8C96A3") : C("#6C707E")), 1);
        var g = Geometry.Parse("M3.5,1.5 L9.5,1.5 L12.5,4.5 L12.5,14.5 L3.5,14.5 Z M9.5,1.5 L9.5,4.5 L12.5,4.5");
        dc.DrawGeometry(Brushes.Transparent, stroke, g);
    }

    private void DrawFolder(DrawingContext dc)
    {
        var color = FolderColor(Path, IsRoot);
        var fill = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)70 : (byte)55, color.R, color.G, color.B));
        var pen = new Pen(new SolidColorBrush(color), 1.1);
        Geometry g = IsExpanded
            ? Geometry.Parse("M1.5,3.5 L6,3.5 L7.5,5 L13.5,5 L13.5,7 M1.5,3.5 L1.5,13.5 L12.5,13.5 L15,7.5 L4,7.5 L1.5,13.5")
            : Geometry.Parse("M1.5,3 L6,3 L7.5,4.5 L14.5,4.5 L14.5,13.5 L1.5,13.5 Z");
        dc.DrawGeometry(fill, pen, g);
        if (!IsExpanded) dc.DrawLine(pen, new Point(1.5, 6.5), new Point(14.5, 6.5));
    }
}
