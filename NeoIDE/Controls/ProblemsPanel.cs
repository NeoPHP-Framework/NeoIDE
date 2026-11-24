using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NeoIDE.Services;
using NeoIDE.Services.Diagnostics;

namespace NeoIDE.Controls;

/// <summary>
/// "Problems" tool window (Alt+6): problems of the current file or of the whole project, grouped by file —
/// IDE analysis, php -l, NeoPHP checks and PHPStan (inspection of the project on demand).
/// </summary>
public sealed class ProblemsPanel : DockPanel
{
    private readonly TreeView _tree = new() { BorderThickness = new Thickness(0), Padding = new Thickness(0, 2, 0, 4) };
    private readonly ToggleButton _fileMode, _projectMode, _showWarnings, _showHints;
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
    private readonly Button _inspect;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _inspecting;

    /// <summary>File of the selected editor tab (set by the main window).</summary>
    public Func<string?> CurrentFile { get; set; } = () => null;
    public event Action<string, int, int>? NavigateRequested;   // file, line, offset (-1 = use the line)
    public event Action<string>? StatusMessage;
    public event Action? InstallPhpStanRequested;

    private static Style S(string key) => (Style)Application.Current.FindResource(key);
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public ProblemsPanel()
    {
        var bar = new DockPanel { Margin = new Thickness(6, 3, 6, 3) };
        ToggleButton Mode(string text, string tip)
        {
            var t = new ToggleButton { Style = S("ModeToggle"), Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0) };
            return t;
        }
        _fileMode = Mode("Current File", "Problems of the file opened in the editor");
        _projectMode = Mode("Project", "Problems of every file: opened files, PHPStan inspection");
        _fileMode.IsChecked = true;
        _fileMode.Click += (_, _) => { _fileMode.IsChecked = true; _projectMode.IsChecked = false; Rebuild(); };
        _projectMode.Click += (_, _) => { _projectMode.IsChecked = true; _fileMode.IsChecked = false; Rebuild(); };
        _showWarnings = new ToggleButton { Style = S("ModeToggle"), Content = "Warnings", IsChecked = true, ToolTip = "Show the warnings", Margin = new Thickness(10, 0, 4, 0) };
        _showHints = new ToggleButton { Style = S("ModeToggle"), Content = "Hints", IsChecked = false, ToolTip = "Show the hints" };
        _showWarnings.Click += (_, _) => Rebuild();
        _showHints.Click += (_, _) => Rebuild();

        _inspect = new Button { Style = S("ToolButton"), Padding = new Thickness(6, 2, 6, 2), ToolTip = "Analyse the project with PHPStan (Code › Inspect Code)" };
        _inspect.Content = IconText("", "Inspect project");
        _inspect.Click += async (_, _) => await InspectProjectAsync();
        var clear = new Button { Style = S("ToolButton"), ToolTip = "Clear the PHPStan results", Content = new TextBlock { Text = "", FontFamily = Icons, FontSize = 12 } };
        clear.Click += (_, _) => { DiagnosticsStore.SetProject("phpstan", Array.Empty<Diagnostic>()); };

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_inspect);
        right.Children.Add(clear);
        SetDock(right, Dock.Right);
        bar.Children.Add(right);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_fileMode);
        left.Children.Add(_projectMode);
        left.Children.Add(_showWarnings);
        left.Children.Add(_showHints);
        left.Children.Add(_summary);
        bar.Children.Add(left);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(_tree);
        VirtualizingPanel.SetIsVirtualizing(_tree, true);
        VirtualizingPanel.SetVirtualizationMode(_tree, VirtualizationMode.Recycling);

        _tree.MouseDoubleClick += (_, e) => { if (Open()) e.Handled = true; };
        _tree.KeyDown += (_, e) => { if (e.Key == Key.Enter && Open()) e.Handled = true; };
        _refresh.Tick += (_, _) => { _refresh.Stop(); Rebuild(); };
        Action<string?> changed = _ => Dispatcher.BeginInvoke(() => { _refresh.Stop(); _refresh.Start(); });
        Loaded += (_, _) =>
        {
            DiagnosticsStore.Changed -= changed;
            DiagnosticsStore.Changed += changed;
            ThemeManager.ThemeChanged -= Rebuild;
            ThemeManager.ThemeChanged += Rebuild;
        };
        Unloaded += (_, _) => { DiagnosticsStore.Changed -= changed; ThemeManager.ThemeChanged -= Rebuild; };
        IsVisibleChanged += (_, _) => { if (IsVisible) Rebuild(); };
    }

    private static StackPanel IconText(string glyph, string text)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    public bool ProjectMode
    {
        get => _projectMode.IsChecked == true;
        set { _projectMode.IsChecked = value; _fileMode.IsChecked = !value; Rebuild(); }
    }

    /// <summary>The selected editor changed.</summary>
    public void CurrentFileChanged() { if (!ProjectMode && IsVisible) { _refresh.Stop(); _refresh.Start(); } }

    private bool Open()
    {
        if (_tree.SelectedItem is TreeViewItem { Tag: Diagnostic d })
        {
            NavigateRequested?.Invoke(d.File, d.Line, d.Start);
            return true;
        }
        return false;
    }

    private bool Visible(Diagnostic d) => d.Severity switch
    {
        Severity.Error => true,
        Severity.Warning => _showWarnings.IsChecked == true,
        _ => _showHints.IsChecked == true,
    };

    public void Rebuild()
    {
        if (!IsVisible) return;
        List<Diagnostic> all;
        if (ProjectMode) all = DiagnosticsStore.All();
        else all = CurrentFile() is { } f ? DiagnosticsStore.For(f) : new List<Diagnostic>();
        // the same problem reported by two sources (php -l and PHPStan) is shown once
        var visible = all.Where(Visible)
            .GroupBy(d => (Path.GetFullPath(d.File).ToLowerInvariant(), d.Line, d.Message))
            .Select(g => g.First()).ToList();
        var errors = all.Count(d => d.Severity == Severity.Error);
        var warnings = all.Count(d => d.Severity == Severity.Warning);
        _summary.Text = errors + warnings == 0 ? (ProjectMode ? "No problems found" : "No problems in this file")
            : $"{errors} error(s), {warnings} warning(s)";
        _summary.Foreground = ThemeManager.Brush(errors > 0 ? "ErrorFg" : warnings > 0 ? "WarnFg" : "FgSecondary");

        var expanded = _tree.Items.OfType<TreeViewItem>().Where(i => i.IsExpanded).Select(i => i.Tag as string).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var first = _tree.Items.Count == 0;
        _tree.Items.Clear();
        foreach (var group in visible.GroupBy(d => Path.GetFullPath(d.File), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Min(d => d.Severity)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var fileItem = new TreeViewItem { Tag = group.Key, IsExpanded = first || expanded.Contains(group.Key) || !ProjectMode || expanded.Count == 0 };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new FileIcon { Path = group.Key, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = Path.GetFileName(group.Key), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var dir = Workspace.Root != null && group.Key.StartsWith(Workspace.Root, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(Path.GetRelativePath(Workspace.Root, group.Key)) : Path.GetDirectoryName(group.Key);
            header.Children.Add(new TextBlock { Text = "  " + dir, Foreground = ThemeManager.Brush("FgSecondary"), VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = $"  {group.Count()} problem(s)", Foreground = ThemeManager.Brush("FgSecondary"), VerticalAlignment = VerticalAlignment.Center });
            fileItem.Header = header;
            foreach (var d in group.OrderBy(d => d.Line).ThenBy(d => d.Column).Take(500))
                fileItem.Items.Add(new TreeViewItem { Tag = d, Header = Row(d) });
            _tree.Items.Add(fileItem);
        }
        if (_tree.Items.Count == 0)
            _tree.Items.Add(new TreeViewItem
            {
                IsEnabled = false,
                Header = new TextBlock
                {
                    Text = ProjectMode ? "No problems. \"Inspect project\" runs PHPStan on the whole project." : CurrentFile() == null ? "No file opened" : "No problems in this file",
                    Foreground = ThemeManager.Brush("FgSecondary"), Margin = new Thickness(6, 4, 0, 0),
                },
            });
    }

    private static FrameworkElement Row(Diagnostic d)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        var (glyph, color) = d.Severity switch
        {
            Severity.Error => ("", "ErrorFg"),
            Severity.Warning => ("", "WarnFg"),
            _ => ("", "FgSecondary"),
        };
        p.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 12, Foreground = ThemeManager.Brush(color), Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        var message = d.Message.Split('\n')[0];
        p.Children.Add(new TextBlock { Text = message, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 1100, TextTrimming = TextTrimming.CharacterEllipsis });
        var source = d.Source switch { "php" => "PHP", "phpstan" => "PHPStan", "neophp" => "NeoPHP", _ => "NeoIDE" };
        p.Children.Add(new TextBlock
        {
            Text = $"   {source}{(d.Code != null ? " · " + d.Code : "")}   :{d.Line}{(d.Column > 0 ? ":" + d.Column : "")}",
            Foreground = ThemeManager.Brush("FgSecondary"), VerticalAlignment = VerticalAlignment.Center,
        });
        if (d.Message.Contains('\n')) p.ToolTip = d.Message;
        return p;
    }

    // ------------------------------------------------------------------ PHPStan

    public async Task InspectProjectAsync()
    {
        if (_inspecting || Workspace.Root == null) return;
        if (Checkers.PhpStanBinary(Workspace.Root) == null)
        {
            if (Dialogs.MessageDialog.Confirm("Inspect project", "PHPStan is not installed in this project.\n\nInstall it now as a development dependency (composer require --dev phpstan/phpstan)?", "Install PHPStan"))
                InstallPhpStanRequested?.Invoke();
            return;
        }
        _inspecting = true;
        _inspect.IsEnabled = false;
        _inspect.Content = IconText("", "Inspecting…");
        StatusMessage?.Invoke("PHPStan: analysing the project…");
        try
        {
            var (list, error) = await Checkers.PhpStanAsync(Array.Empty<string>(), 600000);
            DiagnosticsStore.SetProject("phpstan", list);
            ProjectMode = true;
            StatusMessage?.Invoke(error != null && list.Count == 0 ? "PHPStan: " + error
                : $"PHPStan: {list.Count} problem(s) in {list.Select(d => d.File).Distinct(StringComparer.OrdinalIgnoreCase).Count()} file(s)");
        }
        finally
        {
            _inspecting = false;
            _inspect.IsEnabled = true;
            _inspect.Content = IconText("", "Inspect project");
        }
    }
}
