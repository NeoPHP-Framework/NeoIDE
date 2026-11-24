using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Editor;

/// <summary>A completion item rendered like PhpStorm's lookup list (icon, name, signature, type).</summary>
public sealed class CompletionData : ICompletionData
{
    private readonly CompletionEntry _entry;
    private object? _content;
    public Action<CompletionEntry, TextArea>? AfterInsert { get; set; }

    public CompletionData(CompletionEntry entry) => _entry = entry;

    public CompletionEntry Entry => _entry;
    public ImageSource? Image => null;
    public string Text => _entry.Label;
    public double Priority => _entry.Priority;
    public object? Description => string.IsNullOrEmpty(_entry.Description) ? null : new TextBlock
    {
        Text = _entry.Description, TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
        FontFamily = new FontFamily("JetBrains Mono, Consolas"), FontSize = 12, Foreground = ThemeManager.Brush("Fg"),
    };

    public object Content => _content ??= BuildContent();

    public static (string letter, Color color) IconFor(EntryKind kind) => kind switch
    {
        EntryKind.Method => ("m", Hex("#C75450")),
        EntryKind.Function => ("f", Hex("#C75450")),
        EntryKind.Property => ("f", Hex("#E08855")),
        EntryKind.Constant => ("c", Hex("#AB7FF5")),
        EntryKind.EnumCase => ("e", Hex("#AB7FF5")),
        EntryKind.Class => ("C", Hex("#3D8EDB")),
        EntryKind.Interface => ("I", Hex("#5FB865")),
        EntryKind.Trait => ("T", Hex("#E0A44C")),
        EntryKind.Enum => ("E", Hex("#B189F5")),
        EntryKind.Variable => ("v", Hex("#8C96A3")),
        EntryKind.Namespace => ("n", Hex("#8C96A3")),
        EntryKind.Template => ("t", Hex("#8FBF3F")),
        EntryKind.DocTag => ("@", Hex("#67A37C")),
        EntryKind.Parameter => ("p", Hex("#C77DBB")),
        _ => ("k", Hex("#6F737A")),
    };

    private static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);

    private object BuildContent()
    {
        var (letter, color) = IconFor(_entry.Kind);
        var grid = new Grid { MinWidth = 440 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(_entry.Kind is EntryKind.Keyword or EntryKind.DocTag ? 3 : 8),
            Background = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter, Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            },
        };
        grid.Children.Add(icon);

        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Inlines.Add(new System.Windows.Documents.Run(_entry.Label)
        {
            FontWeight = _entry.Bold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = ThemeManager.Brush("Fg"),
            TextDecorations = _entry.Strike ? TextDecorations.Strikethrough : null,
        });
        if (!string.IsNullOrEmpty(_entry.Tail))
            label.Inlines.Add(new System.Windows.Documents.Run(_entry.Tail) { Foreground = ThemeManager.Brush("FgSecondary") });
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (!string.IsNullOrEmpty(_entry.TypeText))
        {
            var type = new TextBlock
            {
                Text = _entry.TypeText, Foreground = ThemeManager.Brush("FgSecondary"), Margin = new Thickness(16, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(type, 2);
            grid.Children.Add(type);
        }
        return grid;
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var insert = _entry.Insert;
        textArea.Document.Replace(completionSegment, insert);
        if (_entry.CaretBack > 0)
            textArea.Caret.Offset = Math.Max(0, textArea.Caret.Offset - _entry.CaretBack);
        AfterInsert?.Invoke(_entry, textArea);
    }
}
