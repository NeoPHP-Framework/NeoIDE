using System.IO;
using System.Text.RegularExpressions;
using NeoIDE.Php;

namespace NeoIDE.Editor;

/// <summary>
/// CSS / SCSS completion: properties, values of the property, functions, colours, CSS variables of the project,
/// selectors (tags, pseudo-classes / elements, classes and ids of the project), at-rules.
/// Also used inside &lt;style&gt; blocks and style="..." attributes of HTML / Twig / PHP templates.
/// </summary>
public static class CssCompletion
{
    // ------------------------------------------------------------------ data

    private static readonly string[] Global = { "inherit", "initial", "unset", "revert", "revert-layer" };
    private static readonly string[] Length = { "auto", "0", "100%", "1rem", "1em", "1px", "50%", "fit-content", "min-content", "max-content" };
    private static readonly string[] BorderStyle = { "none", "solid", "dashed", "dotted", "double", "groove", "ridge", "inset", "outset", "hidden" };
    private static readonly string[] Align = { "start", "end", "center", "stretch", "flex-start", "flex-end", "baseline", "normal" };
    private static readonly string[] Justify = { "start", "end", "center", "flex-start", "flex-end", "space-between", "space-around", "space-evenly", "stretch", "normal" };
    private static readonly string[] ColorFns = { "var()", "rgb()", "rgba()", "hsl()", "hsla()", "currentColor", "transparent", "color-mix()" };

    private static readonly Dictionary<string, string[]> Properties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["display"] = new[] { "block", "inline", "inline-block", "flex", "inline-flex", "grid", "inline-grid", "none", "contents", "table", "table-cell", "table-row", "list-item", "flow-root" },
        ["position"] = new[] { "static", "relative", "absolute", "fixed", "sticky" },
        ["top"] = Length, ["right"] = Length, ["bottom"] = Length, ["left"] = Length, ["inset"] = Length, ["z-index"] = new[] { "auto", "0", "1", "10", "100", "999" },
        ["float"] = new[] { "none", "left", "right", "inline-start", "inline-end" }, ["clear"] = new[] { "none", "left", "right", "both" },
        ["box-sizing"] = new[] { "border-box", "content-box" },
        ["width"] = Length, ["height"] = Length, ["min-width"] = Length, ["min-height"] = Length, ["max-width"] = Length.Append("none").ToArray(), ["max-height"] = Length.Append("none").ToArray(),
        ["inline-size"] = Length, ["block-size"] = Length, ["aspect-ratio"] = new[] { "auto", "1", "16 / 9", "4 / 3" },
        ["margin"] = Length, ["margin-top"] = Length, ["margin-right"] = Length, ["margin-bottom"] = Length, ["margin-left"] = Length, ["margin-inline"] = Length, ["margin-block"] = Length,
        ["padding"] = Length, ["padding-top"] = Length, ["padding-right"] = Length, ["padding-bottom"] = Length, ["padding-left"] = Length, ["padding-inline"] = Length, ["padding-block"] = Length,
        ["overflow"] = new[] { "visible", "hidden", "scroll", "auto", "clip" }, ["overflow-x"] = new[] { "visible", "hidden", "scroll", "auto", "clip" }, ["overflow-y"] = new[] { "visible", "hidden", "scroll", "auto", "clip" },
        ["visibility"] = new[] { "visible", "hidden", "collapse" }, ["opacity"] = new[] { "0", "0.5", "1" },
        ["color"] = ColorFns, ["background"] = ColorFns.Concat(new[] { "none", "url()", "linear-gradient()", "radial-gradient()" }).ToArray(), ["background-color"] = ColorFns,
        ["background-image"] = new[] { "none", "url()", "linear-gradient()", "radial-gradient()", "conic-gradient()" },
        ["background-size"] = new[] { "auto", "cover", "contain", "100%" }, ["background-position"] = new[] { "center", "top", "bottom", "left", "right", "center center" },
        ["background-repeat"] = new[] { "repeat", "no-repeat", "repeat-x", "repeat-y", "space", "round" }, ["background-attachment"] = new[] { "scroll", "fixed", "local" },
        ["background-clip"] = new[] { "border-box", "padding-box", "content-box", "text" },
        ["border"] = new[] { "none", "1px solid", "1px solid currentColor", "0" }, ["border-top"] = new[] { "none", "1px solid" }, ["border-right"] = new[] { "none", "1px solid" },
        ["border-bottom"] = new[] { "none", "1px solid" }, ["border-left"] = new[] { "none", "1px solid" }, ["border-width"] = new[] { "0", "1px", "2px", "thin", "medium", "thick" },
        ["border-style"] = BorderStyle, ["border-color"] = ColorFns, ["border-radius"] = new[] { "0", "4px", "8px", "50%", "9999px" }, ["border-collapse"] = new[] { "collapse", "separate" },
        ["outline"] = new[] { "none", "0", "1px solid", "2px solid" }, ["outline-color"] = ColorFns, ["outline-style"] = BorderStyle, ["outline-offset"] = new[] { "0", "2px" },
        ["box-shadow"] = new[] { "none", "0 1px 3px rgba(0, 0, 0, 0.2)", "inset" }, ["text-shadow"] = new[] { "none", "0 1px 2px rgba(0, 0, 0, 0.3)" },
        ["font"] = new[] { "inherit", "menu", "caption" }, ["font-family"] = new[] { "system-ui", "sans-serif", "serif", "monospace", "-apple-system", "\"Segoe UI\"", "Roboto", "Arial" },
        ["font-size"] = new[] { "1rem", "0.875rem", "1.25rem", "16px", "small", "medium", "large", "larger", "smaller" },
        ["font-weight"] = new[] { "normal", "bold", "lighter", "bolder", "100", "300", "400", "500", "600", "700", "900" }, ["font-style"] = new[] { "normal", "italic", "oblique" },
        ["font-display"] = new[] { "auto", "block", "swap", "fallback", "optional" }, ["line-height"] = new[] { "normal", "1", "1.5", "1.25" }, ["letter-spacing"] = new[] { "normal", "0.05em" },
        ["text-align"] = new[] { "left", "right", "center", "justify", "start", "end" }, ["text-decoration"] = new[] { "none", "underline", "line-through", "overline" },
        ["text-transform"] = new[] { "none", "uppercase", "lowercase", "capitalize" }, ["text-overflow"] = new[] { "clip", "ellipsis" }, ["white-space"] = new[] { "normal", "nowrap", "pre", "pre-wrap", "pre-line", "break-spaces" },
        ["word-break"] = new[] { "normal", "break-all", "keep-all", "break-word" }, ["overflow-wrap"] = new[] { "normal", "anywhere", "break-word" }, ["vertical-align"] = new[] { "baseline", "middle", "top", "bottom", "text-top", "text-bottom", "sub", "super" },
        ["list-style"] = new[] { "none", "disc", "circle", "square", "decimal" }, ["list-style-type"] = new[] { "none", "disc", "circle", "square", "decimal", "lower-alpha", "upper-roman" },
        ["flex"] = new[] { "1", "auto", "none", "0 0 auto", "1 1 0" }, ["flex-direction"] = new[] { "row", "row-reverse", "column", "column-reverse" }, ["flex-wrap"] = new[] { "nowrap", "wrap", "wrap-reverse" },
        ["flex-flow"] = new[] { "row wrap", "column nowrap" }, ["flex-grow"] = new[] { "0", "1" }, ["flex-shrink"] = new[] { "0", "1" }, ["flex-basis"] = Length, ["order"] = new[] { "0", "1", "-1" },
        ["justify-content"] = Justify, ["justify-items"] = Align, ["justify-self"] = Align.Append("auto").ToArray(), ["align-items"] = Align, ["align-content"] = Justify, ["align-self"] = Align.Append("auto").ToArray(),
        ["place-items"] = new[] { "center", "start", "end", "stretch" }, ["place-content"] = new[] { "center", "space-between", "start", "end" }, ["gap"] = new[] { "0", "0.5rem", "1rem", "8px", "16px" }, ["row-gap"] = new[] { "0", "1rem" }, ["column-gap"] = new[] { "0", "1rem" },
        ["grid-template-columns"] = new[] { "repeat(3, 1fr)", "repeat(auto-fill, minmax(200px, 1fr))", "1fr 1fr", "none", "subgrid" }, ["grid-template-rows"] = new[] { "auto", "1fr", "repeat(2, 1fr)", "none" },
        ["grid-template-areas"] = new[] { "none" }, ["grid-area"] = new[] { "auto" }, ["grid-column"] = new[] { "auto", "1 / -1", "span 2" }, ["grid-row"] = new[] { "auto", "1 / -1", "span 2" },
        ["grid-auto-flow"] = new[] { "row", "column", "dense", "row dense" }, ["grid-auto-columns"] = new[] { "auto", "1fr", "min-content" }, ["grid-auto-rows"] = new[] { "auto", "1fr", "min-content" },
        ["transform"] = new[] { "none", "translate()", "translateX()", "translateY()", "scale()", "rotate()", "skew()", "matrix()" }, ["transform-origin"] = new[] { "center", "top left", "50% 50%" },
        ["transition"] = new[] { "none", "all 0.2s ease", "opacity 0.2s ease-in-out", "transform 0.3s" }, ["transition-property"] = new[] { "all", "none", "opacity", "transform", "color", "background-color" },
        ["transition-duration"] = new[] { "0.2s", "150ms", "0.3s" }, ["transition-timing-function"] = new[] { "ease", "ease-in", "ease-out", "ease-in-out", "linear", "cubic-bezier()", "steps()" },
        ["animation"] = new[] { "none" }, ["animation-name"] = new[] { "none" }, ["animation-duration"] = new[] { "1s", "300ms" }, ["animation-iteration-count"] = new[] { "1", "infinite" },
        ["animation-direction"] = new[] { "normal", "reverse", "alternate", "alternate-reverse" }, ["animation-fill-mode"] = new[] { "none", "forwards", "backwards", "both" }, ["animation-play-state"] = new[] { "running", "paused" },
        ["cursor"] = new[] { "auto", "default", "pointer", "text", "move", "not-allowed", "wait", "help", "grab", "grabbing", "crosshair", "progress", "zoom-in", "zoom-out", "none" },
        ["pointer-events"] = new[] { "auto", "none" }, ["user-select"] = new[] { "auto", "none", "text", "all" }, ["resize"] = new[] { "none", "both", "horizontal", "vertical" },
        ["content"] = new[] { "\"\"", "none", "attr()", "counter()", "open-quote", "close-quote" }, ["object-fit"] = new[] { "fill", "contain", "cover", "none", "scale-down" }, ["object-position"] = new[] { "center", "top", "50% 50%" },
        ["filter"] = new[] { "none", "blur()", "brightness()", "contrast()", "grayscale()", "drop-shadow()", "invert()", "saturate()", "sepia()" }, ["backdrop-filter"] = new[] { "none", "blur()", "saturate()" },
        ["mix-blend-mode"] = new[] { "normal", "multiply", "screen", "overlay", "darken", "lighten", "difference" }, ["isolation"] = new[] { "auto", "isolate" },
        ["table-layout"] = new[] { "auto", "fixed" }, ["caption-side"] = new[] { "top", "bottom" }, ["empty-cells"] = new[] { "show", "hide" },
        ["scroll-behavior"] = new[] { "auto", "smooth" }, ["scroll-snap-type"] = new[] { "none", "x mandatory", "y mandatory", "both proximity" }, ["overscroll-behavior"] = new[] { "auto", "contain", "none" },
        ["accent-color"] = ColorFns, ["caret-color"] = ColorFns, ["fill"] = ColorFns, ["stroke"] = ColorFns, ["stroke-width"] = new[] { "1", "2", "1px" },
        ["appearance"] = new[] { "none", "auto" }, ["will-change"] = new[] { "auto", "transform", "opacity", "scroll-position" }, ["contain"] = new[] { "none", "strict", "content", "layout", "paint", "size" },
        ["container-type"] = new[] { "normal", "inline-size", "size" }, ["container-name"] = Array.Empty<string>(), ["columns"] = new[] { "auto", "2", "3" }, ["column-count"] = new[] { "auto", "2", "3" },
        ["writing-mode"] = new[] { "horizontal-tb", "vertical-rl", "vertical-lr" }, ["direction"] = new[] { "ltr", "rtl" }, ["hyphens"] = new[] { "none", "manual", "auto" },
        ["clip-path"] = new[] { "none", "circle()", "ellipse()", "inset()", "polygon()" }, ["mask"] = new[] { "none", "url()" }, ["counter-reset"] = new[] { "none" }, ["counter-increment"] = new[] { "none" },
        ["text-wrap"] = new[] { "wrap", "nowrap", "balance", "pretty" }, ["text-underline-offset"] = new[] { "auto", "2px", "0.2em" }, ["tab-size"] = new[] { "4", "2" },
    };

    private static readonly string[] AtRules =
        { "media", "import", "keyframes", "font-face", "supports", "layer", "container", "charset", "page", "namespace", "property", "use", "forward", "mixin", "include", "extend", "if", "else", "each", "function", "return" };

    private static readonly string[] PseudoClasses =
    {
        "hover", "focus", "focus-visible", "focus-within", "active", "visited", "link", "checked", "disabled", "enabled", "required", "optional", "invalid", "valid",
        "placeholder-shown", "first-child", "last-child", "only-child", "nth-child()", "nth-last-child()", "nth-of-type()", "first-of-type", "last-of-type", "not()", "is()", "where()", "has()",
        "empty", "root", "target", "read-only", "read-write", "default", "indeterminate", "in-range", "out-of-range", "lang()", "dir()",
    };

    private static readonly string[] PseudoElements = { "before", "after", "placeholder", "selection", "first-line", "first-letter", "marker", "backdrop", "file-selector-button" };

    private static readonly string[] Tags =
    {
        "html", "body", "main", "header", "footer", "nav", "section", "article", "aside", "div", "span", "p", "a", "img", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6",
        "button", "input", "textarea", "select", "option", "label", "form", "fieldset", "legend", "table", "thead", "tbody", "tr", "th", "td", "pre", "code", "blockquote",
        "strong", "em", "small", "hr", "figure", "figcaption", "video", "audio", "canvas", "svg", "details", "summary", "dialog",
    };

    private static readonly string[] Functions =
        { "var()", "calc()", "min()", "max()", "clamp()", "rgb()", "rgba()", "hsl()", "url()", "linear-gradient()", "radial-gradient()", "env()", "attr()" };

    // ------------------------------------------------------------------ context

    private enum Ctx { Property, Value, Selector, Pseudo, PseudoElement, ClassName, Id, AtRule, Variable, Important, None }

    /// <summary>
    /// Completes <paramref name="css"/> (the CSS part of the document, ending at the caret).
    /// <paramref name="declarationsOnly"/>: inside a style="..." attribute (no selectors).
    /// </summary>
    public static CompletionResult Complete(string css, int caretInCss, int cssStartInDocument, bool declarationsOnly, string? projectRoot)
    {
        var result = new CompletionResult();
        var text = css[..caretInCss];
        int start = text.Length;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '-' or '_')) start--;
        var prefix = text[start..];
        var before = start > 0 ? text[start - 1] : '\0';
        result.ReplaceStart = cssStartInDocument + start;

        var (ctx, property) = Context(text, start, declarationsOnly);
        var entries = result.Entries;
        void Add(string label, string insert, EntryKind kind, double priority, string? type = null, int caretBack = 0) =>
            entries.Add(new CompletionEntry { Label = label, Insert = insert, Kind = kind, Priority = priority, TypeText = type, CaretBack = caretBack });

        switch (ctx)
        {
            case Ctx.Property:
                foreach (var p in Properties.Keys) Add(p, p + ": ;", EntryKind.Property, 5, caretBack: 1);
                break;
            case Ctx.Value:
                if (property != null && Properties.TryGetValue(property, out var values))
                    foreach (var v in values) Add(v, v, v.EndsWith("()") ? EntryKind.Function : EntryKind.Constant, 10, property, v.EndsWith("()") ? 1 : 0);
                if (property != null && (property.Contains("color") || property is "background" or "fill" or "stroke" or "border" or "outline" || property.StartsWith("--")))
                    foreach (var c in typeof(System.Windows.Media.Colors).GetProperties().Select(p => p.Name.ToLowerInvariant())) Add(c, c, EntryKind.Constant, 3, "colour");
                foreach (var f in Functions) Add(f, f, EntryKind.Function, 4, "function", 1);
                foreach (var g in Global) Add(g, g, EntryKind.Keyword, 2);
                foreach (var v in Variables(css, projectRoot)) Add($"var({v})", $"var({v})", EntryKind.Variable, 3, "variable"); // after the var() function
                break;
            case Ctx.Variable:
                result.ReplaceStart = cssStartInDocument + text.LastIndexOf("--", StringComparison.Ordinal);
                foreach (var v in Variables(css, projectRoot)) Add(v, v, EntryKind.Variable, 10, "variable");
                break;
            case Ctx.Important:
                Add("important", "important", EntryKind.Keyword, 10);
                break;
            case Ctx.AtRule:
                foreach (var a in AtRules) Add(a, a + " ", EntryKind.Keyword, 5, "at-rule");
                break;
            case Ctx.Pseudo:
                foreach (var p in PseudoClasses) Add(p, p, EntryKind.Function, 5, "pseudo-class", p.EndsWith("()") ? 1 : 0);
                break;
            case Ctx.PseudoElement:
                foreach (var p in PseudoElements) Add(p, p, EntryKind.Function, 5, "pseudo-element");
                break;
            case Ctx.ClassName:
                foreach (var c in ProjectSelectors(css, projectRoot, '.')) Add(c, c, EntryKind.Class, 5, "class");
                break;
            case Ctx.Id:
                foreach (var c in ProjectSelectors(css, projectRoot, '#')) Add(c, c, EntryKind.Constant, 5, "id");
                break;
            case Ctx.Selector:
                foreach (var t in Tags) Add(t, t, EntryKind.Keyword, 4, "tag");
                break;
        }
        _ = prefix;
        _ = before;
        return result;
    }

    private static (Ctx ctx, string? property) Context(string text, int wordStart, bool declarationsOnly)
    {
        var before = wordStart > 0 ? text[wordStart - 1] : '\0';
        if (before == '@') return (Ctx.AtRule, null);
        if (before == '!') return (Ctx.Important, null);
        if (before == '#' && Regex.IsMatch(text[..wordStart], @":[^;{}]*#$")) return (Ctx.None, null); // typing a hex colour
        if (Regex.IsMatch(text, @"var\(\s*-?-?[\w-]*$")) return (Ctx.Variable, null);

        // strip comments and strings to find the structure
        var clean = Regex.Replace(text[..wordStart], @"/\*.*?(\*/|$)|""[^""\n]*""|'[^'\n]*'", m => new string(' ', m.Length), RegexOptions.Singleline);
        int depth = 0, stmtStart = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            var c = clean[i];
            if (c == '{') { depth++; stmtStart = i + 1; }
            else if (c == '}') { depth = Math.Max(0, depth - 1); stmtStart = i + 1; }
            else if (c == ';') stmtStart = i + 1;
        }
        var statement = clean[stmtStart..];
        bool inBlock = depth > 0 || declarationsOnly;

        if (inBlock)
        {
            // "prop: value" (and not a nested SCSS selector like "&:hover")
            var decl = Regex.Match(statement, @"^\s*(?<prop>--[\w-]+|-?[a-zA-Z][\w-]*)\s*:(?!:)");
            if (decl.Success && !statement.TrimStart().StartsWith("&"))
                return (Ctx.Value, decl.Groups["prop"].Value);
            if (before == ':' && statement.TrimStart().StartsWith("&")) return (Ctx.Pseudo, null);
            return (Ctx.Property, null);
        }
        // selectors
        if (before == ':' && wordStart >= 2 && text[wordStart - 2] == ':') return (Ctx.PseudoElement, null);
        if (before == ':') return (Ctx.Pseudo, null);
        if (before == '.') return (Ctx.ClassName, null);
        if (before == '#') return (Ctx.Id, null);
        return (Ctx.Selector, null);
    }

    // ------------------------------------------------------------------ project symbols

    private static readonly Dictionary<string, (DateTime time, HashSet<string> vars, HashSet<string> classes, HashSet<string> ids)> Cache = new();

    private static (HashSet<string> vars, HashSet<string> classes, HashSet<string> ids) ProjectSymbols(string? root)
    {
        var empty = (new HashSet<string>(), new HashSet<string>(), new HashSet<string>());
        if (root == null || !Directory.Exists(root)) return empty;
        if (Cache.TryGetValue(root, out var c) && (DateTime.Now - c.time).TotalSeconds < 20) return (c.vars, c.classes, c.ids);
        var vars = new HashSet<string>();
        var classes = new HashSet<string>();
        var ids = new HashSet<string>();
        foreach (var f in RefactorService.ProjectFiles(root, ".css", ".scss", ".less", ".php", ".twig", ".html").Take(800))
        {
            string t;
            try { if (new FileInfo(f).Length > 500_000) continue; t = File.ReadAllText(f); } catch { continue; }
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".css" or ".scss" or ".less")
            {
                foreach (Match m in Regex.Matches(t, @"(?<![\w-])(--[\w-]+)\s*:")) vars.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(t, @"(?<![\w-])\.([a-zA-Z_][\w-]*)(?=[^{};]*\{)")) classes.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(t, @"(?<![\w&-])#([a-zA-Z_][\w-]*)(?=[^{};]*\{)")) ids.Add(m.Groups[1].Value);
            }
            else
            {
                foreach (Match m in Regex.Matches(t, @"\bclass\s*=\s*[""']([^""'<>{}]*)[""']"))
                    foreach (var cls in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) if (Regex.IsMatch(cls, @"^[a-zA-Z_][\w-]*$")) classes.Add(cls);
                foreach (Match m in Regex.Matches(t, @"\bid\s*=\s*[""']([a-zA-Z_][\w-]*)[""']")) ids.Add(m.Groups[1].Value);
            }
        }
        Cache[root] = (DateTime.Now, vars, classes, ids);
        return (vars, classes, ids);
    }

    private static IEnumerable<string> Variables(string css, string? root) =>
        Regex.Matches(css, @"(?<![\w-])(--[\w-]+)\s*:").Select(m => m.Groups[1].Value).Concat(ProjectSymbols(root).vars).Distinct();

    private static IEnumerable<string> ProjectSelectors(string css, string? root, char kind)
    {
        var local = Regex.Matches(css, kind == '.' ? @"(?<![\w-])\.([a-zA-Z_][\w-]*)" : @"(?<![\w&-])#([a-zA-Z_][\w-]*)").Select(m => m.Groups[1].Value);
        var (_, classes, ids) = ProjectSymbols(root);
        return local.Concat(kind == '.' ? classes : ids).Distinct();
    }

    // ------------------------------------------------------------------ CSS regions in HTML / Twig / PHP

    /// <summary>
    /// When the caret is in a &lt;style&gt; block or a style="..." attribute: the CSS text, its start in the document
    /// and whether it is a declaration list (attribute).
    /// </summary>
    public static (string css, int start, bool declarations)? EmbeddedCss(string text, int caret)
    {
        var head = text[..caret];
        var open = head.LastIndexOf("<style", StringComparison.OrdinalIgnoreCase);
        if (open >= 0 && head.LastIndexOf("</style", StringComparison.OrdinalIgnoreCase) < open)
        {
            var gt = head.IndexOf('>', open);
            if (gt > 0 && gt < caret) return (text[(gt + 1)..], gt + 1, false);
        }
        var attr = Regex.Match(head, @"\bstyle\s*=\s*([""'])(?<css>[^""']*)$", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
        if (attr.Success)
        {
            var s = attr.Groups["css"].Index;
            return (text[s..], s, true);
        }
        return null;
    }
}
