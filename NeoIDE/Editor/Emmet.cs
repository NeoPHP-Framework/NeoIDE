using System.Text;
using System.Text.RegularExpressions;

namespace NeoIDE.Editor;

/// <summary>
/// Emmet abbreviations expanded with Tab in HTML, Twig and the HTML of PHP views:
/// <c>h1</c> → &lt;h1&gt;&lt;/h1&gt;, <c>p.test</c> → &lt;p class="test"&gt;&lt;/p&gt;, <c>#main</c> → a div,
/// <c>ul>li.item$*3</c>, <c>a[href=# target=_blank]{Home}</c>, <c>div+p</c>, <c>(tr>td*2)*3</c>, <c>^</c> to climb,
/// <c>!</c> for an HTML5 document. The caret goes to the first empty attribute or content.
/// </summary>
public static class Emmet
{
    public const char Caret = '\u0001';

    public static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr", "param" };

    private static readonly HashSet<string> InlineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "bdi", "bdo", "br", "button", "cite", "code", "data", "dfn", "em", "i", "img", "input", "kbd", "label",
        "mark", "q", "s", "samp", "select", "small", "span", "strong", "sub", "sup", "textarea", "time", "u", "var", "option",
    };

    private static readonly Dictionary<string, string> DefaultAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = "href", ["img"] = "src alt", ["link"] = "rel=stylesheet href", ["input"] = "type=text", ["form"] = "action",
        ["label"] = "for", ["iframe"] = "src", ["script"] = "", ["source"] = "src", ["video"] = "src", ["audio"] = "src",
        ["meta"] = "", ["area"] = "shape=rect coords href alt", ["object"] = "data", ["embed"] = "src", ["option"] = "value",
    };

    private sealed class Node
    {
        public string? Tag;           // null: implicit (div, li, td…)
        public string? Id;
        public List<string> Classes = new();
        public List<(string name, string? value)> Attributes = new();
        public string? Text;
        public int Count = 1;
        public bool IsGroup;
        public List<Node> Children = new();
    }

    /// <summary>The abbreviation ending at the caret in the line before it (null when there is none).</summary>
    public static (int start, string abbreviation)? AbbreviationBefore(string lineBeforeCaret)
    {
        // walk back over the abbreviation characters (spaces are allowed inside {} and [])
        int i = lineBeforeCaret.Length, braces = 0, brackets = 0;
        while (i > 0)
        {
            var c = lineBeforeCaret[i - 1];
            if (c == '}') braces++;
            else if (c == '{') { if (braces == 0) break; braces--; }
            else if (c == ']') brackets++;
            else if (c == '[') { if (brackets == 0) break; brackets--; }
            else if (braces == 0 && brackets == 0 && !(char.IsLetterOrDigit(c) || c is '.' or '#' or '>' or '+' or '*' or '^' or '$' or '(' or ')' or '-' or ':' or '!' or '_' or '@'))
                break;
            i--;
        }
        var abbr = lineBeforeCaret[i..];
        // "<div>p.x": the abbreviation starts after the tag written before it ("<div" alone is a tag being typed)
        if (i > 0 && lineBeforeCaret[i - 1] == '<')
        {
            var firstGt = abbr.IndexOf('>');
            if (firstGt < 0) return null;
            i += firstGt + 1;
            abbr = abbr[(firstGt + 1)..];
        }
        while (abbr.Length > 0 && abbr[0] == '>') { abbr = abbr[1..]; i++; }
        if (abbr.Length == 0 || abbr.EndsWith('>') || abbr.EndsWith('+')) return null;
        if (!(char.IsLetter(abbr[0]) || abbr[0] is '.' or '#' or '!' or '(')) return null;
        return (i, abbr);
    }

    /// <summary>The HTML of an abbreviation (null when it is not one), with <see cref="Caret"/> marks at the editing places.</summary>
    public static string? Expand(string abbreviation, string indentUnit = "    ", string newLine = "\n", string baseIndent = "")
    {
        abbreviation = abbreviation.Trim();
        if (abbreviation is "!" or "html:5" or "!!!")
            return string.Join(newLine, new[]
            {
                "<!DOCTYPE html>", "<html lang=\"en\">", "<head>", indentUnit + "<meta charset=\"UTF-8\">",
                indentUnit + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">", indentUnit + $"<title>{Caret}</title>",
                "</head>", "<body>", indentUnit + Caret, "</body>", "</html>",
            }.Select((l, k) => k == 0 ? l : baseIndent + l));
        int i = 0;
        List<Node> nodes;
        try
        {
            nodes = ParseSequence(abbreviation, ref i);
            if (i != abbreviation.Length || nodes.Count == 0) return null;
        }
        catch (FormatException) { return null; }
        if (!Known(nodes)) return null;
        var sb = new StringBuilder();
        var lines = new List<string>();
        foreach (var n in nodes) Render(n, null, 0, lines, indentUnit, null);
        return string.Join(newLine, lines.Select((l, k) => k == 0 ? l : baseIndent + l));
    }

    /// <summary>Only HTML tags (or custom elements "x-y") are expanded: "hello" + Tab stays an indentation.</summary>
    private static bool Known(List<Node> nodes) =>
        nodes.All(n => n.IsGroup ? Known(n.Children) : (n.Tag == null || HtmlCompletion.IsTag(n.Tag) || n.Tag.Contains('-')) && Known(n.Children));

    // ------------------------------------------------------------------ parser

    private static List<Node> ParseSequence(string s, ref int i)
    {
        var root = new Node { IsGroup = true };
        var stack = new List<Node> { root };
        while (true)
        {
            var item = ParseItem(s, ref i);
            stack[^1].Children.Add(item);
            if (i >= s.Length || s[i] == ')') break;
            var op = s[i++];
            switch (op)
            {
                case '>': stack.Add(item); break;
                case '+': break;
                case '^':
                    if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                    while (i < s.Length && s[i] == '^') { i++; if (stack.Count > 1) stack.RemoveAt(stack.Count - 1); }
                    break;
                default: throw new FormatException();
            }
        }
        return root.Children;
    }

    private static Node ParseItem(string s, ref int i)
    {
        Node node;
        if (i < s.Length && s[i] == '(')
        {
            i++;
            node = new Node { IsGroup = true, Children = ParseSequence(s, ref i) };
            if (i >= s.Length || s[i] != ')') throw new FormatException();
            i++;
        }
        else
        {
            node = new Node();
            var m = Regex.Match(s[i..], @"^[A-Za-z][\w:-]*");
            if (m.Success) { node.Tag = m.Value; i += m.Length; }
            bool any = m.Success;
            while (i < s.Length)
            {
                var c = s[i];
                if (c is '#' or '.')
                {
                    var v = Regex.Match(s[(i + 1)..], @"^[\w$@-]+");
                    if (!v.Success) throw new FormatException();
                    if (c == '#') node.Id = v.Value; else node.Classes.Add(v.Value);
                    i += 1 + v.Length;
                }
                else if (c == '[')
                {
                    var end = s.IndexOf(']', i);
                    if (end < 0) throw new FormatException();
                    foreach (Match a in Regex.Matches(s[(i + 1)..end], @"(?<n>[\w:@-]+)(?:=(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s\]]*)))?"))
                        node.Attributes.Add((a.Groups["n"].Value, a.Groups["v"].Success ? a.Groups["v"].Value : null));
                    i = end + 1;
                }
                else if (c == '{')
                {
                    var end = s.IndexOf('}', i);
                    if (end < 0) throw new FormatException();
                    node.Text = s[(i + 1)..end];
                    i = end + 1;
                }
                else break;
                any = true;
            }
            if (!any) throw new FormatException();
        }
        if (i < s.Length && s[i] == '*')
        {
            var n = Regex.Match(s[(i + 1)..], @"^\d+");
            if (!n.Success) throw new FormatException();
            node.Count = Math.Clamp(int.Parse(n.Value), 1, 200);
            i += 1 + n.Length;
        }
        return node;
    }

    // ------------------------------------------------------------------ output

    private static string ImplicitTag(string? parent) => parent?.ToLowerInvariant() switch
    {
        "ul" or "ol" or "menu" => "li",
        "table" or "tbody" or "thead" or "tfoot" => "tr",
        "tr" => "td",
        "select" or "optgroup" or "datalist" => "option",
        "dl" => "dt",
        "span" or "a" or "p" or "em" or "strong" or "b" or "i" or "label" or "button" => "span",
        _ => "div",
    };

    private static string Number(string value, int index) =>
        Regex.Replace(value, @"\$+", m => index.ToString().PadLeft(m.Length, '0'));

    private static void Render(Node node, string? parentTag, int depth, List<string> lines, string unit, int? inheritedIndex)
    {
        for (int k = 1; k <= node.Count; k++)
        {
            int? index = node.Count > 1 ? k : inheritedIndex;
            if (node.IsGroup)
            {
                foreach (var c in node.Children) Render(c, parentTag, depth, lines, unit, index);
                continue;
            }
            string N(string v) => index is { } ix ? Number(v, ix) : v;
            var tag = node.Tag ?? ImplicitTag(parentTag);
            var open = new StringBuilder("<").Append(tag);
            // like Emmet: the default attributes of the tag first (href of a, src / alt of img…), then id, class, the others
            var attributes = new List<(string name, string? value)>();
            var given = node.Attributes.Select(a => (a.name, value: a.value == null ? null : N(a.value))).ToList();
            if (node.Tag != null && DefaultAttributes.TryGetValue(tag, out var defaults))
                foreach (var d in defaults.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = d.Split('=', 2);
                    var explicitValue = given.FirstOrDefault(a => a.name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
                    if (explicitValue.name != null) given.Remove(explicitValue);
                    attributes.Add((parts[0], explicitValue.name != null ? explicitValue.value : parts.Length > 1 ? parts[1] : ""));
                }
            if (node.Id != null) attributes.Add(("id", N(node.Id)));
            if (node.Classes.Count > 0) attributes.Add(("class", string.Join(" ", node.Classes.Select(N))));
            attributes.AddRange(given);
            foreach (var (name, value) in attributes)
                open.Append(' ').Append(name).Append("=\"").Append(string.IsNullOrEmpty(value) ? Caret.ToString() : value).Append('"');
            open.Append('>');
            var indent = string.Concat(Enumerable.Repeat(unit, depth));
            if (VoidTags.Contains(tag)) { lines.Add(indent + open); continue; }
            var close = $"</{tag}>";
            var text = node.Text != null ? N(node.Text) : null;
            // like Emmet: inline children stay on the line of their parent, unless there are 3 or more of them
            var childCount = node.Children.Sum(ch => ch.Count * (ch.IsGroup ? ch.Children.Count : 1));
            var blockChildren = node.Children.Count > 0 && !(node.Children.All(IsInline) && childCount < 3);
            if (node.Children.Count == 0)
                lines.Add(indent + open + (string.IsNullOrEmpty(text) ? Caret.ToString() : text) + close);
            else if (!blockChildren)
            {
                var inner = new List<string>();
                foreach (var c in node.Children) Render(c, tag, 0, inner, unit, index);
                lines.Add(indent + open + text + string.Concat(inner) + close);
            }
            else
            {
                lines.Add(indent + open + text);
                foreach (var c in node.Children) Render(c, tag, depth + 1, lines, unit, index);
                lines.Add(indent + close);
            }
        }
    }

    private static bool IsInline(Node n) => !n.IsGroup && n.Tag != null && InlineTags.Contains(n.Tag) && n.Children.All(IsInline);

    /// <summary>The expansion without its marks, and the offset of the first mark (the end when there is none).</summary>
    public static (string text, int caret) Finish(string expansion)
    {
        var first = expansion.IndexOf(Caret);
        var text = expansion.Replace(Caret.ToString(), "");
        return (text, first < 0 ? text.Length : first);
    }
}
