using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Controls;
using NeoIDE.Editor;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

public sealed record SearchHit(string Title, string? Detail, string File, int Offset, string? PhpKind, EntryKind? Kind, int Score);

/// <summary>PhpStorm "Search Everywhere" popup: files, classes and members.</summary>
public sealed class SearchEverywhereWindow : DialogWindow
{
    public enum Mode { All, Classes, Files, Symbols }

    private readonly TextBox _box = new() { FontSize = 15, Padding = new Thickness(8, 6, 8, 6) };
    private readonly ListBox _list = new() { Height = 420 };
    private readonly List<string> _files;
    private Mode _mode;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    public SearchHit? Result { get; private set; }

    public SearchEverywhereWindow(Mode mode)
    {
        _mode = mode;
        Title = "Search Everywhere";
        Width = 720;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _files = Workspace.Root == null ? new() : EnumerateProjectFiles(Workspace.Root).ToList();

        foreach (var m in Enum.GetValues<Mode>())
        {
            var b = new Button { Content = m.ToString(), Style = (Style)Application.Current.FindResource("ToolButton"), Margin = new Thickness(0, 0, 4, 0), Tag = m, Padding = new Thickness(10, 3, 10, 3) };
            b.Click += (_, _) => { _mode = (Mode)b.Tag; UpdateTabs(); Search(); _box.Focus(); };
            _tabs.Children.Add(b);
        }
        var p = new StackPanel { Margin = new Thickness(14, 0, 14, 14) };
        p.Children.Add(_tabs);
        p.Children.Add(_box);
        _list.Margin = new Thickness(0, 8, 0, 0);
        _list.ItemTemplate = BuildTemplate();
        p.Children.Add(_list);
        p.Children.Add(Hint("Enter: open · Tab: switch scope · class names support camel humps (e.g. HoCo → HomeController)"));
        Content = p;

        _box.TextChanged += (_, _) => Search();
        _box.PreviewKeyDown += OnKey;
        _list.MouseDoubleClick += (_, _) => Accept();
        Loaded += (_, _) => { UpdateTabs(); _box.Focus(); Search(); };
    }

    private void UpdateTabs()
    {
        foreach (Button b in _tabs.Children)
            b.Background = (Mode)b.Tag == _mode ? ThemeManager.Brush("SelectedInactiveBg") : Brushes.Transparent;
    }

    private static DataTemplate BuildTemplate()
    {
        var t = new DataTemplate(typeof(SearchHit));
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        var icon = new FrameworkElementFactory(typeof(FileIcon));
        icon.SetBinding(FileIcon.PathProperty, new System.Windows.Data.Binding("File"));
        icon.SetBinding(FileIcon.PhpKindProperty, new System.Windows.Data.Binding("PhpKind"));
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        dock.AppendChild(icon);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Detail"));
        detail.SetValue(DockPanel.DockProperty, Dock.Right);
        detail.SetValue(TextBlock.ForegroundProperty, ThemeManager.Brush("FgSecondary"));
        detail.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        detail.SetValue(FrameworkElement.MaxWidthProperty, 360.0);
        dock.AppendChild(detail);
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        dock.AppendChild(title);
        t.VisualTree = dock;
        return t;
    }

    public static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(d); dirs = Directory.GetDirectories(d); } catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in dirs)
            {
                var n = Path.GetFileName(s);
                if (n is ".git" or "node_modules" or ".idea" or ".neoide") continue;
                if (string.Equals(Path.GetDirectoryName(s), root, StringComparison.OrdinalIgnoreCase) && n is "var" or "vendor") continue;
                stack.Push(s);
            }
        }
    }

    private static int Score(string candidate, string query)
    {
        if (query.Length == 0) return 1;
        var c = candidate;
        if (c.Equals(query, StringComparison.OrdinalIgnoreCase)) return 1000;
        if (c.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 800 - c.Length;
        var idx = c.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return 500 - idx - c.Length;
        // camel humps: "HoCo" → HomeController
        int qi = 0;
        for (int i = 0; i < c.Length && qi < query.Length; i++)
        {
            if (char.ToLowerInvariant(c[i]) == char.ToLowerInvariant(query[qi]) && (qi == 0 ? i == 0 || char.IsUpper(c[i]) : char.IsUpper(c[i]) || char.IsUpper(query[qi]) == false))
                qi++;
        }
        return qi == query.Length ? 200 - c.Length : 0;
    }

    private void Search()
    {
        var q = _box.Text.Trim();
        var hits = new List<SearchHit>();
        var root = Workspace.Root ?? "";

        if (_mode is Mode.All or Mode.Classes)
            foreach (var c in Workspace.Index.AllClasses())
            {
                if (c.File == null) continue;
                var s = Score(c.Name, q);
                if (s <= 0) continue;
                if (c.File.Contains("\\vendor\\")) s -= 50;
                hits.Add(new SearchHit(c.Name, c.Namespace, c.File, c.Offset, c.KindLabel, null, s + 20));
            }
        if (_mode is Mode.All or Mode.Files)
            foreach (var f in _files)
            {
                var name = Path.GetFileName(f);
                var s = Score(name, q);
                if (s <= 0) continue;
                hits.Add(new SearchHit(name, Path.GetRelativePath(root, Path.GetDirectoryName(f)!), f, 0, null, null, s));
            }
        if ((_mode is Mode.Symbols || _mode is Mode.All && q.Length >= 3) && q.Length > 0)
            foreach (var c in Workspace.Index.AllClasses())
            {
                if (c.File == null || c.File.Contains("\\vendor\\")) continue;
                foreach (var m in c.Members)
                {
                    var s = Score(m.Name, q);
                    if (s <= 0) continue;
                    var label = m.Kind == PhpMemberKind.Method ? m.Name + "()" : m.Kind == PhpMemberKind.Property ? "$" + m.Name : m.Name;
                    hits.Add(new SearchHit(label, c.Name, c.File, m.Offset, null, EntryKind.Method, s - 10));
                }
            }
        _list.ItemsSource = hits.OrderByDescending(h => h.Score).ThenBy(h => h.Title.Length).Take(80).ToList();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (_list.SelectedIndex < _list.Items.Count - 1) _list.SelectedIndex++;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                if (_list.SelectedIndex > 0) _list.SelectedIndex--;
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Tab:
                _mode = (Mode)(((int)_mode + 1) % 4);
                UpdateTabs();
                Search();
                e.Handled = true;
                break;
        }
    }

    private void Accept()
    {
        if (_list.SelectedItem is SearchHit h)
        {
            Result = h;
            DialogResult = true;
        }
    }
}
