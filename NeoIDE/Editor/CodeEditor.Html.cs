namespace NeoIDE.Editor;

/// <summary>HTML markup: Emmet abbreviations (Tab), end tags, tag / attribute completion.</summary>
public sealed partial class CodeEditor
{
    /// <summary>HTML, Twig and PHP files (the HTML of a PHP file only: checked by <see cref="HtmlCompletion.ContextAt"/>).</summary>
    private bool SupportsHtml => !IsPreview && Language is "HTML" or "Twig" or "PHP";

    /// <summary>
    /// Tab after an abbreviation of the markup ("h1", "p.test", "ul>li*3"…): replaced by its HTML, the caret on the
    /// first empty attribute / content. False when there is no abbreviation (Tab indents as usual).
    /// </summary>
    public bool TryExpandAbbreviation()
    {
        var caret = CaretOffset;
        var line = Document.GetLineByOffset(caret);
        var before = Document.GetText(line.Offset, caret - line.Offset);
        if (Emmet.AbbreviationBefore(before) is not { } found) return false;
        var start = line.Offset + found.start;
        // only in the text of the markup (not in a tag, PHP code, a Twig expression or a comment)
        if (HtmlCompletion.ContextAt(Text, start, Language)?.Kind != HtmlContextKind.Text) return false;
        var lineText = Document.GetText(line);
        var baseIndent = new string(lineText.TakeWhile(ch => ch is ' ' or '\t').ToArray());
        var unit = Options.ConvertTabsToSpaces ? new string(' ', Options.IndentationSize) : "\t";
        var nl = Document.Text.Contains("\r\n") ? "\r\n" : "\n";
        var expansion = Emmet.Expand(found.abbreviation, unit, nl, baseIndent);
        if (expansion == null) return false;
        var (html, caretInHtml) = Emmet.Finish(expansion);
        Document.Replace(start, caret - start, html);
        CaretOffset = start + caretInHtml;
        StatusMessage?.Invoke($"Expanded '{found.abbreviation}'");
        return true;
    }

    /// <summary>A character typed in the markup: true when it was handled here.</summary>
    private bool OnHtmlTyped(char c, int caret)
    {
        var text = Text;
        // ">" of a start tag: its end tag after the caret
        if (c == '>')
        {
            if (HtmlCompletion.EndTagFor(text, caret - 1, Language) is { } end)
            {
                Document.Insert(caret, end);
                CaretOffset = caret;
                return true;
            }
            return false;
        }
        // "</": closes the innermost open element
        if (c == '/' && caret >= 2 && text[caret - 2] == '<' && HtmlCompletion.ContextAt(text, caret, Language)?.Kind == HtmlContextKind.ClosingTag)
        {
            if (HtmlCompletion.InnermostOpenTag(text, caret - 2) is { } open && !(caret < text.Length && char.IsLetter(text[caret])))
            {
                Document.Insert(caret, open + ">");
                CaretOffset = caret + open.Length + 1;
                Reindent(Document.GetLineByOffset(caret));
                return true;
            }
            return false;
        }
        if (_completion != null) return false;
        var ctx = HtmlCompletion.ContextAt(text, caret, Language);
        if (ctx == null || ctx.Kind == HtmlContextKind.Text) return false;
        var prev = caret >= 2 ? text[caret - 2] : '\0';
        var show = ctx.Kind switch
        {
            // any letter of the name (after Escape / Backspace, typing again reopens the list)
            HtmlContextKind.TagName => c == '<' || char.IsLetterOrDigit(c),
            HtmlContextKind.AttributeName => c == ' ' || char.IsLetter(c) || c == '-',
            HtmlContextKind.AttributeValue => c is '"' or '\'',
            _ => false,
        };
        if (!show) return false;
        // values: only for the attributes that have known values (others: style = CSS, class…)
        if (ctx.Kind == HtmlContextKind.AttributeValue && HtmlCompletion.Complete(text, caret, Language) == null) return false;
        ShowCompletion(false);
        return true;
    }
}
