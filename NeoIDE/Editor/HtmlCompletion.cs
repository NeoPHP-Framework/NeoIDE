using System.Text.RegularExpressions;
using NeoIDE.Php;

namespace NeoIDE.Editor;

/// <summary>Where the caret is in HTML markup.</summary>
public enum HtmlContextKind { Text, TagName, ClosingTag, AttributeName, AttributeValue }

public sealed record HtmlContext(HtmlContextKind Kind, int Start, string? Tag, string? Attribute);

/// <summary>
/// HTML intelligence of HTML / Twig files and of the HTML of PHP views: tag names after "&lt;", attributes in a tag
/// (name="" with the caret between the quotes), known values (type, method, target, rel…), "&lt;/" closes the
/// innermost open element, and typing the "&gt;" of a start tag inserts its end tag.
/// </summary>
public static class HtmlCompletion
{
    private static readonly string[] Tags =
    {
        "a", "abbr", "address", "area", "article", "aside", "audio", "b", "base", "bdi", "bdo", "blockquote", "body", "br", "button",
        "canvas", "caption", "cite", "code", "col", "colgroup", "data", "datalist", "dd", "del", "details", "dfn", "dialog", "div",
        "dl", "dt", "em", "embed", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "head",
        "header", "hgroup", "hr", "html", "i", "iframe", "img", "input", "ins", "kbd", "label", "legend", "li", "link", "main", "map",
        "mark", "menu", "meta", "meter", "nav", "noscript", "object", "ol", "optgroup", "option", "output", "p", "param", "picture",
        "pre", "progress", "q", "rp", "rt", "ruby", "s", "samp", "script", "search", "section", "select", "slot", "small", "source",
        "span", "strong", "style", "sub", "summary", "sup", "svg", "table", "tbody", "td", "template", "textarea", "tfoot", "th",
        "thead", "time", "title", "tr", "track", "u", "ul", "var", "video", "wbr",
    };

    private static readonly HashSet<string> TagSet = new(Tags, StringComparer.OrdinalIgnoreCase);

    public static bool IsTag(string name) => TagSet.Contains(name);

    private static readonly string[] GlobalAttributes =
    {
        "class", "id", "style", "title", "lang", "dir", "hidden", "tabindex", "role", "accesskey", "contenteditable", "draggable",
        "spellcheck", "translate", "inert", "popover", "data-", "aria-label", "aria-hidden", "aria-describedby", "aria-labelledby",
        "aria-expanded", "aria-controls", "aria-current", "onclick", "onchange", "oninput", "onsubmit", "onkeydown", "onload",
    };

    private static readonly Dictionary<string, string[]> TagAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = new[] { "href", "target", "rel", "download", "hreflang", "type", "referrerpolicy" },
        ["img"] = new[] { "src", "alt", "width", "height", "srcset", "sizes", "loading", "decoding", "fetchpriority", "crossorigin", "usemap" },
        ["input"] = new[] { "type", "name", "value", "placeholder", "required", "disabled", "readonly", "checked", "autocomplete", "autofocus", "min", "max", "step", "minlength", "maxlength", "pattern", "multiple", "accept", "list", "form", "size" },
        ["button"] = new[] { "type", "name", "value", "disabled", "form", "formaction", "formmethod", "popovertarget", "autofocus" },
        ["form"] = new[] { "action", "method", "enctype", "novalidate", "target", "autocomplete", "name" },
        ["select"] = new[] { "name", "required", "disabled", "multiple", "size", "form", "autofocus" },
        ["option"] = new[] { "value", "selected", "disabled", "label" },
        ["textarea"] = new[] { "name", "rows", "cols", "placeholder", "required", "disabled", "readonly", "maxlength", "minlength", "wrap", "autofocus" },
        ["label"] = new[] { "for", "form" },
        ["link"] = new[] { "rel", "href", "type", "media", "sizes", "crossorigin", "integrity", "as" },
        ["script"] = new[] { "src", "type", "defer", "async", "crossorigin", "integrity", "nomodule" },
        ["meta"] = new[] { "name", "content", "charset", "http-equiv", "property" },
        ["iframe"] = new[] { "src", "width", "height", "allow", "allowfullscreen", "loading", "name", "sandbox", "referrerpolicy" },
        ["video"] = new[] { "src", "controls", "autoplay", "loop", "muted", "poster", "preload", "width", "height", "playsinline" },
        ["audio"] = new[] { "src", "controls", "autoplay", "loop", "muted", "preload" },
        ["source"] = new[] { "src", "type", "srcset", "media", "sizes" },
        ["td"] = new[] { "colspan", "rowspan", "headers" },
        ["th"] = new[] { "colspan", "rowspan", "scope", "abbr" },
        ["ol"] = new[] { "start", "reversed", "type" },
        ["time"] = new[] { "datetime" },
        ["html"] = new[] { "lang" },
        ["details"] = new[] { "open", "name" },
        ["dialog"] = new[] { "open" },
        ["progress"] = new[] { "value", "max" },
        ["meter"] = new[] { "value", "min", "max", "low", "high", "optimum" },
        ["col"] = new[] { "span" },
        ["colgroup"] = new[] { "span" },
        ["fieldset"] = new[] { "disabled", "form", "name" },
        ["output"] = new[] { "for", "form", "name" },
        ["track"] = new[] { "src", "kind", "srclang", "label", "default" },
        ["area"] = new[] { "href", "alt", "shape", "coords", "target" },
        ["object"] = new[] { "data", "type", "width", "height", "name" },
        ["embed"] = new[] { "src", "type", "width", "height" },
        ["blockquote"] = new[] { "cite" },
        ["q"] = new[] { "cite" },
        ["slot"] = new[] { "name" },
    };

    /// <summary>Attributes without a value (written alone).</summary>
    private static readonly HashSet<string> BooleanAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "hidden", "required", "disabled", "readonly", "checked", "selected", "multiple", "autofocus", "novalidate", "defer", "async",
        "controls", "autoplay", "loop", "muted", "playsinline", "open", "reversed", "allowfullscreen", "default", "inert", "nomodule", "download",
    };

    private static readonly Dictionary<string, string[]> AttributeValues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["input.type"] = new[] { "text", "email", "password", "number", "tel", "url", "search", "date", "datetime-local", "time", "month", "week", "checkbox", "radio", "file", "hidden", "submit", "reset", "button", "range", "color", "image" },
        ["button.type"] = new[] { "button", "submit", "reset" },
        ["form.method"] = new[] { "post", "get", "dialog" },
        ["form.enctype"] = new[] { "multipart/form-data", "application/x-www-form-urlencoded", "text/plain" },
        ["target"] = new[] { "_blank", "_self", "_parent", "_top" },
        ["link.rel"] = new[] { "stylesheet", "icon", "preload", "preconnect", "prefetch", "manifest", "canonical", "alternate", "modulepreload" },
        ["a.rel"] = new[] { "noopener", "noreferrer", "nofollow", "external", "next", "prev" },
        ["loading"] = new[] { "lazy", "eager" },
        ["dir"] = new[] { "ltr", "rtl", "auto" },
        ["script.type"] = new[] { "module", "text/javascript", "application/json", "importmap" },
        ["autocomplete"] = new[] { "on", "off", "name", "email", "username", "current-password", "new-password", "tel", "street-address", "postal-code", "country" },
        ["th.scope"] = new[] { "col", "row", "colgroup", "rowgroup" },
        ["preload"] = new[] { "none", "metadata", "auto" },
        ["decoding"] = new[] { "async", "sync", "auto" },
        ["crossorigin"] = new[] { "anonymous", "use-credentials" },
        ["referrerpolicy"] = new[] { "no-referrer", "origin", "same-origin", "strict-origin-when-cross-origin", "unsafe-url" },
        ["wrap"] = new[] { "soft", "hard" },
        ["meta.name"] = new[] { "viewport", "description", "keywords", "author", "robots", "theme-color", "csrf-token" },
        ["contenteditable"] = new[] { "true", "false", "plaintext-only" },
        ["draggable"] = new[] { "true", "false" },
        ["spellcheck"] = new[] { "true", "false" },
        ["translate"] = new[] { "yes", "no" },
        ["popover"] = new[] { "auto", "manual" },
        ["fetchpriority"] = new[] { "high", "low", "auto" },
    };

    // ------------------------------------------------------------------ context

    /// <summary>
    /// The HTML context at the caret, null when the caret is not in HTML markup (PHP code of a .php file, Twig
    /// expression, comment). <paramref name="language"/>: "HTML", "Twig" or "PHP".
    /// </summary>
    public static HtmlContext? ContextAt(string text, int caret, string? language)
    {
        if (caret < 0 || caret > text.Length) return null;
        if (language == "PHP")
        {
            var tokens = PhpLexer.Tokenize(text, caret);
            var last = tokens.Count > 0 ? tokens[^1] : default;
            if (tokens.Count > 0 && last.Kind is not (TokKind.InlineHtml or TokKind.CloseTag)) return null;
        }
        else if (language == "Twig")
        {
            if (TwigInsight.InExpression(text, caret)) return null;
        }
        else if (language != "HTML") return null;

        // in a comment
        var commentOpen = text.LastIndexOf("<!--", Math.Max(0, caret - 1), StringComparison.Ordinal);
        if (commentOpen >= 0 && text.IndexOf("-->", commentOpen, StringComparison.Ordinal) is var commentEnd && (commentEnd < 0 || commentEnd + 3 > caret)) return null;

        // the last "<" before the caret not closed by a ">" (ignoring "?>" / "%}" / "}}" of templates)
        int lt = -1;
        for (int i = caret - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c == '>' && !(i > 0 && text[i - 1] is '?' or '%' or '-')) break;
            if (c == '<' && !(i + 1 < text.Length && text[i + 1] is '?' or '!' or '%')) { lt = i; break; }
            if (c == '\n' && caret - i > 2000) break;
        }
        if (lt < 0) return new HtmlContext(HtmlContextKind.Text, caret, null, null);
        var inTag = text[lt..caret];
        var name = Regex.Match(inTag, @"^<(?<close>/)?(?<name>[A-Za-z][\w:.-]*)?");
        if (name.Length == inTag.Length)
            return name.Groups["close"].Success
                ? new HtmlContext(HtmlContextKind.ClosingTag, lt + 2, null, null)
                : new HtmlContext(HtmlContextKind.TagName, lt + 1, null, null);
        if (name.Groups["close"].Success || !name.Groups["name"].Success) return null;
        var tag = name.Groups["name"].Value;
        // attribute value: an odd number of quotes after "name="
        var rest = Regex.Replace(inTag[name.Length..], @"<\?.*?\?>|\{\{.*?\}\}|\{%.*?%\}", m => new string('x', m.Length));
        var value = Regex.Match(rest, @"(?<attr>[\w:@.-]+)\s*=\s*(?<q>[""'])(?<v>[^""']*)$");
        if (value.Success) return new HtmlContext(HtmlContextKind.AttributeValue, caret - value.Groups["v"].Length, tag, value.Groups["attr"].Value);
        if (rest.Count(c => c == '"') % 2 == 1 || rest.Count(c => c == '\'') % 2 == 1) return null;
        var word = Regex.Match(rest, @"(?<=\s)(?<w>[\w:@.-]*)$");
        if (!word.Success) return null;
        return new HtmlContext(HtmlContextKind.AttributeName, caret - word.Groups["w"].Length, tag, null);
    }

    // ------------------------------------------------------------------ completion

    public static (int start, List<CompletionEntry> entries)? Complete(string text, int caret, string? language)
    {
        var ctx = ContextAt(text, caret, language);
        if (ctx == null || ctx.Kind == HtmlContextKind.Text) return null;
        var entries = new List<CompletionEntry>();
        switch (ctx.Kind)
        {
            case HtmlContextKind.TagName:
                entries.AddRange(Tags.Select(t => new CompletionEntry { Label = t, Insert = t, Kind = EntryKind.Keyword, TypeText = "tag", Priority = 5 }));
                break;
            case HtmlContextKind.ClosingTag:
                if (InnermostOpenTag(text, ctx.Start - 2) is { } open)
                    entries.Add(new CompletionEntry { Label = open, Insert = open + ">", Kind = EntryKind.Keyword, TypeText = "close", Priority = 10 });
                break;
            case HtmlContextKind.AttributeName:
            {
                var written = Regex.Matches(text[text.LastIndexOf('<', ctx.Start)..ctx.Start], @"\s([\w:@.-]+)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var specific = TagAttributes.GetValueOrDefault(ctx.Tag!) ?? Array.Empty<string>();
                foreach (var a in specific.Concat(GlobalAttributes).Distinct())
                {
                    if (written.Contains(a)) continue;
                    var boolean = BooleanAttributes.Contains(a) || a.EndsWith('-');
                    entries.Add(new CompletionEntry
                    {
                        Label = a, Insert = boolean ? a : a + "=\"\"", CaretBack = boolean ? 0 : 1, Kind = EntryKind.Property,
                        TypeText = specific.Contains(a) ? ctx.Tag : null, Priority = specific.Contains(a) ? 7 : a is "class" or "id" ? 6 : 4,
                    });
                }
                break;
            }
            case HtmlContextKind.AttributeValue:
            {
                var values = AttributeValues.GetValueOrDefault($"{ctx.Tag}.{ctx.Attribute}") ?? AttributeValues.GetValueOrDefault(ctx.Attribute!);
                if (values == null) return null;
                entries.AddRange(values.Select(v => new CompletionEntry { Label = v, Insert = v, Kind = EntryKind.Constant, Priority = 5 }));
                break;
            }
        }
        return entries.Count == 0 ? null : (ctx.Start, entries);
    }

    /// <summary>The innermost element still open before <paramref name="offset"/> (for "&lt;/").</summary>
    public static string? InnermostOpenTag(string text, int offset)
    {
        var stack = new List<string>();
        var clean = Regex.Replace(text[..Math.Clamp(offset, 0, text.Length)], @"<!--.*?-->|<\?.*?\?>|\{#.*?#\}", m => new string(' ', m.Length), RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(clean, @"<(?<close>/)?(?<name>[A-Za-z][\w:.-]*)(?<attrs>(?:[^<>""']|""[^""]*""|'[^']*')*)(?<self>/)?>"))
        {
            var name = m.Groups["name"].Value;
            if (m.Groups["close"].Success)
            {
                var i = stack.FindLastIndex(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) stack.RemoveRange(i, stack.Count - i);
            }
            else if (!m.Groups["self"].Success && !Emmet.VoidTags.Contains(name) && !name.Equals("!doctype", StringComparison.OrdinalIgnoreCase))
                stack.Add(name);
        }
        return stack.Count > 0 ? stack[^1] : null;
    }

    /// <summary>
    /// The end tag to insert after the "&gt;" just typed at <paramref name="gtOffset"/> (null for a void / self-closed tag,
    /// an end tag, or when the end tag is already there).
    /// </summary>
    public static string? EndTagFor(string text, int gtOffset, string? language)
    {
        if (gtOffset <= 0 || gtOffset >= text.Length || text[gtOffset] != '>') return null;
        var lt = text.LastIndexOf('<', gtOffset);
        if (lt < 0) return null;
        // the "<" must start a tag of the markup (not PHP code, not a Twig expression)
        if (ContextAt(text, lt + 1, language)?.Kind != HtmlContextKind.TagName) return null;
        var inTag = Regex.Replace(text[lt..gtOffset], @"""[^""]*""|'[^']*'", m => new string('x', m.Length));
        var m = Regex.Match(inTag, @"^<(?<name>[A-Za-z][\w:.-]*)(?:\s[^<>]*)?$");
        if (!m.Success || text[gtOffset - 1] == '/') return null;
        var name = m.Groups["name"].Value;
        if (Emmet.VoidTags.Contains(name)) return null;
        var after = text[(gtOffset + 1)..Math.Min(text.Length, gtOffset + 1 + name.Length + 3)];
        if (after.StartsWith($"</{name}", StringComparison.OrdinalIgnoreCase)) return null;
        return $"</{name}>";
    }
}
