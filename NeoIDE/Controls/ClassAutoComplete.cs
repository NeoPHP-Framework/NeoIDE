using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NeoIDE.Editor;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Controls;

/// <summary>
/// Class name completion for a TextBox (New PHP Class: extends / implements): the classes of the index matching the
/// word being typed (camel humps, or a namespace prefix), project classes first. Accepting inserts the fully
/// qualified name. With <see cref="Multiple"/>, the field is a comma separated list and the last item is completed.
/// </summary>
public sealed class ClassAutoComplete
{
    private readonly TextBox _box;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private bool _accepting;

    /// <summary>Which classes are proposed (e.g. only interfaces for "implements").</summary>
    public Func<PhpClass, bool> Filter { get; set; } = _ => true;

    public bool Multiple { get; init; }

    private ClassAutoComplete(TextBox box)
    {
        _box = box;
        _list = new ListBox
        {
            MaxHeight = 260, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Focusable = false,
            Foreground = ThemeManager.Brush("Fg"),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: PhpClass c })
            {
                Accept(c);
                e.Handled = true;
            }
        };
        _popup = new Popup
        {
            PlacementTarget = box, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = Frame(_list),
        };
        box.TextChanged += (_, _) => { if (!_accepting && box.IsKeyboardFocused) Refresh(); };
        box.PreviewKeyDown += OnKey;
        box.LostKeyboardFocus += (_, _) => { if (!_popup.IsKeyboardFocusWithin) _popup.IsOpen = false; };
    }

    public static ClassAutoComplete Attach(TextBox box, Func<PhpClass, bool>? filter = null, bool multiple = false) =>
        new(box) { Filter = filter ?? (_ => true), Multiple = multiple };

    /// <summary>The item being typed: the whole text, or the last comma separated item.</summary>
    private (int start, string word) Current()
    {
        var text = _box.Text;
        var caret = _box.CaretIndex;
        var start = Multiple ? text.LastIndexOf(',', Math.Max(0, caret - 1)) + 1 : 0;
        if (Multiple && caret > 0 && text[caret - 1] == ',') start = caret;
        while (start < caret && char.IsWhiteSpace(text[start])) start++;
        return (start, text[start..caret]);
    }

    /// <summary>Classes matching a typed name (also used by the tests).</summary>
    public static List<PhpClass> Candidates(string typed, Func<PhpClass, bool> filter, int max = 60)
    {
        typed = typed.Trim();
        var qualified = typed.Contains('\\');
        var t = typed.TrimStart('\\');
        return Workspace.Index.AllClasses()
            .Where(c => filter(c) && (qualified ? c.Fqn.StartsWith(t, StringComparison.OrdinalIgnoreCase) : t.Length == 0 || PhpCompletion.Matches(c.Name, t)))
            .OrderByDescending(c => !qualified && c.Name.StartsWith(t, StringComparison.Ordinal))
            .ThenByDescending(c => !qualified && c.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase))
            .ThenBy(c => c.File == null ? 2 : c.File.Contains("\\vendor\\") ? 1 : 0)
            .ThenBy(c => c.Name.Length)
            .ThenBy(c => c.Fqn, StringComparer.OrdinalIgnoreCase)
            .Take(max).ToList();
    }

    private void Refresh()
    {
        var (_, word) = Current();
        if (word.Length == 0 && !_forced) { _popup.IsOpen = false; return; }
        _forced = false;
        var items = Candidates(word, Filter);
        if (items.Count == 0) { _popup.IsOpen = false; return; }
        _list.Items.Clear();
        foreach (var c in items) _list.Items.Add(new ListBoxItem { DataContext = c, Content = Row(c), Padding = new Thickness(6, 3, 6, 3) });
        _list.SelectedIndex = 0;
        _list.ScrollIntoView(_list.Items[0]);
        _popup.MinWidth = _box.ActualWidth;
        _popup.MaxWidth = Math.Max(_box.ActualWidth, 640);
        _popup.IsOpen = true;
    }

    private bool _forced;

    private static Border Frame(UIElement content) => new()
    {
        Child = content, Background = ThemeManager.Brush("PopupBg"), BorderBrush = ThemeManager.Brush("PopupBorder"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(2), Margin = new Thickness(0, 2, 0, 0),
    };

    /// <summary>Test harness: the list shown for a typed name, outside of its popup.</summary>
    public static FrameworkElement Preview(string typed, Func<PhpClass, bool> filter)
    {
        var list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = ThemeManager.Brush("Fg") };
        foreach (var c in Candidates(typed, filter, 8)) list.Items.Add(new ListBoxItem { DataContext = c, Content = Row(c), Padding = new Thickness(6, 3, 6, 3) });
        list.SelectedIndex = 0;
        return Frame(list);
    }

    private static FrameworkElement Row(PhpClass c)
    {
        var kind = c.Kind switch { PhpClassKind.Interface => EntryKind.Interface, PhpClassKind.Trait => EntryKind.Trait, PhpClassKind.Enum => EntryKind.Enum, _ => EntryKind.Class };
        var (letter, color) = CompletionData.IconFor(kind);
        var row = new DockPanel { LastChildFill = true };
        var icon = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock { Text = letter, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        row.Children.Add(icon);
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run(c.Name) { Foreground = ThemeManager.Brush("Fg"), TextDecorations = c.IsDeprecated ? TextDecorations.Strikethrough : null });
        if (c.Namespace.Length > 0) text.Inlines.Add(new System.Windows.Documents.Run("  " + c.Namespace) { Foreground = ThemeManager.Brush("FgSecondary") });
        if (c.IsAbstract && c.Kind == PhpClassKind.Class) text.Inlines.Add(new System.Windows.Documents.Run("  abstract") { Foreground = ThemeManager.Brush("FgSecondary"), FontStyle = FontStyles.Italic });
        row.Children.Add(text);
        return row;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) { _forced = true; Refresh(); e.Handled = true; return; }
        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down: Move(1); e.Handled = true; break;
            case Key.Up: Move(-1); e.Handled = true; break;
            case Key.PageDown: Move(8); e.Handled = true; break;
            case Key.PageUp: Move(-8); e.Handled = true; break;
            case Key.Enter or Key.Tab:
                if (_list.SelectedItem is ListBoxItem { DataContext: PhpClass c }) { Accept(c); e.Handled = true; }
                break;
            case Key.Escape: _popup.IsOpen = false; e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        if (_list.Items.Count == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void Accept(PhpClass c)
    {
        var (start, _) = Current();
        var text = _box.Text;
        var end = _box.CaretIndex;
        // replace the whole item, up to the next comma
        if (Multiple) { var comma = text.IndexOf(',', end); end = comma < 0 ? text.Length : comma; }
        else end = text.Length;
        var insert = c.Fqn;
        _accepting = true;
        try
        {
            _box.Text = text[..start] + insert + text[end..];
            _box.CaretIndex = start + insert.Length;
        }
        finally { _accepting = false; }
        _popup.IsOpen = false;
        _box.Focus();
    }
}
