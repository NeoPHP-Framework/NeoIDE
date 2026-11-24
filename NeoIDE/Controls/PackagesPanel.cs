using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Dialogs;
using NeoIDE.Services;

namespace NeoIDE.Controls;

/// <summary>
/// Composer package manager of the project: installed (required / dev), platform requirements,
/// optional packages suggested by the dependencies, Packagist search (NeoPHP packages installed with neophp:package:install).
/// </summary>
public sealed class PackagesPanel : DockPanel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _root;
    private readonly ListBox _installed = new(), _requirements = new(), _suggested = new(), _search = new();
    private readonly TextBox _query = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly CheckBox _neoOnly = new() { Content = "NeoPHP packages only", VerticalAlignment = VerticalAlignment.Center };
    private readonly ConsoleView _log = new() { Height = 170, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Margin = new Thickness(4, 6, 0, 6) };
    private readonly TabControl _tabs;
    private bool _busy;

    /// <summary>Raised after composer changed the dependencies (the IDE re-indexes).</summary>
    public static event Action? PackagesChanged;

    public PackagesPanel(string root)
    {
        _root = root;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "FgSecondary");
        _tabs = new TabControl { Style = (Style)Application.Current.FindResource("ToolTabControl"), BorderThickness = new Thickness(0) };
        TabItem Tab(string header, UIElement content) => new() { Header = header, Content = content, Style = (Style)Application.Current.FindResource("ToolTabItem") };

        var searchBar = new DockPanel { Margin = new Thickness(0, 4, 0, 8) };
        var go = new Button { Content = "Search", MinWidth = 0 };
        go.Click += async (_, _) => await SearchAsync();
        _query.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await SearchAsync(); };
        _neoOnly.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(_neoOnly, Dock.Right);
        DockPanel.SetDock(go, Dock.Right);
        searchBar.Children.Add(_neoOnly);
        searchBar.Children.Add(go);
        searchBar.Children.Add(_query);
        var searchPage = new DockPanel();
        DockPanel.SetDock(searchBar, Dock.Top);
        searchPage.Children.Add(searchBar);
        searchPage.Children.Add(_search);

        _tabs.Items.Add(Tab("Installed", _installed));
        _tabs.Items.Add(Tab("Required", _requirements));
        _tabs.Items.Add(Tab("Optional (suggested)", _suggested));
        _tabs.Items.Add(Tab("Packagist", searchPage));
        var refresh = new Button { Style = (Style)Application.Current.FindResource("ToolButton"), ToolTip = "Refresh", Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12 } };
        refresh.Click += async (_, _) => await LoadAsync();
        _tabs.Tag = refresh;

        SetDock(_status, Dock.Top);
        Children.Add(_status);
        var logBorder = new Border { Child = _log, BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 6, 0, 0) };
        logBorder.SetResourceReference(Border.BorderBrushProperty, "SubtleBorder");
        SetDock(logBorder, Dock.Bottom);
        Children.Add(logBorder);
        Children.Add(_tabs);
        Loaded += async (_, _) => { if (_installed.Items.Count == 0) await LoadAsync(); };
    }

    // ------------------------------------------------------------------ data

    private sealed record LockedPackage(string Name, string Version, string Description, string Type, Dictionary<string, string> Suggest);

    private Dictionary<string, LockedPackage> ReadLock()
    {
        var map = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(_root, "composer.lock");
        if (!File.Exists(file)) return map;
        try
        {
            var lockJson = JsonNode.Parse(File.ReadAllText(file));
            foreach (var section in new[] { "packages", "packages-dev" })
                foreach (var p in lockJson?[section]?.AsArray() ?? new JsonArray())
                {
                    if (p == null) continue;
                    var suggest = new Dictionary<string, string>();
                    if (p["suggest"] is JsonObject s) foreach (var kv in s) suggest[kv.Key] = kv.Value?.ToString() ?? "";
                    var name = p["name"]!.ToString();
                    map[name] = new LockedPackage(name, p["version"]?.ToString() ?? "", p["description"]?.ToString() ?? "", p["type"]?.ToString() ?? "library", suggest);
                }
        }
        catch { }
        return map;
    }

    private (Dictionary<string, string> require, Dictionary<string, string> dev) ReadComposerJson()
    {
        var req = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dev = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "composer.json")));
            if (json?["require"] is JsonObject r) foreach (var kv in r) req[kv.Key] = kv.Value?.ToString() ?? "";
            if (json?["require-dev"] is JsonObject d) foreach (var kv in d) dev[kv.Key] = kv.Value?.ToString() ?? "";
        }
        catch { }
        return (req, dev);
    }

    public async Task LoadAsync()
    {
        _status.Text = "Reading composer.json / composer.lock…";
        var locked = ReadLock();
        var (require, dev) = ReadComposerJson();

        // ---- installed
        _installed.Items.Clear();
        void AddInstalled(string name, string constraint, bool isDev)
        {
            locked.TryGetValue(name, out var lp);
            var platform = name == "php" || name.StartsWith("ext-") || name.StartsWith("lib-");
            var actions = new List<(string, Func<Task>)>();
            if (!platform)
            {
                actions.Add(("Update", () => Composer($"update {name} --with-dependencies")));
                actions.Add(("Remove", async () =>
                {
                    if (MessageDialog.Confirm("Remove package", $"Remove {name} from the project (composer remove)?", "Remove"))
                        await Composer($"remove {name}{(isDev ? " --dev" : "")}");
                }));
            }
            _installed.Items.Add(Row(name, lp?.Description ?? (platform ? "Platform requirement" : ""),
                new[] { (isDev ? "dev" : "required", isDev ? "WarnFg" : "Accent"), (constraint, "FgSecondary"), (lp != null ? "installed " + lp.Version : platform ? "" : "not installed", lp != null ? "SuccessFg" : "ErrorFg") }
                    .Where(b => b.Item1.Length > 0).ToArray(),
                actions, lp?.Type == "neophp-package"));
        }
        foreach (var (n, c) in require.OrderBy(k => k.Key.StartsWith("php") || k.Key.StartsWith("ext-") ? 0 : 1).ThenBy(k => k.Key)) AddInstalled(n, c, false);
        foreach (var (n, c) in dev.OrderBy(k => k.Key)) AddInstalled(n, c, true);
        if (!File.Exists(Path.Combine(_root, "composer.lock")))
            _installed.Items.Insert(0, Notice("composer.lock is missing: run composer install.", "WarnFg"));

        // ---- optional (suggested by the installed packages)
        _suggested.Items.Clear();
        var loadedExt = await LoadedExtensionsAsync();
        var suggestions = locked.Values.SelectMany(p => p.Suggest.Select(s => (by: p.Name, name: s.Key, reason: s.Value)))
            .GroupBy(s => s.name).OrderBy(g => g.Key.StartsWith("ext-") ? 1 : 0).ThenBy(g => g.Key);
        foreach (var g in suggestions)
        {
            var name = g.Key;
            var isExt = name.StartsWith("ext-");
            var installed = isExt ? loadedExt.Contains(name[4..]) : locked.ContainsKey(name);
            var reason = string.Join(" · ", g.Select(s => s.reason).Distinct());
            var constraint = Regex.Match(reason, @"\((\^?[~>=<\d][^)]*)\)") is { Success: true } m ? m.Groups[1].Value : null;
            var actions = new List<(string, Func<Task>)>();
            if (!installed && !isExt) actions.Add(("Install", () => Composer($"require {name}{(constraint != null ? ":" + ProcessRunner.Quote(constraint) : "")}")));
            if (!installed && isExt) actions.Add(("How to enable", () => { ShowExtensionHelp(name[4..]); return Task.CompletedTask; }));
            _suggested.Items.Add(Row(name, reason,
                new[] { ("optional", "FgSecondary"), ("suggested by " + string.Join(", ", g.Select(s => s.by).Distinct()), "FgSecondary"), (installed ? (isExt ? "enabled" : "installed") : (isExt ? "extension not enabled" : "not installed"), installed ? "SuccessFg" : "WarnFg") },
                actions, false));
        }
        if (_suggested.Items.Count == 0) _suggested.Items.Add(Notice("No suggestion from the installed packages.", "FgSecondary"));

        // ---- required platform (php, extensions)
        _requirements.Items.Clear();
        _requirements.Items.Add(Notice("Checking the platform requirements of the dependencies…", "FgSecondary"));
        _status.Text = $"{require.Count} required · {dev.Count} dev · {locked.Count} installed in vendor/";
        await LoadRequirementsAsync();
    }

    private async Task<HashSet<string>> LoadedExtensionsAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Workspace.PhpExe == null) return set;
        var (code, stdout, _) = await ProcessRunner.RunAsync(Workspace.PhpExe, "-m", _root, 15000);
        if (code == 0) foreach (var l in stdout.Split('\n')) { var t = l.Trim(); if (t.Length > 0 && !t.StartsWith("[")) set.Add(t.Replace(" ", "_")); }
        return set;
    }

    private async Task LoadRequirementsAsync()
    {
        var (exe, args) = ComposerService.Command(Workspace.PhpExe, "check-platform-reqs --format=json");
        var (code, stdout, _) = await ProcessRunner.RunAsync(exe, args, _root, 60000);
        _requirements.Items.Clear();
        try
        {
            var arr = JsonNode.Parse(stdout.Trim())?.AsArray() ?? new JsonArray();
            foreach (var r in arr.Where(r => r != null).OrderBy(r => r!["status"]?.ToString() == "success" ? 1 : 0))
            {
                var name = r!["name"]!.ToString();
                var ok = r["status"]?.ToString() == "success";
                var failed = r["failed_requirement"];
                var detail = ok ? $"{r["version"]}{(r["provider"] != null && r["provider"]!.ToString().Length > 0 ? " (provided by " + r["provider"] + ")" : "")}"
                                : $"required by {failed?["source"]} ({failed?["constraint"]}), found: {r["version"]}";
                var actions = new List<(string, Func<Task>)>();
                if (!ok && name.StartsWith("ext-")) actions.Add(("How to enable", () => { ShowExtensionHelp(name[4..]); return Task.CompletedTask; }));
                if (!ok && name == "php") actions.Add(("Change interpreter", () => { MessageDialog.Show("PHP", "Choose another interpreter in NeoPHP › PHP interpreter…"); return Task.CompletedTask; }));
                _requirements.Items.Add(Row(name, detail, new[] { ("required", "Accent"), (ok ? "✔ OK" : "✘ missing", ok ? "SuccessFg" : "ErrorFg") }, actions, false));
            }
            if (arr.Count == 0) _requirements.Items.Add(Notice("No platform requirement.", "FgSecondary"));
        }
        catch
        {
            _requirements.Items.Add(Notice(code == 0 ? "Unable to read the requirements." : "composer check-platform-reqs failed: run composer install first.", "WarnFg"));
        }
    }

    // ------------------------------------------------------------------ Packagist

    private async Task SearchAsync()
    {
        var q = _query.Text.Trim();
        _search.Items.Clear();
        if (q.Length == 0 && _neoOnly.IsChecked != true) return;
        _search.Items.Add(Notice("Searching packagist.org…", "FgSecondary"));
        try
        {
            var url = $"https://packagist.org/search.json?per_page=40&q={Uri.EscapeDataString(q)}{(_neoOnly.IsChecked == true ? "&type=neophp-package" : "")}";
            var json = JsonNode.Parse(await Http.GetStringAsync(url));
            _search.Items.Clear();
            var (require, dev) = ReadComposerJson();
            foreach (var r in json?["results"]?.AsArray() ?? new JsonArray())
            {
                if (r == null) continue;
                var name = r["name"]!.ToString();
                var installed = require.ContainsKey(name) || dev.ContainsKey(name);
                var isNeo = _neoOnly.IsChecked == true || name.StartsWith("neophp/");
                var actions = new List<(string, Func<Task>)>();
                if (!installed)
                {
                    actions.Add(("Install", () => Install(name, false, isNeo)));
                    actions.Add(("Install --dev", () => Install(name, true, isNeo)));
                }
                _search.Items.Add(Row(name, r["description"]?.ToString() ?? "",
                    new[] { ($"⬇ {Downloads(r["downloads"]?.GetValue<long>() ?? 0)}", "FgSecondary"), ($"★ {r["favers"]}", "FgSecondary"), (installed ? "installed" : "", "SuccessFg") }.Where(b => b.Item1.Length > 0).ToArray(),
                    actions, isNeo, r["url"]?.ToString()));
            }
            if (_search.Items.Count == 0) _search.Items.Add(Notice("No package found.", "FgSecondary"));
        }
        catch (Exception ex)
        {
            _search.Items.Clear();
            _search.Items.Add(Notice("Packagist is not reachable: " + ex.Message, "ErrorFg"));
        }
    }

    private static string Downloads(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 1000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    private Task Install(string name, bool dev, bool neoPackage) =>
        neoPackage && File.Exists(Path.Combine(_root, "bin", "neo"))
            ? Run(Workspace.PhpExe!, $"bin/neo neophp:package:install {name}{(dev ? " --dev" : "")} --no-ansi --no-interaction", $"neo neophp:package:install {name}")
            : Composer($"require {name}{(dev ? " --dev" : "")}");

    // ------------------------------------------------------------------ running composer

    private Task Composer(string args)
    {
        var (exe, a) = ComposerService.Command(Workspace.PhpExe, args + " --no-interaction");
        return Run(exe, a, "composer " + args);
    }

    private async Task Run(string exe, string args, string title)
    {
        if (_busy) { _status.Text = "An operation is already running…"; return; }
        _busy = true;
        _log.Visibility = Visibility.Visible;
        _status.Text = title + "…";
        var code = await _log.RunAsync(exe, args, _root, title);
        _busy = false;
        _status.Text = code == 0 ? $"{title}: done" : $"{title}: failed (exit code {code})";
        await LoadAsync();
        if (code == 0) PackagesChanged?.Invoke();
    }

    private async void ShowExtensionHelp(string ext)
    {
        var ini = "";
        if (Workspace.PhpExe != null)
        {
            var (_, stdout, _) = await ProcessRunner.RunAsync(Workspace.PhpExe, "--ini", _root, 10000);
            ini = Regex.Match(stdout, @"Loaded Configuration File:\s*(.+)").Groups[1].Value.Trim();
        }
        var text = $"The PHP extension '{ext}' is not enabled for {Workspace.PhpExe ?? "php"}.\n\n" +
                   $"1. Open php.ini{(ini.Length > 0 ? $" ({ini})" : "")}\n2. Uncomment or add the line:  extension={ext}\n" +
                   "3. If the DLL is missing, download it (e.g. from PECL / windows.php.net) into the ext/ directory of PHP.\n4. Restart the NeoPHP server.";
        if (ini.Length > 0 && File.Exists(ini) && MessageDialog.Confirm($"Enable ext-{ext}", text, "Open php.ini", "Close"))
            Process.Start(new ProcessStartInfo(ini) { UseShellExecute = true });
        else if (ini.Length == 0) MessageDialog.Show($"Enable ext-{ext}", text);
    }

    // ------------------------------------------------------------------ rows

    private static ListBoxItem Notice(string text, string brush) => new()
    {
        Content = new TextBlock { Text = text, Foreground = ThemeManager.Brush(brush), Margin = new Thickness(4, 6, 0, 6), TextWrapping = TextWrapping.Wrap },
        IsHitTestVisible = false,
    };

    private FrameworkElement Row(string name, string description, (string text, string brush)[] badges, List<(string label, Func<Task> action)> actions, bool neo, string? url = null)
    {
        var dock = new DockPanel { Margin = new Thickness(2, 4, 2, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, action) in actions)
        {
            var b = new Button { Content = label, MinWidth = 0, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
            if (label is "Install") b.Style = (Style)Application.Current.FindResource("PrimaryButton");
            b.Click += async (_, _) => await action();
            buttons.Children.Add(b);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        var text = new StackPanel();
        var head = new WrapPanel();
        if (neo)
            head.Children.Add(new Image { Source = (ImageSource)Application.Current.FindResource("NeoLogo"), Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        if (url != null) { title.Cursor = Cursors.Hand; title.ToolTip = url; title.MouseLeftButtonUp += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        head.Children.Add(title);
        foreach (var (t, brush) in badges)
            head.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 1, 0, 0), BorderThickness = new Thickness(1),
                BorderBrush = ThemeManager.Brush(brush), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = t, FontSize = 11, Foreground = ThemeManager.Brush(brush) },
            });
        text.Children.Add(head);
        if (description.Length > 0)
            text.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("FgSecondary"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0) });
        dock.Children.Add(text);
        return dock;
    }
}
