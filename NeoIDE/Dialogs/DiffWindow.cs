using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using NeoIDE.Editor;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

/// <summary>Side-by-side diff viewer (old on the left, new on the right) with synchronised scrolling.</summary>
public sealed class DiffWindow : DialogWindow
{
    private readonly CodeEditor _left = new() { IsReadOnly = true, IsPreview = true };
    private readonly CodeEditor _right = new() { IsReadOnly = true, IsPreview = true };
    private readonly List<DiffHunk> _hunks;
    private int _current = -1;
    private bool _syncing;
    private readonly TextBlock _counter = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };

    public DiffWindow(string title, string path, string oldText, string newText, string oldLabel, string newLabel)
    {
        Title = title;
        SizeToContent = SizeToContent.Manual;
        ResizeMode = ResizeMode.CanResize;
        Width = 1300;
        Height = 820;

        _left.Load(path, oldText);
        _right.Load(path, newText);
        _hunks = LineDiff.Compute(oldText, newText);

        var oldLines = new Dictionary<int, Color>();
        var newLines = new Dictionary<int, Color>();
        var dark = ThemeManager.IsDark;
        Color added = dark ? Color.FromArgb(70, 0x55, 0x9B, 0x5E) : Color.FromArgb(90, 0xB5, 0xE6, 0xB9);
        Color removed = dark ? Color.FromArgb(70, 0xC8, 0x50, 0x50) : Color.FromArgb(90, 0xF5, 0xB8, 0xB8);
        Color changed = dark ? Color.FromArgb(70, 0x3D, 0x6B, 0xC4) : Color.FromArgb(80, 0xBE, 0xD3, 0xF7);
        foreach (var h in _hunks)
        {
            for (int i = 0; i < h.ALength; i++) oldLines[h.AStart + i + 1] = h.BLength == 0 ? removed : changed;
            for (int i = 0; i < h.BLength; i++) newLines[h.BStart + i + 1] = h.ALength == 0 ? added : changed;
        }
        _left.TextArea.TextView.BackgroundRenderers.Add(new LineBackgroundRenderer(oldLines));
        _right.TextArea.TextView.BackgroundRenderers.Add(new LineBackgroundRenderer(newLines));

        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        var prev = new Button { Content = "↑ Previous change", Margin = new Thickness(0, 0, 8, 0) };
        var next = new Button { Content = "↓ Next change" };
        prev.Click += (_, _) => Go(-1);
        next.Click += (_, _) => Go(1);
        var toolbar = new DockPanel { Margin = new Thickness(12, 2, 12, 8) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(prev);
        nav.Children.Add(next);
        nav.Children.Add(_counter);
        toolbar.Children.Add(nav);
        _counter.Foreground = ThemeManager.Brush("FgSecondary");
        _counter.Text = _hunks.Count == 0 ? "No differences" : $"{_hunks.Count} difference(s)";

        var grid = new Grid { Margin = new Thickness(12, 0, 12, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        void Header(string text, int col)
        {
            var t = new TextBlock { Text = text, Margin = new Thickness(2, 0, 0, 6), Foreground = ThemeManager.Brush("FgSecondary"), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        Header(oldLabel, 0);
        Header(newLabel, 2);
        Border Frame(UIElement e) => new() { BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(1), Child = e };
        var l = Frame(_left);
        var r = Frame(_right);
        Grid.SetRow(l, 1);
        Grid.SetRow(r, 1);
        Grid.SetColumn(r, 2);
        grid.Children.Add(l);
        grid.Children.Add(r);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(grid);
        Content = root;
        Loaded += (_, _) => { if (_hunks.Count > 0) Go(1); };
        Closed += (_, _) => { _left.Detach(); _right.Detach(); };
    }

    private void Sync(CodeEditor from, CodeEditor to)
    {
        if (_syncing) return;
        _syncing = true;
        to.ScrollToVerticalOffset(from.TextArea.TextView.VerticalOffset);
        to.ScrollToHorizontalOffset(from.TextArea.TextView.HorizontalOffset);
        _syncing = false;
    }

    private void Go(int dir)
    {
        if (_hunks.Count == 0) return;
        _current = Math.Clamp(_current + dir, 0, _hunks.Count - 1);
        var h = _hunks[_current];
        var line = Math.Max(1, h.BStart + 1);
        if (line <= _right.Document.LineCount)
        {
            _right.ScrollTo(line, 1);
            var top = _right.TextArea.TextView.GetVisualTopByDocumentLine(line);
            _right.ScrollToVerticalOffset(Math.Max(0, top - 120));
        }
        _counter.Text = $"Difference {_current + 1} of {_hunks.Count}";
    }

    private sealed class LineBackgroundRenderer(Dictionary<int, Color> lines) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            foreach (var vl in textView.VisualLines)
            {
                if (!lines.TryGetValue(vl.FirstDocumentLine.LineNumber, out var color)) continue;
                var top = vl.VisualTop - textView.VerticalOffset;
                dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, top, Math.Max(textView.ActualWidth, 4000), vl.Height));
            }
        }
    }
}
