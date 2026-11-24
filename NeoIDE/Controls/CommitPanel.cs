using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Services;
using NeoIDE.Services.Git;

namespace NeoIDE.Controls;

/// <summary>PhpStorm-like "Commit" tool window: changed files with checkboxes, message, amend, commit / commit and push.</summary>
public sealed class CommitPanel : DockPanel
{
    private readonly ListBox _list = new() { Padding = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _message = new()
    {
        AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 220, VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6),
    };
    private readonly TextBlock _placeholder = new() { Text = "Commit message", Margin = new Thickness(9, 7, 0, 0), IsHitTestVisible = false };
    private readonly CheckBox _amend = new() { Content = "Amend", ToolTip = "Replace the last commit (git commit --amend)" };
    private readonly Button _commit = new() { Content = "Commit" };
    private readonly Button _commitPush = new() { Content = "Commit and Push…", Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly StackPanel _noRepo = new() { Margin = new Thickness(14), Visibility = Visibility.Collapsed };

    public event Action<GitChange>? DiffRequested;
    public event Action<string>? OpenRequested;
    public event Action<List<GitChange>>? RollbackRequested;
    public event Action<string, List<GitChange>, bool, bool>? CommitRequested; // message, files, amend, push
    public event Action? InitRequested;

    public CommitPanel()
    {
        _commit.Style = (Style)Application.Current.FindResource("PrimaryButton");
        _commit.Click += (_, _) => DoCommit(false);
        _commitPush.Click += (_, _) => DoCommit(true);
        _message.TextChanged += (_, _) => _placeholder.Visibility = _message.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _message.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { DoCommit(false); e.Handled = true; } };
        _amend.Click += async (_, _) =>
        {
            if (_amend.IsChecked == true && _message.Text.Trim().Length == 0)
            {
                var (_, last, _) = await GitService.Run("log -1 --pretty=%B");
                _message.Text = last.Trim();
            }
        };

        // toolbar
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 2) };
        toolbar.Children.Add(ToolButton("", "Refresh", async () => await GitService.RefreshAsync()));
        toolbar.Children.Add(ToolButton("", "Rollback selected changes", () => { var sel = Selected(); if (sel.Count > 0) RollbackRequested?.Invoke(sel); }));
        toolbar.Children.Add(ToolButton("", "Show diff (double-click)", () => { if (_list.SelectedItem is FrameworkElement { Tag: GitChange c }) DiffRequested?.Invoke(c); }));
        toolbar.Children.Add(ToolButton("", "Select all", () => { foreach (var c in GitService.Status?.Changes ?? new()) _checked.Add(c.Path); Rebuild(); }));
        toolbar.Children.Add(ToolButton("", "Select none", () => { _checked.Clear(); Rebuild(); }));
        SetDock(toolbar, Dock.Top);
        Children.Add(toolbar);

        // bottom: message + buttons
        var bottom = new StackPanel { Margin = new Thickness(10, 6, 10, 10) };
        bottom.Children.Add(_summary);
        var msgHost = new Grid();
        msgHost.Children.Add(_message);
        msgHost.Children.Add(_placeholder);
        bottom.Children.Add(msgHost);
        var options = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        options.Children.Add(_amend);
        bottom.Children.Add(options);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_commit);
        buttons.Children.Add(_commitPush);
        bottom.Children.Add(buttons);
        bottom.Children.Add(new TextBlock { Text = "Ctrl+Enter: commit", FontSize = 11, Margin = new Thickness(0, 6, 0, 0), Foreground = ThemeManager.Brush("FgSecondary") });
        var bottomBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = bottom };
        bottomBorder.SetResourceReference(Border.BorderBrushProperty, "SubtleBorder");
        SetDock(bottomBorder, Dock.Bottom);
        Children.Add(bottomBorder);

        // no repository
        _noRepo.Children.Add(new TextBlock { Text = "This project is not under Git version control.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var init = new Button { Content = "Create Git Repository…", HorizontalAlignment = HorizontalAlignment.Left };
        init.Click += (_, _) => InitRequested?.Invoke();
        _noRepo.Children.Add(init);
        SetDock(_noRepo, Dock.Top);
        Children.Add(_noRepo);

        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is FrameworkElement { Tag: GitChange c }) DiffRequested?.Invoke(c); };
        _list.KeyDown += (_, e) =>
        {
            if (_list.SelectedItem is not FrameworkElement { Tag: GitChange c }) return;
            if (e.Key == Key.Space) { Toggle(c.Path); e.Handled = true; }
            if (e.Key == Key.F4) { OpenRequested?.Invoke(c.FullPath); e.Handled = true; }
            if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control) { DiffRequested?.Invoke(c); e.Handled = true; }
        };
        Children.Add(_list);

        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        GitService.Changed += () => Dispatcher.BeginInvoke(Rebuild);
        ThemeManager.ThemeChanged += Rebuild;
    }

    private static Button ToolButton(string glyph, string tip, Action action)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("ToolButton"), ToolTip = tip,
            Content = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 },
        };
        b.Click += (_, _) => action();
        return b;
    }

    public void FocusMessage() => _message.Focus();

    private void Toggle(string path)
    {
        if (!_checked.Remove(path)) _checked.Add(path);
        Rebuild();
    }

    private List<GitChange> Selected() =>
        (GitService.Status?.Changes ?? new()).Where(c => _checked.Contains(c.Path)).ToList();

    public static Brush StatusBrush(GitChangeKind kind)
    {
        var dark = ThemeManager.IsDark;
        string hex = kind switch
        {
            GitChangeKind.Added => dark ? "#629755" : "#067D17",
            GitChangeKind.Untracked => dark ? "#D1675A" : "#993300",
            GitChangeKind.Deleted => dark ? "#8C8C8C" : "#6C707E",
            GitChangeKind.Conflicted => dark ? "#E5534B" : "#D50000",
            _ => dark ? "#6897BB" : "#0032A0",
        };
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public void Rebuild()
    {
        var status = GitService.Status;
        var hasRepo = GitService.IsRepository;
        _noRepo.Visibility = hasRepo ? Visibility.Collapsed : Visibility.Visible;
        _list.Visibility = hasRepo ? Visibility.Visible : Visibility.Collapsed;
        _commit.IsEnabled = _commitPush.IsEnabled = hasRepo;
        _list.Items.Clear();
        if (status == null) { _summary.Text = ""; return; }

        // new tracked changes are checked by default, unversioned ones are not (like PhpStorm)
        foreach (var c in status.Changes)
            if (_known.Add(c.Path) && c.Kind != GitChangeKind.Untracked) _checked.Add(c.Path);
        _known.IntersectWith(status.Changes.Select(c => c.Path));
        _checked.IntersectWith(status.Changes.Select(c => c.Path));

        var tracked = status.Changes.Where(c => c.Kind != GitChangeKind.Untracked).OrderBy(c => c.Path).ToList();
        var untracked = status.Changes.Where(c => c.Kind == GitChangeKind.Untracked).OrderBy(c => c.Path).ToList();
        void Group(string title, List<GitChange> items)
        {
            if (items.Count == 0) return;
            var head = new TextBlock { Text = $"{title}   {items.Count} file{(items.Count > 1 ? "s" : "")}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 6, 0, 2) };
            _list.Items.Add(new ListBoxItem { Content = head, Focusable = false, IsHitTestVisible = false });
            foreach (var c in items) _list.Items.Add(Row(c));
        }
        Group("Changes", tracked);
        Group("Unversioned Files", untracked);
        if (status.Changes.Count == 0)
            _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "No changes", Margin = new Thickness(4, 8, 0, 0), Foreground = ThemeManager.Brush("FgSecondary") }, IsHitTestVisible = false });
        var n = _checked.Count;
        _summary.Text = $"{status.Branch}{(status.Ahead > 0 ? $"  ↑{status.Ahead}" : "")}{(status.Behind > 0 ? $"  ↓{status.Behind}" : "")}  ·  {n} of {status.Changes.Count} file(s) selected";
    }

    private FrameworkElement Row(GitChange c)
    {
        var dock = new DockPanel { Tag = c, Margin = new Thickness(10, 0, 0, 0), Background = Brushes.Transparent };
        var box = new CheckBox { IsChecked = _checked.Contains(c.Path), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        box.Click += (_, _) => { if (box.IsChecked == true) _checked.Add(c.Path); else _checked.Remove(c.Path); Rebuild(); };
        SetDock(box, Dock.Left);
        dock.Children.Add(box);
        var icon = new FileIcon { Path = c.FullPath, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new System.Windows.Documents.Run(c.FileName)
        {
            Foreground = StatusBrush(c.Kind),
            TextDecorations = c.Kind == GitChangeKind.Deleted ? TextDecorations.Strikethrough : null,
        });
        var dir = c.Directory;
        if (dir.Length > 0) name.Inlines.Add(new System.Windows.Documents.Run("  " + dir) { Foreground = ThemeManager.Brush("FgSecondary"), FontSize = 11.5 });
        if (c.OldPath != null) name.Inlines.Add(new System.Windows.Documents.Run($"  ← {c.OldPath}") { Foreground = ThemeManager.Brush("FgSecondary"), FontSize = 11.5 });
        dock.Children.Add(name);
        dock.ToolTip = $"{c.Path}  ({c.Kind}{(c.IsStaged ? ", staged" : "")})";
        var menu = new ContextMenu();
        void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("Show Diff", () => DiffRequested?.Invoke(c));
        Item("Jump to Source", () => OpenRequested?.Invoke(c.FullPath));
        Item("Rollback…", () => RollbackRequested?.Invoke(new List<GitChange> { c }));
        dock.ContextMenu = menu;
        return dock;
    }

    private void DoCommit(bool push)
    {
        var msg = _message.Text.Trim();
        var files = Selected();
        if (msg.Length == 0) { _summary.Text = "Enter a commit message"; _summary.Foreground = ThemeManager.Brush("ErrorFg"); _message.Focus(); return; }
        if (files.Count == 0 && _amend.IsChecked != true) { _summary.Text = "Select the files to commit"; _summary.Foreground = ThemeManager.Brush("ErrorFg"); return; }
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        CommitRequested?.Invoke(msg, files, _amend.IsChecked == true, push);
    }

    public void CommitSucceeded()
    {
        _message.Text = "";
        _amend.IsChecked = false;
    }
}
