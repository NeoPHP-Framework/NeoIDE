using System.Text;
using System.Text.RegularExpressions;

namespace NeoIDE.Editor;

/// <summary>
/// Built-in "Reformat Code": re-indents every line from the structure of the file — brackets, HTML elements, Twig
/// blocks, PHP alternative syntax (<c>foreach (...): … endforeach</c>), switch cases, method chains — and trims the
/// trailing whitespace. Strings, heredocs and comments spanning several lines are kept as they are.
/// YAML, Markdown and the other indentation sensitive languages only get the whitespace cleanup.
/// </summary>
public static class Reindenter
{
    private enum Mode { Html, Php, Js, Css }

    private sealed class Frame
    {
        public required string Kind;
        public bool Counted;
        public bool IsSwitch;
        public bool CaseOpen;
        public int Line;
    }

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr", "param", "keygen", "!doctype" };

    private static readonly HashSet<string> TwigBlocks = new(StringComparer.Ordinal)
        { "if", "for", "block", "macro", "embed", "apply", "autoescape", "sandbox", "spaceless", "verbatim", "with", "filter", "set", "trans", "cache", "guard", "component" };

    private static readonly Regex PhpAltOpen = new(@"^\s*(?<k>if|foreach|for|while|switch)\b.*:\s*$", RegexOptions.Compiled);
    private static readonly Regex PhpAltElse = new(@"^\s*(?:else\s*:|elseif\b.*:|else\s*if\b.*:)\s*$", RegexOptions.Compiled);
    private static readonly Regex PhpAltClose = new(@"^\s*(?<k>endif|endforeach|endfor|endwhile|endswitch)\b", RegexOptions.Compiled);

    /// <summary>Languages whose indentation is structural (re-indented); the others only get the whitespace cleanup.</summary>
    public static bool CanReindent(string? language) => language is "PHP" or "Twig" or "HTML" or "XML" or "JavaScript" or "TypeScript" or "CSS" or "JSON";

    public static string Reformat(string text, string? language, string unit, int fromLine = 1, int toLine = int.MaxValue)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string[] result;
        if (CanReindent(language)) result = new Scanner(language!, unit).Run(lines, fromLine, toLine);
        else result = lines.Select((l, i) => i + 1 >= fromLine && i + 1 <= toLine ? l.TrimEnd() : l).ToArray();
        var s = string.Join(nl, result);
        if (toLine == int.MaxValue) s = s.TrimEnd('\r', '\n') + nl; // exactly one final newline
        return s;
    }

    private sealed class Scanner(string language, string unit)
    {
        private readonly List<Frame> _stack = new();
        private Mode _mode = language switch { "PHP" => Mode.Html, "JavaScript" or "TypeScript" or "JSON" => Mode.Js, "CSS" => Mode.Css, _ => Mode.Html };
        private readonly bool _php = language == "PHP";
        private readonly bool _twig = language == "Twig";
        private readonly bool _phpFileStartsInCode = language == "PHP";

        // state that survives a line break
        private char _string;            // in a string of this quote
        private bool _blockComment;      // /* */
        private bool _htmlComment;       // <!-- -->
        private bool _twigComment;       // {# #}
        private string? _heredoc;        // closing identifier
        private bool _inTag;             // between "<tag" and ">"
        private Frame? _tagFrame;        // the frame of the tag being written (null for a void tag)
        private string? _tagName;
        private char _tagQuote;
        private string? _twigEnd;        // inside {% %} / {{ }}: the closing delimiter

        // state of the current line
        private bool _leading;
        private int _lineIndent;
        private int _extra;
        private int _lineNo;
        private Frame? _firstOpenedThisLine;

        public string[] Run(string[] lines, int fromLine, int toLine)
        {
            var output = new string[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                _lineNo = i + 1;
                var line = lines[i];
                var trimmed = line.Trim();
                bool verbatim = _string != '\0' || _heredoc != null;
                bool comment = _blockComment || _htmlComment || _twigComment;
                bool inRange = _lineNo >= fromLine && _lineNo <= toLine;

                _leading = true;
                _extra = 0;
                _firstOpenedThisLine = null;
                _lineIndent = Counted();
                bool heredocEnds = _heredoc != null && Regex.IsMatch(line, @"^\s*" + Regex.Escape(_heredoc) + @"\b");
                Scan(line);
                if (_altPending != null && _mode == Mode.Php) { Push(_altPending); _altPending = null; }
                // the frames still open at the end of the line indent the next lines by one level, whatever their number
                var opened = _stack.Where(f => f.Line == _lineNo).ToList();
                for (int k = 0; k < opened.Count; k++) opened[k].Counted = k == 0;

                if (!inRange || verbatim || heredocEnds) { output[i] = verbatim || heredocEnds ? line : line.TrimEnd(); continue; }
                if (trimmed.Length == 0) { output[i] = ""; continue; }
                var level = Math.Max(0, _lineIndent + _extra);
                var indent = string.Concat(Enumerable.Repeat(unit, level));
                if (comment && !trimmed.StartsWith("*/") && !trimmed.StartsWith("-->"))
                    output[i] = indent + (trimmed.StartsWith("*") ? " " : "") + trimmed;
                else if (comment)
                    output[i] = indent + (trimmed.StartsWith("*/") ? " " : "") + trimmed;
                else output[i] = indent + trimmed;
            }
            return output;
        }

        private int Counted() => _stack.Count(f => f.Counted);

        private void Push(string kind, bool isSwitch = false)
        {
            var f = new Frame { Kind = kind, Line = _lineNo, IsSwitch = isSwitch };
            _stack.Add(f);
            _firstOpenedThisLine ??= f;
            EndLeading();
        }

        /// <summary>Closes the innermost frame of this kind (and the unclosed ones above it); ignored without a match.</summary>
        private void Pop(string kind)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
                if (_stack[i].Kind == kind)
                {
                    _stack.RemoveRange(i, _stack.Count - i);
                    if (_leading) _lineIndent = Counted();
                    return;
                }
        }

        private void EndLeading()
        {
            if (!_leading) return;
            _leading = false;
            _lineIndent = Counted();
        }

        private void Content() => EndLeading();

        private void Scan(string s)
        {
            int i = 0;
            // pure PHP files: the alternative syntax / switch cases at the start of the line
            if (_mode == Mode.Php && _string == '\0' && !_blockComment && _heredoc == null) PhpChunkStart(s, 0);
            if (_mode is Mode.Php or Mode.Js && _string == '\0' && !_blockComment) LineStartKeywords(s);
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c) && _string == '\0') { i++; continue; }

                // ---- multi-line states
                if (_heredoc != null)
                {
                    var m = Regex.Match(s[i..], @"^\s*" + Regex.Escape(_heredoc) + @"\b");
                    if (i == 0 && m.Success) { _heredoc = null; i += m.Length; Content(); continue; }
                    return; // the whole line belongs to the heredoc
                }
                if (_blockComment)
                {
                    var end = s.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end < 0) return;
                    _blockComment = false; i = end + 2; continue;
                }
                if (_htmlComment)
                {
                    var end = s.IndexOf("-->", i, StringComparison.Ordinal);
                    if (end < 0) return;
                    _htmlComment = false; i = end + 3; continue;
                }
                if (_twigComment)
                {
                    var end = s.IndexOf("#}", i, StringComparison.Ordinal);
                    if (end < 0) return;
                    _twigComment = false; i = end + 2; continue;
                }
                if (_string != '\0')
                {
                    if (c == '\\') { i += 2; continue; }
                    if (c == _string) _string = '\0';
                    i++;
                    continue;
                }

                // ---- Twig regions (inside HTML)
                if (_twigEnd != null)
                {
                    if (string.CompareOrdinal(s, i, _twigEnd, 0, _twigEnd.Length) == 0 || c == '-' && string.CompareOrdinal(s, i + 1, _twigEnd, 0, _twigEnd.Length) == 0)
                    {
                        i += _twigEnd.Length + (c == '-' ? 1 : 0);
                        _twigEnd = null;
                        continue;
                    }
                    if (c is '\'' or '"') { _string = c; Content(); i++; continue; }
                    if (Bracket(c)) { i++; continue; }
                    Content();
                    i++;
                    continue;
                }

                switch (_mode)
                {
                    case Mode.Html: i = ScanHtml(s, i); break;
                    case Mode.Php: i = ScanPhp(s, i); break;
                    default: i = ScanCode(s, i); break;
                }
            }
            // a "//" comment ends with the line; PHP's "?>" was handled in ScanPhp
        }

        private bool Bracket(char c)
        {
            switch (c)
            {
                case '(': Push("("); return true;
                case '[': Push("["); return true;
                case '{': Push("{", _switchPending); _switchPending = false; return true;
                case ')': Pop("("); return true;
                case ']': Pop("["); return true;
                case '}': Pop("{"); return true;
            }
            return false;
        }

        private bool _switchPending;

        /// <summary>case / default of a switch, "->" chains: depend on the first word of the line.</summary>
        private void LineStartKeywords(string s)
        {
            var t = s.TrimStart();
            var top = _stack.Count > 0 ? _stack[^1] : null;
            // leading closers are handled while scanning: look at the frame the line will be in
            if (top is { IsSwitch: true })
            {
                if (Regex.IsMatch(t, @"^(?:case\b|default\s*:)")) { top.CaseOpen = true; }
                else if (top.CaseOpen && !t.StartsWith("}")) _extra = 1;
            }
            if (_mode == Mode.Php && (t.StartsWith("->") || t.StartsWith("?->")) || _mode == Mode.Js && t.StartsWith(".") && !t.StartsWith("..."))
                _extra += 1;
        }

        /// <summary>The PHP code that starts at <paramref name="from"/> (up to "?&gt;" or the end of the line).</summary>
        private void PhpChunkStart(string s, int from)
        {
            var close = s.IndexOf("?>", from, StringComparison.Ordinal);
            var chunk = StripStrings(close >= 0 ? s[from..close] : s[from..]);
            chunk = Regex.Replace(chunk, @"(//|#(?!\[)).*$", "");
            Match m;
            if ((m = PhpAltClose.Match(chunk)).Success) { Pop("?" + m.Groups["k"].Value[3..]); return; }
            if (PhpAltElse.IsMatch(chunk))
            {
                if (_leading) { EndLeading(); _extra -= 1; }
                return;
            }
            if ((m = PhpAltOpen.Match(chunk)).Success) _altPending = "?" + m.Groups["k"].Value;
            if (Regex.IsMatch(chunk, @"\bswitch\s*\(")) _switchPending = true;
        }

        private string? _altPending;

        private static string StripStrings(string s) => Regex.Replace(s, @"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", "''");

        private int ScanHtml(string s, int i)
        {
            char c = s[i];
            if (_inTag)
            {
                if (_tagQuote != '\0') { if (c == _tagQuote) _tagQuote = '\0'; return i + 1; }
                if (c is '"' or '\'') { _tagQuote = c; return i + 1; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '>')
                {
                    if (_tagFrame != null) { _stack.Remove(_tagFrame); if (_firstOpenedThisLine == _tagFrame) _firstOpenedThisLine = null; }
                    _inTag = false; _tagFrame = null;
                    return i + 2;
                }
                if (c == '>')
                {
                    _inTag = false;
                    if (_tagFrame != null && _tagName is "script") _mode = Mode.Js;
                    else if (_tagFrame != null && _tagName is "style") _mode = Mode.Css;
                    _tagFrame = null;
                    return i + 1;
                }
                if (_php && c == '<' && i + 1 < s.Length && s[i + 1] == '?') return EnterPhp(s, i);
                if (_twig && c == '{' && i + 1 < s.Length && s[i + 1] is '{' or '%') return EnterTwig(s, i);
                return i + 1;
            }
            if (c == '<')
            {
                if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0) { _htmlComment = true; Content(); return i + 4; }
                if (_php && i + 1 < s.Length && s[i + 1] == '?') return EnterPhp(s, i);
                var m = Regex.Match(s[i..], @"^<(?<close>/)?(?<name>!?[A-Za-z][\w:.-]*)");
                if (m.Success)
                {
                    var name = m.Groups["name"].Value.ToLowerInvariant();
                    if (m.Groups["close"].Success)
                    {
                        Pop("<" + name);
                        var gt = s.IndexOf('>', i);
                        return gt < 0 ? s.Length : gt + 1;
                    }
                    _inTag = true;
                    _tagName = name;
                    _tagQuote = '\0';
                    if (VoidTags.Contains(name)) { _tagFrame = null; Content(); }
                    else { Push("<" + name); _tagFrame = _stack[^1]; }
                    return i + m.Length;
                }
            }
            if (_twig && c == '{' && i + 1 < s.Length && s[i + 1] is '{' or '%' or '#') return EnterTwig(s, i);
            Content();
            return i + 1;
        }

        private int EnterPhp(string s, int i)
        {
            var len = string.CompareOrdinal(s, i, "<?php", 0, 5) == 0 ? 5 : string.CompareOrdinal(s, i, "<?=", 0, 3) == 0 ? 3 : 2;
            _mode = Mode.Php;
            _phpReturn = _inTag ? Mode.Html : Mode.Html;
            PhpChunkStart(s, i + len);
            return i + len;
        }

        private Mode _phpReturn = Mode.Html;

        private int EnterTwig(string s, int i)
        {
            if (s[i + 1] == '#') { _twigComment = true; Content(); return i + 2; }
            var block = s[i + 1] == '%';
            _twigEnd = block ? "%}" : "}}";
            if (!block) { Content(); return i + 2; }
            var m = Regex.Match(s[(i + 2)..], @"^-?\s*(?<tag>\w+)(?<rest>[^%]*)");
            var tag = m.Success ? m.Groups["tag"].Value : "";
            var rest = m.Groups["rest"].Value;
            if (tag.StartsWith("end")) { Pop("%" + tag[3..]); return i + 2; }
            if (tag is "else" or "elseif" or "elif")
            {
                if (_leading) { EndLeading(); _extra -= 1; }
                return i + 2;
            }
            var opens = TwigBlocks.Contains(tag)
                        && !(tag == "set" && rest.Contains('='))
                        && !(tag == "block" && !Regex.IsMatch(rest, @"^\s*\w+\s*-?$"));
            if (opens) Push("%" + tag);
            else Content();
            return i + 2;
        }

        private int ScanPhp(string s, int i)
        {
            char c = s[i];
            if (c == '?' && i + 1 < s.Length && s[i + 1] == '>')
            {
                _mode = _phpReturn;
                if (_altPending != null) { Push(_altPending); _altPending = null; }
                return i + 2;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/' || c == '#' && !(i + 1 < s.Length && s[i + 1] == '['))
            {
                // line comment: up to the end of the line or "?>"
                Content();
                var close = s.IndexOf("?>", i, StringComparison.Ordinal);
                return close < 0 ? s.Length : close;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { _blockComment = true; Content(); return i + 2; }
            if (c == '<' && string.CompareOrdinal(s, i, "<<<", 0, 3) == 0)
            {
                var m = Regex.Match(s[(i + 3)..], @"^\s*['""]?(?<id>[A-Za-z_]\w*)['""]?");
                if (m.Success) { _heredoc = m.Groups["id"].Value; Content(); return s.Length; }
            }
            if (c is '\'' or '"' or '`') { _string = c; Content(); return i + 1; }
            if (c == 's' && Regex.IsMatch(s[i..], @"^switch\s*\(") && (i == 0 || !char.IsLetterOrDigit(s[i - 1]))) _switchPending = true;
            if (Bracket(c)) return i + 1;
            Content();
            return i + 1;
        }

        private int ScanCode(string s, int i)
        {
            char c = s[i];
            if (c == '<' && i + 1 < s.Length && s[i + 1] == '/' && language is "HTML" or "PHP" or "Twig")
            {
                var m = Regex.Match(s[i..], @"^</(?<name>script|style)\b", RegexOptions.IgnoreCase);
                if (m.Success) { _mode = Mode.Html; return ScanHtml(s, i); }
            }
            if (_mode == Mode.Js && c == '/' && i + 1 < s.Length && s[i + 1] == '/') { Content(); return s.Length; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { _blockComment = true; Content(); return i + 2; }
            if (c is '\'' or '"' || c == '`' && _mode == Mode.Js) { _string = c; Content(); return i + 1; }
            if (_mode == Mode.Js && c == 's' && Regex.IsMatch(s[i..], @"^switch\s*\(") && (i == 0 || !char.IsLetterOrDigit(s[i - 1]))) _switchPending = true;
            if (Bracket(c)) return i + 1;
            Content();
            return i + 1;
        }
    }
}
