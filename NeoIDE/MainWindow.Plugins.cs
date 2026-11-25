using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Dialogs;
using NeoIDE.Sdk;
using NeoIDE.Services;
using NeoIDE.Services.Plugins;

namespace NeoIDE;

/// <summary>Tool windows, editor documents and menu entries contributed by the plugins.</summary>
public partial class MainWindow
{
    private sealed class PluginPanel
    {
        public required ToolWindow Def { get; init; }
        public required ToggleButton Toggle { get; init; }
        public FrameworkElement? Content { get; set; }
        public TabItem? Tab { get; set; }
        public string Key => "plugin:" + Def.Id;
    }

    private readonly Dictionary<string, PluginPanel> _pluginPanels = new();
    private readonly Dictionary<string, TabItem> _pluginDocs = new();
    private Separator? _pluginMenuSeparator;

    private void InitPlugins()
    {
        foreach (var w in IdeHost.Instance.ToolWindows) AddPluginToolWindow(w);
        foreach (var m in IdeHost.Instance.MenuItems) AddPluginMenuItem(m);
    }

    // ------------------------------------------------------------------ tool windows

    public void AddPluginToolWindow(ToolWindow def)
    {
        if (_pluginPanels.ContainsKey("plugin:" + def.Id)) return;
        var toggle = new ToggleButton
        {
            Style = (Style)FindResource("StripToggle"), ToolTip = def.Title,
            Content = new TextBlock { Text = def.Icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = def.Location == ToolWindowLocation.Left ? 15 : 14 },
        };
        var panel = new PluginPanel { Def = def, Toggle = toggle };
        _pluginPanels[panel.Key] = panel;
        if (def.Location == ToolWindowLocation.Left)
        {
            StripTop.Children.Add(toggle);
            toggle.Click += (_, _) => ShowSidePanel(toggle.IsChecked == true ? panel.Key : null);
        }
        else
        {
            StripBottom.Children.Insert(StripBottom.Children.Count, toggle);
            toggle.Click += (_, _) => ShowBottom(toggle.IsChecked == true ? panel.Key : null);
        }
    }

    public void ShowPluginToolWindow(string id)
    {
        if (!_pluginPanels.TryGetValue("plugin:" + id, out var p)) return;
        if (p.Def.Location == ToolWindowLocation.Left) ShowSidePanel(p.Key);
        else ShowBottom(p.Key);
    }

    private FrameworkElement CreatePluginContent(PluginPanel p)
    {
        try { return p.Def.Create(); }
        catch (Exception ex)
        {
            PluginManager.Log($"tool window {p.Def.Id}: {ex}");
            return new TextBlock { Text = $"{p.Def.Title} failed to load:\n{ex.Message}", Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.Brush("ErrorFg") };
        }
    }

    /// <summary>Called by ShowSidePanel: shows the plugin panel named <paramref name="which"/> (or none).</summary>
    private void SyncPluginSidePanels(string? which)
    {
        foreach (var p in _pluginPanels.Values.Where(p => p.Def.Location == ToolWindowLocation.Left))
        {
            var active = p.Key == which;
            p.Toggle.IsChecked = active;
            if (active && p.Content == null)
            {
                p.Content = CreatePluginContent(p);
                SideHost.Children.Add(p.Content);
            }
            if (p.Content != null) p.Content.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active) SidePanelTitle.Text = p.Def.Title;
        }
    }

    private TabItem? PluginBottomTab(string? which)
    {
        if (which == null || !_pluginPanels.TryGetValue(which, out var p) || p.Def.Location != ToolWindowLocation.Bottom) return null;
        if (p.Tab == null)
        {
            p.Content = CreatePluginContent(p);
            p.Tab = new TabItem { Header = p.Def.Title, Content = p.Content, Style = (Style)FindResource("ToolTabItem") };
            BottomTabs.Items.Add(p.Tab);
        }
        return p.Tab;
    }

    private void SyncPluginBottomPanels(string? which)
    {
        foreach (var p in _pluginPanels.Values.Where(p => p.Def.Location == ToolWindowLocation.Bottom)) p.Toggle.IsChecked = p.Key == which;
    }

    // ------------------------------------------------------------------ documents (editor area)

    public void OpenPluginDocument(string id, string title, Func<FrameworkElement> create, string? icon)
    {
        if (_pluginDocs.TryGetValue(id, out var existing)) { EditorTabs.SelectedItem = existing; return; }
        FrameworkElement content;
        try { content = create(); }
        catch (Exception ex)
        {
            PluginManager.Log($"document {id}: {ex}");
            ShowStatus($"{title}: {ex.Message}", NotificationKind.Error);
            return;
        }
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = icon ?? "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("Accent"),
        });
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button
        {
            Style = (Style)FindResource("ToolButton"), Padding = new Thickness(3), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Close",
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 9, Foreground = ThemeManager.Brush("FgSecondary") },
        };
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = content, Style = (Style)FindResource("EditorTabItem"), ToolTip = title };
        close.Click += (_, _) => ClosePluginDocument(id);
        tab.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) ClosePluginDocument(id); };
        tab.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
            _suppressAltMenu = true;
            ClosePluginDocument(id);
            e.Handled = true;
        };
        _pluginDocs[id] = tab;
        EditorTabs.Items.Add(tab);
        EditorTabs.SelectedItem = tab;
        UpdateEmptyHint();
    }

    public void ClosePluginDocument(string id)
    {
        if (!_pluginDocs.Remove(id, out var tab)) return;
        if (tab.Content is IDisposable d) try { d.Dispose(); } catch { }
        EditorTabs.Items.Remove(tab);
        UpdateEmptyHint();
    }

    // ------------------------------------------------------------------ menu, files, console, status

    public void AddPluginMenuItem(PluginMenuItem item)
    {
        if (_pluginMenuSeparator == null)
        {
            _pluginMenuSeparator = new Separator();
            ToolsMenu.Items.Insert(0, _pluginMenuSeparator);
        }
        var mi = new MenuItem { Header = item.Header.Literal() };
        if (item.Icon != null) mi.Icon = new TextBlock { Text = item.Icon, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 13 };
        mi.Click += (_, _) =>
        {
            try { item.OnClick(); }
            catch (Exception ex) { PluginManager.Log($"menu {item.Header}: {ex}"); ShowStatus(ex.Message, NotificationKind.Error); }
        };
        ToolsMenu.Items.Insert(ToolsMenu.Items.IndexOf(_pluginMenuSeparator), mi);
    }

    public void OpenFileAt(string path, int line)
    {
        OpenFile(path, focus: true);
        if (line > 0 && CurrentEditor is { } ed && line <= ed.Document.LineCount)
            ed.GoToOffset(ed.Document.GetLineByNumber(line).Offset);
    }

    public void RunNeoInConsole(string args) => RunNeo(args);

    public void ShowStatus(string message, NotificationKind kind)
    {
        SetStatus(message);
        StatusMessage.Foreground = kind switch
        {
            NotificationKind.Error => ThemeManager.Brush("ErrorFg"),
            NotificationKind.Warning => ThemeManager.Brush("WarnFg"),
            NotificationKind.Success => ThemeManager.Brush("SuccessFg"),
            _ => ThemeManager.Brush("FgSecondary"),
        };
    }

    private void ManagePlugins_Click(object sender, RoutedEventArgs e) => new SettingsDialog("Plugins").ShowDialog();
}
