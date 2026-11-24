using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NeoIDE.Editor;
using NeoIDE.Services;

namespace NeoIDE.Controls;

/// <summary>Markdown file editor with the PhpStorm modes: Editor only / Editor and Preview / Preview only.</summary>
public sealed class MarkdownHost : Grid
{
    public enum Mode { Code, Split, Preview }

    private readonly CodeEditor _editor;
    private readonly MarkdownView _preview = new();
    private readonly GridSplitter _splitter = new() { Width = 9, HorizontalAlignment = HorizontalAlignment.Center, ResizeDirection = GridResizeDirection.Columns, Margin = new Thickness(-4, 0, -4, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Dictionary<Mode, ToggleButton> _buttons = new();
    private Mode _mode;

    public MarkdownHost(CodeEditor editor, string path)
    {
        _editor = editor;
        _preview.BaseDirectory = Path.GetDirectoryName(path);
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        ColumnDefinitions.Add(new ColumnDefinition());

        Children.Add(_editor);
        SetColumn(_splitter, 1);
        Children.Add(_splitter);
        var previewBorder = new Border { Child = _preview };
        previewBorder.SetResourceReference(Border.BackgroundProperty, "EditorBg");
        SetColumn(previewBorder, 2);
        Children.Add(previewBorder);

        // mode switcher (top right, like PhpStorm)
        var bar = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 18, 0),
            CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(2),
        };
        bar.SetResourceReference(Border.BackgroundProperty, "PopupBg");
        bar.SetResourceReference(Border.BorderBrushProperty, "PopupBorder");
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, glyph, tip) in new[] { (Mode.Code, "", "Editor only"), (Mode.Split, "", "Editor and Preview"), (Mode.Preview, "", "Preview only") })
        {
            var b = new ToggleButton
            {
                Width = 28, Height = 24, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0), Cursor = System.Windows.Input.Cursors.Hand,
                Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
                Style = (Style)Application.Current.FindResource("ModeToggle"),
            };
            var m = mode;
            b.Click += (_, _) => SetMode(m);
            _buttons[mode] = b;
            stack.Children.Add(b);
        }
        bar.Child = stack;
        SetColumnSpan(bar, 3);
        Panel.SetZIndex(bar, 200);
        Children.Add(bar);

        _timer.Tick += (_, _) => { _timer.Stop(); Refresh(); };
        _editor.TextChanged += (_, _) => { _timer.Stop(); _timer.Start(); };
        _editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncScroll();
        ThemeManager.ThemeChanged += Refresh;
        DisplayManager.Changed += Refresh;
        Unloaded += (_, _) => { ThemeManager.ThemeChanged -= Refresh; DisplayManager.Changed -= Refresh; };
        Loaded += (_, _) => Refresh();
        SetMode(Enum.TryParse<Mode>(AppSettings.Current.MarkdownMode, out var saved) ? saved : Mode.Split);
    }

    public void SetMode(Mode mode)
    {
        _mode = mode;
        AppSettings.Current.MarkdownMode = mode.ToString();
        AppSettings.Current.Save();
        foreach (var (m, b) in _buttons) b.IsChecked = m == mode;
        ColumnDefinitions[0].Width = mode == Mode.Preview ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ColumnDefinitions[1].Width = mode == Mode.Split ? new GridLength(1) : new GridLength(0);
        ColumnDefinitions[2].Width = mode == Mode.Code ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        _splitter.Visibility = mode == Mode.Split ? Visibility.Visible : Visibility.Collapsed;
        if (mode != Mode.Code) Refresh();
        if (mode != Mode.Preview) Dispatcher.BeginInvoke(() => _editor.TextArea.Focus(), DispatcherPriority.Input);
    }

    private void Refresh()
    {
        if (_mode == Mode.Code) return;
        _preview.Render(_editor.Text);
    }

    /// <summary>Keeps the preview at the same relative position as the editor.</summary>
    private void SyncScroll()
    {
        if (_mode != Mode.Split) return;
        var view = _editor.TextArea.TextView;
        var max = Math.Max(1, view.DocumentHeight - view.ActualHeight);
        var ratio = Math.Clamp(view.VerticalOffset / max, 0, 1);
        var sv = _preview.FindScrollViewer();
        if (sv != null) sv.ScrollToVerticalOffset(ratio * sv.ScrollableHeight);
    }
}
