using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using NeoIDE.Controls;
using NeoIDE.Controls.Terminal;
using NeoIDE.Dialogs;
using NeoIDE.Services.Debug;
using NeoIDE.Services.Diagnostics;
using NeoIDE.Editor;
using NeoIDE.Php;
using NeoIDE.Services;
using NeoIDE.Services.Git;
using NeoIDE.Services.Plugins;
using NeoIDE.ViewModels;
using Visibility = System.Windows.Visibility;

namespace NeoIDE;

public sealed class EditorTab
{
    public string Path { get; init; } = "";
    public TabItem Tab { get; init; } = null!;
    public CodeEditor? Editor { get; init; }
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public bool Dirty { get; set; }
    public TextBlock Title { get; init; } = null!;
    public DateTime LastWrite { get; set; }
}

public partial class MainWindow : Window, IIdeShell
{
    private readonly List<EditorTab> _tabs = new();
    private FileNode? _root;
    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _fsTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly HashSet<string> _pendingFsChanges = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _structureTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private DateTime _lastShift = DateTime.MinValue;
    private bool _shiftClean;
    private CancellationTokenSource? _indexCts;
    private List<(string name, string description)> _neoCommands = new();
    public bool ReturnToWelcome { get; private set; }

    public MainWindow()
    {
        InitializeComponent();
        ProjectTree.ItemsSource = Array.Empty<FileNode>();
        UpdateThemeMenu();
        DisplayManager.Attach(this, 40);
        WindowPlacement.Attach(this, remember: true);
        DisplayManager.Changed += OnDisplayChanged;
        PackagesPanel.PackagesChanged += OnPackagesChanged;
        Closed += (_, _) => PackagesPanel.PackagesChanged -= OnPackagesChanged;
        Closed += (_, _) => DisplayManager.Changed -= OnDisplayChanged;
        InitPlugins();
        UpdateService.Changed += RefreshUpdateStatus;
        Closed += (_, _) => UpdateService.Changed -= RefreshUpdateStatus;
        RefreshUpdateStatus();
        AvailableUpdates.Changed += RefreshUpdateBadge;
        Closed += (_, _) => AvailableUpdates.Changed -= RefreshUpdateBadge;
        Loaded += (_, _) => RefreshUpdateBadge();
        OnDisplayChanged();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        Deactivated += (_, _) => SaveAll(); // PhpStorm saves when the IDE loses the focus
        Activated += (_, _) => { if (GitService.IsRepository) _ = GitService.RefreshAsync(); };
        StateChanged += (_, _) =>
        {
            // a maximized borderless window overhangs the screen: inset the content so the title bar stays visible
            RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
            RootBorder.Margin = WindowPlacement.MaximizedInset(this);
            Dispatcher.BeginInvoke(() => RootBorder.Margin = WindowPlacement.MaximizedInset(this), DispatcherPriority.Loaded);
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
        };
        Closing += OnClosing;
        _fsTimer.Tick += (_, _) => FlushFsChanges();
        _structureTimer.Tick += (_, _) => { _structureTimer.Stop(); UpdateStructure(); };
        ThemeManager.ThemeChanged += () => { UpdateThemeMenu(); _root?.RefreshKinds(); };
        RunConsole.RunningChanged += running => Dispatcher.Invoke(() =>
        {
            RunButton.IsEnabled = DebugButton.IsEnabled = !running;
            StopButton.IsEnabled = running || DebugService.State is DebugState.Running or DebugState.Paused;
        });
        UpdateEmptyHint();
        InitProblems();
        InitDebugger();
    }

    // ================================================================== project

    public async void OpenProject(string path)
    {
        path = Path.GetFullPath(path);
        Workspace.Open(path);
        DiagnosticsStore.Clear();
        NeoProject.Clear();
        var neoRoot = path;
        _ = Task.Run(() => { try { NeoProject.Build(neoRoot); } catch { } });
        AppSettings.Current.AddRecent(path);
        Title = $"{Workspace.Name} — NeoIDE";
        ProjectTitle.Text = Workspace.Name;
        ProjectInitials.Text = new RecentItem(Workspace.Name, path).Initials;
        BuildRecentMenu();

        _root = new FileNode(path, true, 0, null);
        ProjectTree.ItemsSource = new[] { _root };
        _root.IsExpanded = true;
        _root.IsSelected = true;
        RunConsole.WorkingDirectory = path;
        ResetTerminals(path);

        StartWatcher(path);

        // PHP interpreter
        var php = Workspace.Settings.PhpPath;
        if (php == null || !File.Exists(php)) php = (await PhpLocator.GetDefaultAsync())?.Path;
        await SetInterpreter(php, save: false);

        // restore editors
        foreach (var f in Workspace.Settings.OpenFiles.Where(File.Exists)) OpenFile(f, focus: false);
        if (Workspace.Settings.ActiveFile != null && File.Exists(Workspace.Settings.ActiveFile)) OpenFile(Workspace.Settings.ActiveFile);
        else if (_tabs.Count == 0)
        {
            var home = Path.Combine(path, "src", "Controller", "HomeController.php");
            if (File.Exists(home)) OpenFile(home);
        }

        _ = LoadNeoCommandsAsync();
        IdeHost.Instance.RaiseProjectOpened(path);
        await InitGitAsync(path);
        await ReindexAsync();
    }

    private async Task SetInterpreter(string? php, bool save)
    {
        Workspace.PhpExe = php;
        Workspace.PhpVersion = php != null ? await PhpLocator.GetVersionAsync(php) : null;
        StatusPhp.Text = Workspace.PhpVersion != null ? $"PHP {Workspace.PhpVersion}" : "No PHP interpreter";
        StatusPhp.Foreground = Workspace.PhpVersion != null ? ThemeManager.Brush("FgSecondary") : ThemeManager.Brush("ErrorFg");
        if (save)
        {
            Workspace.Settings.PhpPath = php;
            Workspace.SaveSettings();
        }
    }

    private async Task ReindexAsync()
    {
        if (Workspace.Root == null) return;
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        var index = Workspace.Index;
        index.Progress += msg => Dispatcher.BeginInvoke(() => StatusIndex.Text = msg);
        StatusIndex.Text = "Indexing…";
        var sw = Stopwatch.StartNew();
        try
        {
            await index.BuildAsync(Workspace.Root, Workspace.PhpExe, _indexCts.Token);
            StatusIndex.Text = "";
            var count = index.AllClasses().Count(c => c.File != null);
            SetStatus($"Indexed {count} classes in {sw.Elapsed.TotalSeconds:0.0}s{(index.RuntimeVersion != null ? $" · PHP {index.RuntimeVersion} built-ins" : "")}");
            _root?.RefreshKinds();
            UpdateStructure();
        }
        catch (OperationCanceledException) { }
    }

    private void BuildRecentMenu()
    {
        RecentMenu.Items.Clear();
        foreach (var r in AppSettings.Current.RecentProjects.Where(r => Directory.Exists(r.Path)).Take(15))
        {
            var item = new MenuItem { Header = $"{r.Name}   ({r.Path})" };
            var p = r.Path;
            item.Click += (_, _) => SwitchProject(p);
            RecentMenu.Items.Add(item);
        }
    }

    private void SwitchProject(string path)
    {
        if (string.Equals(path, Workspace.Root, StringComparison.OrdinalIgnoreCase)) return;
        SaveAll();
        SaveSession();
        RunConsole.Stop();
        foreach (var t in _tabs.ToList()) CloseTab(t, save: false);
        IdeHost.Instance.RaiseProjectClosed();
        OpenProject(path);
    }

    private void SaveSession()
    {
        if (Workspace.Root == null) return;
        Workspace.Settings.OpenFiles = _tabs.Select(t => t.Path).ToList();
        Workspace.Settings.ActiveFile = CurrentTab?.Path;
        Workspace.SaveSettings();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveAll();
        SaveSession();
        RunConsole.Stop();
        foreach (var c in AllTerminals()) c.Stop();
        foreach (var s in AllShells()) s.Stop();
        DebugService.StopListening();
        foreach (var t in _tabs) t.Editor?.Detach(); // the editors listen to static events (theme, problems, breakpoints)
        _watcher?.Dispose();
        IdeHost.Instance.RaiseProjectClosed();
    }

    // ================================================================== file system watcher

    private void StartWatcher(string root)
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite };
        FileSystemEventHandler h = (_, e) => Queue(e.FullPath);
        _watcher.Created += h;
        _watcher.Deleted += h;
        _watcher.Changed += h;
        _watcher.Renamed += (_, e) => { Queue(e.OldFullPath); Queue(e.FullPath); };
        _watcher.EnableRaisingEvents = true;
    }

    private void Queue(string path)
    {
        var rel = Workspace.Root != null ? Path.GetRelativePath(Workspace.Root, path) : path;
        if (rel.StartsWith(".git") || rel.StartsWith("var\\") || rel.StartsWith(".neoide") || rel.StartsWith(".idea")) return;
        NeoProject.Invalidate(path);
        lock (_pendingFsChanges) _pendingFsChanges.Add(path);
        Dispatcher.BeginInvoke(() => { _fsTimer.Stop(); _fsTimer.Start(); });
    }

    private void FlushFsChanges()
    {
        _fsTimer.Stop();
        List<string> changes;
        lock (_pendingFsChanges) { changes = _pendingFsChanges.ToList(); _pendingFsChanges.Clear(); }
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool vendorChanged = false;
        foreach (var path in changes)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) dirs.Add(dir);
            if (path.Contains("\\vendor\\")) { vendorChanged = true; continue; }
            if (path.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(path)) Workspace.Index.UpdateFile(path);
                else Workspace.Index.RemoveFile(path);
            }
            // external modification of an opened file
            var tab = _tabs.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
            if (tab?.Editor != null && File.Exists(path) && !tab.Dirty && File.GetLastWriteTimeUtc(path) > tab.LastWrite.AddMilliseconds(500))
            {
                try
                {
                    var text = File.ReadAllText(path);
                    if (text != tab.Editor.Text)
                    {
                        var caret = tab.Editor.CaretOffset;
                        tab.Editor.Document.Text = text;
                        tab.Editor.CaretOffset = Math.Min(caret, text.Length);
                        tab.Dirty = false;
                        UpdateTabTitle(tab);
                    }
                    tab.LastWrite = File.GetLastWriteTimeUtc(path);
                }
                catch { }
            }
        }
        if (_root != null)
            foreach (var d in dirs)
            {
                var node = _root.Find(d);
                if (node != null && node.IsDirectory && (node.IsExpanded || node == _root)) node.Reload();
            }
        if (vendorChanged) _ = DelayedReindex();
        _ = GitService.RefreshAsync();
    }

    private bool _reindexScheduled;
    private async Task DelayedReindex()
    {
        if (_reindexScheduled) return;
        _reindexScheduled = true;
        await Task.Delay(3000);
        _reindexScheduled = false;
        await ReindexAsync();
    }

    // ================================================================== editors

    private EditorTab? CurrentTab => _tabs.FirstOrDefault(t => t.Tab == EditorTabs.SelectedItem);
    private CodeEditor? CurrentEditor => CurrentTab?.Editor;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp" };

    public void OpenFile(string path, int? offset = null, bool focus = true)
    {
        path = Path.GetFullPath(path);
        var existing = _tabs.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            EditorTabs.SelectedItem = existing.Tab;
            if (offset != null) existing.Editor?.GoToOffset(offset.Value);
            else if (focus) existing.Editor?.TextArea.Focus();
            return;
        }
        if (!File.Exists(path)) return;

        var ext = Path.GetExtension(path);
        var title = new TextBlock { Text = Path.GetFileName(path), VerticalAlignment = VerticalAlignment.Center };
        var header = BuildTabHeader(path, title, out var closeButton);
        var tabItem = new TabItem { Header = header, Style = (Style)FindResource("EditorTabItem"), ToolTip = path };
        EditorTab tab;

        if (ImageExt.Contains(ext))
        {
            var img = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                img.Source = bmp;
            }
            catch { }
            tabItem.Content = new ScrollViewer { Content = img, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
            tab = new EditorTab { Path = path, Tab = tabItem, Title = title };
        }
        else
        {
            var info = new FileInfo(path);
            if (info.Length > 15 * 1024 * 1024 || IsBinary(path))
            {
                SetStatus($"{Path.GetFileName(path)} is a binary or too large file");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return;
            }
            var (text, encoding) = ReadText(path);
            var editor = new CodeEditor();
            editor.Load(path, text);
            tab = new EditorTab { Path = path, Tab = tabItem, Editor = editor, Title = title, Encoding = encoding, LastWrite = File.GetLastWriteTimeUtc(path) };
            editor.TextChanged += (_, _) =>
            {
                if (!tab.Dirty) { tab.Dirty = true; UpdateTabTitle(tab); }
                _structureTimer.Stop();
                _structureTimer.Start();
            };
            editor.NavigateRequested += (file, off) => OpenFile(file, off);
            editor.StatusMessage += msg =>
            {
                if (Regex.IsMatch(msg, @"^\d+:\d+$")) StatusPosition.Text = msg;
                else SetStatus(msg);
            };
            editor.TextArea.LostKeyboardFocus += (_, _) => { if (tab.Dirty) Save(tab); };
            _ = LoadGitBaseAsync(tab);
            tabItem.Content = editor.Language == "Markdown" ? new MarkdownHost(editor, path) : editor;
        }
        closeButton.Click += (_, _) => CloseTab(tab);
        tabItem.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) CloseTab(tab); };
        // Alt+click closes the tab (and the Alt release must not activate the main menu afterwards)
        tabItem.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
            _suppressAltMenu = true;
            CloseTab(tab);
            e.Handled = true;
        };
        tabItem.ContextMenu = BuildTabMenu(tab);
        _tabs.Add(tab);
        EditorTabs.Items.Add(tabItem);
        EditorTabs.SelectedItem = tabItem;
        UpdateEmptyHint();
        if (offset != null) tab.Editor?.GoToOffset(offset.Value);
        else if (focus) Dispatcher.BeginInvoke(() => tab.Editor?.TextArea.Focus(), DispatcherPriority.Input);
    }

    private static bool IsBinary(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(8000, fs.Length)];
            var n = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < n; i++) if (buf[i] == 0) return true;
        }
        catch { }
        return false;
    }

    private static (string, Encoding) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);
        try
        {
            var strict = new UTF8Encoding(false, true);
            return (strict.GetString(bytes), new UTF8Encoding(false));
        }
        catch
        {
            var latin = Encoding.Latin1;
            return (latin.GetString(bytes), latin);
        }
    }

    private FrameworkElement BuildTabHeader(string path, TextBlock title, out Button close)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new FileIcon { Path = path, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, PhpKind = KindOf(path) });
        panel.Children.Add(title);
        close = new Button
        {
            Style = (Style)FindResource("ToolButton"), Padding = new Thickness(3), Margin = new Thickness(6, 0, 0, 0),
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 9, Foreground = ThemeManager.Brush("FgSecondary") },
            ToolTip = "Close (Ctrl+F4)",
        };
        panel.Children.Add(close);
        return panel;
    }

    private static string? KindOf(string path)
    {
        var cls = Workspace.Index.GetFile(path)?.Classes.FirstOrDefault();
        return cls == null ? null : cls.Kind switch
        {
            PhpClassKind.Interface => "interface",
            PhpClassKind.Trait => "trait",
            PhpClassKind.Enum => "enum",
            _ => cls.IsAbstract ? "abstract" : "class",
        };
    }

    private ContextMenu BuildTabMenu(EditorTab tab)
    {
        var menu = new ContextMenu();
        void Add(string header, Action a, string? gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("Close", () => CloseTab(tab), "Ctrl+F4");
        Add("Close Other Tabs", () => { foreach (var t in _tabs.Where(x => x != tab).ToList()) CloseTab(t); });
        Add("Close All Tabs", () => { foreach (var t in _tabs.ToList()) CloseTab(t); });
        menu.Items.Add(new Separator());
        Add("Select in Project View", () => SelectInProject(tab.Path), "Alt+F1");
        Add("Copy Path", () => Clipboard.SetText(tab.Path));
        Add("Open in Explorer", () => Process.Start("explorer.exe", $"/select,\"{tab.Path}\""));
        return menu;
    }

    private void UpdateTabTitle(EditorTab tab)
    {
        tab.Title.Text = Path.GetFileName(tab.Path) + (tab.Dirty ? " *" : "");
        tab.Title.FontStyle = tab.Dirty ? FontStyles.Italic : FontStyles.Normal;
        var change = GitService.ChangeFor(tab.Path);
        if (change != null) tab.Title.Foreground = CommitPanel.StatusBrush(change.Kind);
        else tab.Title.ClearValue(TextBlock.ForegroundProperty);
    }

    private void CloseTab(EditorTab tab, bool save = true)
    {
        if (save && tab.Dirty) Save(tab);
        tab.Editor?.Detach();
        _tabs.Remove(tab);
        EditorTabs.Items.Remove(tab.Tab);
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint() => EmptyEditorHint.Visibility = EditorTabs.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Save(EditorTab tab)
    {
        if (tab.Editor == null || !tab.Dirty) return;
        try
        {
            File.WriteAllText(tab.Path, tab.Editor.Text, tab.Encoding);
            tab.LastWrite = File.GetLastWriteTimeUtc(tab.Path);
            tab.Dirty = false;
            UpdateTabTitle(tab);
            if (tab.Path.EndsWith(".php", StringComparison.OrdinalIgnoreCase))
            {
                Workspace.Index.UpdateFile(tab.Path, tab.Editor.Text);
                PhpStanOnSave(tab.Path);
            }
            if (Path.GetFileName(tab.Path).Equals("composer.json", StringComparison.OrdinalIgnoreCase))
                SetStatus("composer.json saved — run NeoPHP › Composer › update to apply the changes");
        }
        catch (Exception ex)
        {
            SetStatus($"Cannot save {tab.Path}: {ex.Message}");
        }
    }

    private void SaveAll()
    {
        foreach (var t in _tabs.Where(t => t.Dirty).ToList()) Save(t);
    }

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != EditorTabs) return;
        var tab = CurrentTab;
        if (tab == null)
        {
            StatusLanguage.Text = StatusPosition.Text = StatusEol.Text = StatusIndent.Text = "";
            ProblemsView.CurrentFileChanged();
            RefreshProblemsStatus();
            return;
        }
        ProblemsView.CurrentFileChanged();
        RefreshProblemsStatus();
        foreach (var t in _tabs.Where(t => t != tab && t.Dirty)) Save(t);
        var ed = tab.Editor;
        StatusLanguage.Text = SyntaxRegistry.DisplayName(ed?.Language ?? (ImageExt.Contains(Path.GetExtension(tab.Path)) ? "Image" : null));
        StatusEncoding.Text = tab.Encoding is UTF8Encoding u ? (u.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8") : tab.Encoding.WebName.ToUpperInvariant();
        StatusEol.Text = ed == null ? "" : ed.Text.Contains("\r\n") ? "CRLF" : "LF";
        StatusIndent.Text = ed == null ? "" : $"{ed.Options.IndentationSize} spaces";
        if (ed != null)
        {
            var line = ed.Document.GetLineByOffset(ed.CaretOffset);
            StatusPosition.Text = $"{line.LineNumber}:{ed.CaretOffset - line.Offset + 1}";
        }
        UpdateStructure();
    }

    // ================================================================== structure view

    private sealed record StructureItem(string Label, int Offset, EntryKind Kind, string? Type, bool Inherited)
    {
        public override string ToString() => Label;
    }

    private void UpdateStructure()
    {
        if (StructureList.Visibility != Visibility.Visible) return;
        var ed = CurrentEditor;
        var items = new List<FrameworkElement>();
        if (ed is { IsPhp: true })
        {
            var info = PhpParser.Parse(ed.Text, ed.FilePath);
            foreach (var c in info.Classes)
            {
                items.Add(StructureRow(c.Name, c.Offset, c.Kind switch { PhpClassKind.Interface => EntryKind.Interface, PhpClassKind.Trait => EntryKind.Trait, PhpClassKind.Enum => EntryKind.Enum, _ => EntryKind.Class },
                    c.Parent != null ? "← " + c.Parent.Split('\\').Last() : null, 0));
                foreach (var m in c.Members.Where(m => !m.IsMagic).OrderBy(m => m.Kind == PhpMemberKind.Method ? 2 : m.Kind == PhpMemberKind.Property ? 1 : 0))
                {
                    var kind = m.Kind switch { PhpMemberKind.Method => EntryKind.Method, PhpMemberKind.Property => EntryKind.Property, PhpMemberKind.EnumCase => EntryKind.EnumCase, _ => EntryKind.Constant };
                    var label = m.Kind == PhpMemberKind.Method ? m.Name + m.Signature() : m.Kind == PhpMemberKind.Property ? "$" + m.Name : m.Name;
                    items.Add(StructureRow(label, m.Offset, kind, PhpTypes.Short(m.EffectiveType), 1, m.Visibility));
                }
            }
            foreach (var f in info.Functions)
                items.Add(StructureRow(f.Name + f.Signature(), f.Offset, EntryKind.Function, PhpTypes.Short(f.EffectiveReturn), 0));
        }
        StructureList.ItemsSource = items;
    }

    private static FrameworkElement StructureRow(string label, int offset, EntryKind kind, string? type, int depth, Php.Visibility vis = Php.Visibility.Public)
    {
        var (letter, color) = CompletionData.IconFor(kind);
        var dock = new DockPanel { Tag = offset, Margin = new Thickness(depth * 16, 0, 0, 0) };
        var icon = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 4, 0),
            Child = new TextBlock { Text = letter, Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        var visMark = new TextBlock
        {
            Text = vis switch { Php.Visibility.Private => "🔒", Php.Visibility.Protected => "◆", _ => "" }, FontSize = 8, Width = 12,
            Foreground = ThemeManager.Brush("FgSecondary"), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(visMark, Dock.Left);
        dock.Children.Add(visMark);
        if (!string.IsNullOrEmpty(type))
        {
            var t = new TextBlock { Text = type, Foreground = ThemeManager.Brush("FgSecondary"), Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(t, Dock.Right);
            dock.Children.Add(t);
        }
        dock.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis });
        return dock;
    }

    private void StructureList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StructureList.SelectedItem is FrameworkElement { Tag: int offset }) CurrentEditor?.GoToOffset(offset);
    }

    private void ProjectToggle_Click(object sender, RoutedEventArgs e) => ShowSidePanel(ProjectToggle.IsChecked == true ? "project" : null);
    private void StructureToggle_Click(object sender, RoutedEventArgs e) => ShowSidePanel(StructureToggle.IsChecked == true ? "structure" : null);

    private double _sideWidth = AppSettings.Current.SidePanelWidth;

    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SideColumn.Width.Value > 0) { _sideWidth = SideColumn.ActualWidth; AppSettings.Current.SidePanelWidth = _sideWidth; }
        if (BottomRow.Height.Value > 0) { _bottomHeight = BottomRow.ActualHeight; AppSettings.Current.BottomPanelHeight = _bottomHeight; }
        AppSettings.Current.Save();
    }
    private void ShowSidePanel(string? which)
    {
        if (which == null)
        {
            if (SideColumn.Width.Value > 0) _sideWidth = SideColumn.Width.Value;
            SideColumn.Width = new GridLength(0);
            ProjectToggle.IsChecked = StructureToggle.IsChecked = CommitToggle.IsChecked = false;
            SyncPluginSidePanels(null);
            return;
        }
        SideColumn.Width = new GridLength(_sideWidth);
        ProjectToggle.IsChecked = which == "project";
        StructureToggle.IsChecked = which == "structure";
        CommitToggle.IsChecked = which == "commit";
        ProjectTree.Visibility = which == "project" ? Visibility.Visible : Visibility.Collapsed;
        StructureList.Visibility = which == "structure" ? Visibility.Visible : Visibility.Collapsed;
        CommitView.Visibility = which == "commit" ? Visibility.Visible : Visibility.Collapsed;
        ProjectActions.Visibility = which == "project" ? Visibility.Visible : Visibility.Collapsed;
        SidePanelTitle.Text = which switch { "project" => "Project", "commit" => "Commit", "structure" => "Structure", _ => SidePanelTitle.Text };
        SyncPluginSidePanels(which);
        if (which == "commit") { CommitView.Rebuild(); _ = GitService.RefreshAsync(); }
        if (which == "structure") UpdateStructure();
    }

    // ================================================================== bottom tool windows

    private double _bottomHeight = AppSettings.Current.BottomPanelHeight;
    private void ShowBottom(string? which)
    {
        if (which == null)
        {
            if (BottomRow.Height.Value > 0) _bottomHeight = BottomRow.Height.Value;
            BottomRow.Height = new GridLength(0);
            BottomSplitterRow.Height = new GridLength(0);
            RunToggle.IsChecked = TerminalToggle.IsChecked = ProblemsToggle.IsChecked = GitToggle.IsChecked = DebugToggle.IsChecked = FindToggle.IsChecked = false;
            SyncPluginBottomPanels(null);
            CurrentEditor?.TextArea.Focus();
            return;
        }
        BottomRow.Height = new GridLength(_bottomHeight);
        BottomSplitterRow.Height = new GridLength(1);
        BottomTabs.SelectedItem = PluginBottomTab(which) ?? BuiltInBottomTab(which);
        SyncPluginBottomPanels(which);
        GitToggle.IsChecked = which == "git";
        if (which == "git") _ = GitLogView.LoadAsync();
        RunToggle.IsChecked = which == "run";
        TerminalToggle.IsChecked = which == "terminal";
        ProblemsToggle.IsChecked = which == "problems";
        DebugToggle.IsChecked = which == "debug";
        FindToggle.IsChecked = which == "find";
        if (which == "problems") ProblemsView.Rebuild();
        if (which == "terminal") Dispatcher.BeginInvoke(FocusSelectedTerminal, DispatcherPriority.Input);
    }

    private TabItem BuiltInBottomTab(string? which) => which switch
    {
        "terminal" => TerminalTab, "problems" => ProblemsTab, "find" => FindTab, "git" => GitTab, "debug" => DebugTab, _ => RunTab,
    };

    private bool IsBottomVisible(string which) => BottomRow.Height.Value > 0 &&
        BottomTabs.SelectedItem == (PluginBottomTab(which) ?? BuiltInBottomTab(which));

    private void BottomToggle_Click(object sender, RoutedEventArgs e)
    {
        var which = (string)((FrameworkElement)sender).Tag;
        ShowBottom(((ToggleButton)sender).IsChecked == true ? which : null);
    }

    private void HideBottom_Click(object sender, RoutedEventArgs e) => ShowBottom(null);

    private void ClearConsole_Click(object sender, RoutedEventArgs e)
    {
        if (BottomTabs.SelectedItem == RunTab) RunConsole.Clear();
        else if (BottomTabs.SelectedItem == TerminalTab)
        {
            if (SelectedTerminalContent is TerminalView shell) shell.Clear();
            else if (SelectedTerminalContent is ConsoleView console) console.Clear();
        }
        else if (BottomTabs.SelectedItem == FindTab) FindResults.ItemsSource = null;
    }

    private void ShowTerminal_Click(object sender, RoutedEventArgs e) => ShowBottom(IsBottomVisible("terminal") ? null : "terminal");
    private void ShowRun_Click(object sender, RoutedEventArgs e) => ShowBottom(IsBottomVisible("run") ? null : "run");

    // ================================================================== terminals

    /// <summary>A console able to run a command (RunAsync): the NeoConsole.</summary>
    private ConsoleView TerminalConsole => Terminal(ConsoleKind.Neo);

    private object? SelectedTerminalContent => (TerminalTabs.SelectedItem as TabItem)?.Content;

    private void FocusSelectedTerminal()
    {
        if (TerminalTabs.Items.Count == 0) NewShell(DefaultShell);
        if (SelectedTerminalContent is TerminalView shell) shell.Focus();
        else if (SelectedTerminalContent is ConsoleView console) console.FocusInput();
    }

    private static ShellKind DefaultShell => TerminalView.Executable(ShellKind.Pwsh) != null ? ShellKind.Pwsh : ShellKind.PowerShell;

    private IEnumerable<ConsoleView> AllTerminals() => TerminalTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<ConsoleView>();
    private IEnumerable<TerminalView> AllShells() => TerminalTabs.Items.OfType<TabItem>().Select(t => t.Content).OfType<TerminalView>();

    /// <summary>Existing session of that kind, or a new one.</summary>
    private ConsoleView Terminal(ConsoleKind kind)
    {
        var tab = TerminalTabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Content is ConsoleView c && c.Kind == kind);
        if (tab != null) { TerminalTabs.SelectedItem = tab; return (ConsoleView)tab.Content; }
        return NewTerminal(kind);
    }

    private void ResetTerminals(string root)
    {
        foreach (var c in AllTerminals()) c.Stop();
        foreach (var s in AllShells()) s.Stop();
        TerminalTabs.Items.Clear();
        if (PseudoConsole.IsSupported) NewShell(DefaultShell, root, select: false);
        else NewTerminal(ConsoleKind.PowerShell, root, select: false);
        NewTerminal(ConsoleKind.Neo, root, select: false);
        TerminalTabs.SelectedIndex = 0;
    }

    private static string ShellTitle(ShellKind kind) => kind switch
    {
        ShellKind.Pwsh => "PowerShell 7",
        ShellKind.PowerShell => "PowerShell",
        ShellKind.Cmd => "Command Prompt",
        ShellKind.GitBash => "Git Bash",
        ShellKind.Wsl => "WSL",
        _ => "Terminal",
    };

    /// <summary>A real terminal (ConPTY) in a new tab of the Terminal tool window.</summary>
    private TerminalView NewShell(ShellKind kind, string? cwd = null, bool select = true)
    {
        var view = new TerminalView(kind, cwd ?? Workspace.Root ?? Environment.CurrentDirectory);
        var title = ShellTitle(kind);
        var same = AllShells().Count(s => s.Shell == kind);
        if (same > 0) title += $" ({same + 1})";
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = kind switch { ShellKind.Cmd => ">", ShellKind.GitBash or ShellKind.Wsl => "$", _ => "PS" },
            FontSize = 9, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeManager.Brush("FgSecondary"),
        });
        var titleBlock = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(titleBlock);
        var close = new Button
        {
            Style = (Style)FindResource("ToolButton"), Padding = new Thickness(3), Margin = new Thickness(6, 0, 0, 0),
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 8, Foreground = ThemeManager.Brush("FgSecondary") },
            ToolTip = "Close session",
        };
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = view, Style = (Style)FindResource("ToolTabItem") };
        void Close() { view.Stop(); TerminalTabs.Items.Remove(tab); }
        close.Click += (_, _) => Close();
        tab.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) Close(); };
        view.TitleChanged += () => tab.ToolTip = view.Title;
        view.LinkClicked += OnTerminalLink;
        TerminalTabs.Items.Add(tab);
        if (select)
        {
            TerminalTabs.SelectedItem = tab;
            Dispatcher.BeginInvoke(() => view.Focus(), DispatcherPriority.Input);
        }
        return view;
    }

    /// <summary>Ctrl+click in a terminal: URL → browser, "src/Foo.php:12" → editor.</summary>
    private void OnTerminalLink(string link, string cwd)
    {
        if (link.StartsWith("http://") || link.StartsWith("https://"))
        {
            try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); } catch { }
            return;
        }
        var m = Regex.Match(link, @"^(?<path>.+?)(?:(?::|\()(?<line>\d+)\)?)?$");
        var rel = m.Groups["path"].Value.Replace('/', '\\');
        foreach (var dir in new[] { cwd, Workspace.Root }.Where(d => d != null))
        {
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(rel) ? rel : Path.Combine(dir!, rel)); } catch { continue; }
            if (!File.Exists(full)) continue;
            OpenFileAt(full, m.Groups["line"].Success ? int.Parse(m.Groups["line"].Value) : 0);
            return;
        }
        SetStatus($"File not found: {link}");
    }

    private static string TerminalTitle(ConsoleKind kind) => kind switch
    {
        ConsoleKind.Neo => "NeoConsole",
        ConsoleKind.PowerShell => "Local (PowerShell)",
        ConsoleKind.Cmd => "Command Prompt",
        ConsoleKind.Bash => "Git Bash",
        _ => "Output",
    };

    private ConsoleView NewTerminal(ConsoleKind kind, string? cwd = null, bool select = true)
    {
        var console = new ConsoleView { Kind = kind, WorkingDirectory = cwd ?? Workspace.Root ?? Environment.CurrentDirectory };
        var title = TerminalTitle(kind);
        var same = TerminalTabs.Items.OfType<TabItem>().Count(t => t.Content is ConsoleView c && c.Kind == kind);
        if (same > 0) title += $" ({same + 1})";
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = kind switch { ConsoleKind.Neo => "N", ConsoleKind.PowerShell => "PS", ConsoleKind.Bash => "$", _ => ">" },
            FontSize = 9, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeManager.Brush(kind == ConsoleKind.Neo ? "Accent" : "FgSecondary"),
        });
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button
        {
            Style = (Style)FindResource("ToolButton"), Padding = new Thickness(3), Margin = new Thickness(6, 0, 0, 0),
            Content = new TextBlock { Text = "\uE711", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 8, Foreground = ThemeManager.Brush("FgSecondary") },
            ToolTip = "Close session",
        };
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = console, Style = (Style)FindResource("ToolTabItem") };
        close.Click += (_, _) => { console.Stop(); TerminalTabs.Items.Remove(tab); };
        tab.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { console.Stop(); TerminalTabs.Items.Remove(tab); } };
        TerminalTabs.Items.Add(tab);
        if (kind == ConsoleKind.Neo)
            console.AppendKind("NeoConsole: type a bin/neo command (e.g. make:controller Blog). Tab / Ctrl+Space: completion of commands and --options.", ConsoleView.LineKind.Info);
        if (select)
        {
            TerminalTabs.SelectedItem = tab;
            Dispatcher.BeginInvoke(() => console.FocusInput(), DispatcherPriority.Input);
        }
        return console;
    }

    private void NewTerminalMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string header, ConsoleKind kind, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled };
            mi.Click += (_, _) => { NewTerminal(kind); ShowBottom("terminal"); };
            menu.Items.Add(mi);
        }
        void AddShell(ShellKind kind)
        {
            var available = TerminalView.Executable(kind) != null && PseudoConsole.IsSupported;
            var mi = new MenuItem { Header = ShellTitle(kind) + (available ? "" : " (not installed)"), IsEnabled = available };
            mi.Click += (_, _) => { NewShell(kind); ShowBottom("terminal"); };
            menu.Items.Add(mi);
        }
        Add("NeoConsole (php bin/neo)", ConsoleKind.Neo);
        menu.Items.Add(new Separator());
        if (TerminalView.Executable(ShellKind.Pwsh) != null) AddShell(ShellKind.Pwsh);
        AddShell(ShellKind.PowerShell);
        AddShell(ShellKind.Cmd);
        AddShell(ShellKind.GitBash);
        if (TerminalView.Executable(ShellKind.Wsl) != null) AddShell(ShellKind.Wsl);
        menu.Items.Add(new Separator());
        Add("PowerShell (one command at a time)", ConsoleKind.PowerShell);
        menu.IsOpen = true;
    }

    // ================================================================== NeoPHP / composer

    private bool EnsurePhp()
    {
        if (Workspace.PhpExe != null) return true;
        MessageDialog.Show("PHP interpreter", "No PHP interpreter is configured for this project. Choose one in NeoPHP › PHP interpreter…");
        return false;
    }

    private string NeoScript => Workspace.Root != null && File.Exists(Path.Combine(Workspace.Root, "bin", "neo")) ? "bin/neo" : "vendor/bin/neo";

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePhp() || Workspace.Root == null || RunConsole.IsRunning) return;
        SaveAll();
        ShowBottom("run");
        RunConsole.Clear();
        var port = Workspace.Settings.ServerPort;
        await RunConsole.RunAsync(Workspace.PhpExe!, $"{NeoScript} serve --port={port} --no-ansi", Workspace.Root, $"php {NeoScript} serve --port={port}");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (DebugService.State is DebugState.Paused or DebugState.Running) _ = DebugService.Stop();
        RunConsole.Stop();
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{Workspace.Settings.ServerPort}") { UseShellExecute = true });

    private async Task LoadNeoCommandsAsync()
    {
        MakeMenu.Items.Clear();
        CommandsMenu.Items.Clear();
        if (Workspace.PhpExe == null || Workspace.Root == null) return;
        var (code, stdout, _) = await ProcessRunner.RunAsync(Workspace.PhpExe, $"{NeoScript} list --no-ansi", Workspace.Root, 30000);
        if (code != 0) { MakeMenu.Items.Add(new MenuItem { Header = "(bin/neo not available)", IsEnabled = false }); return; }
        _neoCommands = Regex.Matches(stdout, @"^\s{2}([a-z][\w:-]*)\s{2,}(.+)$", RegexOptions.Multiline)
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value.Trim())).ToList();
        Workspace.NeoCommands = _neoCommands;

        foreach (var (name, desc) in _neoCommands.Where(c => c.name.StartsWith("make:")))
        {
            var item = new MenuItem { Header = name[5..], ToolTip = desc };
            var n = name;
            item.Click += (_, _) => RunMake(n, desc);
            MakeMenu.Items.Add(item);
        }
        foreach (var group in _neoCommands.GroupBy(c => c.name.Contains(':') ? c.name[..c.name.IndexOf(':')] : ""))
        {
            ItemsControl parent = CommandsMenu;
            if (group.Key.Length > 0)
            {
                var sub = new MenuItem { Header = group.Key };
                CommandsMenu.Items.Add(sub);
                parent = sub;
            }
            foreach (var (name, desc) in group)
            {
                var item = new MenuItem { Header = name, ToolTip = desc };
                var n = name;
                item.Click += (_, _) => RunNeo(n);
                parent.Items.Add(item);
            }
        }
    }

    private async void RunMake(string command, string description)
    {
        if (!EnsurePhp()) return;
        // NeoPHP asks its own questions (name, fields...) in the interactive NeoConsole
        await RunNeoAndOpen(command);
    }

    private async void RunNeo(string command)
    {
        if (!EnsurePhp()) return;
        if (command == "serve") { Run_Click(this, new RoutedEventArgs()); return; }
        await RunNeoAndOpen(command);
    }

    private async void RunNeoCommand_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePhp()) return;
        ShowBottom("terminal");
        var neo = Terminal(ConsoleKind.Neo);
        Dispatcher.BeginInvoke(() => neo.FocusInput(), DispatcherPriority.Input);
        await Task.CompletedTask;
    }

    /// <summary>Runs a bin/neo command in the interactive NeoConsole and opens the files it generates.</summary>
    private async Task RunNeoAndOpen(string args)
    {
        if (Workspace.Root == null) return;
        SaveAll();
        ShowBottom("terminal");
        var neo = Terminal(ConsoleKind.Neo);
        if (neo.IsRunning) neo = NewTerminal(ConsoleKind.Neo);
        var files = new List<string>();
        void OnLine(string line) => CollectCreated(line, files);
        neo.LineReceived += OnLine;
        try { await neo.Execute(args); }
        finally { neo.LineReceived -= OnLine; }
        OpenCreated(files);
    }

    private static void CollectCreated(string line, List<string> files)
    {
        var m = Regex.Match(line, @"\b(created|updated|generated)\s+(?<path>[\w./\\-]+\.\w+)", RegexOptions.IgnoreCase);
        if (m.Success) lock (files) files.Add(m.Groups["path"].Value);
    }

    private void OpenCreated(List<string> files)
    {
        Dispatcher.BeginInvoke(() =>
        {
            FlushFsChanges();
            foreach (var f in files.Take(3))
            {
                var full = Path.GetFullPath(Path.Combine(Workspace.Root!, f));
                if (File.Exists(full)) OpenFile(full);
            }
            if (files.Count > 0) SelectInProject(Path.GetFullPath(Path.Combine(Workspace.Root!, files[0])));
        }, DispatcherPriority.Background);
    }

    private void ManagePackages_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.Root != null) new PackagesDialog(Workspace.Root).ShowDialog();
    }

    private async void Composer_Click(object sender, RoutedEventArgs e) => await RunComposer((string)((FrameworkElement)sender).Tag);

    private async void ComposerRequire_Click(object sender, RoutedEventArgs e)
    {
        var dev = Equals(((FrameworkElement)sender).Tag, "dev");
        var pkg = InputDialog.Ask(dev ? "composer require --dev" : "composer require", "Package(s):", "", "e.g. symfony/var-dumper or vendor/package:^1.0");
        if (string.IsNullOrWhiteSpace(pkg)) return;
        await RunComposer($"require {(dev ? "--dev " : "")}{pkg}");
    }

    private async Task RunComposer(string args)
    {
        if (Workspace.Root == null) return;
        SaveAll();
        ShowBottom(RunConsole.IsRunning ? "terminal" : "run");
        var (exe, a) = ComposerService.Command(Workspace.PhpExe, args + " --no-interaction");
        var console = RunConsole.IsRunning ? TerminalConsole : RunConsole;
        var code = await console.RunAsync(exe, a, Workspace.Root, $"composer {args}");
        if (code == 0 && Regex.IsMatch(args, @"^(install|update|require|remove|dump-autoload)")) await ReindexAsync();
    }

    private async void Interpreter_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new InterpreterDialog(Workspace.PhpExe);
        if (dlg.ShowDialog() != true || dlg.Selected == null) return;
        await SetInterpreter(dlg.Selected.Path, save: true);
        await LoadNeoCommandsAsync();
        await ReindexAsync();
    }

    private async void Reindex_Click(object sender, RoutedEventArgs e) => await ReindexAsync();

    // ================================================================== project tree

    private FileNode? SelectedNode => ProjectTree.SelectedItem as FileNode;

    private string SelectedDirectory()
    {
        var n = SelectedNode;
        if (n == null) return Workspace.Root ?? Environment.CurrentDirectory;
        return n.IsDirectory ? n.FullPath : Path.GetDirectoryName(n.FullPath)!;
    }

    private void ProjectTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedNode is { IsDirectory: false } n) { OpenFile(n.FullPath); e.Handled = true; }
    }

    private void ProjectTree_KeyDown(object sender, KeyEventArgs e)
    {
        var n = SelectedNode;
        if (n == null) return;
        switch (e.Key)
        {
            case Key.Enter when SelectedNodes().Count(x => !x.IsDirectory) > 1: OpenSelected_Click(sender, e); e.Handled = true; break;
            case Key.Enter when !n.IsDirectory: OpenFile(n.FullPath); e.Handled = true; break;
            case Key.Enter: n.IsExpanded = !n.IsExpanded; e.Handled = true; break;
            case Key.Delete: Delete_Click(sender, e); e.Handled = true; break;
            case Key.F6 when Keyboard.Modifiers == ModifierKeys.Shift: Rename_Click(sender, e); e.Handled = true; break;
        }
    }

    private void ProjectTree_RightDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is FileNode n) n.IsSelected = true;
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private void SelectInProject(string path)
    {
        if (_root == null) return;
        ShowSidePanel("project");
        // expand ancestors
        var dir = Path.GetDirectoryName(path);
        var chain = new Stack<string>();
        while (dir != null && dir.Length >= _root.FullPath.Length) { chain.Push(dir); dir = Path.GetDirectoryName(dir); }
        foreach (var d in chain) { var node = _root.Find(d); if (node != null) node.IsExpanded = true; }
        var target = _root.Find(path);
        if (target == null) return;
        target.IsSelected = true;
        Dispatcher.BeginInvoke(() =>
        {
            var tvi = FindTreeItem(ProjectTree, target);
            tvi?.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private static TreeViewItem? FindTreeItem(ItemsControl parent, FileNode target)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem tvi) continue;
            if (item == target) return tvi;
            if (item is FileNode n && target.FullPath.StartsWith(n.FullPath, StringComparison.OrdinalIgnoreCase) && tvi.IsExpanded)
            {
                tvi.UpdateLayout();
                var r = FindTreeItem(tvi, target);
                if (r != null) return r;
            }
        }
        return null;
    }

    private void SelectInProject_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTab != null) SelectInProject(CurrentTab.Path);
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (_root == null) return;
        void Collapse(FileNode n) { foreach (var c in n.Children) { if (c.IsDirectory) Collapse(c); c.IsExpanded = false; } }
        Collapse(_root);
    }

    private void RefreshTree_Click(object sender, RoutedEventArgs e)
    {
        _root?.Reload();
        _root?.RefreshKinds();
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        var dir = SelectedDirectory();
        var name = InputDialog.Ask("New File", $"File name (in {Path.GetRelativePath(Workspace.Root ?? dir, dir)}):", "",
            "Sub-directories are created: e.g. home/show.php", ValidateNewPath(dir));
        if (name == null) return;
        CreateFile(Path.Combine(dir, name), DefaultContent(Path.Combine(dir, name)));
    }

    private void NewTypedFile_Click(object sender, RoutedEventArgs e)
    {
        var ext = (string)((FrameworkElement)sender).Tag;
        var dir = SelectedDirectory();
        var name = InputDialog.Ask("New File", $"Name of the {ext} file:", "", null, v => ValidateNewPath(dir)(v.EndsWith(ext) ? v : v + ext));
        if (name == null) return;
        if (!name.EndsWith(ext)) name += ext;
        CreateFile(Path.Combine(dir, name), DefaultContent(Path.Combine(dir, name)));
    }

    private static Func<string, string?> ValidateNewPath(string dir) => v =>
    {
        if (string.IsNullOrWhiteSpace(v)) return "Enter a name";
        if (v.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || v.Contains(':')) return "Invalid name";
        return File.Exists(Path.Combine(dir, v)) || Directory.Exists(Path.Combine(dir, v)) ? "This file already exists" : null;
    };

    private static string DefaultContent(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".php")
        {
            var inTemplates = path.Contains("\\templates\\", StringComparison.OrdinalIgnoreCase);
            if (inTemplates) return "<?php /** @var array $data */ ?>\n";
            var ns = Workspace.NamespaceForDirectory(Path.GetDirectoryName(path)!);
            return "<?php\n\ndeclare(strict_types=1);\n" + (ns != null ? $"\nnamespace {ns};\n" : "") + "\n";
        }
        if (path.EndsWith(".html.twig")) return "{% extends 'base.html.twig' %}\n\n{% block body %}\n\n{% endblock %}\n";
        if (ext is ".html" or ".htm") return "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n    <meta charset=\"UTF-8\">\n    <title></title>\n</head>\n<body>\n\n</body>\n</html>\n";
        return "";
    }

    private void CreateFile(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            FlushAndSelect(path);
            OpenFile(path, content.Length);
        }
        catch (Exception ex) { MessageDialog.Show("New File", ex.Message); }
    }

    private void FlushAndSelect(string path)
    {
        var dirNode = _root?.Find(Path.GetDirectoryName(path)!);
        dirNode?.Reload();
        if (dirNode != null) dirNode.IsExpanded = true;
        if (path.EndsWith(".php")) Workspace.Index.UpdateFile(path);
        SelectInProject(path);
    }

    private void NewDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dir = SelectedDirectory();
        var name = InputDialog.Ask("New Directory", "Directory name (a/b/c creates nested directories):", "", null, ValidateNewPath(dir));
        if (name == null) return;
        var full = Path.Combine(dir, name);
        Directory.CreateDirectory(full);
        _root?.Find(dir)?.Reload();
        SelectInProject(full);
    }

    private void NewClass_Click(object sender, RoutedEventArgs e)
    {
        var dir = SelectedDirectory();
        if (Workspace.Root != null && string.Equals(dir, Workspace.Root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.Combine(dir, "src");
        var dlg = new NewClassDialog(dir);
        if (dlg.ShowDialog() == true && dlg.CreatedFile != null)
        {
            FlushAndSelect(dlg.CreatedFile);
            var text = File.ReadAllText(dlg.CreatedFile);
            OpenFile(dlg.CreatedFile, Math.Max(0, text.LastIndexOf("    \n", StringComparison.Ordinal) + 4));
        }
    }

    private void NewNeoTemplate_Click(object sender, RoutedEventArgs e)
    {
        var kind = (string)((FrameworkElement)sender).Tag;
        switch (kind)
        {
            case "controller":
                if (_neoCommands.Any(c => c.name == "make:controller")) RunMake("make:controller", "Generates a controller in src/Controller/ and its template");
                else
                {
                    var dlg = new NewClassDialog(Path.Combine(Workspace.Root!, "src", "Controller"), "Controller", "Class", "NeoPHP\\Component\\Controller\\Contract\\AbstractController");
                    if (dlg.ShowDialog() == true && dlg.CreatedFile != null) { FlushAndSelect(dlg.CreatedFile); OpenFile(dlg.CreatedFile); }
                }
                break;
            case "command":
                RunMake("make:command", "Generates a console command in src/Command/");
                break;
            case "view":
                var dir = SelectedDirectory();
                if (!dir.Contains("templates", StringComparison.OrdinalIgnoreCase) && Workspace.Root != null) dir = Path.Combine(Workspace.Root, "templates");
                var name = InputDialog.Ask("New template", "Template name (e.g. blog/index):", "", null, v => ValidateNewPath(dir)(v.EndsWith(".php") ? v : v + ".php"));
                if (name == null) return;
                if (!name.EndsWith(".php")) name += ".php";
                CreateFile(Path.Combine(dir, name), "<?php\n/**\n * @var string $title\n */\n?>\n<h1><?= htmlspecialchars($title) ?></h1>\n");
                break;
        }
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        var n = SelectedNode;
        if (n == null || n.IsRoot || Workspace.Root == null) return;
        var dir = Path.GetDirectoryName(n.FullPath)!;
        var name = InputDialog.Ask("Rename", $"Rename {(n.IsDirectory ? "directory" : "file")} '{n.Name}' to:", n.Name,
            "References are updated (classes, namespaces, use statements, templates). You will see the list of files before anything is changed.",
            v => v == n.Name ? null : ValidateNewPath(dir)(v));
        if (name == null || name == n.Name) return;
        var oldPath = n.FullPath;
        var target = Path.Combine(dir, name);
        try
        {
            SaveAll();
            // ---- compute the refactoring on the current files (before moving anything)
            string Read(string p) => _tabs.FirstOrDefault(t => string.Equals(t.Path, p, StringComparison.OrdinalIgnoreCase))?.Editor?.Text ?? File.ReadAllText(p);
            var changes = new List<FileChange>();
            string? description = null;
            var templatesDir = Path.Combine(Workspace.Root, "templates");
            if (!n.IsDirectory && oldPath.EndsWith(".php", StringComparison.OrdinalIgnoreCase) && target.EndsWith(".php", StringComparison.OrdinalIgnoreCase)
                && !oldPath.StartsWith(templatesDir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                var oldClass = Path.GetFileNameWithoutExtension(oldPath);
                var newClass = Path.GetFileNameWithoutExtension(target);
                var cls = Workspace.Index.GetFile(oldPath)?.Classes.FirstOrDefault(c => c.Name == oldClass);
                if (cls != null && Regex.IsMatch(newClass, @"^[A-Za-z_]\w*$"))
                {
                    var newFqn = (cls.Namespace.Length > 0 ? cls.Namespace + "\\" : "") + newClass;
                    changes = RefactorService.RenameClasses(Workspace.Root, new Dictionary<string, string> { [cls.Fqn] = newFqn }, new(), Read);
                    description = $"Rename class {cls.Fqn} → {newClass}";
                }
            }
            else if (n.IsDirectory)
            {
                var oldNs = Workspace.NamespaceForDirectory(oldPath);
                var newNs = Workspace.NamespaceForDirectory(target);
                if (oldNs != null && newNs != null && oldNs != newNs)
                {
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var moved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var c in Workspace.Index.AllClasses().Where(c => c.File != null && c.File.StartsWith(oldPath + "\\", StringComparison.OrdinalIgnoreCase)))
                    {
                        map[c.Fqn] = newNs + c.Fqn[oldNs.Length..];
                        moved[c.File!] = newNs + c.Namespace[Math.Min(oldNs.Length, c.Namespace.Length)..];
                    }
                    if (map.Count > 0)
                    {
                        changes = RefactorService.RenameClasses(Workspace.Root, map, moved, Read);
                        description = $"Move namespace {oldNs} → {newNs} ({map.Count} class(es))";
                    }
                }
            }
            if (oldPath.StartsWith(templatesDir + "\\", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(oldPath) is ".php" or ".twig")
            {
                static string TemplateName(string root, string p)
                {
                    var rel = Path.GetRelativePath(root, p).Replace('\\', '/');
                    return rel.EndsWith(".html.twig") ? rel[..^10] : rel[..rel.LastIndexOf('.')];
                }
                var oldName = TemplateName(templatesDir, oldPath);
                var newName = TemplateName(templatesDir, target);
                if (oldName != newName)
                {
                    changes = RefactorService.RenameTemplate(Workspace.Root, oldName, newName, Read);
                    description = $"Rename template '{oldName}' → '{newName}'";
                }
            }

            if (changes.Count > 0)
            {
                var list = string.Join("\n", changes.Take(25).Select(c => $"  {Path.GetRelativePath(Workspace.Root, c.Path)}   ({c.Occurrences})"))
                           + (changes.Count > 25 ? $"\n  … {changes.Count - 25} more" : "");
                if (!MessageDialog.Confirm("Rename refactoring", $"{description}\n\n{changes.Sum(c => c.Occurrences)} change(s) in {changes.Count} file(s):\n{list}", "Refactor", "Rename only"))
                    changes.Clear();
            }

            // ---- move on disk, then write the refactored contents at their new location
            var openTabs = _tabs.Where(t => t.Path.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase)).Select(t => t.Path).ToList();
            foreach (var t in _tabs.Where(t => t.Path.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase)).ToList()) CloseTab(t, save: false);
            if (n.IsDirectory) Directory.Move(oldPath, target);
            else File.Move(oldPath, target);
            string Moved(string p) => p.StartsWith(oldPath, StringComparison.OrdinalIgnoreCase) ? target + p[oldPath.Length..] : p;
            foreach (var c in changes)
            {
                var dest = Moved(c.Path);
                var tab = _tabs.FirstOrDefault(t => string.Equals(t.Path, dest, StringComparison.OrdinalIgnoreCase));
                if (tab?.Editor != null) { tab.Editor.Document.Text = c.NewText; tab.Dirty = true; Save(tab); }
                else File.WriteAllText(dest, c.NewText);
                if (dest.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) Workspace.Index.UpdateFile(dest);
            }
            if (n.IsDirectory)
                foreach (var f in PhpIndex.EnumeratePhpFiles(target)) { Workspace.Index.RemoveFile(oldPath + f[target.Length..]); Workspace.Index.UpdateFile(f); }
            else if (target.EndsWith(".php", StringComparison.OrdinalIgnoreCase)) { Workspace.Index.RemoveFile(oldPath); Workspace.Index.UpdateFile(target); }
            foreach (var p in openTabs) OpenFile(Moved(p), focus: false);
            _root?.Find(dir)?.Reload();
            SelectInProject(target);
            SetStatus(changes.Count > 0 ? $"{description}: {changes.Sum(c => c.Occurrences)} change(s) in {changes.Count} file(s)" : $"Renamed to {name}");
        }
        catch (Exception ex) { MessageDialog.Show("Rename", ex.Message); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var nodes = SelectedNodes().Where(x => !x.IsRoot).ToList();
        if (nodes.Count == 0) return;
        var what = nodes.Count == 1 ? $"{(nodes[0].IsDirectory ? "the directory" : "the file")} '{nodes[0].Name}'" : $"{nodes.Count} items";
        if (!MessageDialog.Confirm("Delete", $"Move {what} to the Recycle Bin?", "Delete")) return;
        foreach (var n in nodes)
        {
            try
            {
                foreach (var t in _tabs.Where(t => t.Path.StartsWith(n.FullPath, StringComparison.OrdinalIgnoreCase)).ToList()) CloseTab(t, save: false);
                if (!File.Exists(n.FullPath) && !Directory.Exists(n.FullPath)) continue; // inside an already deleted directory
                RecycleBin.Send(n.FullPath);
                if (n.FullPath.EndsWith(".php")) Workspace.Index.RemoveFile(n.FullPath);
            }
            catch (Exception ex) { MessageDialog.Show("Delete", ex.Message); }
        }
        foreach (var parent in nodes.Select(x => x.Parent).Distinct()) parent?.Reload();
        ClearMultiSelection();
    }

    // ------------------------------------------------------------------ multi-selection (Ctrl+click / Shift+click)

    private FileNode? _selectionAnchor;

    private List<FileNode> SelectedNodes()
    {
        var list = new List<FileNode>();
        void Walk(FileNode n) { if (n.IsMultiSelected) list.Add(n); foreach (var c in n.Children) if (c.FullPath.Length > 0) Walk(c); }
        if (_root != null) Walk(_root);
        if (list.Count == 0 && SelectedNode != null) list.Add(SelectedNode);
        return list;
    }

    private void ClearMultiSelection()
    {
        void Walk(FileNode n) { n.IsMultiSelected = false; foreach (var c in n.Children) if (c.FullPath.Length > 0) Walk(c); }
        if (_root != null) Walk(_root);
    }

    private List<FileNode> VisibleNodes()
    {
        var list = new List<FileNode>();
        void Walk(FileNode n) { list.Add(n); if (n.IsExpanded) foreach (var c in n.Children) if (c.FullPath.Length > 0) Walk(c); }
        if (_root != null) Walk(_root);
        return list;
    }

    private void ProjectTree_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<ToggleButton>(e.OriginalSource as DependencyObject) != null) return; // expander arrow
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not FileNode node) return;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0)
        {
            // Ctrl+click: toggle the node in the selection
            if (!SelectedNodes().Any(x => x.IsMultiSelected) && SelectedNode != null && SelectedNode != node) SelectedNode.IsMultiSelected = true;
            node.IsMultiSelected = !node.IsMultiSelected;
            _selectionAnchor = node;
            e.Handled = true;
            ProjectTree.Focus();
            return;
        }
        if ((mods & ModifierKeys.Shift) != 0 && (_selectionAnchor ?? SelectedNode) is { } anchor)
        {
            // Shift+click: range of visible nodes
            var visible = VisibleNodes();
            int a = visible.IndexOf(anchor), b = visible.IndexOf(node);
            if (a >= 0 && b >= 0)
            {
                ClearMultiSelection();
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) visible[i].IsMultiSelected = true;
                e.Handled = true;
                ProjectTree.Focus();
                return;
            }
        }
        ClearMultiSelection();
        _selectionAnchor = node;
    }

    private void OpenSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in SelectedNodes().Where(x => !x.IsDirectory)) OpenFile(n.FullPath, focus: false);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e) { var n = SelectedNodes(); if (n.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, n.Select(x => x.FullPath))); }

    private void CopyRelativePath_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode != null && Workspace.Root != null) Clipboard.SetText(Path.GetRelativePath(Workspace.Root, SelectedNode.FullPath).Replace('\\', '/'));
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedNode != null) Process.Start("explorer.exe", SelectedNode.IsDirectory ? $"\"{SelectedNode.FullPath}\"" : $"/select,\"{SelectedNode.FullPath}\"");
    }

    private void OpenInTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (PseudoConsole.IsSupported) NewShell(DefaultShell, SelectedDirectory());
        else NewTerminal(ConsoleKind.PowerShell, SelectedDirectory());
        ShowBottom("terminal");
    }

    // ================================================================== navigation

    private void ShowSearch(SearchEverywhereWindow.Mode mode)
    {
        if (Workspace.Root == null) return;
        var dlg = new SearchEverywhereWindow(mode);
        if (dlg.ShowDialog() == true && dlg.Result != null) OpenFile(dlg.Result.File, dlg.Result.Offset > 0 ? dlg.Result.Offset : null);
    }

    private void SearchEverywhere_Click(object sender, RoutedEventArgs e) => ShowSearch(SearchEverywhereWindow.Mode.All);
    private void GoToClass_Click(object sender, RoutedEventArgs e) => ShowSearch(SearchEverywhereWindow.Mode.Classes);
    private void GoToFile_Click(object sender, RoutedEventArgs e) => ShowSearch(SearchEverywhereWindow.Mode.Files);
    private void GoToDeclaration_Click(object sender, RoutedEventArgs e) { if (CurrentEditor is { } ed) ed.GoToDeclaration(ed.CaretOffset); }
    private void Completion_Click(object sender, RoutedEventArgs e) => CurrentEditor?.ShowCompletion(true);
    private void QuickFix_Click(object sender, RoutedEventArgs e) => CurrentEditor?.ShowQuickFixes();
    private void OptimizeImports_Click(object sender, RoutedEventArgs e) => CurrentEditor?.OptimizeImports();
    private void Undo_Click(object sender, RoutedEventArgs e) => CurrentEditor?.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => CurrentEditor?.Redo();
    private void Find_Click(object sender, RoutedEventArgs e) => ApplicationCommands.Find.Execute(null, CurrentEditor?.TextArea);

    private async void FindInFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Workspace.Root == null) return;
        var initial = CurrentEditor?.SelectedText is { Length: > 0 and < 100 } s ? s : "";
        var query = InputDialog.Ask("Find in Files", "Text to find (project files, vendor excluded):", initial);
        if (string.IsNullOrEmpty(query)) return;
        ShowBottom("find");
        FindTab.Header = "Find: searching…";
        var root = Workspace.Root;
        var results = await Task.Run(() =>
        {
            var list = new List<FindHit>();
            foreach (var f in SearchEverywhereWindow.EnumerateProjectFiles(root))
            {
                if (list.Count > 2000) break;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 2 * 1024 * 1024 || IsBinary(f)) continue;
                    var lines = File.ReadAllLines(f);
                    int offset = 0;
                    var text = File.ReadAllText(f);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var idx = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            var lineStart = NthLineOffset(text, i);
                            list.Add(new FindHit(f, lineStart + idx, $"{Path.GetRelativePath(root, f)}:{i + 1}    {lines[i].Trim()}"));
                        }
                        offset += lines[i].Length + 1;
                    }
                }
                catch { }
            }
            return list;
        });
        FindResults.ItemsSource = results;
        FindTab.Header = $"Find: {results.Count} result(s) for '{query}'";
    }

    private static int NthLineOffset(string text, int line)
    {
        int off = 0;
        for (int i = 0; i < line; i++)
        {
            var nl = text.IndexOf('\n', off);
            if (nl < 0) return text.Length;
            off = nl + 1;
        }
        return off;
    }

    private sealed record FindHit(string File, int Offset, string Display)
    {
        public override string ToString() => Display;
    }

    private void FindResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindResults.SelectedItem is FindHit h) OpenFile(h.File, h.Offset);
    }

    // ================================================================== keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        // AltGr (= Ctrl+Alt for Windows) types characters like @ { } [ ] on AZERTY: no IDE shortcut must catch it
        if (KeyboardHelper.IsAltGr) { _shiftClean = false; return; }
        if (key is not (Key.LeftShift or Key.RightShift)) _shiftClean = false;
        else if (!e.IsRepeat) _shiftClean = mods == ModifierKeys.Shift;

        bool Handle(Action a) { a(); e.Handled = true; return true; }

        // a terminal gets every key (Ctrl+K, Ctrl+T, F7, Esc… belong to the shell) except the tool window shortcuts
        if (Keyboard.FocusedElement is TerminalView)
        {
            var toolWindowKey = mods == ModifierKeys.Alt && key is Key.F12 or (>= Key.D0 and <= Key.D9) || mods == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.S;
            if (!toolWindowKey) return;
        }

        // debugger keys (PhpStorm keymap) while a script is debugged
        var debugging = DebugService.State is DebugState.Paused or DebugState.Running;
        if (debugging)
        {
            var handled = (key, mods) switch
            {
                (Key.F9, ModifierKeys.None) => Handle(() => _ = DebugService.Resume()),
                (Key.F8, ModifierKeys.None) => Handle(() => _ = DebugService.StepOver()),
                (Key.F7, ModifierKeys.None) => Handle(() => _ = DebugService.StepInto()),
                (Key.F8, ModifierKeys.Shift) => Handle(() => _ = DebugService.StepOut()),
                (Key.F8, ModifierKeys.Alt) => Handle(() => DebugEvaluate_Click(this, e)),
                (Key.F2, ModifierKeys.Control) => Handle(() => Stop_Click(this, e)),
                _ => false,
            };
            if (handled) return;
        }

        _ = (key, mods) switch
        {
            (Key.D5, ModifierKeys.Alt) => Handle(() => ShowBottom(IsBottomVisible("debug") ? null : "debug")),
            (Key.D6, ModifierKeys.Alt) => Handle(() => ShowBottom(IsBottomVisible("problems") ? null : "problems")),
            (Key.F9, ModifierKeys.Shift) => Handle(() => DebugServer_Click(this, e)),
            (Key.S, ModifierKeys.Control) => Handle(() => { SaveAll(); SetStatus("All files saved"); }),
            (Key.K, ModifierKeys.Control) => Handle(() => GitCommitPanel_Click(this, e)),
            (Key.K, ModifierKeys.Control | ModifierKeys.Shift) => Handle(() => GitPush_Click(this, e)),
            (Key.T, ModifierKeys.Control) => Handle(() => GitPull_Click(this, e)),
            (Key.D0, ModifierKeys.Alt) => Handle(() => ShowSidePanel(SideColumn.Width.Value > 0 && CommitView.Visibility == Visibility.Visible ? null : "commit")),
            (Key.D9, ModifierKeys.Alt) => Handle(() => ShowBottom(IsBottomVisible("git") ? null : "git")),
            (Key.Oem3, ModifierKeys.Control | ModifierKeys.Shift) => Handle(() => GitWidget_Click(GitWidget, e)),
            (Key.F4, ModifierKeys.Control) => Handle(() => { if (CurrentTab != null) CloseTab(CurrentTab); }),
            (Key.N, ModifierKeys.Control) => Handle(() => ShowSearch(SearchEverywhereWindow.Mode.Classes)),
            (Key.N, ModifierKeys.Control | ModifierKeys.Shift) => Handle(() => ShowSearch(SearchEverywhereWindow.Mode.Files)),
            (Key.F, ModifierKeys.Control | ModifierKeys.Shift) => Handle(() => FindInFiles_Click(this, e)),
            (Key.D1, ModifierKeys.Alt) => Handle(() => ShowSidePanel(SideColumn.Width.Value > 0 && ProjectTree.Visibility == Visibility.Visible ? null : "project")),
            (Key.D7, ModifierKeys.Alt) => Handle(() => ShowSidePanel(SideColumn.Width.Value > 0 && StructureList.Visibility == Visibility.Visible ? null : "structure")),
            (Key.D4, ModifierKeys.Alt) => Handle(() => ShowBottom(IsBottomVisible("run") ? null : "run")),
            (Key.F12, ModifierKeys.Alt) => Handle(() => ShowBottom(IsBottomVisible("terminal") ? null : "terminal")),
            (Key.F1, ModifierKeys.Alt) => Handle(() => { if (CurrentTab != null) SelectInProject(CurrentTab.Path); }),
            (Key.Insert, ModifierKeys.Alt) => Handle(() => NewFile_Click(this, e)),
            (Key.F10, ModifierKeys.Shift) => Handle(() => Run_Click(this, e)),
            (Key.F2, ModifierKeys.Control) => Handle(() => RunConsole.Stop()),
            (Key.S, ModifierKeys.Control | ModifierKeys.Alt) => Handle(() => Settings_Click(this, e)),
            (Key.X, ModifierKeys.Control | ModifierKeys.Shift) => Handle(() => RunNeoCommand_Click(this, e)),
            (Key.OemPlus, ModifierKeys.Control | ModifierKeys.Alt) or (Key.Add, ModifierKeys.Control | ModifierKeys.Alt) => Handle(() => ZoomIn_Click(this, e)),
            (Key.OemMinus, ModifierKeys.Control | ModifierKeys.Alt) or (Key.Subtract, ModifierKeys.Control | ModifierKeys.Alt) => Handle(() => ZoomOut_Click(this, e)),
            (Key.D0, ModifierKeys.Control | ModifierKeys.Alt) or (Key.NumPad0, ModifierKeys.Control | ModifierKeys.Alt) => Handle(() => ResetZoom_Click(this, e)),
            (Key.OemPlus, ModifierKeys.Control) or (Key.Add, ModifierKeys.Control) => Handle(() => ChangeFont(1)),
            (Key.OemMinus, ModifierKeys.Control) or (Key.Subtract, ModifierKeys.Control) => Handle(() => ChangeFont(-1)),
            (Key.Tab, ModifierKeys.Control) => Handle(() => CycleTab(1)),
            (Key.Tab, ModifierKeys.Control | ModifierKeys.Shift) => Handle(() => CycleTab(-1)),
            (Key.Escape, ModifierKeys.None) when BottomRow.Height.Value > 0 && Keyboard.FocusedElement is not ICSharpCode.AvalonEdit.Editing.TextArea && !IsInside(TerminalTabs)
                => Handle(() => ShowBottom(null)),
            _ => false,
        };
    }

    private bool IsInside(DependencyObject container) =>
        Keyboard.FocusedElement is DependencyObject d && FindAncestor(d, container);

    private static bool FindAncestor(DependencyObject d, DependencyObject container)
    {
        while (d != null) { if (d == container) return true; d = VisualTreeHelper.GetParent(d); }
        return false;
    }

    private bool _suppressAltMenu;

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_suppressAltMenu && e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt)
        {
            _suppressAltMenu = false;
            e.Handled = true; // no menu activation after an Alt+click
            return;
        }
        if (e.Key is not (Key.LeftShift or Key.RightShift) || !_shiftClean) return;
        var now = DateTime.Now;
        if ((now - _lastShift).TotalMilliseconds < 400)
        {
            _lastShift = DateTime.MinValue;
            ShowSearch(SearchEverywhereWindow.Mode.All);
        }
        else _lastShift = now;
    }

    private void CycleTab(int dir)
    {
        if (_tabs.Count < 2) return;
        var i = EditorTabs.SelectedIndex;
        EditorTabs.SelectedIndex = (i + dir + _tabs.Count) % _tabs.Count;
        CurrentEditor?.TextArea.Focus();
    }

    private void ChangeFont(double delta)
    {
        AppSettings.Current.EditorFontSize = Math.Clamp(AppSettings.Current.EditorFontSize + delta, 8, 40);
        DisplayManager.Apply();
        SetStatus($"Editor font size: {AppSettings.Current.EditorFontSize}");
    }

    // ================================================================== appearance & display

    private async void OnPackagesChanged()
    {
        FlushFsChanges();
        await LoadNeoCommandsAsync();
        await ReindexAsync();
    }

    private void OnDisplayChanged()
    {
        var s = AppSettings.Current;
        StatusBar.Visibility = s.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
        ToolStrip.Visibility = s.ShowToolStrip ? Visibility.Visible : Visibility.Collapsed;
        StatusZoom.Text = DisplayManager.ZoomLabel;
        ZoomButton.Visibility = Math.Abs(s.UiScale - 1) > 0.001 ? Visibility.Visible : Visibility.Collapsed;
        CompactItem.IsChecked = s.CompactMode;
        WrapItem.IsChecked = s.EditorWordWrap;
        WhitespaceItem.IsChecked = s.EditorShowWhitespace;
        LineNumbersItem.IsChecked = s.EditorShowLineNumbers;
        StatusBarItem.IsChecked = s.ShowStatusBar;
        ToolStripItem.IsChecked = s.ShowToolStrip;
    }

    private static void Toggle(Action<AppSettings> change)
    {
        change(AppSettings.Current);
        DisplayManager.Apply();
    }

    private void Appearance_Click(object sender, RoutedEventArgs e) => new SettingsDialog("Appearance").ShowDialog();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { DisplayManager.Zoom(1); SetStatus("IDE zoom: " + DisplayManager.ZoomLabel); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { DisplayManager.Zoom(-1); SetStatus("IDE zoom: " + DisplayManager.ZoomLabel); }
    private void ResetZoom_Click(object sender, RoutedEventArgs e) { DisplayManager.Zoom(0); SetStatus("IDE zoom: 100%"); }
    private void Compact_Click(object sender, RoutedEventArgs e) => Toggle(s => s.CompactMode = !s.CompactMode);
    private void Wrap_Click(object sender, RoutedEventArgs e) => Toggle(s => s.EditorWordWrap = !s.EditorWordWrap);
    private void Whitespace_Click(object sender, RoutedEventArgs e) => Toggle(s => s.EditorShowWhitespace = !s.EditorShowWhitespace);
    private void LineNumbers_Click(object sender, RoutedEventArgs e) => Toggle(s => s.EditorShowLineNumbers = !s.EditorShowLineNumbers);
    private void StatusBar_Click(object sender, RoutedEventArgs e) => Toggle(s => s.ShowStatusBar = !s.ShowStatusBar);
    private void ToolStrip_Click(object sender, RoutedEventArgs e) => Toggle(s => s.ShowToolStrip = !s.ShowToolStrip);

    private void FontBigger_Click(object sender, RoutedEventArgs e) => ChangeFont(1);
    private void FontSmaller_Click(object sender, RoutedEventArgs e) => ChangeFont(-1);

    // ================================================================== misc menu actions

    private void SetStatus(string msg)
    {
        StatusMessage.Text = msg;
        StatusMessage.ClearValue(TextBlock.ForegroundProperty);
    }

    private void UpdateThemeMenu()
    {
        ThemeMenu.Items.Clear();
        foreach (var t in Editor.ThemeCatalog.All)
        {
            var item = new MenuItem { Header = t.Name, IsCheckable = true, IsChecked = t.Name == ThemeManager.CurrentName, InputGestureText = t.IsDark ? "dark" : "light" };
            var name = t.Name;
            item.Click += (_, _) => ThemeManager.Apply(name);
            ThemeMenu.Items.Add(item);
        }
        ThemeMenu.Items.Add(new Separator());
        var more = new MenuItem { Header = "Themes settings…" };
        more.Click += (_, _) => new SettingsDialog("Themes").ShowDialog();
        ThemeMenu.Items.Add(more);
    }
    private void ToggleTheme_Click(object sender, RoutedEventArgs e) => ThemeManager.Apply(ThemeManager.IsDark ? IdeTheme.Light : IdeTheme.Dark);
    private void ToggleProject_Click(object sender, RoutedEventArgs e) => ShowSidePanel(SideColumn.Width.Value > 0 ? null : "project");

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // with a badge, open the page that has something to update
        var page = AvailableUpdates.Ide ? "Updates" : AvailableUpdates.Plugins.Count > 0 ? "Plugins" : AvailableUpdates.Themes.Count > 0 ? "Themes" : null;
        new SettingsDialog(page).ShowDialog();
    }

    private void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewProjectWindow();
        if (dlg.ShowDialog() == true && dlg.CreatedPath != null) SwitchProject(dlg.CreatedPath);
    }

    private void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Open project directory" };
        if (dlg.ShowDialog(this) == true) SwitchProject(dlg.FolderName);
    }

    private void ProjectWidget_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProjectWidget, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var np = new MenuItem { Header = "New Project…" };
        np.Click += NewProject_Click;
        var op = new MenuItem { Header = "Open…" };
        op.Click += OpenProject_Click;
        menu.Items.Add(np);
        menu.Items.Add(op);
        menu.Items.Add(new Separator());
        foreach (var r in AppSettings.Current.RecentProjects.Where(r => Directory.Exists(r.Path)).Take(12))
        {
            var item = new MenuItem { Header = r.Name, InputGestureText = r.Path, IsChecked = string.Equals(r.Path, Workspace.Root, StringComparison.OrdinalIgnoreCase) };
            var p = r.Path;
            item.Click += (_, _) => SwitchProject(p);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveAll();
    private void CloseTab_Click(object sender, RoutedEventArgs e) { if (CurrentTab != null) CloseTab(CurrentTab); }

    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        ReturnToWelcome = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Docs_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://github.com/NeoPHP-Framework/neophp/blob/v2.x/docs/v2.x/README.md") { UseShellExecute = true });

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => MessageDialog.Show("Keyboard shortcuts (PhpStorm keymap)",
        "Double Shift   Search Everywhere\nCtrl+N   Go to class\nCtrl+Shift+N   Go to file\nCtrl+B / Ctrl+Click   Go to declaration\n" +
        "Ctrl+Space   Completion\nAlt+Enter   Quick fixes (import class)\nCtrl+Alt+O   Optimize imports\nCtrl+D   Duplicate line\n" +
        "Ctrl+Y   Delete line\nCtrl+/   Comment line\nCtrl+Shift+↑/↓   Move line\nCtrl+F   Find   ·   Ctrl+Shift+F   Find in files\n" +
        "Alt+Insert   New file\nAlt+1   Project   ·   Alt+7   Structure\nAlt+F12   Terminal   ·   Alt+4   Run\n" +
        "Shift+F10   php bin/neo serve   ·   Ctrl+F2   Stop\nCtrl+Shift+X   Run a neo command\nCtrl+Alt+S   Settings\n\n" +
        "F2 / Shift+F2   Next / previous problem   ·   Alt+6   Problems\n" +
        "Shift+F9   Debug bin/neo serve   ·   Alt+5   Debug\nCtrl+F8   Toggle breakpoint   ·   F9   Resume\nF8   Step over   ·   F7   Step into   ·   Shift+F8   Step out\nAlt+F8   Evaluate expression");

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => UpdateDialog.ShowFor();

    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(UpdateService.RepositoryUrl + "/releases") { UseShellExecute = true });

    private string? _flashedFor;

    /// <summary>Badge on the settings button + overlay on the taskbar icon; the taskbar flashes once per new NeoIDE version.</summary>
    private void RefreshUpdateBadge()
    {
        var count = AvailableUpdates.Count;
        SettingsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.ToolTip = count > 0 ? $"Settings (Ctrl+Alt+S) — {AvailableUpdates.Describe()}" : "Settings (Ctrl+Alt+S)";
        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        TaskbarItemInfo.Overlay = count > 0 ? BadgeImage() : null;
        TaskbarItemInfo.Description = count > 0 ? AvailableUpdates.Describe() : "";
        var version = UpdateService.AvailableVersion;
        if (AvailableUpdates.Ide && version != null && version != _flashedFor && !IsActive)
        {
            _flashedFor = version;
            TaskbarFlash.Flash(this);
        }
    }

    private static ImageSource BadgeImage()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xE8, 0x55, 0x3D)), new Pen(Brushes.White, 1.5), new EllipseGeometry(new Point(8, 8), 6.5, 6.5)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private void RefreshUpdateStatus()
    {
        var v = UpdateService.AvailableVersion;
        (string? text, string tip) = UpdateService.State switch
        {
            UpdateState.Available => ($"Update {v}", $"NeoIDE {v} is available — click to update"),
            UpdateState.Downloading => ($"Update {v} · {UpdateService.Progress}%", $"Downloading NeoIDE {v}…"),
            UpdateState.Ready => ($"Restart to update ({v})", $"NeoIDE {v} is downloaded — click to restart"),
            _ => ((string?)null, ""),
        };
        StatusUpdateButton.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        StatusUpdate.Text = text ?? "";
        StatusUpdateButton.ToolTip = tip;
    }

    private void About_Click(object sender, RoutedEventArgs e) => MessageDialog.Show("About NeoIDE",
        $"NeoIDE {UpdateService.CurrentVersion}{(UpdateService.IsInstalled ? $" — {AppSettings.Current.UpdateChannel} channel" : " — development build")}\nThe IDE for the NeoPHP framework.\n\n" +
        $"PHP runtime: {Workspace.PhpVersion ?? "none"}\nComposer: {ComposerService.FindComposerPhar() ?? "composer (PATH)"}");
}
