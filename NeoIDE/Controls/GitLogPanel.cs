using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Services;
using NeoIDE.Services.Git;

namespace NeoIDE.Controls;

/// <summary>"Git" tool window: commit history with refs, details and changed files of the selected commit.</summary>
public sealed class GitLogPanel : Grid
{
    private readonly ListBox _commits = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _files = new();
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(10, 6, 10, 6) };
    private readonly TextBox _filter = new() { Margin = new Thickness(6, 2, 6, 4), ToolTip = "Filter by message, author or hash" };
    private List<GitCommit> _all = new();
    private string? _pathFilter;
    private readonly TextBlock _title = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };

    public event Action<GitCommit, string>? DiffRequested;     // commit, relative path
    public event Action<GitCommit>? CheckoutRequested;

    public GitLogPanel()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        var left = new DockPanel();
        var top = new DockPanel();
        var refresh = new Button { Style = (Style)Application.Current.FindResource("ToolButton"), ToolTip = "Refresh", Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 } };
        refresh.Click += async (_, _) => await LoadAsync(_pathFilter);
        DockPanel.SetDock(refresh, Dock.Right);
        DockPanel.SetDock(_title, Dock.Left);
        top.Children.Add(refresh);
        top.Children.Add(_title);
        top.Children.Add(_filter);
        DockPanel.SetDock(top, Dock.Top);
        left.Children.Add(top);
        left.Children.Add(_commits);
        Children.Add(left);

        var splitter = new GridSplitter { Width = 9, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
        SetColumn(splitter, 1);
        Children.Add(splitter);

        var right = new DockPanel();
        var detailsHost = new ScrollViewer { Content = _details, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        DockPanel.SetDock(detailsHost, Dock.Top);
        right.Children.Add(detailsHost);
        right.Children.Add(_files);
        SetColumn(right, 2);
        Children.Add(right);

        _filter.TextChanged += (_, _) => Apply();
        _commits.SelectionChanged += async (_, _) => await ShowSelectedAsync();
        _files.MouseDoubleClick += (_, _) =>
        {
            if (_commits.SelectedItem is FrameworkElement { Tag: GitCommit c } && _files.SelectedItem is FrameworkElement { Tag: string path }) DiffRequested?.Invoke(c, path);
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _details.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        // reload when the repository state changes (commit, checkout, pull...) while the log is visible
        GitService.Changed += () => Dispatcher.BeginInvoke(async () =>
        {
            var st = GitService.Status;
            var key = $"{GitService.Root}|{st?.Branch}|{st?.Ahead}|{st?.Behind}|{st?.Upstream}";
            if (key == _lastStateKey) return;
            _lastStateKey = key;
            if (IsVisible) await LoadAsync(_pathFilter);
        });
        IsVisibleChanged += async (_, _) => { if (IsVisible && _all.Count == 0 && GitService.IsRepository) await LoadAsync(_pathFilter); };
    }

    private string? _lastStateKey;

    public async Task LoadAsync(string? path = null)
    {
        _pathFilter = path;
        _title.Text = path == null ? "All branches" : "History of " + System.IO.Path.GetFileName(path);
        _all = GitService.IsRepository ? await GitService.LogAsync(500, path, all: path == null) : new List<GitCommit>();
        Apply();
    }

    private void Apply()
    {
        var q = _filter.Text.Trim();
        _commits.Items.Clear();
        foreach (var c in _all.Where(c => q.Length == 0 || c.Subject.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                           c.Author.Contains(q, StringComparison.OrdinalIgnoreCase) || c.Hash.StartsWith(q, StringComparison.OrdinalIgnoreCase)))
            _commits.Items.Add(Row(c));
        if (_all.Count == 0)
            _commits.Items.Add(new ListBoxItem { Content = new TextBlock { Text = GitService.IsRepository ? "No commits yet" : "Not a Git repository", Margin = new Thickness(6) }, IsHitTestVisible = false });
    }

    private FrameworkElement Row(GitCommit c)
    {
        var dock = new DockPanel { Tag = c };
        var date = new TextBlock { Text = Relative(c.Date), Width = 110, Foreground = ThemeManager.Brush("FgSecondary"), TextAlignment = TextAlignment.Right };
        DockPanel.SetDock(date, Dock.Right);
        var author = new TextBlock { Text = c.Author, Width = 140, Foreground = ThemeManager.Brush("FgSecondary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(author, Dock.Right);
        dock.Children.Add(date);
        dock.Children.Add(author);
        var subject = new StackPanel { Orientation = Orientation.Horizontal };
        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(2, 0, 8, 0), Fill = ThemeManager.Brush(c.Parents.Length > 1 ? "WarnFg" : "Accent"), VerticalAlignment = VerticalAlignment.Center };
        subject.Children.Add(dot);
        foreach (var r in c.Refs.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var label = r.Replace("HEAD -> ", "");
            var isHead = r.StartsWith("HEAD");
            var isTag = r.StartsWith("tag: ");
            var isRemote = label.Contains('/') && !isTag;
            var badge = new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(isTag ? Color.FromArgb(60, 0xD5, 0xB7, 0x78) : isRemote ? Color.FromArgb(55, 0xB1, 0x89, 0xF5) : Color.FromArgb(60, 0x5F, 0xB8, 0x65)),
                Child = new TextBlock { Text = (isHead ? "● " : "") + label.Replace("tag: ", "🏷 "), FontSize = 11, Foreground = ThemeManager.Brush("Fg") },
            };
            subject.Children.Add(badge);
        }
        subject.Children.Add(new TextBlock { Text = c.Subject, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        dock.Children.Add(subject);
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "Copy Revision Number" };
        copy.Click += (_, _) => Clipboard.SetText(c.Hash);
        var checkout = new MenuItem { Header = "Checkout Revision" };
        checkout.Click += (_, _) => CheckoutRequested?.Invoke(c);
        menu.Items.Add(copy);
        menu.Items.Add(checkout);
        dock.ContextMenu = menu;
        return dock;
    }

    private static string Relative(DateTime d)
    {
        var span = DateTime.Now - d;
        if (span.TotalMinutes < 1) return "now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} days ago";
        return d.ToString("dd/MM/yyyy HH:mm");
    }

    private async Task ShowSelectedAsync()
    {
        _files.Items.Clear();
        if (_commits.SelectedItem is not FrameworkElement { Tag: GitCommit c }) { _details.Text = ""; return; }
        var message = await GitService.CommitMessageAsync(c.Hash);
        _details.Text = $"{message}\n\n{c.ShortHash}  ·  {c.Author} <{c.Email}>  ·  {c.Date:dd/MM/yyyy HH:mm}";
        foreach (var (status, path) in await GitService.CommitFilesAsync(c.Hash))
        {
            var kind = status switch { 'A' => GitChangeKind.Added, 'D' => GitChangeKind.Deleted, 'R' => GitChangeKind.Renamed, _ => GitChangeKind.Modified };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Tag = path };
            row.Children.Add(new FileIcon { Path = path, Margin = new Thickness(0, 0, 6, 0) });
            row.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), Foreground = CommitPanel.StatusBrush(kind) });
            row.Children.Add(new TextBlock { Text = "  " + System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/'), Foreground = ThemeManager.Brush("FgSecondary"), FontSize = 11.5 });
            row.ToolTip = "Double-click: show the diff of this file in the commit";
            _files.Items.Add(row);
        }
    }
}
