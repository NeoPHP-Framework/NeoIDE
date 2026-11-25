using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NeoIDE.Services;

namespace NeoIDE.Dialogs;

/// <summary>Settings › Live Templates: the abbreviations expanded with Tab, by context, editable.</summary>
public sealed partial class SettingsDialog
{
    private List<LiveTemplate> _templates = new();
    private bool _templatesChanged;
    private readonly ListBox _templateList = new() { Height = 330, BorderThickness = new Thickness(1) };
    private readonly TextBox _tplAbbr = new(), _tplDescription = new();
    private readonly TextBox _tplBody = new()
    {
        AcceptsReturn = true, AcceptsTab = true, Height = 150, TextWrapping = TextWrapping.NoWrap,
        VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(6, 6, 6, 6),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly ComboBox _tplContext = new() { ItemsSource = TemplateContext.All };
    private readonly CheckBox _tplEnabled = new() { Content = "Enabled" };
    private readonly TextBlock _tplPreview = new() { FontSize = 12, TextWrapping = TextWrapping.NoWrap };
    private readonly ComboBox _tplFilter = new() { Width = 150 };
    private string _tplQuery = "";
    private bool _tplLoading;

    private LiveTemplate? SelectedTemplate => (_templateList.SelectedItem as ListBoxItem)?.Tag as LiveTemplate;

    private FrameworkElement BuildTemplates()
    {
        _templates = LiveTemplates.All.Select(t => t.Clone()).ToList();
        var p = new StackPanel();
        p.Children.Add(Section("Live Templates"));
        p.Children.Add(Hint("Type the abbreviation then Tab (Ctrl+J lists the templates of the place). In the body: $name$ is a field " +
                            "(Tab goes to the next one, a name used twice is typed once), $name:default$ a field with its text, $END$ the caret " +
                            "at the end, $SELECTION$ the selected text, $$ a \"$\"; a tab is one indentation level."));

        // filter + search
        _tplFilter.ItemsSource = new[] { "All contexts" }.Concat(TemplateContext.All).ToList();
        _tplFilter.SelectedIndex = 0;
        _tplFilter.SelectionChanged += (_, _) => RefreshTemplates();
        var search = SearchBox("Search: abbreviation, description, body…", q => { _tplQuery = q; RefreshTemplates(); });
        search.Margin = new Thickness(0, 0, 10, 0);
        var filters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
        ((FrameworkElement)search).Width = 300;
        filters.Children.Add(search);
        _tplFilter.VerticalAlignment = VerticalAlignment.Bottom;
        filters.Children.Add(_tplFilter);
        p.Children.Add(filters);

        // list | editor
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel { VerticalAlignment = VerticalAlignment.Top };
        var listButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        Button Small(string text, Action a)
        {
            var b = new Button { Content = text, MinWidth = 0, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            b.Click += (_, _) => a();
            listButtons.Children.Add(b);
            return b;
        }
        Small("Add", () =>
        {
            var context = _tplFilter.SelectedIndex > 0 ? (string)_tplFilter.SelectedItem : Language();
            var t = new LiveTemplate { Abbreviation = "new", Context = context, Description = "New template", Body = "$END$" };
            _templates.Add(t);
            _templatesChanged = true;
            RefreshTemplates(t);
            _tplAbbr.Focus();
            _tplAbbr.SelectAll();
        });
        Small("Duplicate", () =>
        {
            if (SelectedTemplate is not { } s) return;
            var t = s.Clone();
            t.Abbreviation += "2";
            _templates.Add(t);
            _templatesChanged = true;
            RefreshTemplates(t);
        });
        Small("Remove", () =>
        {
            if (SelectedTemplate is not { } s) return;
            _templates.Remove(s);
            _templatesChanged = true;
            RefreshTemplates();
        });
        Small("Reset", () =>
        {
            if (!MessageDialog.Confirm("Live Templates", "Replace every template by the defaults of NeoIDE? Your templates and changes are removed.", "Reset")) return;
            _templates = LiveTemplates.Defaults();
            _templatesChanged = true;
            RefreshTemplates();
        });
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.Add(listButtons);
        left.Children.Add(_templateList);
        grid.Children.Add(left);

        var editor = new StackPanel();
        Grid.SetColumn(editor, 2);
        var abbrRow = new Grid();
        abbrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        abbrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        abbrRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        var a1 = new StackPanel();
        a1.Children.Add(Label("Abbreviation:", 0));
        a1.Children.Add(_tplAbbr);
        var a2 = new StackPanel();
        a2.Children.Add(Label("Context:", 0));
        a2.Children.Add(_tplContext);
        Grid.SetColumn(a2, 2);
        abbrRow.Children.Add(a1);
        abbrRow.Children.Add(a2);
        editor.Children.Add(abbrRow);
        editor.Children.Add(Label("Description:"));
        editor.Children.Add(_tplDescription);
        editor.Children.Add(Label("Template text:"));
        _tplBody.FontFamily = DisplayManager.EditorFont;
        _tplBody.FontSize = 12.5;
        editor.Children.Add(_tplBody);
        _tplEnabled.Margin = new Thickness(0, 10, 0, 0);
        editor.Children.Add(_tplEnabled);
        editor.Children.Add(Label("Preview:"));
        _tplPreview.FontFamily = DisplayManager.EditorFont;
        _tplPreview.Foreground = ThemeManager.Brush("FgSecondary");
        editor.Children.Add(new Border
        {
            Child = _tplPreview, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(4),
            BorderBrush = ThemeManager.Brush("SubtleBorder"), BorderThickness = new Thickness(1), Background = ThemeManager.Brush("EditorBg"),
        });
        grid.Children.Add(editor);
        p.Children.Add(grid);

        // edits go to the selected template
        void Edited()
        {
            if (_tplLoading || SelectedTemplate is not { } t) return;
            t.Abbreviation = _tplAbbr.Text.Trim();
            t.Context = _tplContext.SelectedItem as string ?? t.Context;
            t.Description = _tplDescription.Text;
            t.Body = _tplBody.Text.Replace("\r\n", "\n");
            t.Enabled = _tplEnabled.IsChecked == true;
            _templatesChanged = true;
            _tplPreview.Text = LiveTemplates.Preview(t.Body);
            if (_templateList.SelectedItem is ListBoxItem item) item.Content = Row(t);
        }
        _tplAbbr.TextChanged += (_, _) => Edited();
        _tplDescription.TextChanged += (_, _) => Edited();
        _tplBody.TextChanged += (_, _) => Edited();
        _tplContext.SelectionChanged += (_, _) => Edited();
        _tplEnabled.Click += (_, _) => Edited();
        _templateList.SelectionChanged += (_, _) => LoadTemplate();
        RefreshTemplates();
        return p;

        static string Language() => TemplateContext.Twig;
    }

    private static FrameworkElement Row(LiveTemplate t)
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new System.Windows.Documents.Run(t.Abbreviation) { FontWeight = FontWeights.SemiBold, Foreground = ThemeManager.Brush(t.Enabled ? "Fg" : "FgSecondary"), TextDecorations = t.Enabled ? null : TextDecorations.Strikethrough });
        text.Inlines.Add(new System.Windows.Documents.Run("  " + t.Description) { Foreground = ThemeManager.Brush("FgSecondary") });
        return text;
    }

    private void RefreshTemplates(LiveTemplate? select = null)
    {
        select ??= SelectedTemplate;
        var filter = _tplFilter.SelectedIndex > 0 ? _tplFilter.SelectedItem as string : null;
        _templateList.Items.Clear();
        foreach (var group in _templates.Where(t => filter == null || t.Context == filter)
                     .Where(t => Matches(_tplQuery, t.Abbreviation, t.Description, t.Body))
                     .GroupBy(t => t.Context).OrderBy(g => Array.IndexOf(TemplateContext.All, g.Key)))
        {
            _templateList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = group.Key, FontWeight = FontWeights.SemiBold, Foreground = ThemeManager.Brush("Accent"), Margin = new Thickness(0, 4, 0, 0) },
                IsEnabled = false,
            });
            foreach (var t in group.OrderBy(x => x.Abbreviation))
            {
                var item = new ListBoxItem { Content = Row(t), Tag = t, Padding = new Thickness(12, 2, 4, 2) };
                _templateList.Items.Add(item);
                if (t == select) _templateList.SelectedItem = item;
            }
        }
        if (_templateList.SelectedItem == null)
            _templateList.SelectedItem = _templateList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is LiveTemplate);
        LoadTemplate();
    }

    private void LoadTemplate()
    {
        _tplLoading = true;
        try
        {
            var t = SelectedTemplate;
            foreach (var c in new Control[] { _tplAbbr, _tplDescription, _tplBody, _tplContext, _tplEnabled }) c.IsEnabled = t != null;
            _tplAbbr.Text = t?.Abbreviation ?? "";
            _tplContext.SelectedItem = t?.Context;
            _tplDescription.Text = t?.Description ?? "";
            _tplBody.Text = t?.Body.Replace("\n", Environment.NewLine) ?? "";
            _tplEnabled.IsChecked = t?.Enabled ?? false;
            _tplPreview.Text = t != null ? LiveTemplates.Preview(t.Body) : "";
        }
        finally { _tplLoading = false; }
    }

    /// <summary>Test harness: the page with a template selected.</summary>
    public void SelectTemplateForPreview(string context, string abbreviation)
    {
        var t = _templates.FirstOrDefault(x => x.Context == context && x.Abbreviation == abbreviation);
        if (t != null) RefreshTemplates(t);
    }

    private void CommitTemplates()
    {
        if (!_templatesChanged) return;
        LiveTemplates.Save(_templates.Where(t => t.Abbreviation.Length > 0));
        _templatesChanged = false;
    }
}
