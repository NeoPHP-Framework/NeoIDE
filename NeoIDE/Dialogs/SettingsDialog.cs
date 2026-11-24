using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NeoIDE.Editor;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

/// <summary>
/// Settings (Ctrl+Alt+S): Appearance &amp; display, Editor, PHP, New projects.
/// Appearance and editor changes are applied live; Cancel restores the previous values.
/// </summary>
public sealed class SettingsDialog : DialogWindow
{
    private readonly AppSettings _s = AppSettings.Current;
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly ListBox _nav = new() { Width = 190, Padding = new Thickness(6) };
    private readonly ContentControl _host = new();
    private readonly Snapshot _before;
    private bool _committed, _loading = true;

    private sealed record Snapshot(string Theme, double UiScale, double UiFontSize, bool Compact, bool StatusBar, bool ToolStrip,
        string Font, double FontSize, bool Wrap, bool Lines, bool Ws, bool CurLine, bool Folding);

    public SettingsDialog(string? page = null)
    {
        Title = "Settings";
        SizeToContent = SizeToContent.Manual;
        Width = 860;
        Height = 600;
        ResizeMode = ResizeMode.CanResize;
        _before = new Snapshot(ThemeManager.CurrentName, _s.UiScale, _s.UiFontSize, _s.CompactMode, _s.ShowStatusBar, _s.ShowToolStrip,
            _s.EditorFontFamily, _s.EditorFontSize, _s.EditorWordWrap, _s.EditorShowLineNumbers, _s.EditorShowWhitespace,
            _s.EditorHighlightCurrentLine, _s.EditorShowFolding);

        _pages["Themes"] = BuildThemes();
        _pages["Appearance"] = BuildAppearance();
        _pages["Editor"] = BuildEditor();
        _pages["PHP"] = BuildPhp();
        if (Workspace.Root != null) _pages["Composer packages"] = BuildPackages();
        _pages["New projects"] = BuildProjects();
        _pages["Updates"] = BuildUpdates();

        foreach (var name in _pages.Keys) _nav.Items.Add(new ListBoxItem { Content = name });
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is ListBoxItem { Content: string n }) _host.Content = new ScrollViewer { Content = _pages[n], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 6, 24, 12) };
        };

        var ok = Primary("OK");
        var cancel = Secondary("Cancel");
        var apply = new Button { Content = "Apply" };
        ok.Click += (_, _) => { Commit(); DialogResult = true; };
        apply.Click += (_, _) => Commit();
        cancel.Click += (_, _) => Close();
        Closing += (_, _) => { if (!_committed) Restore(); };

        var footer = new Border
        {
            BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(16, 10, 16, 10),
            Child = new DockPanel { LastChildFill = false },
        };
        var buttons = Buttons(ok, cancel, apply);
        buttons.Margin = new Thickness(0);
        DockPanel.SetDock(buttons, Dock.Right);
        ((DockPanel)footer.Child).Children.Add(buttons);

        var side = new Border { Background = ThemeManager.Brush("WelcomeSideBg"), BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(0, 0, 1, 0), Child = _nav };
        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(footer);
        root.Children.Add(side);
        root.Children.Add(_host);
        Content = root;

        _nav.SelectedIndex = Math.Max(0, _pages.Keys.ToList().IndexOf(page ?? "Themes"));
        Loaded += (_, _) => _loading = false;
    }

    // ------------------------------------------------------------------ helpers

    private static TextBlock Section(string text) => new()
    {
        Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 14, 0, 4), Foreground = ThemeManager.Brush("Fg"),
    };

    private static FrameworkElement Row(string label, FrameworkElement control, string? hint = null)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("Fg") };
        g.Children.Add(l);
        var right = new StackPanel();
        right.Children.Add(control);
        if (hint != null) right.Children.Add(Hint(hint));
        Grid.SetColumn(right, 1);
        g.Children.Add(right);
        return g;
    }

    private CheckBox Check(string text, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 5, 0, 5) };
        c.Click += (_, _) => { set(c.IsChecked == true); Live(); };
        return c;
    }

    private void Live()
    {
        if (_loading) return;
        DisplayManager.Apply(save: false);
    }

    private static ComboBox Combo<T>(IEnumerable<T> items, T selected, Func<T, string>? label = null, double width = 260)
    {
        var c = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var it in items) c.Items.Add(new ComboBoxItem { Content = label?.Invoke(it) ?? it!.ToString(), Tag = it });
        c.SelectedItem = c.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected))
                         ?? c.Items.OfType<ComboBoxItem>().OrderBy(i => Math.Abs(Convert.ToDouble(i.Tag is double d ? d : 0) - Convert.ToDouble(selected is double s ? s : 0))).FirstOrDefault();
        return c;
    }

    private static T? Selected<T>(ComboBox c) => c.SelectedItem is ComboBoxItem { Tag: T t } ? t : default;

    // ------------------------------------------------------------------ pages

    private WrapPanel _installedThemes = new();
    private WrapPanel _marketThemes = new();
    private TextBlock _marketStatus = new();
    private MarketplaceIndex? _index;

    private FrameworkElement BuildThemes()
    {
        var p = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 14, 0, 4) };
        var import = new Button { Content = "Import .json…", MinWidth = 0, Margin = new Thickness(8, 0, 0, 0) };
        var folder = new Button { Content = "Open themes folder", MinWidth = 0, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(folder, Dock.Right);
        DockPanel.SetDock(import, Dock.Right);
        head.Children.Add(folder);
        head.Children.Add(import);
        var title = Section("Installed themes");
        title.Margin = new Thickness(0);
        head.Children.Add(title);
        p.Children.Add(head);
        p.Children.Add(Hint("A theme colours the whole IDE: interface, editor and syntax. Click to apply (Cancel restores the previous one). Themes are JSON files."));
        _installedThemes = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        p.Children.Add(_installedThemes);

        p.Children.Add(Section("Marketplace"));
        _marketStatus = Hint("Loading the themes of the NeoIDE marketplace…");
        p.Children.Add(_marketStatus);
        _marketThemes = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        p.Children.Add(_marketThemes);

        import.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "NeoIDE theme (*.json)|*.json" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var t = Editor.ThemeCatalog.Install(File.ReadAllText(dlg.FileName));
                ThemeManager.Apply(t.Id);
                RefreshThemes();
            }
            catch (Exception ex) { MessageDialog.Show("Import theme", "This file is not a valid NeoIDE theme:\n" + ex.Message); }
        };
        folder.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Editor.ThemeCatalog.InstallDirectory);
        RefreshThemes();
        Loaded += async (_, _) => await LoadMarketplaceAsync();
        return p;
    }

    private async Task LoadMarketplaceAsync()
    {
        try
        {
            _index = await Marketplace.FetchIndexAsync();
            _marketStatus.Text = $"{_index.Themes.Count} theme(s) on {Marketplace.RepositoryUrl}";
        }
        catch (Exception ex)
        {
            _marketStatus.Text = "The marketplace is not reachable: " + ex.Message;
        }
        RefreshThemes();
    }

    private void RefreshThemes()
    {
        _installedThemes.Children.Clear();
        foreach (var theme in Editor.ThemeCatalog.All)
        {
            var actions = new List<(string, Action)>();
            if (!theme.IsBundled)
                actions.Add(("Remove", () =>
                {
                    if (!MessageDialog.Confirm("Remove theme", $"Uninstall the theme '{theme.Name}'?", "Remove")) return;
                    var wasCurrent = ThemeManager.CurrentTheme.Id == theme.Id;
                    Editor.ThemeCatalog.Uninstall(theme.Id);
                    if (wasCurrent) ThemeManager.Apply(theme.IsDark ? Editor.ThemeCatalog.DefaultDark : Editor.ThemeCatalog.DefaultLight);
                    RefreshThemes();
                }));
            var update = _index?.Themes.FirstOrDefault(m => m.Id == theme.Id && Marketplace.IsNewer(m.Version, theme.Version));
            if (update != null) actions.Add(($"Update to {update.Version}", () => _ = InstallFromMarketplace(update)));
            _installedThemes.Children.Add(ThemeCard(theme.Name, theme.IsDark, theme.Editor.Background, theme.Ui["PanelBg"], theme.Ui["Fg"], theme.Ui["FgSecondary"],
                t => theme.Syntax[t].Color, theme.IsBundled ? "built-in" : $"v{theme.Version}",
                theme.Id == ThemeManager.CurrentTheme.Id, () => { ThemeManager.Apply(theme.Id); RefreshThemes(); }, actions));
        }

        _marketThemes.Children.Clear();
        if (_index == null) return;
        foreach (var item in _index.Themes.Where(m => Editor.ThemeCatalog.Find(m.Id) == null))
        {
            Color C(string hex) => ColorConverter.ConvertFromString(hex) is Color c ? c : Colors.Gray;
            var pv = item.Preview;
            Color Token(string t) => C(t switch { "Keyword" => pv.Keyword, "Function" or "FunctionCall" => pv.Function, "String" => pv.String, "Number" => pv.Number, "Keyword2" => pv.Variable, _ => pv.Foreground });
            _marketThemes.Children.Add(ThemeCard(item.Name, item.Dark, C(pv.Background), C(pv.Panel), C(pv.Foreground), C(pv.Foreground) is var f ? Color.FromArgb(160, f.R, f.G, f.B) : f,
                Token, $"by {item.Author}", false, null,
                new List<(string, Action)> { ("Install", () => _ = InstallFromMarketplace(item)) }, item.Description));
        }
        if (_marketThemes.Children.Count == 0)
            _marketThemes.Children.Add(Hint("All the marketplace themes are installed."));
    }

    private async Task InstallFromMarketplace(MarketplaceItem item)
    {
        try
        {
            _marketStatus.Text = $"Downloading {item.Name}…";
            var json = await Marketplace.DownloadAsync(item);
            var t = Editor.ThemeCatalog.Install(json);
            ThemeManager.Apply(t.Id);
            _marketStatus.Text = $"{item.Name} {item.Version} installed and applied.";
        }
        catch (Exception ex)
        {
            _marketStatus.Text = $"Unable to install {item.Name}: {ex.Message}";
        }
        RefreshThemes();
    }

    private Border ThemeCard(string name, bool dark, Color background, Color panel, Color fg, Color fg2, Func<string, Color> token, string badge,
        bool selected, Action? apply, List<(string label, Action action)> actions, string? description = null)
    {
        var preview = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };
        var code = new TextBlock { FontFamily = DisplayManager.EditorFont, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 8) };
        void Run(string text, string t) => code.Inlines.Add(new System.Windows.Documents.Run(text) { Foreground = new SolidColorBrush(token(t)) });
        Run("public function ", "Keyword"); Run("index", "Function"); Run("()\n{\n    ", "Default");
        Run("return ", "Keyword"); Run("$this", "Keyword"); Run("->", "Default"); Run("render", "FunctionCall");
        Run("(", "Default"); Run("'home'", "String"); Run(", ", "Default"); Run("42", "Number"); Run(");\n}", "Default");
        preview.Children.Add(code);
        var footerStack = new StackPanel();
        var nameRow = new DockPanel();
        var badgeText = new TextBlock { Text = (dark ? "dark" : "light") + " · " + badge, FontSize = 11, Foreground = new SolidColorBrush(fg2) };
        DockPanel.SetDock(badgeText, Dock.Right);
        nameRow.Children.Add(badgeText);
        nameRow.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(fg) });
        footerStack.Children.Add(nameRow);
        if (!string.IsNullOrEmpty(description))
            footerStack.Children.Add(new TextBlock { Text = description, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(fg2), Margin = new Thickness(0, 2, 0, 0) });
        if (actions.Count > 0)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (label, action) in actions)
            {
                var b = new Button { Content = label, MinWidth = 0, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
                if (label == "Install" || label.StartsWith("Update")) b.Style = (Style)FindResource("PrimaryButton");
                b.Click += (_, e) => { e.Handled = true; action(); };
                row.Children.Add(b);
            }
            footerStack.Children.Add(row);
        }
        var footer = new Border { Background = new SolidColorBrush(panel), Padding = new Thickness(10, 6, 10, 8), Child = footerStack, CornerRadius = new CornerRadius(0, 0, 6, 6) };
        var stack = new StackPanel();
        stack.Children.Add(new Border { Background = new SolidColorBrush(background), Child = preview, CornerRadius = new CornerRadius(6, 6, 0, 0) });
        stack.Children.Add(footer);
        var card = new Border
        {
            Width = 250, Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(7), Child = stack,
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? ThemeManager.Brush("Accent") : ThemeManager.Brush("SubtleBorder"),
            Cursor = apply != null ? System.Windows.Input.Cursors.Hand : null,
        };
        if (apply != null) card.MouseLeftButtonUp += (_, _) => apply();
        return card;
    }

    private FrameworkElement BuildAppearance()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Appearance"));


        var zoom = Combo(DisplayManager.ZoomLevels, DisplayManager.ZoomLevels.OrderBy(z => Math.Abs(z - _s.UiScale)).First(), z => $"{z * 100:0}%");
        zoom.SelectionChanged += (_, _) => { _s.UiScale = Selected<double>(zoom); Live(); };
        p.Children.Add(Row("IDE zoom:", zoom, "Scales the whole interface (Ctrl+Alt+= / Ctrl+Alt+- / Ctrl+Alt+0)."));

        var uiFont = Combo(new[] { 11.0, 12.0, 13.0, 14.0, 15.0, 16.0 }, _s.UiFontSize, v => $"{v:0} pt");
        uiFont.SelectionChanged += (_, _) => { _s.UiFontSize = Selected<double>(uiFont); Live(); };
        p.Children.Add(Row("Interface font size:", uiFont, "Menus, project tree, tabs and dialogs."));

        p.Children.Add(Section("Layout"));
        p.Children.Add(Check("Compact mode (denser tree, tabs and menus)", _s.CompactMode, v => _s.CompactMode = v));
        p.Children.Add(Check("Show status bar", _s.ShowStatusBar, v => _s.ShowStatusBar = v));
        p.Children.Add(Check("Show tool window bar (left strip)", _s.ShowToolStrip, v => _s.ShowToolStrip = v));

        p.Children.Add(Section("Logo"));
        var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var size in new[] { 16.0, 24.0, 32.0, 48.0, 64.0 })
            logo.Children.Add(new Image { Source = (ImageSource)FindResource("NeoLogo"), Width = size, Height = size, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Bottom });
        p.Children.Add(logo);
        return p;
    }

    private FrameworkElement BuildEditor()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Font"));
        var fonts = DisplayManager.MonospaceFonts();
        if (!fonts.Contains(_s.EditorFontFamily)) fonts.Insert(0, _s.EditorFontFamily);
        var font = Combo(fonts, _s.EditorFontFamily, width: 300);
        font.SelectionChanged += (_, _) => { if (Selected<string>(font) is { } f) { _s.EditorFontFamily = f; Live(); } };
        p.Children.Add(Row("Font:", font, "Fixed-width fonts installed on this machine (JetBrains Mono recommended)."));

        var sizes = new[] { 10.0, 11, 12, 12.5, 13, 13.5, 14, 15, 16, 18, 20, 22, 24 };
        var size = Combo(sizes, _s.EditorFontSize, v => $"{v} pt", 140);
        size.SelectionChanged += (_, _) => { _s.EditorFontSize = Selected<double>(size); Live(); };
        p.Children.Add(Row("Size:", size, "Ctrl+= / Ctrl+- in the editor."));

        p.Children.Add(Section("Display"));
        p.Children.Add(Check("Soft-wrap long lines", _s.EditorWordWrap, v => _s.EditorWordWrap = v));
        p.Children.Add(Check("Show line numbers", _s.EditorShowLineNumbers, v => _s.EditorShowLineNumbers = v));
        p.Children.Add(Check("Show whitespaces", _s.EditorShowWhitespace, v => _s.EditorShowWhitespace = v));
        p.Children.Add(Check("Highlight the caret line", _s.EditorHighlightCurrentLine, v => _s.EditorHighlightCurrentLine = v));
        p.Children.Add(Check("Show code folding outline", _s.EditorShowFolding, v => _s.EditorShowFolding = v));

        p.Children.Add(Section("Preview"));
        var preview = new CodeEditor { Height = 190, IsReadOnly = true, IsPreview = true, Margin = new Thickness(0, 4, 0, 0) };
        preview.Load("Preview.php", "<?php\n\nnamespace App\\Controller;\n\nuse NeoPHP\\Component\\Controller\\Contract\\AbstractController;\nuse NeoPHP\\Component\\Http\\Response\\Response;\n\n" +
                                    "class HomeController extends AbstractController\n{\n    /** Home page */\n    #[Route('/', name: 'home')]\n    public function index(int $page = 1): Response\n    {\n" +
                                    "        return $this->render('home/index', ['title' => \"Page {$page}\", 'max' => self::MAX]);\n    }\n}\n");
        Unloaded += (_, _) => preview.Detach();
        p.Children.Add(new Border { BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = preview });
        return p;
    }

    private FrameworkElement BuildPackages()
    {
        var p = new DockPanel();
        var title = Section($"Composer packages of {Workspace.Name}");
        DockPanel.SetDock(title, Dock.Top);
        p.Children.Add(title);
        var hint = Hint("Installed (required / dev), platform requirements (PHP, extensions), optional packages suggested by your dependencies, Packagist search. NeoPHP packages are installed with neophp:package:install (configuration copied into config/packages/).");
        DockPanel.SetDock(hint, Dock.Top);
        p.Children.Add(hint);
        p.Children.Add(new Controls.PackagesPanel(Workspace.Root!) { Height = 430, Margin = new Thickness(0, 8, 0, 0) });
        return p;
    }

    private ComboBox _php = new();

    private FrameworkElement BuildPhp()
    {
        var p = new StackPanel();
        p.Children.Add(Section("PHP"));
        _php = new ComboBox { MinWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var add = new Button { Content = "Add php.exe…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        var stack = new StackPanel();
        stack.Children.Add(_php);
        stack.Children.Add(add);
        p.Children.Add(Row("Default interpreter:", stack, "Used for new projects and projects without their own interpreter."));

        async Task Fill(string? select)
        {
            var list = await PhpLocator.FindAllAsync(select != null);
            _php.ItemsSource = list;
            _php.SelectedItem = list.FirstOrDefault(x => string.Equals(x.Path, select ?? _s.DefaultPhpPath, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
        }
        Loaded += async (_, _) => await Fill(null);
        add.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "php.exe|php.exe" };
            if (dlg.ShowDialog(this) != true) return;
            if (!_s.CustomPhpPaths.Contains(dlg.FileName)) _s.CustomPhpPaths.Add(dlg.FileName);
            await Fill(dlg.FileName);
        };
        return p;
    }

    private TextBox _dir = new(), _vendor = new(), _author = new(), _email = new();

    private FrameworkElement BuildProjects()
    {
        var p = new StackPanel();
        p.Children.Add(Section("New NeoPHP projects"));
        _dir = new TextBox { Text = _s.DefaultProjectsDirectory };
        _vendor = new TextBox { Text = _s.VendorName };
        _author = new TextBox { Text = _s.AuthorName };
        _email = new TextBox { Text = _s.AuthorEmail };
        p.Children.Add(Row("Default location:", _dir));
        p.Children.Add(Row("Composer vendor:", _vendor, "Package name = vendor/project-name"));
        p.Children.Add(Row("Author name:", _author));
        p.Children.Add(Row("Author e-mail:", _email));
        return p;
    }

    private ComboBox _channel = new();
    private CheckBox _autoCheck = new(), _autoDownload = new();

    private FrameworkElement BuildUpdates()
    {
        var p = new StackPanel();
        p.Children.Add(Section("NeoIDE updates"));
        var version = new TextBlock { Foreground = ThemeManager.Brush("Fg"), Margin = new Thickness(0, 4, 0, 4) };
        version.Text = UpdateService.IsInstalled
            ? $"Installed version: {UpdateService.CurrentVersion}"
            : $"Version {UpdateService.CurrentVersion} — development build (Rider / zip): automatic updates only work for the installed NeoIDE.";
        version.TextWrapping = TextWrapping.Wrap;
        p.Children.Add(version);

        _channel = Combo(new[] { "stable", "beta" }, _s.UpdateChannel, c => c == "stable" ? "Stable" : "Beta (pre-releases)", 220);
        p.Children.Add(Row("Channel:", _channel, "Beta also receives the pre-releases published on GitHub."));
        _autoCheck = new CheckBox { Content = "Check for updates automatically (at startup, then every 4 hours)", IsChecked = _s.AutoCheckUpdates, Margin = new Thickness(0, 5, 0, 5) };
        _autoDownload = new CheckBox { Content = "Download updates in the background (only the changed files)", IsChecked = _s.AutoDownloadUpdates, Margin = new Thickness(0, 5, 0, 5) };
        p.Children.Add(_autoCheck);
        p.Children.Add(_autoDownload);
        p.Children.Add(Hint("A downloaded update is installed when you click Restart, or automatically the next time NeoIDE starts."));

        var check = new Button { Content = "Check now…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        check.Click += (_, _) => { Commit(); UpdateDialog.ShowFor(); };
        p.Children.Add(check);
        return p;
    }

    // ------------------------------------------------------------------ commit / restore

    private void Commit()
    {
        var channel = Selected<string>(_channel) ?? _s.UpdateChannel;
        if (channel != _s.UpdateChannel) { _s.UpdateChannel = channel; UpdateService.Reset(); }
        _s.AutoCheckUpdates = _autoCheck.IsChecked == true;
        _s.AutoDownloadUpdates = _autoDownload.IsChecked == true;
        _s.DefaultPhpPath = (_php.SelectedItem as PhpInstall)?.Path ?? _s.DefaultPhpPath;
        _s.DefaultProjectsDirectory = _dir.Text.Trim();
        _s.VendorName = _vendor.Text.Trim();
        _s.AuthorName = _author.Text.Trim();
        _s.AuthorEmail = _email.Text.Trim();
        _committed = true;
        DisplayManager.Apply(save: true);
    }

    private void Restore()
    {
        var b = _before;
        _s.UiScale = b.UiScale;
        _s.UiFontSize = b.UiFontSize;
        _s.CompactMode = b.Compact;
        _s.ShowStatusBar = b.StatusBar;
        _s.ShowToolStrip = b.ToolStrip;
        _s.EditorFontFamily = b.Font;
        _s.EditorFontSize = b.FontSize;
        _s.EditorWordWrap = b.Wrap;
        _s.EditorShowLineNumbers = b.Lines;
        _s.EditorShowWhitespace = b.Ws;
        _s.EditorHighlightCurrentLine = b.CurLine;
        _s.EditorShowFolding = b.Folding;
        if (b.Theme != ThemeManager.CurrentName) ThemeManager.Apply(b.Theme);
        DisplayManager.Apply(save: true);
    }
}

/// <summary>Chooses the PHP interpreter of the current project.</summary>
public sealed class InterpreterDialog : DialogWindow
{
    public PhpInstall? Selected { get; private set; }

    public InterpreterDialog(string? current)
    {
        Title = "PHP Interpreter";
        var list = new ListBox { Height = 220 };
        var p = new StackPanel { Margin = new Thickness(18, 4, 18, 18), Width = 620 };
        p.Children.Add(Label("Interpreter used by this project (completion of built-in functions, bin/neo, composer):", 0));
        p.Children.Add(new Border { BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = list });
        var add = new Button { Content = "Add php.exe…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        p.Children.Add(add);
        var ok = Primary("OK");
        p.Children.Add(Buttons(ok, Secondary("Cancel")));
        Content = p;

        async Task Fill(string? select)
        {
            var all = await PhpLocator.FindAllAsync(select != null);
            list.ItemsSource = all;
            list.SelectedItem = all.FirstOrDefault(x => string.Equals(x.Path, select ?? current, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        }
        Loaded += async (_, _) => await Fill(null);
        add.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "php.exe|php.exe" };
            if (dlg.ShowDialog(this) != true) return;
            if (!AppSettings.Current.CustomPhpPaths.Contains(dlg.FileName)) { AppSettings.Current.CustomPhpPaths.Add(dlg.FileName); AppSettings.Current.Save(); }
            await Fill(dlg.FileName);
        };
        list.MouseDoubleClick += (_, _) => ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ok.Click += (_, _) =>
        {
            Selected = list.SelectedItem as PhpInstall;
            DialogResult = Selected != null;
        };
    }
}
