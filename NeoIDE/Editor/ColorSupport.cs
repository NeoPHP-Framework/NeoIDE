using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using NeoIDE.Services;

namespace NeoIDE.Editor;

public enum ColorFormat { Hex, Rgb, Hsl, Named }

/// <summary>A colour literal found in the text (#fff, rgb(...), hsl(...), named CSS colour).</summary>
public readonly record struct ColorLiteral(int Index, int Length, Color Color, ColorFormat Format, string Text);

/// <summary>Parsing / formatting of CSS colour literals, keeping the original notation.</summary>
public static class ColorLiterals
{
    private static readonly Dictionary<string, Color> Named = typeof(Colors).GetProperties()
        .ToDictionary(p => p.Name.ToLowerInvariant(), p => (Color)p.GetValue(null)!);

    private static readonly string NamedPattern = string.Join("|", Named.Keys.Where(k => k != "transparent").OrderByDescending(k => k.Length));

    private static readonly Regex Literal = new(
        @"(?<![\w&#.-])#(?<hex>[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})(?![\w-])" +
        @"|(?<![\w-])(?<fn>rgba?|hsla?)\(\s*(?<a>-?[\d.]+)(?<au>deg|%)?\s*[,\s]\s*(?<b>[\d.]+)%?\s*[,\s]\s*(?<c>[\d.]+)%?\s*(?:[,/]\s*(?<alpha>[\d.]+)(?<alphau>%)?\s*)?\)" +
        @"|(?<![\w$@.#-])(?<name>" + NamedPattern + @")(?![\w-])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Colour literals of a line. In CSS-like languages named colours and hex values are only taken in property values.</summary>
    public static IEnumerable<ColorLiteral> Find(string line, string? language)
    {
        bool css = language is "CSS";
        foreach (Match m in Literal.Matches(line))
        {
            if (m.Groups["name"].Success)
            {
                // named colours only in CSS / style values ("color: red"), never in code identifiers
                if (!(css || language is "HTML" or "Twig" or "PHP") || !InValue(line, m.Index)) continue;
            }
            else if (m.Groups["hex"].Success && css && !InValue(line, m.Index)) continue; // "#header" selectors
            else if (m.Groups["hex"].Success && Regex.IsMatch(line[..m.Index], @"\b(href|src|action|id|for|data-[\w-]+)\s*=\s*[""']?$", RegexOptions.IgnoreCase)) continue; // anchors / ids
            if (TryParse(m.Value, out var color, out var format)) yield return new ColorLiteral(m.Index, m.Length, color, format, m.Value);
        }
    }

    /// <summary>"prop: value" context: after the last ':' of the line there is no '{' (selectors like a:hover #id {).</summary>
    private static bool InValue(string line, int index)
    {
        var colon = line.LastIndexOf(':', Math.Max(0, index - 1));
        if (colon < 0 || colon >= index) return false;
        var between = line[colon..index];
        return !between.Contains('{') && !between.Contains(';') && !line[index..].TrimEnd().EndsWith("{");
    }

    public static bool TryParse(string text, out Color color, out ColorFormat format)
    {
        color = default;
        format = ColorFormat.Hex;
        var t = text.Trim();
        if (t.StartsWith("#"))
        {
            var h = t[1..];
            if (h.Length is 3 or 4) h = string.Concat(h.Select(c => $"{c}{c}"));
            if (h.Length is not (6 or 8) || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
            color = h.Length == 6
                ? Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
                : Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8)); // CSS #RRGGBBAA
            return true;
        }
        if (Named.TryGetValue(t.ToLowerInvariant(), out var named)) { color = named; format = ColorFormat.Named; return true; }
        var m = Regex.Match(t, @"^(?<fn>rgba?|hsla?)\(\s*(?<a>-?[\d.]+)(deg|%)?\s*[,\s]\s*(?<b>[\d.]+)(?<bp>%)?\s*[,\s]\s*(?<c>[\d.]+)(?<cp>%)?\s*(?:[,/]\s*(?<alpha>[\d.]+)(?<ap>%)?\s*)?\)$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        double D(string g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        var alpha = m.Groups["alpha"].Success ? (m.Groups["ap"].Success ? D("alpha") / 100 : D("alpha")) : 1.0;
        byte A() => (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        if (m.Groups["fn"].Value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            byte Ch(string g, string pct) => (byte)Math.Round(Math.Clamp(m.Groups[pct].Success ? D(g) * 2.55 : D(g), 0, 255));
            color = Color.FromArgb(A(), Ch("a", "__"), Ch("b", "bp"), Ch("c", "cp"));
            format = ColorFormat.Rgb;
            return true;
        }
        var (r, g2, b) = HslToRgb(D("a"), D("b") / 100, D("c") / 100);
        color = Color.FromArgb(A(), r, g2, b);
        format = ColorFormat.Hsl;
        return true;
    }

    /// <summary>Writes a colour in the notation of the original literal.</summary>
    public static string Format(Color c, ColorFormat format, string original)
    {
        var upper = original.Any(char.IsUpper) && original.StartsWith("#");
        string a = (c.A / 255.0).ToString("0.##", CultureInfo.InvariantCulture);
        switch (format)
        {
            case ColorFormat.Rgb:
                return c.A == 255 ? $"rgb({c.R}, {c.G}, {c.B})" : $"rgba({c.R}, {c.G}, {c.B}, {a})";
            case ColorFormat.Hsl:
                var (h, s, l) = RgbToHsl(c);
                return c.A == 255 ? $"hsl({h:0}, {s * 100:0}%, {l * 100:0}%)" : $"hsla({h:0}, {s * 100:0}%, {l * 100:0}%, {a})";
            default:
                string hex;
                bool shortForm = original.StartsWith("#") && original.Length is 4 or 5;
                bool canShort = c.R % 17 == 0 && c.G % 17 == 0 && c.B % 17 == 0 && c.A % 17 == 0;
                if (c.A == 255) hex = shortForm && canShort ? $"#{c.R / 17:x}{c.G / 17:x}{c.B / 17:x}" : $"#{c.R:x2}{c.G:x2}{c.B:x2}";
                else hex = shortForm && canShort ? $"#{c.R / 17:x}{c.G / 17:x}{c.B / 17:x}{c.A / 17:x}" : $"#{c.R:x2}{c.G:x2}{c.B:x2}{c.A:x2}";
                return upper || format == ColorFormat.Named ? hex.ToUpperInvariant() : hex;
        }
    }

    public static (byte r, byte g, byte b) HslToRgb(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double R, G, B;
        if (s == 0) R = G = B = l;
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            double Hue(double t)
            {
                if (t < 0) t += 1;
                if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 0.5) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }
            R = Hue(h + 1.0 / 3); G = Hue(h); B = Hue(h - 1.0 / 3);
        }
        return ((byte)Math.Round(R * 255), (byte)Math.Round(G * 255), (byte)Math.Round(B * 255));
    }

    public static (double h, double s, double l) RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2;
        if (max == min) s = 0;
        else
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    // HSV helpers for the picker
    public static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    public static Color FromHsv(double h, double s, double v, byte a = 255)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromArgb(a, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}

/// <summary>Inserts a small clickable colour swatch before each colour literal (zero-length inline element).</summary>
public sealed class ColorSwatchGenerator : VisualLineElementGenerator
{
    public string? Language { get; set; }
    public bool Enabled { get; set; } = true;
    public event Action<ColorLiteral, FrameworkElement>? SwatchClicked;

    private int _lineOffset = -1;
    private List<ColorLiteral> _lineLiterals = new();

    private void Load(int offset)
    {
        var line = CurrentContext.Document.GetLineByOffset(offset);
        if (line.Offset == _lineOffset) return;
        _lineOffset = line.Offset;
        _lineLiterals = line.Length > 2000 ? new() : ColorLiterals.Find(CurrentContext.Document.GetText(line), Language).ToList();
    }

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _lineOffset = -1;
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!Enabled) return -1;
        Load(startOffset);
        foreach (var l in _lineLiterals)
            if (_lineOffset + l.Index >= startOffset) return _lineOffset + l.Index;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        Load(offset);
        var lit = _lineLiterals.FirstOrDefault(l => _lineOffset + l.Index == offset);
        if (lit.Length == 0) return null;
        var absolute = lit with { Index = _lineOffset + lit.Index };
        var swatch = new Border
        {
            Width = 11, Height = 11, Margin = new Thickness(1, 0, 4, -1), CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(150, 128, 128, 128)), Cursor = Cursors.Hand,
            Background = Checkerboard(), ToolTip = "Click to pick a colour",
            Child = new Border { Background = new SolidColorBrush(lit.Color), CornerRadius = new CornerRadius(1) },
        };
        // open on mouse *up*: a popup opened on mouse down is closed by the release (StaysOpen=false sees a click outside)
        swatch.MouseLeftButtonDown += (_, e) => e.Handled = true; // no caret move / selection start
        swatch.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            swatch.Dispatcher.BeginInvoke(() => SwatchClicked?.Invoke(absolute, swatch), System.Windows.Threading.DispatcherPriority.Input);
        };
        return new InlineObjectElement(0, swatch);
    }

    public static Brush Checkerboard()
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        g.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), null,
            new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 4, 4)), new RectangleGeometry(new Rect(4, 4, 4, 4)) } }));
        var b = new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        b.Freeze();
        return b;
    }
}
