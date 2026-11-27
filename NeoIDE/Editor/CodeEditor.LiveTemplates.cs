using System.Text.RegularExpressions;
using System.Windows.Controls;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Snippets;
using NeoIDE.Php;
using NeoIDE.Services;

namespace NeoIDE.Editor;

/// <summary>Live templates: abbreviation + Tab (Twig, PHP, NeoPHP views, HTML), Ctrl+J lists them.</summary>
public sealed partial class CodeEditor
{
    private InsertionContext? _snippet;
    private TextAnchor? _snippetStart, _snippetEnd;
    private bool _snippetWatch;

    /// <summary>Whether the fields of an inserted template are being filled (Tab: next field).</summary>
    public bool InSnippet => _snippet != null && !TextArea.StackedInputHandlers.IsEmpty;

    /// <summary>Ends the field mode of the inserted template (Tab then indents again).</summary>
    public void EndSnippet()
    {
        var context = _snippet;
        _snippet = null;
        if (context != null && !TextArea.StackedInputHandlers.IsEmpty) context.Deactivate(new SnippetEventArgs(DeactivateReason.Unknown));
    }

    /// <summary>The field mode ends when the caret leaves the template or when lines are selected (Tab indents them).</summary>
    private void WatchSnippet()
    {
        if (_snippetWatch) return;
        _snippetWatch = true;
        TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_snippet == null || _snippetStart == null || _snippetEnd == null) return;
            if (_snippetStart.IsDeleted || _snippetEnd.IsDeleted || CaretOffset < _snippetStart.Offset || CaretOffset > _snippetEnd.Offset) EndSnippet();
        };
    }

    /// <summary>Tab on several selected lines indents them, even while the fields of a template are being filled.</summary>
    public void EndSnippetOnLineSelection()
    {
        if (_snippet == null || TextArea.Selection.IsEmpty) return;
        var sel = TextArea.Selection.SurroundingSegment;
        if (sel != null && Document.GetLineByOffset(sel.Offset) != Document.GetLineByOffset(sel.EndOffset)) EndSnippet();
    }

    /// <summary>The template contexts at an offset (none in a string, a comment or a Twig expression).</summary>
    public List<string> TemplateContextsAt(int offset)
    {
        var text = Text;
        var list = new List<string>();
        switch (Language)
        {
            case "Twig":
                if (!TwigInsight.InExpression(text, offset) && HtmlCompletion.ContextAt(text, offset, "Twig")?.Kind == HtmlContextKind.Text)
                    list.AddRange(new[] { TemplateContext.Twig, TemplateContext.Html });
                break;
            case "HTML":
                if (HtmlCompletion.ContextAt(text, offset, "HTML")?.Kind == HtmlContextKind.Text) list.Add(TemplateContext.Html);
                break;
            case "PHP":
            {
                var tokens = PhpLexer.Tokenize(text, offset);
                var last = tokens.Count > 0 ? tokens[^1] : default;
                if (tokens.Count == 0 || last.Kind is TokKind.InlineHtml or TokKind.CloseTag)
                {
                    if (HtmlCompletion.ContextAt(text, offset, "PHP")?.Kind == HtmlContextKind.Text)
                        list.AddRange(new[] { TemplateContext.PhpView, TemplateContext.Html });
                }
                else if (!(last.Kind is TokKind.String or TokKind.Comment or TokKind.DocComment && last.End >= offset))
                    list.Add(TemplateContext.Php);
                break;
            }
        }
        return list;
    }

    /// <summary>Tab after the abbreviation of a live template: the template is inserted (Tab: next field, Enter / Esc: done).</summary>
    public bool TryExpandLiveTemplate()
    {
        if (IsPreview || IsReadOnly) return false;
        var caret = CaretOffset;
        var line = Document.GetLineByOffset(caret);
        var before = Document.GetText(line.Offset, caret - line.Offset);
        var m = Regex.Match(before, @"(?<![\w$\\.:>-])[A-Za-z_][\w]*$");
        // "->if" / "::if" / "$if" / "a.if" are not abbreviations ("<p>if" is)
        if (!m.Success)
        {
            m = Regex.Match(before, @"(?<=>)[A-Za-z_][\w]*$");
            if (!m.Success || before.EndsWith("->" + m.Value)) return false;
        }
        var abbreviation = m.Value;
        var start = caret - abbreviation.Length;
        var template = LiveTemplates.Find(abbreviation, TemplateContextsAt(start));
        if (template == null) return false;
        Insert(template, start, abbreviation.Length);
        return true;
    }

    /// <summary>Inserts a template in place of [start, start + length).</summary>
    public void Insert(LiveTemplate template, int start, int length)
    {
        var snippet = new Snippet();
        var fields = new Dictionary<string, SnippetReplaceableTextElement>();
        // AvalonEdit's snippet insertion indents the new lines like the current one, turns "\t" into the indentation
        // unit and "\n" into the new line of the document: the body is given as is
        foreach (var part in LiveTemplates.Parse(template.Body, "", "\t", "\n"))
        {
            switch (part)
            {
                case TemplateText t:
                    snippet.Elements.Add(new SnippetTextElement { Text = t.Text });
                    break;
                case TemplateField f when fields.TryGetValue(f.Name, out var first):
                    snippet.Elements.Add(new SnippetBoundElement { TargetElement = first });
                    break;
                case TemplateField f:
                    var field = new SnippetReplaceableTextElement { Text = f.Default };
                    fields[f.Name] = field;
                    snippet.Elements.Add(field);
                    break;
                case TemplateEnd:
                    snippet.Elements.Add(new SnippetCaretElement());
                    break;
                case TemplateSelection:
                    snippet.Elements.Add(new SnippetSelectionElement());
                    break;
            }
        }
        EndSnippet();
        WatchSnippet();
        using (Document.RunUpdate())
        {
            Document.Remove(start, length);
            CaretOffset = start;
            // what Snippet.Insert(TextArea) does, keeping the context to end the field mode later
            var selection = TextArea.Selection.SurroundingSegment;
            var at = CaretOffset;
            if (selection != null) at = selection.Offset + TextUtilities.GetWhitespaceAfter(Document, selection.Offset).Length;
            var context = new InsertionContext(TextArea, at);
            if (selection != null) Document.Remove(at, selection.EndOffset - at);
            snippet.Insert(context);
            context.RaiseInsertionCompleted(EventArgs.Empty);
            if (!TextArea.StackedInputHandlers.IsEmpty)
            {
                _snippet = context;
                _snippetStart = Document.CreateAnchor(context.StartPosition);
                _snippetEnd = Document.CreateAnchor(context.InsertionPosition);
                _snippetEnd.MovementType = AnchorMovementType.AfterInsertion;
                context.Deactivated += (_, _) => { if (_snippet == context) _snippet = null; };
            }
        }
        StatusMessage?.Invoke(fields.Count > 0 ? $"{template.Description} — Tab: next field, Enter: done" : template.Description);
    }

    /// <summary>Ctrl+J: the templates available at the caret.</summary>
    public void ShowLiveTemplates()
    {
        var contexts = TemplateContextsAt(CaretOffset);
        var templates = LiveTemplates.For(contexts).OrderBy(t => t.Abbreviation).ToList();
        if (templates.Count == 0) { StatusMessage?.Invoke("No live template here (Settings › Live Templates)"); return; }
        var menu = new ContextMenu { PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint };
        var pos = TextArea.TextView.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(Document.GetLocation(CaretOffset)), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom)
                  - TextArea.TextView.ScrollOffset;
        menu.HorizontalOffset = pos.X + 40;
        menu.VerticalOffset = pos.Y;
        foreach (var t in templates)
        {
            var template = t;
            var mi = new MenuItem { Header = $"{t.Abbreviation}   —   {t.Description}".Literal(), InputGestureText = t.Context };
            mi.Click += (_, _) => Insert(template, CaretOffset, 0);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }
}
