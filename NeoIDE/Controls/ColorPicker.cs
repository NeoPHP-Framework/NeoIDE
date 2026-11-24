using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NeoIDE.Editor;
using NeoIDE.Services;

namespace NeoIDE.Controls;

/// <summary>Colour picker: saturation/value square, hue and alpha bars, hex input, colours of the file and a standard palette.</summary>
public sealed class ColorPicker : Border
{
    private double _h, _s, _v;
    private byte _a = 255;
    private bool _updating;
    private readonly Rectangle _hueLayer = new();
    private readonly Ellipse _svThumb = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Border _hueThumb = new() { Width = 6, Height = 16, BorderBrush = Brushes.White, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
    private readonly Border _alphaThumb = new() { Width = 6, Height = 16, BorderBrush = Brushes.White, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2), IsHitTestVisible = false };
    private readonly Rectangle _alphaGradient = new();
    private readonly Border _oldSwatch = new(), _newSwatch = new();
    private readonly TextBox _hex = new() { Width = 100, FontFamily = new FontFamily("JetBrains Mono, Consolas") };
    private readonly TextBlock _rgbText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 11.5 };
    private const double W = 248, H = 160;

    public Color Color { get; private set; }
    public event Action<Color>? ColorChanged;
    public event Action? Accepted;

    public ColorPicker(Color initial, IEnumerable<Color> fileColors)
    {
        SetResourceReference(BackgroundProperty, "PopupBg");
        SetResourceReference(BorderBrushProperty, "PopupBorder");
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12);
        Margin = new Thickness(6, 2, 10, 12);
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.3, Direction = 270 };
        TextElement.SetForeground(this, ThemeManager.Brush("Fg"));
        _rgbText.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");

        var root = new StackPanel { Width = W };

        // saturation / value square
        var sv = new Grid { Width = W, Height = H, ClipToBounds = true, Cursor = Cursors.Cross };
        sv.Children.Add(_hueLayer);
        sv.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) });
        sv.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) });
        var svCanvas = new Canvas();
        svCanvas.Children.Add(_svThumb);
        sv.Children.Add(svCanvas);
        void SetSv(Point p) { _s = Math.Clamp(p.X / W, 0, 1); _v = 1 - Math.Clamp(p.Y / H, 0, 1); Update(true); }
        Drag(sv, SetSv);
        root.Children.Add(new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = sv });

        // hue bar
        var hueBar = new Grid { Height = 14, Margin = new Thickness(0, 10, 0, 0), Cursor = Cursors.Hand };
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(ColorLiterals.FromHsv(i * 60 % 360 == 0 && i == 6 ? 359.9 : i * 60, 1, 1), i / 6.0));
        hueBar.Children.Add(new Rectangle { Fill = rainbow, RadiusX = 3, RadiusY = 3 });
        var hueCanvas = new Canvas();
        hueCanvas.Children.Add(_hueThumb);
        hueBar.Children.Add(hueCanvas);
        Drag(hueBar, p => { _h = Math.Clamp(p.X / W, 0, 0.9999) * 360; Update(true); });
        root.Children.Add(hueBar);

        // alpha bar
        var alphaBar = new Grid { Height = 14, Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand };
        alphaBar.Children.Add(new Rectangle { Fill = ColorSwatchGenerator.Checkerboard(), RadiusX = 3, RadiusY = 3 });
        _alphaGradient.RadiusX = _alphaGradient.RadiusY = 3;
        alphaBar.Children.Add(_alphaGradient);
        var alphaCanvas = new Canvas();
        alphaCanvas.Children.Add(_alphaThumb);
        alphaBar.Children.Add(alphaCanvas);
        Drag(alphaBar, p => { _a = (byte)Math.Round(Math.Clamp(p.X / W, 0, 1) * 255); Update(true); });
        root.Children.Add(alphaBar);

        // preview old / new + hex
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var preview = new Border { Width = 44, Height = 26, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = ColorSwatchGenerator.Checkerboard(), Margin = new Thickness(0, 0, 8, 0) };
        var halves = new Grid();
        halves.ColumnDefinitions.Add(new ColumnDefinition());
        halves.ColumnDefinitions.Add(new ColumnDefinition());
        _oldSwatch.ToolTip = "Original colour (click to restore)";
        _oldSwatch.Cursor = Cursors.Hand;
        _oldSwatch.MouseLeftButtonUp += (_, _) => SetColor(initial, true);
        Grid.SetColumn(_newSwatch, 1);
        halves.Children.Add(_oldSwatch);
        halves.Children.Add(_newSwatch);
        preview.Child = halves;
        DockPanel.SetDock(preview, Dock.Left);
        row.Children.Add(preview);
        DockPanel.SetDock(_hex, Dock.Left);
        row.Children.Add(_hex);
        row.Children.Add(_rgbText);
        root.Children.Add(row);
        _hex.TextChanged += (_, _) =>
        {
            if (_updating) return;
            if (ColorLiterals.TryParse(_hex.Text.StartsWith("#") ? _hex.Text : "#" + _hex.Text, out var c, out _)) SetColor(c, true, keepHexText: true);
        };
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accepted?.Invoke(); e.Handled = true; } };

        // palettes
        var used = fileColors.Distinct().Take(16).ToList();
        if (used.Count > 0)
        {
            root.Children.Add(new TextBlock { Text = "In this file", FontSize = 11, Margin = new Thickness(0, 10, 0, 4), Foreground = ThemeManager.Brush("FgSecondary") });
            root.Children.Add(Palette(used));
        }
        root.Children.Add(new TextBlock { Text = "Palette", FontSize = 11, Margin = new Thickness(0, 10, 0, 4), Foreground = ThemeManager.Brush("FgSecondary") });
        root.Children.Add(Palette(StandardPalette.Select(h => (Color)ColorConverter.ConvertFromString(h))));
        root.Children.Add(new TextBlock { Text = "Enter / click outside: keep · Esc: cancel", FontSize = 10.5, Margin = new Thickness(0, 10, 0, 0), Foreground = ThemeManager.Brush("FgSecondary") });

        Child = root;
        _oldSwatch.Background = new SolidColorBrush(initial);
        SetColor(initial, false);
    }

    private static readonly string[] StandardPalette =
    {
        "#000000", "#434343", "#999999", "#FFFFFF", "#F44336", "#E91E63", "#9C27B0", "#673AB7",
        "#3F51B5", "#2196F3", "#03A9F4", "#00BCD4", "#009688", "#4CAF50", "#8BC34A", "#CDDC39",
        "#FFEB3B", "#FFC107", "#FF9800", "#FF5722", "#795548", "#607D8B", "#3574F0", "#21D0B2",
    };

    private WrapPanel Palette(IEnumerable<Color> colors)
    {
        var wrap = new WrapPanel();
        foreach (var c in colors)
        {
            var b = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(4), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
                Background = ColorSwatchGenerator.Checkerboard(), ToolTip = $"#{c.R:X2}{c.G:X2}{c.B:X2}{(c.A < 255 ? c.A.ToString("X2") : "")}",
                Child = new Border { Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(3) },
            };
            var color = c;
            b.MouseLeftButtonUp += (_, _) => SetColor(color, true);
            wrap.Children.Add(b);
        }
        return wrap;
    }

    private static void Drag(FrameworkElement area, Action<Point> set)
    {
        area.MouseLeftButtonDown += (_, e) => { area.CaptureMouse(); set(e.GetPosition(area)); e.Handled = true; };
        area.MouseMove += (_, e) => { if (area.IsMouseCaptured) set(e.GetPosition(area)); };
        area.MouseLeftButtonUp += (_, _) => area.ReleaseMouseCapture();
    }

    public void SetColor(Color c, bool notify, bool keepHexText = false)
    {
        var (h, s, v) = ColorLiterals.ToHsv(c);
        if (s > 0 && v > 0) _h = h; // keep the hue on greys
        _s = s;
        _v = v;
        _a = c.A;
        Update(notify, keepHexText);
    }

    private void Update(bool notify, bool keepHexText = false)
    {
        Color = ColorLiterals.FromHsv(_h, _s, _v, _a);
        var opaque = Color.FromRgb(Color.R, Color.G, Color.B);
        _hueLayer.Fill = new SolidColorBrush(ColorLiterals.FromHsv(_h, 1, 1));
        Canvas.SetLeft(_svThumb, _s * W - 6);
        Canvas.SetTop(_svThumb, (1 - _v) * H - 6);
        _svThumb.Stroke = _v > 0.6 && _s < 0.4 ? Brushes.Black : Brushes.White;
        Canvas.SetLeft(_hueThumb, _h / 360 * W - 3);
        Canvas.SetTop(_hueThumb, -1);
        _alphaGradient.Fill = new LinearGradientBrush(Color.FromArgb(0, opaque.R, opaque.G, opaque.B), opaque, 0);
        Canvas.SetLeft(_alphaThumb, _a / 255.0 * W - 3);
        Canvas.SetTop(_alphaThumb, -1);
        _newSwatch.Background = new SolidColorBrush(Color);
        _updating = true;
        if (!keepHexText) _hex.Text = _a == 255 ? $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}" : $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}{Color.A:X2}";
        _updating = false;
        _rgbText.Text = $"{Color.R},{Color.G},{Color.B}" + (_a < 255 ? $" · {(_a / 255.0).ToString("0.##", CultureInfo.InvariantCulture)}" : "");
        _rgbText.ToolTip = $"rgb({Color.R}, {Color.G}, {Color.B})";
        if (notify) ColorChanged?.Invoke(Color);
    }
}
