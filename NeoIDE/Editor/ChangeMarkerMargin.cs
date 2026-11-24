using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using NeoIDE.Services;

namespace NeoIDE.Editor;

/// <summary>
/// Gutter bars showing lines added / modified / deleted since the last commit (like PhpStorm's VCS markers).
/// Hovering a marker shows the previous content of the lines.
/// </summary>
public sealed class ChangeMarkerMargin : AbstractMargin
{
    private enum Mark : byte { None, Added, Modified, DeletedAfter }

    public string? BaseText { get; set; }
    private Mark[] _marks = Array.Empty<Mark>();
    private List<DiffHunk> _hunks = new();
    private string[] _baseLines = Array.Empty<string>();

    public ChangeMarkerMargin()
    {
        Width = 5;
        Cursor = System.Windows.Input.Cursors.Arrow;
        ToolTipService.SetInitialShowDelay(this, 250);
        ToolTipOpening += OnToolTipOpening;
        ToolTip = "";
    }

    protected override Size MeasureOverride(Size availableSize) => new(5, 0);

    private sealed record Prepared(Mark[] Marks, List<DiffHunk> Hunks, string[] BaseLines);

    /// <summary>Thread-safe part (diff), run in the background analysis.</summary>
    public static object Prepare(string baseText, string text)
    {
        var lines = LineDiff.Lines(text);
        var baseLines = LineDiff.Lines(baseText);
        var hunks = LineDiff.Compute(baseLines, lines);
        var marks = new Mark[lines.Length + 1];
        foreach (var h in hunks)
        {
            if (h.BLength == 0)
            {
                var at = Math.Clamp(h.BStart - 1, 0, marks.Length - 1);
                if (marks[at] == Mark.None) marks[at] = Mark.DeletedAfter;
                continue;
            }
            for (int i = h.BStart; i < h.BStart + h.BLength && i < marks.Length; i++)
                marks[i] = h.ALength == 0 ? Mark.Added : Mark.Modified;
        }
        return new Prepared(marks, hunks, baseLines);
    }

    /// <summary>UI part: shows the result of <see cref="Prepare"/> (null clears the markers).</summary>
    public void Apply(object? prepared)
    {
        if (prepared is Prepared p) { _marks = p.Marks; _hunks = p.Hunks; _baseLines = p.BaseLines; }
        else if (_marks.Length > 0) { _marks = Array.Empty<Mark>(); _hunks = new List<DiffHunk>(); }
        else return;
        InvalidateVisual();
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += OnVisualLinesChanged;
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var view = TextView;
        if (view == null || !view.VisualLinesValid || _marks.Length == 0) return;
        var added = new SolidColorBrush(EditorPalette.GutterAdded);
        var modified = new SolidColorBrush(EditorPalette.GutterModified);
        var deleted = new SolidColorBrush(EditorPalette.GutterDeleted);
        foreach (var vl in view.VisualLines)
        {
            var line = vl.FirstDocumentLine.LineNumber - 1;
            if (line >= _marks.Length) continue;
            var mark = _marks[line];
            if (mark == Mark.None) continue;
            var top = vl.VisualTop - view.VerticalOffset;
            if (mark == Mark.DeletedAfter)
            {
                var y = top + vl.Height;
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(0, y - 4), true, true);
                    g.LineTo(new Point(5, y), true, false);
                    g.LineTo(new Point(0, y + 4), true, false);
                }
                dc.DrawGeometry(deleted, null, tri);
            }
            else dc.DrawRectangle(mark == Mark.Added ? added : modified, null, new Rect(0, top, 4, vl.Height));
        }
    }

    private void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var view = TextView;
        if (view == null || BaseText == null) { e.Handled = true; return; }
        var pos = System.Windows.Input.Mouse.GetPosition(this);
        var vl = view.GetVisualLineFromVisualTop(pos.Y + view.VerticalOffset);
        if (vl == null) { e.Handled = true; return; }
        var line = vl.FirstDocumentLine.LineNumber - 1;
        var hunk = _hunks.FirstOrDefault(h => line >= h.BStart - (h.BLength == 0 ? 1 : 0) && line < h.BStart + Math.Max(1, h.BLength));
        if (hunk == default) { e.Handled = true; return; }
        var old = hunk.ALength == 0 ? "(new lines)" : string.Join("\n", _baseLines.Skip(hunk.AStart).Take(Math.Min(hunk.ALength, 25)));
        ToolTip = new TextBlock
        {
            Text = (hunk.ALength == 0 ? "Added lines" : hunk.BLength == 0 ? "Deleted lines:" : "Previous content:") + (hunk.ALength == 0 ? "" : "\n" + old),
            FontFamily = DisplayManager.EditorFont, FontSize = 12, Foreground = ThemeManager.Brush("Fg"),
        };
    }
}
