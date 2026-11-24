using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;

namespace NeoIDE.Editor;

/// <summary>Folds { } blocks and /* */ comments, skipping strings.</summary>
public sealed class BraceFoldingStrategy
{
    public IEnumerable<NewFolding> CreateFoldings(TextDocument doc)
    {
        var list = new List<NewFolding>();
        var stack = new Stack<int>();
        var text = doc.Text;
        int i = 0, n = text.Length;
        while (i < n)
        {
            char c = text[i];
            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                if (doc.GetLineByOffset(i).LineNumber != doc.GetLineByOffset(Math.Min(end, n)).LineNumber)
                    list.Add(new NewFolding(i, end) { Name = text[i + 2] == '*' ? "/** … */" : "/* … */" });
                i = end;
                continue;
            }
            if ((c == '/' && i + 1 < n && text[i + 1] == '/') || (c == '#' && !(i + 1 < n && text[i + 1] == '[')))
            {
                while (i < n && text[i] != '\n') i++;
                continue;
            }
            if (c is '"' or '\'' or '`')
            {
                i++;
                while (i < n && text[i] != c) { if (text[i] == '\\') i++; i++; }
                i++;
                continue;
            }
            if (c is '{' or '[') stack.Push(i);
            else if ((c is '}' or ']') && stack.Count > 0)
            {
                int start = stack.Pop();
                if (doc.GetLineByOffset(start).LineNumber < doc.GetLineByOffset(i).LineNumber)
                    list.Add(new NewFolding(start, i + 1) { Name = text[start] == '{' ? "{…}" : "[…]" });
            }
            i++;
        }
        list.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        return list;
    }
}

/// <summary>Greys out text segments (unused imports), like PhpStorm.</summary>
public sealed class DimColorizer : DocumentColorizingTransformer
{
    public List<(int start, int end)> Segments { get; set; } = new();

    protected override void ColorizeLine(DocumentLine line)
    {
        foreach (var (start, end) in Segments)
        {
            int s = Math.Max(start, line.Offset), e = Math.Min(end, line.EndOffset);
            if (s >= e) continue;
            ChangeLinePart(s, e, el =>
            {
                el.TextRunProperties.SetForegroundBrush(new SolidColorBrush(EditorPalette.Unused));
            });
        }
    }
}

/// <summary>Ctrl+hover: the navigable symbol under the mouse is shown as a link (PhpStorm behaviour).</summary>
public sealed class LinkColorizer : DocumentColorizingTransformer
{
    public int Start { get; set; } = -1;
    public int Length { get; set; }
    public Brush Brush { get; set; } = Brushes.DodgerBlue;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (Start < 0) return;
        int s = Math.Max(Start, line.Offset), e = Math.Min(Start + Length, line.EndOffset);
        if (s >= e) return;
        ChangeLinePart(s, e, el =>
        {
            el.TextRunProperties.SetForegroundBrush(Brush);
            el.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
        });
    }
}

/// <summary>An underlined range of the editor and its message.</summary>
public sealed record Squiggle(int Start, int Length, string Message, Services.Diagnostics.Severity Severity, string Source = "ide");

/// <summary>Draws wavy underlines under problems (red errors, orange warnings, grey hints).</summary>
public sealed class SquiggleRenderer : IBackgroundRenderer
{
    public List<Squiggle> Markers { get; set; } = new();
    public KnownLayer Layer => KnownLayer.Selection;

    public static Color ColorOf(Services.Diagnostics.Severity s) => s switch
    {
        Services.Diagnostics.Severity.Error => EditorPalette.ErrorUnderline,
        Services.Diagnostics.Severity.Warning => EditorPalette.WarningUnderline,
        _ => EditorPalette.InfoUnderline,
    };

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (Markers.Count == 0 || !textView.VisualLinesValid) return;
        var pens = new Dictionary<Services.Diagnostics.Severity, Pen>();
        // errors drawn last (on top)
        foreach (var m in Markers.OrderByDescending(m => m.Severity))
        {
            var (start, length) = (m.Start, Math.Max(1, m.Length));
            if (start < 0 || start + length > textView.Document.TextLength) continue;
            if (!pens.TryGetValue(m.Severity, out var pen))
            {
                pen = new Pen(new SolidColorBrush(ColorOf(m.Severity)), 1);
                pen.Freeze();
                pens[m.Severity] = pen;
            }
            var seg = new TextSegment { StartOffset = start, Length = length };
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
            {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    double y = r.Bottom - 1;
                    ctx.BeginFigure(new Point(r.Left, y), false, false);
                    bool up = true;
                    for (double x = r.Left + 2; x <= r.Right + 0.1; x += 2)
                    {
                        ctx.LineTo(new Point(x, up ? y - 2 : y), true, false);
                        up = !up;
                    }
                }
                geo.Freeze();
                dc.DrawGeometry(null, pen, geo);
            }
        }
    }
}

/// <summary>Highlights the bracket pair around the caret.</summary>
public sealed class BracketHighlightRenderer : IBackgroundRenderer
{
    public int First { get; set; } = -1;
    public int Second { get; set; } = -1;
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (First < 0 || Second < 0) return;
        var brush = new SolidColorBrush(EditorPalette.BracketMatch);
        foreach (var off in new[] { First, Second })
        {
            if (off >= textView.Document.TextLength) continue;
            var b = new BackgroundGeometryBuilder { CornerRadius = 1 };
            b.AddSegment(textView, new TextSegment { StartOffset = off, Length = 1 });
            var g = b.CreateGeometry();
            if (g != null) dc.DrawGeometry(brush, null, g);
        }
    }

    /// <summary>Bracket pair around the caret, reading the document directly (no full-text copy on every caret move).</summary>
    public static (int, int) FindPair(TextDocument doc, int caret)
    {
        const string open = "([{", close = ")]}";
        int len = doc.TextLength;
        foreach (var pos in new[] { caret - 1, caret })
        {
            if (pos < 0 || pos >= len) continue;
            char c = doc.GetCharAt(pos);
            int oi = open.IndexOf(c), ci = close.IndexOf(c);
            if (oi >= 0)
            {
                int d = 0;
                for (int i = pos; i < len && i < pos + 20000; i++)
                {
                    var ch = doc.GetCharAt(i);
                    if (ch == open[oi]) d++;
                    else if (ch == close[oi] && --d == 0) return (pos, i);
                }
            }
            else if (ci >= 0)
            {
                int d = 0;
                for (int i = pos; i >= 0 && i > pos - 20000; i--)
                {
                    var ch = doc.GetCharAt(i);
                    if (ch == close[ci]) d++;
                    else if (ch == open[ci] && --d == 0) return (i, pos);
                }
            }
        }
        return (-1, -1);
    }
}
