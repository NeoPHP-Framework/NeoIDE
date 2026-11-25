using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Editor;

/// <summary>
/// "3 usages" / "no usages" after the declaration line of each method (PhpStorm's code vision): computed in the
/// background when the file is opened / a PHP file is saved, found again by the method name when lines move.
/// Clicking the label lists the usages.
/// </summary>
public sealed class UsageLens : IBackgroundRenderer
{
    private static readonly Regex Declaration = new(@"\bfunction\s+&?(?<name>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    /// <summary>Usages by method name (methods of the classes of the file).</summary>
    public Dictionary<string, List<Usage>> Usages { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rectangles of the labels drawn last (view coordinates) → method name, for the clicks.</summary>
    public List<(Rect rect, string method)> Hits { get; } = new();

    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        Hits.Clear();
        if (Usages.Count == 0 || !textView.VisualLinesValid || textView.Document == null) return;
        var typeface = new Typeface(DisplayManager.EditorFont, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        var size = Math.Max(9, AppSettings.Current.EditorFontSize * 0.86);
        var brush = new SolidColorBrush(EditorPalette.Unused);
        var unusedBrush = new SolidColorBrush(Color.FromArgb(0xC0, EditorPalette.WarningUnderline.R, EditorPalette.WarningUnderline.G, EditorPalette.WarningUnderline.B));
        var dpi = VisualTreeHelper.GetDpi(textView).PixelsPerDip;
        foreach (var vl in textView.VisualLines)
        {
            var line = vl.FirstDocumentLine;
            var text = textView.Document.GetText(line);
            var m = Declaration.Match(text);
            if (!m.Success || !Usages.TryGetValue(m.Groups["name"].Value, out var usages)) continue;
            var label = usages.Count switch { 0 => "no usages", 1 => "1 usage", var n => $"{n} usages" };
            var ft = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, usages.Count == 0 ? unusedBrush : brush, dpi);
            var end = vl.GetVisualPosition(vl.VisualLength, VisualYPosition.TextMiddle);
            var x = end.X - textView.ScrollOffset.X + 28;
            var y = vl.VisualTop - textView.ScrollOffset.Y + (vl.Height - ft.Height) / 2;
            dc.DrawText(ft, new Point(x, y));
            Hits.Add((new Rect(x - 2, y - 1, ft.Width + 4, ft.Height + 2), m.Groups["name"].Value));
        }
    }

    /// <summary>The methods of a file whose usages are shown (no magic methods, no controller actions with a route).</summary>
    public static List<(PhpClass cls, PhpMember method)> Methods(PhpFileInfo info, string text)
    {
        var list = new List<(PhpClass, PhpMember)>();
        foreach (var cls in info.Classes)
            foreach (var m in cls.Members.Where(m => m.Kind == PhpMemberKind.Method && !m.IsMagic && !m.Name.StartsWith("__")))
            {
                // an action of a controller is "used" by its route (shown in the gutter)
                var before = text[Math.Max(0, m.Offset - 400)..Math.Max(0, m.Offset)];
                var lastFunction = before.LastIndexOf("function", StringComparison.Ordinal);
                var attrs = lastFunction >= 0 ? before[..lastFunction] : before;
                var tail = attrs[(attrs.LastIndexOf('}') + 1)..];
                if (Regex.IsMatch(tail, @"#\[\s*(?:\\?[\w\\]*\\)?Route\b")) continue;
                list.Add((cls, m));
            }
        return list;
    }
}
