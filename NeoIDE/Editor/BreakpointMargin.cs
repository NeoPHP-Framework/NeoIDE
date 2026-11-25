using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using NeoIDE.Services;
using NeoIDE.Services.Debug;

namespace NeoIDE.Editor;

/// <summary>
/// Breakpoints of a PHP file: click the gutter to add / remove one, right-click for a condition; the line of the
/// paused script is shown with an arrow. The breakpoints follow the lines when text is inserted above them.
/// </summary>
public sealed class BreakpointMargin : AbstractMargin
{
    private readonly CodeEditor _editor;
    private readonly Dictionary<BreakpointDef, TextAnchor> _anchors = new();
    private int _hoverLine = -1;

    public BreakpointMargin(CodeEditor editor)
    {
        _editor = editor;
        Cursor = Cursors.Hand;
        ToolTipService.SetInitialShowDelay(this, 400);
        ToolTip = "";
        ToolTipOpening += OnToolTipOpening;
        MouseLeave += (_, _) => { _hoverLine = -1; InvalidateVisual(); };
    }

    protected override Size MeasureOverride(Size availableSize) => new(_editor.IsPhp && !_editor.IsPreview ? 16 : 0, 0);

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= Redraw;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView != null) newTextView.VisualLinesChanged += Redraw;
    }

    protected override void OnDocumentChanged(TextDocument oldDocument, TextDocument newDocument)
    {
        if (oldDocument != null) oldDocument.Changed -= OnDocChanged;
        base.OnDocumentChanged(oldDocument, newDocument);
        if (newDocument != null) newDocument.Changed += OnDocChanged;
        _anchors.Clear();
        Sync();
    }

    private void Redraw(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Anchors for the breakpoints of the file (after a load or a change of the list).</summary>
    public void Sync()
    {
        var doc = Document;
        if (doc == null || _editor.FilePath == null) return;
        var current = Breakpoints.For(_editor.FilePath).ToList();
        foreach (var bp in _anchors.Keys.Except(current).ToList()) _anchors.Remove(bp);
        foreach (var bp in current)
        {
            if (_anchors.ContainsKey(bp)) continue;
            if (bp.Line < 1 || bp.Line > doc.LineCount) continue;
            var anchor = doc.CreateAnchor(doc.GetLineByNumber(bp.Line).Offset);
            anchor.MovementType = AnchorMovementType.BeforeInsertion;
            anchor.SurviveDeletion = true;
            _anchors[bp] = anchor;
        }
        InvalidateVisual();
        InvalidateMeasure();
    }

    private bool _syncing;
    private void OnDocChanged(object? sender, DocumentChangeEventArgs e)
    {
        if (_syncing || _anchors.Count == 0 || _editor.FilePath == null) return;
        bool moved = false;
        foreach (var (bp, anchor) in _anchors)
        {
            var line = anchor.Line;
            if (line != bp.Line) { bp.Line = line; moved = true; }
        }
        if (moved)
        {
            _syncing = true;
            try { Breakpoints.Moved(_editor.FilePath); } finally { _syncing = false; }
        }
    }

    private int LineAt(Point p)
    {
        var view = TextView;
        if (view == null) return -1;
        var vl = view.GetVisualLineFromVisualTop(p.Y + view.VerticalOffset);
        return vl?.FirstDocumentLine.LineNumber ?? -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var line = LineAt(e.GetPosition(this));
        if (line != _hoverLine) { _hoverLine = line; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var line = LineAt(e.GetPosition(this));
        if (line < 1 || _editor.FilePath == null) return;
        e.Handled = true;
        _editor.ToggleBreakpoint(line);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        var line = LineAt(e.GetPosition(this));
        if (line < 1 || _editor.FilePath == null) return;
        e.Handled = true;
        var bp = Breakpoints.At(_editor.FilePath, line);
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        void Add(string header, Action a, string? gesture = null)
        {
            var mi = new MenuItem { Header = header.Literal(), InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        if (bp == null)
        {
            Add("Add breakpoint", () => _editor.ToggleBreakpoint(line), "Ctrl+F8");
            Add("Add conditional breakpoint…", () =>
            {
                var cond = Dialogs.InputDialog.Ask("Conditional breakpoint", "Stop when this PHP expression is true (e.g. $id === 42):", "");
                if (string.IsNullOrWhiteSpace(cond)) return;
                Breakpoints.Add(new BreakpointDef { File = Path.GetFullPath(_editor.FilePath), Line = line, Condition = cond });
            });
        }
        else
        {
            Add(bp.Enabled ? "Disable breakpoint" : "Enable breakpoint", () => { bp.Enabled = !bp.Enabled; Breakpoints.Update(bp); });
            Add(string.IsNullOrEmpty(bp.Condition) ? "Add condition…" : "Edit condition…", () =>
            {
                var cond = Dialogs.InputDialog.Ask("Breakpoint condition", "Stop when this PHP expression is true (empty = always):", bp.Condition ?? "");
                if (cond == null) return;
                bp.Condition = cond.Trim().Length == 0 ? null : cond.Trim();
                Breakpoints.Update(bp);
            });
            Add("Remove breakpoint", () => Breakpoints.Remove(bp), "Ctrl+F8");
        }
        menu.Items.Add(new Separator());
        Add(Breakpoints.Muted ? "Unmute all breakpoints" : "Mute all breakpoints", () => Breakpoints.SetMuted(!Breakpoints.Muted));
        Add("Remove all breakpoints of the project", () => Breakpoints.RemoveAll());
        menu.IsOpen = true;
    }

    private void OnToolTipOpening(object sender, ToolTipEventArgs e)
    {
        var line = LineAt(Mouse.GetPosition(this));
        var bp = _editor.FilePath != null ? Breakpoints.At(_editor.FilePath, line) : null;
        if (bp == null) { ToolTip = "Click: add a breakpoint · right-click: conditional breakpoint"; return; }
        ToolTip = $"Line {bp.Line}" + (bp.Condition != null ? $"\nCondition: {bp.Condition}" : "") + (!bp.Enabled ? "\n(disabled)" : "") + (Breakpoints.Muted ? "\n(all breakpoints muted)" : "");
    }

    protected override void OnRender(DrawingContext dc)
    {
        var view = TextView;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (view == null || !view.VisualLinesValid || _editor.FilePath == null) return;
        var bps = Breakpoints.For(_editor.FilePath).ToDictionary(b => b.Line, b => b);
        var red = new SolidColorBrush(EditorPalette.Breakpoint);
        var pos = DebugService.Position;
        var pausedHere = pos != null && string.Equals(pos.Value.file, Path.GetFullPath(_editor.FilePath), StringComparison.OrdinalIgnoreCase) ? pos.Value.line : -1;
        foreach (var vl in view.VisualLines)
        {
            var line = vl.FirstDocumentLine.LineNumber;
            var top = vl.VisualTop - view.VerticalOffset;
            var center = new Point(8, top + vl.Height / 2);
            if (bps.TryGetValue(line, out var bp))
            {
                var muted = Breakpoints.Muted || !bp.Enabled;
                if (muted) dc.DrawEllipse(null, new Pen(red, 1.4), center, 4.6, 4.6);
                else dc.DrawEllipse(red, null, center, 5, 5);
                if (bp.Condition != null) dc.DrawRectangle(Brushes.White, null, new Rect(center.X - 2.5, center.Y - 0.8, 5, 1.6));
            }
            else if (line == _hoverLine)
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x70, EditorPalette.Breakpoint.R, EditorPalette.Breakpoint.G, EditorPalette.Breakpoint.B)), null, center, 4.6, 4.6);
            if (line == pausedHere)
            {
                var arrow = new StreamGeometry();
                using (var g = arrow.Open())
                {
                    g.BeginFigure(new Point(2, center.Y - 4), true, true);
                    g.LineTo(new Point(8, center.Y - 4), true, false);
                    g.LineTo(new Point(13, center.Y), true, false);
                    g.LineTo(new Point(8, center.Y + 4), true, false);
                    g.LineTo(new Point(2, center.Y + 4), true, false);
                }
                dc.DrawGeometry(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F0B429")), new Pen(Brushes.Black, 0.4), arrow);
            }
        }
    }
}

/// <summary>Background of the paused line and of the breakpoint lines.</summary>
public sealed class DebugLineRenderer(CodeEditor editor) : IBackgroundRenderer
{
    // above the current line highlight (drawn in the Background layer), below the text
    public KnownLayer Layer => KnownLayer.Selection;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (editor.FilePath == null || !editor.IsPhp || !textView.VisualLinesValid) return;
        var file = Path.GetFullPath(editor.FilePath);
        var lines = new Dictionary<int, Color>();
        if (!Breakpoints.Muted)
            foreach (var bp in Breakpoints.For(file).Where(b => b.Enabled)) lines[bp.Line] = EditorPalette.BreakpointLine;
        if (DebugService.Position is { } p && string.Equals(p.file, file, StringComparison.OrdinalIgnoreCase)) lines[p.line] = EditorPalette.ExecutionLine;
        if (lines.Count == 0) return;
        foreach (var vl in textView.VisualLines)
        {
            if (!lines.TryGetValue(vl.FirstDocumentLine.LineNumber, out var color)) continue;
            var top = vl.VisualTop - textView.VerticalOffset;
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, top, textView.ActualWidth, vl.Height));
        }
    }
}
