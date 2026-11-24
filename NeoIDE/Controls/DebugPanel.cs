using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using NeoIDE.Services;
using NeoIDE.Services.Debug;

namespace NeoIDE.Controls;

/// <summary>
/// "Debug" tool window (Alt+5): listen for Xdebug, resume / step / stop, call stack, variables (locals, superglobals),
/// watches, evaluation of expressions and the output of the script.
/// </summary>
public sealed class DebugPanel : DockPanel
{
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static Style S(string key) => (Style)Application.Current.FindResource(key);

    private readonly ToggleButton _listen;
    private readonly Button _resume, _stop, _over, _into, _out;
    private readonly ToggleButton _mute;
    private readonly TextBlock _state = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly ListBox _frames = new() { BorderThickness = new Thickness(0) };
    private readonly TreeView _vars = new() { BorderThickness = new Thickness(0) };
    private readonly TextBox _evaluate = new() { Margin = new Thickness(4, 4, 4, 2), ToolTip = "Evaluate a PHP expression in the selected frame (Enter) — Ctrl+Enter: add it as a watch" };
    private readonly TextEditor _console;
    private TreeViewItem? _evaluated;

    public event Action<string, int>? NavigateRequested;
    public event Action? ListenToggled;

    public DebugPanel()
    {
        // ---- toolbar
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
        _listen = new ToggleButton { Style = S("ModeToggle"), ToolTip = "Start / stop listening for PHP debug connections (Xdebug, port 9003)" };
        _listen.Content = Glyph("", "Listen");
        _listen.Click += (_, _) => ListenToggled?.Invoke();
        bar.Children.Add(_listen);
        bar.Children.Add(new Border { Width = 1, Height = 18, Margin = new Thickness(8, 0, 6, 0), Background = ThemeManager.Brush("SubtleBorder") });
        Button Tool(string glyph, string tip, string colorKey, Func<Task> action)
        {
            var b = new Button { Style = S("ToolButton"), ToolTip = tip, Content = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 14, Foreground = ThemeManager.Brush(colorKey) } };
            b.Click += async (_, _) => await action();
            bar.Children.Add(b);
            return b;
        }
        _resume = Tool("", "Resume program (F9)", "SuccessFg", DebugService.Resume);
        _stop = Tool("", "Stop (Ctrl+F2)", "ErrorFg", DebugService.Stop);
        _over = Tool("", "Step over (F8)", "Accent", DebugService.StepOver);
        _into = Tool("", "Step into (F7)", "Accent", DebugService.StepInto);
        _out = Tool("", "Step out (Shift+F8)", "Accent", DebugService.StepOut);
        _mute = new ToggleButton { Style = S("ModeToggle"), ToolTip = "Mute breakpoints", Margin = new Thickness(6, 0, 0, 0), Content = Glyph("", "Mute") };
        _mute.Click += (_, _) => Breakpoints.SetMuted(_mute.IsChecked == true);
        bar.Children.Add(_mute);
        bar.Children.Add(_state);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        // ---- frames | variables | console
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star), MinWidth = 160 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 140 });

        grid.Children.Add(Titled("Frames", _frames, 0));
        AddSplitter(grid, 1);
        var varsPanel = new DockPanel();
        var evalHost = new Grid();
        evalHost.Children.Add(_evaluate);
        var hint = new TextBlock
        {
            Text = "Evaluate expression (Enter) — add a watch (Ctrl+Enter)", IsHitTestVisible = false, Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("FgSecondary"),
        };
        evalHost.Children.Add(hint);
        _evaluate.TextChanged += (_, _) => hint.Visibility = _evaluate.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetDock(evalHost, Dock.Top);
        varsPanel.Children.Add(evalHost);
        varsPanel.Children.Add(_vars);
        grid.Children.Add(Titled("Variables", varsPanel, 2));
        AddSplitter(grid, 3);
        _console = new TextEditor
        {
            IsReadOnly = true, FontFamily = DisplayManager.EditorFont, FontSize = 12, WordWrap = true, ShowLineNumbers = false,
            Padding = new Thickness(6, 2, 2, 2), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        grid.Children.Add(Titled("Console", _console, 4));
        Children.Add(grid);

        _frames.SelectionChanged += async (_, _) =>
        {
            if (_frames.SelectedItem is ListBoxItem { Tag: DebugFrame f })
            {
                DebugService.SelectedFrame = f.Level;
                NavigateRequested?.Invoke(f.File, f.Line);
                await LoadVariablesAsync();
            }
        };
        _evaluate.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || _evaluate.Text.Trim().Length == 0) return;
            e.Handled = true;
            var expr = _evaluate.Text.Trim();
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (!Workspace.Settings.Watches.Contains(expr)) { Workspace.Settings.Watches.Add(expr); Workspace.SaveSettings(); }
                _evaluate.Text = "";
                await LoadVariablesAsync();
                return;
            }
            await EvaluateAsync(expr);
        };
        _vars.MouseDoubleClick += async (_, e) =>
        {
            if (_vars.SelectedItem is not TreeViewItem { Tag: VarTag tag } || tag.Value.HasChildren || tag.IsWatch) return;
            e.Handled = true;
            var v = Dialogs.InputDialog.Ask("Set value", $"New value of {tag.Value.FullName} (PHP expression):", tag.Value.Type == "string" ? $"'{tag.Value.Value.Replace("'", "\\'")}'" : tag.Value.Display);
            if (v == null) return;
            var error = await DebugService.SetValue(tag.Value, v, DebugService.SelectedFrame);
            if (error != null) Append("Set value: " + error, true);
            await LoadVariablesAsync();
        };

        // static events: only while the panel is in a window (a closed window must not keep it alive)
        Action stateChanged = () => { if (Dispatcher.CheckAccess()) OnStateChanged(); else Dispatcher.BeginInvoke(OnStateChanged); };
        Action<string?> breakpointsChanged = _ => Dispatcher.BeginInvoke(() => _mute.IsChecked = Breakpoints.Muted);
        Loaded += (_, _) =>
        {
            DebugService.StateChanged -= stateChanged;
            DebugService.StateChanged += stateChanged;
            Breakpoints.Changed -= breakpointsChanged;
            Breakpoints.Changed += breakpointsChanged;
            ThemeManager.ThemeChanged -= ApplyTheme;
            ThemeManager.ThemeChanged += ApplyTheme;
            ApplyTheme();
            OnStateChanged();
        };
        Unloaded += (_, _) =>
        {
            DebugService.StateChanged -= stateChanged;
            Breakpoints.Changed -= breakpointsChanged;
            ThemeManager.ThemeChanged -= ApplyTheme;
        };
        ApplyTheme();
        OnStateChanged();
    }

    /// <summary>Output of the debugged script and of the debugger (kept while the panel is hidden).</summary>
    public void AttachOutput() => DebugService.Output += Append;
    public void DetachOutput() => DebugService.Output -= Append;

    private int _shownPause = -1;

    private sealed record VarTag(DebugValue Value, bool IsWatch, string? WatchExpression = null);

    private static StackPanel Glyph(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    private static FrameworkElement Titled(string title, UIElement content, int column)
    {
        var dock = new DockPanel();
        var header = new TextBlock { Text = title, Style = S("SecondaryText"), Margin = new Thickness(8, 2, 0, 2), FontWeight = FontWeights.SemiBold };
        SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(content);
        Grid.SetColumn(dock, column);
        return dock;
    }

    private static void AddSplitter(Grid grid, int column)
    {
        var s = new GridSplitter { Width = 9, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns, Background = Brushes.Transparent };
        Grid.SetColumn(s, column);
        grid.Children.Add(s);
    }

    private void ApplyTheme()
    {
        _console.Background = ThemeManager.Brush("ConsoleBg");
        _console.Foreground = ThemeManager.Brush("Fg");
    }

    public void Append(string text, bool isError)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Append(text, isError)); return; }
        var doc = _console.Document;
        if (doc.TextLength > 0 && doc.GetCharAt(doc.TextLength - 1) != '\n') doc.Insert(doc.TextLength, "\n");
        doc.Insert(doc.TextLength, (isError ? "⚠ " : "") + text.TrimEnd('\n'));
        if (doc.TextLength > 400_000) doc.Remove(0, doc.TextLength - 300_000);
        _console.ScrollToEnd();
    }

    private async void OnStateChanged()
    {
        var s = DebugService.State;
        _listen.IsChecked = DebugService.IsListening;
        var paused = s == DebugState.Paused;
        _resume.IsEnabled = _over.IsEnabled = _into.IsEnabled = _out.IsEnabled = paused;
        _stop.IsEnabled = s is DebugState.Paused or DebugState.Running;
        _evaluate.IsEnabled = paused;
        _state.Text = s switch
        {
            DebugState.Idle => "Not listening — start listening, then run the script with Xdebug (Run › Debug)",
            DebugState.Listening => $"Waiting for a connection on port {DebugService.Port}…",
            DebugState.Running => "Running " + (DebugService.ScriptName != null ? Path.GetFileName(DebugService.ScriptName) : ""),
            DebugState.Paused => DebugService.Position is { } p ? $"Paused at {Path.GetFileName(p.file)}:{p.line}" : "Paused",
            _ => "",
        };
        _state.Foreground = ThemeManager.Brush(paused ? "WarnFg" : s == DebugState.Running ? "SuccessFg" : "FgSecondary");
        if (paused && _shownPause == DebugService.PauseCount) return;
        if (paused)
        {
            _shownPause = DebugService.PauseCount;
            _frames.Items.Clear();
            foreach (var f in DebugService.Stack)
            {
                var label = new TextBlock();
                label.Inlines.Add(new System.Windows.Documents.Run(f.Where.Length > 0 ? f.Where : "{main}"));
                label.Inlines.Add(new System.Windows.Documents.Run($"   {Path.GetFileName(f.File)}:{f.Line}") { Foreground = ThemeManager.Brush("FgSecondary") });
                _frames.Items.Add(new ListBoxItem { Content = label, Tag = f, ToolTip = $"{f.File}:{f.Line}" });
            }
            if (_frames.Items.Count > 0) _frames.SelectedIndex = 0; // loads the variables
            else await LoadVariablesAsync();
        }
        else if (s != DebugState.Running)
        {
            _frames.Items.Clear();
            _vars.Items.Clear();
            _evaluated = null;
        }
    }

    private async Task LoadVariablesAsync()
    {
        var depth = DebugService.SelectedFrame;
        var locals = await DebugService.Variables(depth, 0);
        var globals = await DebugService.Variables(depth, 1);
        _vars.Items.Clear();
        if (_evaluated != null) _vars.Items.Add(_evaluated);
        foreach (var w in Workspace.Settings.Watches.ToList())
        {
            var (value, error) = await DebugService.Evaluate(w, depth);
            var item = value != null ? Node(value, true, w) : new TreeViewItem { Header = Header(w, error ?? "error", "watch", true), Tag = null };
            item.ContextMenu = WatchMenu(w);
            _vars.Items.Add(item);
        }
        foreach (var v in locals) _vars.Items.Add(Node(v, false));
        if (globals.Count > 0)
        {
            var g = new TreeViewItem { Header = new TextBlock { Text = "Superglobals", Foreground = ThemeManager.Brush("FgSecondary") } };
            foreach (var v in globals) g.Items.Add(Node(v, false));
            _vars.Items.Add(g);
        }
        if (_vars.Items.Count == 0) _vars.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "No variables", Foreground = ThemeManager.Brush("FgSecondary") }, IsEnabled = false });
    }

    private ContextMenu WatchMenu(string expression)
    {
        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "Remove watch" };
        remove.Click += async (_, _) => { Workspace.Settings.Watches.Remove(expression); Workspace.SaveSettings(); await LoadVariablesAsync(); };
        menu.Items.Add(remove);
        return menu;
    }

    private async Task EvaluateAsync(string expr)
    {
        var (value, error) = await DebugService.Evaluate(expr, DebugService.SelectedFrame);
        if (value == null) { Append($"{expr}: {error}", true); return; }
        Append($"{expr} = {value.Display}", false);
        _evaluated = Node(value, false);
        ((StackPanel)_evaluated.Header).Children.Insert(0, new TextBlock { Text = "= ", Foreground = ThemeManager.Brush("Accent"), FontWeight = FontWeights.Bold });
        await LoadVariablesAsync();
    }

    private static FrameworkElement Header(string name, string value, string type, bool watch, bool error = false)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        if (watch) p.Children.Add(new TextBlock { Text = "", FontFamily = Icons, FontSize = 11, Margin = new Thickness(0, 1, 6, 0), Foreground = ThemeManager.Brush("Accent"), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = name, Foreground = ThemeManager.Brush("Fg"), FontFamily = DisplayManager.EditorFont, FontSize = 12.5 });
        p.Children.Add(new TextBlock { Text = " = ", Foreground = ThemeManager.Brush("FgSecondary"), FontFamily = DisplayManager.EditorFont, FontSize = 12.5 });
        var color = error ? "ErrorFg" : type switch { "string" => "SuccessFg", "int" or "float" => "LinkFg", "bool" or "null" => "WarnFg", _ => "FgSecondary" };
        p.Children.Add(new TextBlock { Text = value, Foreground = ThemeManager.Brush(color), FontFamily = DisplayManager.EditorFont, FontSize = 12.5, MaxWidth = 900, TextTrimming = TextTrimming.CharacterEllipsis });
        if (type is not ("string" or "int" or "float" or "bool" or "null"))
            p.Children.Add(new TextBlock { Text = "  " + type, Foreground = ThemeManager.Brush("FgSecondary"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    private TreeViewItem Node(DebugValue v, bool watch, string? watchExpr = null)
    {
        var item = new TreeViewItem { Header = Header(watch ? watchExpr! : v.Name, v.Display, v.Type, watch), Tag = new VarTag(v, watch, watchExpr) };
        if (v.Type == "string" && v.Value.Length > 60) item.ToolTip = v.Value.Length > 4000 ? v.Value[..4000] + "…" : v.Value;
        if (v.Children is { Count: > 0 })
            foreach (var c in v.Children) item.Items.Add(Node(c, false));
        else if (v.HasChildren)
        {
            item.Items.Add(new TreeViewItem { Header = "…" });
            item.Expanded += async (_, e) =>
            {
                if (e.OriginalSource != item || item.Items.Count != 1 || item.Items[0] is not TreeViewItem { Tag: null }) return;
                var children = await DebugService.Children(v, DebugService.SelectedFrame);
                item.Items.Clear();
                foreach (var c in children) item.Items.Add(Node(c, false));
                if (children.Count == 0) item.Items.Add(new TreeViewItem { Header = new TextBlock { Text = "(empty)", Foreground = ThemeManager.Brush("FgSecondary") }, IsEnabled = false });
            };
        }
        var copy = new MenuItem { Header = "Copy value" };
        copy.Click += (_, _) => Clipboard.SetText(v.Type == "string" ? v.Value : v.Display);
        item.ContextMenu ??= new ContextMenu();
        item.ContextMenu.Items.Add(copy);
        if (!watch && v.FullName.Length > 0)
        {
            var addWatch = new MenuItem { Header = "Add to watches" };
            addWatch.Click += async (_, _) => { if (!Workspace.Settings.Watches.Contains(v.FullName)) { Workspace.Settings.Watches.Add(v.FullName); Workspace.SaveSettings(); } await LoadVariablesAsync(); };
            item.ContextMenu.Items.Add(addWatch);
        }
        return item;
    }
}
