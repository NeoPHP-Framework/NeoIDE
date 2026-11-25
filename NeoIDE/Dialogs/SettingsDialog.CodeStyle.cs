using System.Windows;
using System.Windows.Controls;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

/// <summary>Settings › Code Style: indentation in general and by language, .editorconfig, detection.</summary>
public sealed partial class SettingsDialog
{
    private static readonly int[] IndentSizes = { 2, 3, 4, 8 };
    private ComboBox _indentSize = new(), _indentWith = new();
    private CheckBox _detectIndent = new(), _editorConfig = new();
    private readonly Dictionary<string, (CheckBox own, ComboBox size, ComboBox with)> _langIndent = new();

    private FrameworkElement BuildCodeStyle()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Indentation"));
        p.Children.Add(Hint("Used by Tab, Enter, Reformat Code, the templates and the generated code. The indentation of a file comes from, " +
                            "in this order: its choice in the status bar, the .editorconfig of the project, the indentation already used " +
                            "by the file, the setting of its language, the general setting."));
        _indentWith = Combo(new[] { false, true }, _s.UseTabs, v => v ? "Tab character" : "Spaces", 180);
        p.Children.Add(Row("Indent with:", _indentWith));
        _indentSize = Combo(IndentSizes, IndentSizes.Contains(_s.IndentSize) ? _s.IndentSize : 4, v => v.ToString(), 100);
        p.Children.Add(Row("Indent size:", _indentSize, "Spaces per level (the width of a tab when indenting with tabs)."));
        _detectIndent = new CheckBox { Content = "Detect and use the indentation of the opened file", IsChecked = _s.DetectIndent, Margin = new Thickness(0, 6, 0, 3) };
        _editorConfig = new CheckBox { Content = "Use the .editorconfig of the project (indent_style, indent_size, tab_width)", IsChecked = _s.UseEditorConfig, Margin = new Thickness(0, 3, 0, 3) };
        p.Children.Add(_detectIndent);
        p.Children.Add(_editorConfig);

        p.Children.Add(Section("By language"));
        p.Children.Add(Hint("A language can have its own indentation (e.g. YAML with 2 spaces)."));
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        int row = 0;
        foreach (var lang in IndentStyle.Languages)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var current = _s.LanguageIndent?.GetValueOrDefault(lang);
            var name = new TextBlock { Text = SyntaxDisplay(lang), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("Fg") };
            var own = new CheckBox { Content = "Own indentation", IsChecked = current != null, VerticalAlignment = VerticalAlignment.Center };
            var size = Combo(IndentSizes, current?.Size ?? _s.IndentSize, v => v.ToString(), 90);
            var with = Combo(new[] { false, true }, current?.UseTabs ?? _s.UseTabs, v => v ? "Tab" : "Spaces", 140);
            void Sync() { size.IsEnabled = with.IsEnabled = own.IsChecked == true; size.Opacity = with.Opacity = own.IsChecked == true ? 1 : 0.45; }
            own.Click += (_, _) => Sync();
            Sync();
            size.Margin = with.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(name, row); Grid.SetRow(own, row); Grid.SetRow(size, row); Grid.SetRow(with, row);
            Grid.SetColumn(own, 1); Grid.SetColumn(size, 2); Grid.SetColumn(with, 3);
            grid.Children.Add(name); grid.Children.Add(own); grid.Children.Add(size); grid.Children.Add(with);
            _langIndent[lang] = (own, size, with);
            row++;
        }
        p.Children.Add(grid);
        return p;

        static string SyntaxDisplay(string lang) => Editor.SyntaxRegistry.DisplayName(lang) ?? lang;
    }

    private void CommitCodeStyle()
    {
        _s.UseTabs = Selected<bool>(_indentWith);
        _s.IndentSize = Selected<int>(_indentSize) is > 0 and var n ? n : 4;
        _s.DetectIndent = _detectIndent.IsChecked == true;
        _s.UseEditorConfig = _editorConfig.IsChecked == true;
        var map = new Dictionary<string, IndentSetting>();
        foreach (var (lang, (own, size, with)) in _langIndent)
            if (own.IsChecked == true)
                map[lang] = new IndentSetting { Size = Selected<int>(size) is > 0 and var s ? s : 4, UseTabs = Selected<bool>(with) };
        _s.LanguageIndent = map.Count > 0 ? map : null;
    }
}
