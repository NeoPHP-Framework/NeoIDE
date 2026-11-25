namespace NeoIDE.Php;

/// <summary>
/// Infers the type of PHP expressions such as <c>$this->repo->find(1)-></c> from the index,
/// variable assignments, parameters, @var annotations, foreach and catch clauses.
/// </summary>
public sealed class TypeResolver
{
    private readonly PhpIndex _index;
    private readonly PhpFileInfo _file;
    private readonly List<Token> _tok;     // significant tokens (with doc comments)
    private readonly PhpClass? _class;
    private int _guard;

    public TypeResolver(PhpIndex index, PhpFileInfo file, List<Token> significantTokens, int offset)
    {
        _index = index;
        _file = file;
        _tok = significantTokens;
        _class = file.ClassAt(offset);
    }

    public PhpClass? CurrentClass => _class;
    public List<Token> Tokens => _tok;

    private static readonly HashSet<string> NonCallKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "elseif", "while", "for", "foreach", "switch", "match", "return", "echo", "print", "array", "list",
        "isset", "empty", "unset", "catch", "function", "fn", "and", "or", "xor", "new", "clone", "throw", "use", "yield",
    };

    private enum SegKind { Var, Name, Call, New, Index, Group }

    private sealed class Seg
    {
        public SegKind Kind;
        public string Text = "";
        public string? Op;            // operator that follows this segment ("->" or "::")
        public string? GroupType;     // for parenthesised expressions
    }

    private static string? Union(IEnumerable<string?> types)
    {
        var parts = types.Where(t => !string.IsNullOrEmpty(t)).SelectMany(t => t!.Split('|')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return parts.Count == 0 ? null : string.Join("|", parts);
    }

    private int MatchOpen(int close, string open, string closeText)
    {
        int d = 0;
        for (int i = close; i >= 0; i--)
        {
            if (_tok[i].Is(closeText)) d++;
            else if (_tok[i].Is(open) || (open == "[" && _tok[i].Is("#["))) { d--; if (d == 0) return i; }
        }
        return -1;
    }

    /// <summary>Type of the expression made of tokens [from, to).</summary>
    public string? ResolveRange(int from, int to)
    {
        if (to <= from || to > _tok.Count) return null;
        if (++_guard > 60) return null;
        try
        {
            // new Foo(...)  as a whole expression
            if (_tok[from].IsWord("new") && from + 1 < to && _tok[from + 1].Kind == TokKind.Identifier)
            {
                // only if nothing chained after the constructor call
                int i = from + 2;
                if (i >= to) return "\\" + ResolveClassName(_tok[from + 1].Text);
                if (_tok[i].Is("("))
                {
                    int d = 0, j = i;
                    for (; j < to; j++)
                    {
                        if (_tok[j].Is("(")) d++;
                        else if (_tok[j].Is(")")) { d--; if (d == 0) break; }
                    }
                    if (j >= to - 1) return "\\" + ResolveClassName(_tok[from + 1].Text);
                }
            }
            // a ?? b, a ?: b, cond ? a : b  -> use the last operand
            for (int k = to - 1, depth = 0; k >= from; k--)
            {
                var t = _tok[k];
                if (t.Is(")") || t.Is("]") || t.Is("}")) depth++;
                else if (t.Is("(") || t.Is("[") || t.Is("{")) depth--;
                else if (depth == 0 && (t.Is("??") || t.Is(":") || t.Is("=")))
                    return ResolveChainEndingAt(to, k + 1);
            }
            return ResolveChainEndingAt(to, from);
        }
        finally { _guard--; }
    }

    /// <summary>Resolves the member-access chain that ends right before token index <paramref name="end"/>.</summary>
    public string? ResolveChainEndingAt(int end, int limit = 0)
    {
        var segs = new List<Seg>();
        int i = end - 1;
        while (i >= limit)
        {
            var t = _tok[i];
            Seg seg;
            if (t.Is(")"))
            {
                int j = MatchOpen(i, "(", ")");
                if (j < 0) return null;
                if (j - 1 >= limit && _tok[j - 1].Kind == TokKind.Identifier && !NonCallKeywords.Contains(_tok[j - 1].Text))
                {
                    if (j - 2 >= limit && _tok[j - 2].IsWord("new"))
                    {
                        seg = new Seg { Kind = SegKind.New, Text = _tok[j - 1].Text };
                        i = j - 3;
                    }
                    else
                    {
                        seg = new Seg { Kind = SegKind.Call, Text = _tok[j - 1].Text };
                        i = j - 2;
                    }
                }
                else if (j - 1 >= limit && _tok[j - 1].Kind == TokKind.Variable)
                {
                    return null; // $callable()
                }
                else
                {
                    seg = new Seg { Kind = SegKind.Group, GroupType = ResolveRange(j + 1, i) };
                    i = j - 1;
                }
            }
            else if (t.Is("]"))
            {
                int j = MatchOpen(i, "[", "]");
                if (j < 0) return null;
                seg = new Seg { Kind = SegKind.Index };
                i = j - 1;
                segs.Add(seg);
                continue; // index applies to the expression before it, no operator in between
            }
            else if (t.Kind == TokKind.Identifier)
            {
                seg = new Seg { Kind = SegKind.Name, Text = t.Text };
                i--;
            }
            else if (t.Kind == TokKind.Variable)
            {
                seg = new Seg { Kind = SegKind.Var, Text = t.Text };
                i--;
            }
            else return null;

            segs.Add(seg);
            if (i >= limit && (_tok[i].Is("->") || _tok[i].Is("?->"))) { seg.Op = "->"; i--; continue; }
            if (i >= limit && _tok[i].Is("::")) { seg.Op = "::"; i--; continue; }
            break;
        }
        if (segs.Count == 0) return null;
        segs.Reverse();

        // ----- operators belong to the segment *before*, re-align: op of seg k means "seg k+1 is accessed through op"
        // (we stored op on the segment that follows it while walking backwards)
        string? type = null;
        string? receiverClass = null;
        for (int k = 0; k < segs.Count; k++)
        {
            var s = segs[k];
            var op = s.Op; // operator between previous segment and this one
            if (k == 0)
            {
                // the operator following the base: the next segment's, or the token at "end" (the completion operator)
                var next = segs.Count > 1 ? segs[1]
                    : end < _tok.Count && _tok[end].Is("::") ? new Seg { Op = "::" } : null;
                type = ResolveBase(s, next, end);
                continue;
            }
            if (s.Kind == SegKind.Index)
            {
                type = ElementType(type);
                continue;
            }
            var classes = PhpTypes.ClassNames(type, _class?.Fqn);
            var results = new List<string?>();
            foreach (var cls in classes)
            {
                receiverClass = cls;
                var name = s.Kind == SegKind.Var ? s.Text.TrimStart('$') : s.Text;
                ResolvedMember? rm;
                if (op == "::")
                {
                    if (s.Kind == SegKind.Name && name.Equals("class", StringComparison.OrdinalIgnoreCase)) { results.Add("string"); continue; }
                    rm = s.Kind switch
                    {
                        SegKind.Call => _index.FindMember(cls, name, PhpMemberKind.Method),
                        SegKind.Var => _index.FindMember(cls, name, PhpMemberKind.Property),
                        _ => _index.FindMember(cls, name, PhpMemberKind.Constant),
                    };
                }
                else
                {
                    rm = s.Kind == SegKind.Call
                        ? _index.FindMember(cls, name, PhpMemberKind.Method)
                        : _index.FindMember(cls, name, PhpMemberKind.Property);
                    if (rm == null && s.Kind == SegKind.Call)
                    {
                        // __call magic: unknown method, keep fluent chains alive
                        if (_index.FindMember(cls, "__call", PhpMemberKind.Method) is { } magic) rm = magic;
                    }
                }
                if (rm == null) continue;
                var m = rm.Value.Member;
                var mt = m.Kind == PhpMemberKind.EnumCase ? "\\" + m.DeclaringClass : m.EffectiveType;
                results.Add(Bind(mt, cls, m.DeclaringClass));
            }
            type = Union(results);
        }
        return type;
    }

    /// <summary>Replaces static/self/$this in a member type with concrete classes.</summary>
    private static string? Bind(string? type, string receiver, string declaring)
    {
        if (type == null) return null;
        var parts = type.Split('|').Select(p =>
        {
            var nullable = p.StartsWith("?");
            var t = p.TrimStart('?');
            t = t switch
            {
                "static" or "$this" => "\\" + receiver,
                "self" => "\\" + declaring,
                _ => t,
            };
            return (nullable ? "?" : "") + t;
        });
        return string.Join("|", parts);
    }

    private string? ElementType(string? type)
    {
        var els = PhpTypes.ClassNames(type, _class?.Fqn, elements: true);
        if (els.Count > 0) return string.Join("|", els.Select(e => "\\" + e));
        // ArrayAccess / Traversable objects: offsetGet / current return types
        var res = new List<string?>();
        foreach (var c in PhpTypes.ClassNames(type, _class?.Fqn))
        {
            var m = _index.FindMember(c, "offsetGet", PhpMemberKind.Method) ?? _index.FindMember(c, "current", PhpMemberKind.Method);
            if (m != null) res.Add(Bind(m.Value.Member.EffectiveType, c, m.Value.Member.DeclaringClass));
        }
        return Union(res);
    }

    private string? ResolveBase(Seg s, Seg? next, int end)
    {
        switch (s.Kind)
        {
            case SegKind.Group:
                return s.GroupType;
            case SegKind.New:
                return "\\" + ResolveClassName(s.Text);
            case SegKind.Var:
                if (s.Text == "$this") return _class != null ? "\\" + _class.Fqn : InferVariable("$this", end); // templates: /** @var Template $this */
                if (next?.Op == "::") return null; // $obj::CONST - rare
                return InferVariable(s.Text, end);
            case SegKind.Name:
                // Class reference before "::"
                if (next != null && next.Op == "::")
                {
                    var lower = s.Text.ToLowerInvariant();
                    if (lower is "self" or "static") return _class != null ? "\\" + _class.Fqn : null;
                    if (lower == "parent") return _class?.Parent != null ? "\\" + _class.Parent : null;
                    return "\\" + ResolveClassName(s.Text);
                }
                return null; // bare constant
            case SegKind.Call:
                var f = _index.GetFunction(s.Text, _file.Namespace);
                return f?.EffectiveReturn;
        }
        return null;
    }

    public string ResolveClassName(string name) => _file.ResolveClass(name);

    // ------------------------------------------------------------------ variables

    /// <summary>Index of the first token of the innermost function containing <paramref name="end"/> (0 for global scope).</summary>
    public int FunctionScopeStart(int end)
    {
        var stack = new Stack<(bool isFunc, int funcIndex)>();
        int pendingFunc = -1;
        for (int i = 0; i < end && i < _tok.Count; i++)
        {
            var t = _tok[i];
            if (t.IsWord("function") || t.IsWord("fn")) pendingFunc = i;
            else if (t.Is("{"))
            {
                stack.Push((pendingFunc >= 0, pendingFunc));
                pendingFunc = -1;
            }
            else if (t.Is("}")) { if (stack.Count > 0) stack.Pop(); }
            else if (t.Is(";")) pendingFunc = -1;
        }
        foreach (var frame in stack) if (frame.isFunc) return frame.funcIndex; // innermost first
        return pendingFunc >= 0 ? pendingFunc : 0;
    }

    public string? InferVariable(string varName, int end)
    {
        if (++_guard > 40) { _guard--; return null; }
        try
        {
            int start = FunctionScopeStart(end);
            string? type = null;
            string? pendingDocVar = null;

            for (int i = start; i < end && i < _tok.Count; i++)
            {
                var t = _tok[i];

                if (t.Kind == TokKind.DocComment)
                {
                    // /** @var Foo $var */  or  /** @var Foo */ followed by assignment
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                                 t.Text, @"@(?:phpstan-|psalm-)?var\s+(?<type>[^\s*]+)(?:\s+(?<name>\$\w+))?"))
                    {
                        var nm = m.Groups["name"].Success ? m.Groups["name"].Value : null;
                        if (nm == varName) type = _file.ResolveType(PhpParser.ReadDocType(m.Groups["type"].Value));
                        else if (nm == null) pendingDocVar = m.Groups["type"].Value;
                    }
                    continue;
                }

                if (t.Kind == TokKind.Variable && t.Text == varName)
                {
                    // parameter: (Type $var  /  , Type $var
                    if (i > 0 && IsParameterPosition(i, out var paramType))
                    {
                        if (paramType != null) type = paramType;
                        pendingDocVar = null;
                        continue;
                    }
                    // assignment
                    if (i + 1 < _tok.Count && _tok[i + 1].Is("=") && i + 1 < end)
                    {
                        if (pendingDocVar != null)
                        {
                            type = _file.ResolveType(PhpParser.ReadDocType(pendingDocVar));
                            pendingDocVar = null;
                            continue;
                        }
                        int rhsStart = i + 2, rhsEnd = rhsStart, d = 0;
                        while (rhsEnd < end && rhsEnd < _tok.Count)
                        {
                            var r = _tok[rhsEnd];
                            if (r.Is("(") || r.Is("[") || r.Is("{")) d++;
                            else if (r.Is(")") || r.Is("]") || r.Is("}")) { if (d == 0) break; d--; }
                            else if ((r.Is(";") || r.Is(",")) && d == 0) break;
                            rhsEnd++;
                        }
                        if (rhsEnd > rhsStart)
                        {
                            var rhs = ResolveRange(rhsStart, rhsEnd);
                            if (rhs != null) type = rhs;
                        }
                        continue;
                    }
                    // foreach (... as $k => $var) / (... as $var)
                    if (i >= 1 && (_tok[i - 1].IsWord("as") || (_tok[i - 1].Is("=>") && i >= 3 && _tok[i - 3].IsWord("as"))))
                    {
                        int asIdx = _tok[i - 1].IsWord("as") ? i - 1 : i - 3;
                        int open = asIdx - 1, d = 0;
                        for (; open >= start; open--)
                        {
                            if (_tok[open].Is(")")) d++;
                            else if (_tok[open].Is("(")) { if (d == 0) break; d--; }
                        }
                        if (open > start && _tok[open - 1].IsWord("foreach"))
                        {
                            var coll = ResolveRange(open + 1, asIdx);
                            var el = ElementType(coll);
                            if (pendingDocVar != null) { el = _file.ResolveType(PhpParser.ReadDocType(pendingDocVar)); pendingDocVar = null; }
                            if (el != null) type = el;
                        }
                        continue;
                    }
                    // $var instanceof Foo
                    if (i + 2 < _tok.Count && _tok[i + 1].IsWord("instanceof") && _tok[i + 2].Kind == TokKind.Identifier)
                    {
                        type = "\\" + ResolveClassName(_tok[i + 2].Text);
                        continue;
                    }
                }
                if (t.Is(";")) pendingDocVar = null;
            }
            return type;
        }
        finally { _guard--; }
    }

    /// <summary>Is the variable at token i declared as a function/catch parameter? Returns its declared type.</summary>
    private bool IsParameterPosition(int i, out string? type)
    {
        type = null;
        // walk back over type tokens
        int j = i - 1;
        if (j >= 0 && (_tok[j].Is("...") || _tok[j].Is("&"))) j--;
        int typeEnd = j;
        while (j >= 0 && (_tok[j].Kind == TokKind.Identifier || _tok[j].Is("?") || _tok[j].Is("|") || (_tok[j].Is("&") && j > 0 && _tok[j - 1].Kind == TokKind.Identifier)))
        {
            if (_tok[j].IsWord("public") || _tok[j].IsWord("private") || _tok[j].IsWord("protected") || _tok[j].IsWord("readonly")) break;
            j--;
        }
        int typeStart = j + 1;
        // skip promotion modifiers / attributes
        int k = j;
        while (k >= 0 && (_tok[k].IsWord("public") || _tok[k].IsWord("private") || _tok[k].IsWord("protected") || _tok[k].IsWord("readonly"))) k--;
        if (k >= 0 && _tok[k].Is("]"))
        {
            int open = MatchOpen(k, "[", "]");
            if (open > 0) k = open - 1;
        }
        if (k < 0 || !(_tok[k].Is("(") || _tok[k].Is(","))) return false;

        // make sure we are inside a parameter list (function/fn/catch)
        int d = 0, p = k;
        if (_tok[k].Is(","))
        {
            for (p = k; p >= 0; p--)
            {
                if (_tok[p].Is(")") || _tok[p].Is("]")) d++;
                else if (_tok[p].Is("(") || _tok[p].Is("[")) { if (d == 0) break; d--; }
            }
            if (p < 0 || !_tok[p].Is("(")) return false;
        }
        var before = p - 1 >= 0 ? _tok[p - 1] : default;
        bool isDecl = before.IsWord("catch") || before.IsWord("fn") || before.IsWord("function") ||
                      (before.Kind == TokKind.Identifier && p - 2 >= 0 && (_tok[p - 2].IsWord("function") || _tok[p - 2].Is("&") && p - 3 >= 0 && _tok[p - 3].IsWord("function")));
        if (!isDecl) return false;

        if (typeStart <= typeEnd)
        {
            var raw = string.Concat(_tok.Skip(typeStart).Take(typeEnd - typeStart + 1).Select(x => x.Text));
            type = _file.ResolveType(raw);
        }
        // fallback to @param in the method doc
        if (type == null)
        {
            for (int q = p - 1; q >= 0 && q > p - 40; q--)
            {
                if (_tok[q].Kind == TokKind.DocComment)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(_tok[q].Text, @"@param\s+(?<type>[^\s*]+)\s+" + System.Text.RegularExpressions.Regex.Escape(_tok[i].Text) + @"\b");
                    if (m.Success) type = _file.ResolveType(PhpParser.ReadDocType(m.Groups["type"].Value));
                    break;
                }
                if (_tok[q].Is(";") || _tok[q].Is("}") || _tok[q].Is("{")) break;
            }
        }
        return true;
    }

    /// <summary>Variables visible at the end position (parameters, assignments, foreach, catch...).</summary>
    public List<string> VisibleVariables(int end)
    {
        int start = FunctionScopeStart(end);
        var set = new List<string>();
        for (int i = start; i < end && i < _tok.Count; i++)
        {
            var t = _tok[i];
            if (t.Kind != TokKind.Variable || t.Text == "$this") continue;
            if (!set.Contains(t.Text)) set.Add(t.Text);
        }
        return set;
    }
}
