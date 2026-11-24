using System.Text;
using System.Text.RegularExpressions;

namespace NeoIDE.Php;

/// <summary>Extracts declarations (namespace, use, classes, members, functions) from PHP source.</summary>
public sealed class PhpParser
{
    private readonly List<Token> _t;
    private readonly PhpFileInfo _info;
    private readonly string _text;
    private int _p;
    private bool _pendingAttribute;

    private PhpParser(string text, string? path)
    {
        _text = text;
        _t = PhpLexer.Significant(PhpLexer.Tokenize(text));
        _info = new PhpFileInfo { Path = path };
    }

    public static PhpFileInfo Parse(string text, string? path = null)
    {
        var parser = new PhpParser(text, path);
        try { parser.Run(); } catch { /* tolerate broken code: keep what was parsed */ }
        return parser._info;
    }

    private Token Cur => _p < _t.Count ? _t[_p] : new Token(TokKind.Punct, "", _text.Length);
    private Token Peek(int k = 1) => _p + k < _t.Count ? _t[_p + k] : new Token(TokKind.Punct, "", _text.Length);
    private bool Eof => _p >= _t.Count;

    private void Run()
    {
        var all = PhpLexer.Tokenize(_text);
        var open = all.FirstOrDefault(t => t.Kind == TokKind.OpenTag);
        _info.HeaderEnd = open.Text != null ? open.End : 0;

        string? doc = null;
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int depth = 0;

        while (!Eof)
        {
            var t = Cur;
            if (t.Kind == TokKind.DocComment) { doc = t.Text; _p++; continue; }

            if (t.Kind == TokKind.Identifier)
            {
                var w = t.Text.ToLowerInvariant();
                if (w == "declare" && depth == 0 && Peek().Is("(") && !PrevIs("->") && !PrevIs("::") && !PrevIs("?->"))
                {
                    _p++;
                    SkipBalanced("(", ")");
                    if (Cur.Is(";")) { _info.HeaderEnd = Cur.End; _p++; }
                    continue;
                }
                if (w == "namespace" && !Peek().Is("\\") && !(Peek().Kind == TokKind.Punct && Peek().Text == "("))
                {
                    _p++;
                    var ns = Cur.Kind == TokKind.Identifier ? Cur.Text : "";
                    if (ns.Length > 0) _p++;
                    _info.Namespace = ns.Trim('\\');
                    if (Cur.Is(";")) { _info.NamespaceStatementEnd = Cur.End; _p++; }
                    else if (Cur.Is("{")) { _info.NamespaceStatementEnd = Cur.End; depth++; _p++; }
                    doc = null;
                    continue;
                }
                if (w == "use" && depth <= 1 && !PrevIs(")"))
                {
                    ParseUse();
                    doc = null;
                    continue;
                }
                if (w is "abstract" or "final" or "readonly")
                {
                    modifiers.Add(w);
                    _p++;
                    continue;
                }
                if (w is "class" or "interface" or "trait" or "enum" && !PrevIs("::") && !PrevIsWord("new")
                    && Peek().Kind == TokKind.Identifier && !(w == "enum" && Peek().IsWord("extends")))
                {
                    ParseClass(w, modifiers, doc);
                    _pendingAttribute = false;
                    modifiers.Clear();
                    doc = null;
                    continue;
                }
                if (w == "function" && depth <= 1 && (Peek().Kind == TokKind.Identifier || (Peek().Is("&") && Peek(2).Kind == TokKind.Identifier)))
                {
                    ParseFunction(doc);
                    doc = null;
                    continue;
                }
                if (w == "const" && depth <= 1 && Peek().Kind == TokKind.Identifier)
                {
                    _info.Constants.Add(Join(_info.Namespace, Peek().Text));
                }
                if (w == "define" && Peek().Is("(") && Peek(2).Kind == TokKind.String)
                {
                    _info.Constants.Add(Peek(2).Text.Trim('\'', '"'));
                }
            }
            if (t.Is("{")) depth++;
            else if (t.Is("}")) depth = Math.Max(0, depth - 1);
            if (!t.IsWord("abstract") && !t.IsWord("final") && !t.IsWord("readonly")) modifiers.Clear();
            if (t.Is(";") || t.Is("}")) doc = null;
            if (t.Is("#["))
            {
                // #[Attribute] (or #[\Attribute(...)]) before a class declaration
                if (Peek().Kind == TokKind.Identifier && Peek().Text.TrimStart('\\').Equals("Attribute", StringComparison.OrdinalIgnoreCase)) _pendingAttribute = true;
                SkipBalanced("#[", "]");
                continue;
            }
            _p++;
        }
    }

    private bool PrevIs(string punct) => _p > 0 && _t[_p - 1].Is(punct);
    private bool PrevIsWord(string w) => _p > 0 && _t[_p - 1].IsWord(w);

    private static string Join(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "\\" + name;

    private void SkipBalanced(string open, string close)
    {
        if (!Cur.Is(open) && !(open == "#[" && Cur.Is("#["))) return;
        int d = 0;
        while (!Eof)
        {
            var t = Cur;
            if (t.Is(open) || (close == "]" && t.Is("[")) || (close == "]" && t.Is("#["))) d++;
            else if (t.Is(close)) { d--; if (d == 0) { _p++; return; } }
            _p++;
        }
    }

    private void ParseUse()
    {
        int start = Cur.Start;
        _p++; // use
        var kind = "class";
        if (Cur.IsWord("function")) { kind = "function"; _p++; }
        else if (Cur.IsWord("const")) { kind = "const"; _p++; }

        var pending = new List<PhpUse>();
        while (!Eof && !Cur.Is(";"))
        {
            if (Cur.Kind == TokKind.Identifier)
            {
                var name = Cur.Text.Trim('\\');
                _p++;
                if (Cur.Is("{"))
                {
                    // group use: use A\B\{C, D as E};
                    _p++;
                    var prefix = name.TrimEnd('\\');
                    while (!Eof && !Cur.Is("}"))
                    {
                        var subKind = kind;
                        if (Cur.IsWord("function")) { subKind = "function"; _p++; }
                        else if (Cur.IsWord("const")) { subKind = "const"; _p++; }
                        if (Cur.Kind == TokKind.Identifier)
                        {
                            var fqn = prefix + "\\" + Cur.Text.Trim('\\');
                            _p++;
                            var alias = fqn.Split('\\').Last();
                            if (Cur.IsWord("as")) { _p++; alias = Cur.Text; _p++; }
                            pending.Add(new PhpUse { Alias = alias, Fqn = fqn, Kind = subKind, IsGroup = true });
                        }
                        else _p++;
                    }
                    if (Cur.Is("}")) _p++;
                    continue;
                }
                var a = name.Split('\\').Last();
                if (Cur.IsWord("as")) { _p++; if (Cur.Kind == TokKind.Identifier) { a = Cur.Text; _p++; } }
                pending.Add(new PhpUse { Alias = a, Fqn = name, Kind = kind });
                continue;
            }
            _p++;
        }
        int end = Cur.Is(";") ? Cur.End : Cur.Start;
        if (Cur.Is(";")) _p++;
        foreach (var u in pending)
        {
            u.StatementStart = start;
            u.StatementEnd = end;
            _info.Uses.Add(u);
        }
    }

    private void ParseClass(string keyword, HashSet<string> modifiers, string? doc)
    {
        var cls = new PhpClass
        {
            Kind = keyword switch { "interface" => PhpClassKind.Interface, "trait" => PhpClassKind.Trait, "enum" => PhpClassKind.Enum, _ => PhpClassKind.Class },
            IsAbstract = modifiers.Contains("abstract"),
            IsAttribute = _pendingAttribute,
            IsFinal = modifiers.Contains("final"),
            Offset = Cur.Start,
            File = _info.Path,
            Namespace = _info.Namespace,
        };
        _p++;
        cls.Name = Cur.Text;
        cls.Fqn = Join(_info.Namespace, cls.Name);
        cls.Offset = Cur.Start;
        _p++;

        var backed = false;
        if (cls.Kind == PhpClassKind.Enum && Cur.Is(":")) { backed = true; _p++; _p++; } // backed enum type
        while (!Eof && !Cur.Is("{"))
        {
            if (Cur.IsWord("extends"))
            {
                _p++;
                var names = ReadNameList();
                if (cls.Kind == PhpClassKind.Interface) cls.Interfaces.AddRange(names);
                else cls.Parent = names.FirstOrDefault();
                continue;
            }
            if (Cur.IsWord("implements"))
            {
                _p++;
                cls.Interfaces.AddRange(ReadNameList());
                continue;
            }
            _p++;
        }
        if (cls.Kind == PhpClassKind.Enum)
            cls.Interfaces.Add(backed ? "BackedEnum" : "UnitEnum");

        ApplyClassDoc(cls, doc);
        cls.BodyStart = Cur.Start;
        _p++; // {
        // offset of the closing "}" (set by ParseClassBody); end of text while the class is not closed yet
        cls.BodyEnd = _text.Length;
        ParseClassBody(cls);
        _info.Classes.Add(cls);
    }

    private List<string> ReadNameList()
    {
        var list = new List<string>();
        while (!Eof)
        {
            if (Cur.Kind == TokKind.Identifier && !Cur.IsWord("implements") && !Cur.IsWord("extends"))
            {
                list.Add(_info.ResolveClass(Cur.Text));
                _p++;
                if (Cur.Is(",")) { _p++; continue; }
            }
            break;
        }
        return list;
    }

    private void ParseClassBody(PhpClass cls)
    {
        string? doc = null;
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!Eof)
        {
            var t = Cur;
            if (t.Is("}")) { cls.BodyEnd = t.Start; _p++; return; }
            if (t.Kind == TokKind.DocComment) { doc = t.Text; _p++; continue; }
            if (t.Is("#[")) { SkipBalanced("#[", "]"); continue; }

            if (t.Kind == TokKind.Identifier)
            {
                var w = t.Text.ToLowerInvariant();
                switch (w)
                {
                    case "public" or "protected" or "private" or "static" or "abstract" or "final" or "readonly" or "var":
                        // "public private(set)" asymmetric visibility (PHP 8.4)
                        mods.Add(w);
                        _p++;
                        if (Cur.Is("(") && Peek().IsWord("set")) SkipBalanced("(", ")");
                        continue;
                    case "use":
                        _p++;
                        while (!Eof && !Cur.Is(";") && !Cur.Is("{"))
                        {
                            if (Cur.Kind == TokKind.Identifier) cls.Traits.Add(_info.ResolveClass(Cur.Text));
                            _p++;
                        }
                        if (Cur.Is("{")) SkipBalanced("{", "}");
                        else _p++;
                        doc = null;
                        mods.Clear();
                        continue;
                    case "const":
                        _p++;
                        ParseConstants(cls, mods, doc);
                        doc = null;
                        mods.Clear();
                        continue;
                    case "case" when cls.Kind == PhpClassKind.Enum:
                        _p++;
                        if (Cur.Kind == TokKind.Identifier)
                        {
                            cls.Members.Add(new PhpMember
                            {
                                Name = Cur.Text, Kind = PhpMemberKind.EnumCase, IsStatic = true, DeclaringClass = cls.Fqn,
                                Type = "\\" + cls.Fqn, File = _info.Path, Offset = Cur.Start, Summary = DocSummary(doc),
                            });
                        }
                        SkipToStatementEnd();
                        doc = null;
                        continue;
                    case "function":
                        ParseMethod(cls, mods, doc);
                        doc = null;
                        mods.Clear();
                        continue;
                }
            }

            if (t.Kind == TokKind.Variable || (mods.Count > 0 && (t.Kind == TokKind.Identifier || t.Is("?") || t.Is("("))))
            {
                ParseProperties(cls, mods, doc);
                doc = null;
                mods.Clear();
                continue;
            }
            _p++;
        }
    }

    private void SkipToStatementEnd()
    {
        int d = 0;
        while (!Eof)
        {
            var t = Cur;
            if (t.Is("(") || t.Is("[") || t.Is("{") || t.Is("#[")) d++;
            else if (t.Is(")") || t.Is("]") || t.Is("}")) { if (d == 0) return; d--; }
            else if (t.Is(";") && d == 0) { _p++; return; }
            _p++;
        }
    }

    private void ParseConstants(PhpClass cls, HashSet<string> mods, string? doc)
    {
        // const [type] NAME = value, NAME2 = value;
        string? type = null;
        if (Cur.Kind == TokKind.Identifier && Peek().Kind == TokKind.Identifier) { type = Cur.Text; _p++; }
        while (!Eof)
        {
            if (Cur.Kind == TokKind.Identifier && Peek().Is("="))
            {
                var m = new PhpMember
                {
                    Name = Cur.Text, Kind = PhpMemberKind.Constant, IsStatic = true, DeclaringClass = cls.Fqn,
                    Visibility = VisibilityOf(mods), File = _info.Path, Offset = Cur.Start, Summary = DocSummary(doc),
                    Type = _info.ResolveType(type),
                };
                _p += 2;
                var valueStart = _p;
                int d = 0;
                while (!Eof)
                {
                    var t = Cur;
                    if (t.Is("(") || t.Is("[") || t.Is("{")) d++;
                    else if (t.Is(")") || t.Is("]") || t.Is("}")) { if (d == 0) break; d--; }
                    else if ((t.Is(",") || t.Is(";")) && d == 0) break;
                    _p++;
                }
                if (m.Type == null) m.Type = GuessLiteralType(valueStart, _p);
                cls.Members.Add(m);
                if (Cur.Is(",")) { _p++; continue; }
                if (Cur.Is(";")) _p++;
                return;
            }
            if (Cur.Is(";")) { _p++; return; }
            if (Cur.Is("}")) return;
            _p++;
        }
    }

    private string? GuessLiteralType(int from, int to)
    {
        if (to - from != 1 && !(to - from == 2 && _t[from].Is("-"))) return null;
        var t = _t[to - 1];
        return t.Kind switch
        {
            TokKind.String => "string",
            TokKind.Number => t.Text.Contains('.') ? "float" : "int",
            TokKind.Identifier when t.IsWord("true") || t.IsWord("false") => "bool",
            _ => null,
        };
    }

    private static Visibility VisibilityOf(HashSet<string> mods) =>
        mods.Contains("private") ? Visibility.Private : mods.Contains("protected") ? Visibility.Protected : Visibility.Public;

    private string? ReadType()
    {
        // type tokens until a variable / & / ... / { / ; / =
        var sb = new StringBuilder();
        while (!Eof)
        {
            var t = Cur;
            if (t.Kind == TokKind.Identifier && !t.IsWord("as") || t.Is("?") || t.Is("|") || t.Is("(") || t.Is(")") && sb.ToString().Contains('(') && Depth(sb) > 0)
            {
                sb.Append(t.Text);
                _p++;
                continue;
            }
            if (t.Is("&") && (Peek().Kind == TokKind.Identifier || Peek().Is("(")) && sb.Length > 0)
            {
                sb.Append('&');
                _p++;
                continue;
            }
            break;
        }
        return sb.Length == 0 ? null : _info.ResolveType(sb.ToString());
    }

    private static int Depth(StringBuilder sb)
    {
        int d = 0;
        foreach (var ch in sb.ToString()) { if (ch == '(') d++; else if (ch == ')') d--; }
        return d;
    }

    private void ParseProperties(PhpClass cls, HashSet<string> mods, string? doc)
    {
        var type = Cur.Kind == TokKind.Variable ? null : ReadType();
        var docVar = DocTag(doc, "var");
        while (!Eof)
        {
            if (Cur.Kind == TokKind.Variable)
            {
                var m = new PhpMember
                {
                    Name = Cur.Text[1..], Kind = PhpMemberKind.Property, Visibility = VisibilityOf(mods),
                    IsStatic = mods.Contains("static"), IsReadonly = mods.Contains("readonly"),
                    Type = type, DocType = _info.ResolveType(docVar), DeclaringClass = cls.Fqn,
                    File = _info.Path, Offset = Cur.Start, Summary = DocSummary(doc),
                    IsDeprecated = doc?.Contains("@deprecated") == true,
                };
                cls.Members.Add(m);
                _p++;
                // default value / property hooks
                int d = 0;
                while (!Eof)
                {
                    var t = Cur;
                    if (t.Is("(") || t.Is("[") || t.Is("{")) d++;
                    else if (t.Is(")") || t.Is("]") || t.Is("}"))
                    {
                        if (d == 0) return;
                        d--;
                        if (d == 0 && t.Is("}") && !Peek().Is(";") && !Peek().Is(",")) { _p++; return; } // hooks block
                    }
                    else if (d == 0 && (t.Is(",") || t.Is(";"))) break;
                    _p++;
                }
                if (Cur.Is(",")) { _p++; continue; }
                if (Cur.Is(";")) _p++;
                return;
            }
            if (Cur.Is(";")) { _p++; return; }
            if (Cur.Is("}") || Cur.IsWord("function")) return;
            _p++;
        }
    }

    private void ParseMethod(PhpClass cls, HashSet<string> mods, string? doc)
    {
        _p++; // function
        if (Cur.Is("&")) _p++;
        if (Cur.Kind != TokKind.Identifier) return;
        var m = new PhpMember
        {
            Name = Cur.Text, Kind = PhpMemberKind.Method, Visibility = VisibilityOf(mods),
            IsStatic = mods.Contains("static"), IsAbstract = mods.Contains("abstract") || cls.Kind == PhpClassKind.Interface,
            IsFinal = mods.Contains("final"), DeclaringClass = cls.Fqn, File = _info.Path, Offset = Cur.Start,
            Summary = DocSummary(doc), IsDeprecated = doc?.Contains("@deprecated") == true,
        };
        _p++;
        m.Parameters = ParseParameters(doc, out var promoted);
        if (Cur.Is(":")) { _p++; m.Type = ReadType(); }
        m.DocType = _info.ResolveType(DocTag(doc, "return"));
        cls.Members.Add(m);

        foreach (var pp in promoted)
        {
            cls.Members.Add(new PhpMember
            {
                Name = pp.param.Name, Kind = PhpMemberKind.Property, Visibility = pp.vis, Type = pp.param.Type,
                IsReadonly = pp.isReadonly, DeclaringClass = cls.Fqn, File = _info.Path, Offset = pp.offset,
                DocType = _info.ResolveType(DocParam(doc, pp.param.Name)),
            });
        }

        // body
        if (Cur.Is("{")) SkipBalanced("{", "}");
        else if (Cur.Is(";")) _p++;
    }

    private List<PhpParameter> ParseParameters(string? doc, out List<(PhpParameter param, Visibility vis, bool isReadonly, int offset)> promoted)
    {
        var list = new List<PhpParameter>();
        promoted = new();
        if (!Cur.Is("(")) return list;
        _p++;
        while (!Eof && !Cur.Is(")"))
        {
            var p = new PhpParameter();
            Visibility? vis = null;
            bool ro = false;
            // modifiers / attributes
            while (!Eof)
            {
                if (Cur.Is("#[")) { SkipBalanced("#[", "]"); continue; }
                if (Cur.IsWord("public")) { vis = Visibility.Public; _p++; continue; }
                if (Cur.IsWord("protected")) { vis = Visibility.Protected; _p++; continue; }
                if (Cur.IsWord("private")) { vis = Visibility.Private; _p++; continue; }
                if (Cur.IsWord("readonly")) { ro = true; vis ??= Visibility.Public; _p++; continue; }
                if (Cur.Is("(") && Peek().IsWord("set")) { SkipBalanced("(", ")"); continue; }
                break;
            }
            if (Cur.Kind != TokKind.Variable && !Cur.Is("&") && !Cur.Is("...")) p.Type = ReadType();
            if (Cur.Is("&")) { p.IsByRef = true; _p++; }
            if (Cur.Is("...")) { p.IsVariadic = true; _p++; }
            int offset = Cur.Start;
            if (Cur.Kind == TokKind.Variable) { p.Name = Cur.Text[1..]; _p++; }
            if (Cur.Is("="))
            {
                _p++;
                var sb = new StringBuilder();
                int d = 0;
                while (!Eof)
                {
                    var t = Cur;
                    if (t.Is("(") || t.Is("[") || t.Is("{")) d++;
                    else if (t.Is(")") || t.Is("]") || t.Is("}")) { if (d == 0) break; d--; }
                    else if (t.Is(",") && d == 0) break;
                    sb.Append(t.Text);
                    _p++;
                }
                p.Default = sb.ToString();
            }
            if (p.Type == null)
            {
                var dt = DocParam(doc, p.Name);
                if (dt != null) p.Type = _info.ResolveType(dt);
            }
            if (p.Name.Length > 0)
            {
                list.Add(p);
                if (vis != null) { p.IsPromoted = true; promoted.Add((p, vis.Value, ro, offset)); }
            }
            if (Cur.Is(",")) { _p++; continue; }
            if (!Cur.Is(")")) _p++;
        }
        if (Cur.Is(")")) _p++;
        return list;
    }

    private void ParseFunction(string? doc)
    {
        _p++;
        if (Cur.Is("&")) _p++;
        var f = new PhpFunction { Name = Cur.Text, Fqn = Join(_info.Namespace, Cur.Text), File = _info.Path, Offset = Cur.Start, Summary = DocSummary(doc) };
        _p++;
        f.Parameters = ParseParameters(doc, out _);
        if (Cur.Is(":")) { _p++; f.ReturnType = ReadType(); }
        f.DocReturnType = _info.ResolveType(DocTag(doc, "return"));
        _info.Functions.Add(f);
        if (Cur.Is("{")) SkipBalanced("{", "}");
    }

    // ---------------- Doc comments ----------------

    private static readonly Regex TagRegex = new(@"@(?<tag>[\w-]+)\s+(?<rest>[^\r\n*]*)", RegexOptions.Compiled);

    internal static string? DocTag(string? doc, string tag)
    {
        if (doc == null) return null;
        foreach (Match m in TagRegex.Matches(doc))
        {
            var t = m.Groups["tag"].Value;
            if (t == tag || t == "phpstan-" + tag || t == "psalm-" + tag)
            {
                var type = ReadDocType(m.Groups["rest"].Value);
                if (type != null) return type;
            }
        }
        return null;
    }

    private static string? DocParam(string? doc, string name)
    {
        if (doc == null) return null;
        foreach (Match m in TagRegex.Matches(doc))
        {
            if (m.Groups["tag"].Value != "param") continue;
            var rest = m.Groups["rest"].Value;
            var type = ReadDocType(rest);
            if (type != null && rest.Contains("$" + name) && Regex.IsMatch(rest, @"\$" + Regex.Escape(name) + @"\b")) return type;
        }
        return null;
    }

    /// <summary>Reads a PHPDoc type at the beginning of a string, supporting generics.</summary>
    internal static string? ReadDocType(string s)
    {
        s = s.TrimStart();
        if (s.Length == 0 || s[0] == '$') return null;
        int depth = 0, i = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '<' or '(' or '{' or '[') depth++;
            else if (c is '>' or ')' or '}' or ']') depth--;
            else if (char.IsWhiteSpace(c) && depth <= 0) break;
        }
        var type = s[..i].Trim();
        return type.Length == 0 ? null : type;
    }

    private static string? DocSummary(string? doc)
    {
        if (doc == null) return null;
        var lines = doc.Split('\n')
            .Select(l => l.Trim().TrimStart('/').TrimStart('*').TrimEnd('/').TrimEnd('*').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("@"));
        var summary = string.Join(" ", lines.TakeWhile(l => l.Length > 0)).Trim();
        return summary.Length == 0 ? null : summary.Length > 300 ? summary[..300] + "…" : summary;
    }

    private static readonly Regex MagicProperty = new(@"@property(?:-read|-write)?\s+(?<type>\S+)\s+\$(?<name>\w+)", RegexOptions.Compiled);
    private static readonly Regex MagicMethod = new(@"@method\s+(?<static>static\s+)?(?:(?<type>[^\s(]+)\s+)?(?<name>\w+)\((?<args>[^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex Mixin = new(@"@mixin\s+(?<type>[\\\w]+)", RegexOptions.Compiled);

    private void ApplyClassDoc(PhpClass cls, string? doc)
    {
        if (doc == null) return;
        cls.Summary = DocSummary(doc);
        cls.IsDeprecated = doc.Contains("@deprecated");
        foreach (Match m in MagicProperty.Matches(doc))
            cls.Members.Add(new PhpMember
            {
                Name = m.Groups["name"].Value, Kind = PhpMemberKind.Property, IsMagic = true,
                DocType = _info.ResolveType(m.Groups["type"].Value), DeclaringClass = cls.Fqn, File = _info.Path, Offset = cls.Offset,
            });
        foreach (Match m in MagicMethod.Matches(doc))
        {
            var member = new PhpMember
            {
                Name = m.Groups["name"].Value, Kind = PhpMemberKind.Method, IsMagic = true,
                IsStatic = m.Groups["static"].Success, DocType = _info.ResolveType(m.Groups["type"].Value),
                DeclaringClass = cls.Fqn, File = _info.Path, Offset = cls.Offset,
            };
            foreach (var arg in m.Groups["args"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var a = arg.Trim();
                var dollar = a.IndexOf('$');
                if (dollar < 0) continue;
                var nm = new string(a[(dollar + 1)..].TakeWhile(PhpLexer.IsIdentPart).ToArray());
                var ty = a[..dollar].Trim();
                member.Parameters.Add(new PhpParameter { Name = nm, Type = ty.Length > 0 ? _info.ResolveType(ty) : null });
            }
            cls.Members.Add(member);
        }
        foreach (Match m in Mixin.Matches(doc)) cls.Mixins.Add(_info.ResolveClass(m.Groups["type"].Value));
    }
}
