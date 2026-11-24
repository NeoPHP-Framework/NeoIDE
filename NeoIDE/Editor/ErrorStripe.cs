using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using NeoIDE.Services;
using NeoIDE.Services.Diagnostics;

namespace NeoIDE.Editor;

/// <summary>
/// Overview of the problems of the file at the right edge of the editor (PhpStorm's error stripe):
/// one mark per problem at its relative position, a summary icon at the top. Click a mark to go to it.
/// Inserted as a layer of the TextView, so it does not scroll with the text.
/// </summary>
public sealed class ErrorStripe : FrameworkElement
{
    private readonly TextView _view;
    private readonly Func<IReadOnlyList<Squiggle>> _markers;
    private readonly Action<int> _goTo;
    private const double StripeWidth = 9, Top = 18;

    public ErrorStripe(TextView view, Func<IReadOnlyList<Squiggle>> markers, Action<int> goTo)
    {
        _view = view;
        _markers = markers;
        _goTo = goTo;
        Cursor = Cursors.Arrow;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTip = "";
        ToolTipOpening += OnToolTipOpening;
        view.ScrollOffsetChanged += (_, _) => InvalidateVisual();
        view.DocumentChanged += (_, _) => InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    protected override HitTestResult? HitTestCore(PointHitTestParameters p) =>
        p.HitPoint.X >= ActualWidth - StripeWidth - 2 ? new PointHitTestResult(this, p.HitPoint) : null;

    private int LineCount => Math.Max(1, _view.Document?.LineCount ?? 1);

    private double YOf(int line)
    {
        var h = Math.Max(1, ActualHeight - Top - 4);
        return Top + (line - 1) * h / LineCount;
    }

    private IEnumerable<(Squiggle m, int line, double y)> Marks()
    {
        var doc = _view.Document;
        if (doc == null) yield break;
        foreach (var m in _markers())
        {
            if (m.Start < 0 || m.Start > doc.TextLength) continue;
            var line = doc.GetLineByOffset(m.Start).LineNumber;
            yield return (m, line, YOf(line));
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var x = ActualWidth - StripeWidth;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(x - 2, 0, StripeWidth + 2, ActualHeight));
        var marks = Marks().ToList();
        // visible part of the file
        if (_view.Document != null && _view.DocumentHeight > _view.ActualHeight)
        {
            var h = Math.Max(1, ActualHeight - Top - 4);
            var top = Top + _view.VerticalOffset / _view.DocumentHeight * h;
            var size = Math.Max(6, _view.ActualHeight / _view.DocumentHeight * h);
            var thumb = new SolidColorBrush(Color.FromArgb(0x18, 0x80, 0x80, 0x80));
            dc.DrawRectangle(thumb, null, new Rect(x, top, StripeWidth - 2, size));
        }
        foreach (var (m, _, y) in marks.OrderByDescending(t => t.m.Severity))
            dc.DrawRectangle(new SolidColorBrush(SquiggleRenderer.ColorOf(m.Severity)), null, new Rect(x + 1, Math.Max(Top, y - 1), StripeWidth - 4, 3));

        // summary icon: red / orange dot with the number, green check when everything is fine
        var errors = marks.Count(t => t.m.Severity == Severity.Error);
        var warnings = marks.Count(t => t.m.Severity == Severity.Warning);
        var color = errors > 0 ? EditorPalette.ErrorUnderline : warnings > 0 ? EditorPalette.WarningUnderline : (Color)ColorConverter.ConvertFromString(ThemeManager.IsDark ? "#57965C" : "#3D8C40");
        var center = new Point(x + (StripeWidth - 2) / 2, 8);
        if (errors + warnings == 0)
        {
            var pen = new Pen(new SolidColorBrush(color), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(center.X - 3.5, center.Y), new Point(center.X - 1, center.Y + 2.5));
            dc.DrawLine(pen, new Point(center.X - 1, center.Y + 2.5), new Point(center.X + 3.5, center.Y - 3));
        }
        else dc.DrawEllipse(new SolidColorBrush(color), null, center, 4, 4);
    }

    private (Squiggle m, int line, double y)? MarkAt(double y) =>
        Marks().Where(t => Math.Abs(t.y - y) <= 4).OrderBy(t => t.m.Severity).ThenBy(t => Math.Abs(t.y - y)).Cast<(Squiggle, int, double)?>().FirstOrDefault();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var y = e.GetPosition(this).Y;
        e.Handled = true;
        if (y < Top)
        {
            var first = Marks().OrderBy(t => t.m.Severity).ThenBy(t => t.line).FirstOrDefault();
            if (first.m != null) _goTo(first.m.Start);
            return;
        }
        if (MarkAt(y) is { } hit) { _goTo(hit.m.Start); return; }
        // click on an empty part: scroll there
        var h = Math.Max(1, ActualHeight - Top - 4);
        var line = Math.Clamp((int)((y - Top) / h * LineCount) + 1, 1, LineCount);
        if (_view.Document != null) _goTo(_view.Document.GetLineByNumber(line).Offset);
    }

    private void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var y = Mouse.GetPosition(this).Y;
        string? text;
        if (y < Top)
        {
            var marks = Marks().ToList();
            var errors = marks.Count(t => t.m.Severity == Severity.Error);
            var warnings = marks.Count(t => t.m.Severity == Severity.Warning);
            var infos = marks.Count - errors - warnings;
            text = errors + warnings + infos == 0 ? "No problems found" :
                string.Join(", ", new[] { errors > 0 ? $"{errors} error(s)" : null, warnings > 0 ? $"{warnings} warning(s)" : null, infos > 0 ? $"{infos} hint(s)" : null }.Where(s => s != null))
                + "\nClick: go to the first one · F2 / Shift+F2: next / previous";
        }
        else
        {
            var near = Marks().Where(t => Math.Abs(t.y - y) <= 4).OrderBy(t => t.m.Severity).Take(5).ToList();
            text = near.Count == 0 ? null : string.Join("\n", near.Select(t => $"{t.line}: {t.m.Message.Split('\n')[0]}"));
        }
        if (text == null) { e.Handled = true; return; }
        ToolTip = new TextBlock { Text = text, FontSize = 12, MaxWidth = 600, TextWrapping = TextWrapping.Wrap };
    }
}
