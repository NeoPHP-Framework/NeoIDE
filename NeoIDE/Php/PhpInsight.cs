namespace NeoIDE.Php;

public sealed record SymbolTarget(string? File, int Offset, string Description);

/// <summary>Abstract / interface methods a concrete class does not implement yet.</summary>
public sealed record MissingMethods(string ClassName, string ClassFqn, int NameOffset, int NameLength, int BodyEnd, List<PhpMember> Methods);

/// <summary>Go to declaration, quick documentation and unresolved class detection.</summary>
public sealed class PhpInsight
{
    private readonly PhpIndex _index;
    public PhpInsight(PhpIndex index) => _index = index;

    private static (int start, int end) WordAt(string text, int offset)
    {
        int s = offset, e = offset;
        while (s > 0 && (PhpLexer.IsIdentPart(text[s - 1]) || text[s - 1] == '\\')) s--;
        while (e < text.Length && (PhpLexer.IsIdentPart(text[e]) || text[e] == '\\')) e++;
        if (s > 0 && text[s - 1] == '$') s--;
        return (s, e);
    }

    /// <summary>Finds the symbol under the given offset.</summary>
    public SymbolTarget? Resolve(string text, int offset, string? path)
    {
        if (offset < 0 || offset > text.Length) return null;
        var (s, e) = WordAt(text, offset);
        if (e <= s) return null;
        var word = text[s..e];
        var info = PhpParser.Parse(text, path);
        _index.UpdateFromBuffer(info, path);

        // inside a string / comment / html → nothing
        var all = PhpLexer.Tokenize(text, e);
        var tokAt = all.LastOrDefault(t => t.Start <= s);
        if (tokAt.Text != null && tokAt.Kind is TokKind.String or TokKind.Comment or TokKind.InlineHtml && tokAt.End >= e) return null;

        var sig = PhpLexer.Significant(PhpLexer.Tokenize(text, s));
        var resolver = new TypeResolver(_index, info, sig, s);
        var prev = sig.Count > 0 ? sig[^1] : default;

        // ---- member: $x->name / Foo::name
        if (prev.Text != null && (prev.Is("->") || prev.Is("?->") || prev.Is("::")))
        {
            var receiver = resolver.ResolveChainEndingAt(sig.Count - 1);
            var isCall = NextNonSpace(text, e) == '(';
            var name = word.TrimStart('$');
            foreach (var cls in PhpTypes.ClassNames(receiver, resolver.CurrentClass?.Fqn))
            {
                ResolvedMember? rm = prev.Is("::")
                    ? isCall ? _index.FindMember(cls, name, PhpMemberKind.Method)
                      : word.StartsWith("$") ? _index.FindMember(cls, name, PhpMemberKind.Property)
                      : _index.FindMember(cls, name, PhpMemberKind.Constant)
                    : isCall ? _index.FindMember(cls, name, PhpMemberKind.Method) : _index.FindMember(cls, name, PhpMemberKind.Property);
                if (rm != null) return new SymbolTarget(rm.Value.Member.File, rm.Value.Member.Offset, DescribeMember(rm.Value.Member));
            }
            return null;
        }

        // ---- variable: show its inferred type
        if (word.StartsWith("$"))
        {
            if (word == "$this") return resolver.CurrentClass is { } c ? new SymbolTarget(c.File, c.Offset, DescribeClass(c)) : null;
            var t = resolver.InferVariable(word, sig.Count);
            // declaration: first occurrence in scope
            int scope = resolver.FunctionScopeStart(sig.Count);
            var first = sig.Skip(scope).FirstOrDefault(x => x.Kind == TokKind.Variable && x.Text == word);
            return new SymbolTarget(path, first.Text != null ? first.Start : s, $"{word}: {PhpTypes.Short(t) switch { "" => "mixed", var x => x }}");
        }

        var next = NextNonSpace(text, e);
        var lower = word.ToLowerInvariant();
        if (lower is "self" or "static" && resolver.CurrentClass != null) return new SymbolTarget(resolver.CurrentClass.File, resolver.CurrentClass.Offset, DescribeClass(resolver.CurrentClass));
        if (lower == "parent" && resolver.CurrentClass?.Parent is { } parentName && _index.GetClass(parentName) is { } parentCls)
            return new SymbolTarget(parentCls.File, parentCls.Offset, DescribeClass(parentCls));

        // ---- function call
        if (next == '(' && !(prev.Text != null && prev.IsWord("new")) && !(prev.Text != null && prev.IsWord("function")))
        {
            var f = _index.GetFunction(word, info.Namespace);
            if (f != null) return new SymbolTarget(f.File, f.Offset, $"function {f.Name}{f.Signature()}{(f.EffectiveReturn != null ? ": " + PhpTypes.Short(f.EffectiveReturn) : "")}\n\n{f.Summary}");
        }

        // ---- class reference (also inside use statements)
        var fqn = IsInUseStatement(info, s) ? word.TrimStart('\\') : info.ResolveClass(word);
        var cls2 = _index.GetClass(fqn);
        if (cls2 != null) return new SymbolTarget(cls2.File, cls2.Offset, DescribeClass(cls2));
        return null;
    }

    private static bool IsInUseStatement(PhpFileInfo info, int offset) =>
        info.Uses.Any(u => offset >= u.StatementStart && offset < u.StatementEnd);

    private static char NextNonSpace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i < text.Length ? text[i] : '\0';
    }

    public static string DescribeClass(PhpClass c)
    {
        var s = $"{c.KindLabel} {c.Fqn}";
        if (c.Parent != null) s += $"\nextends {c.Parent}";
        if (c.Interfaces.Count > 0 && c.Kind != PhpClassKind.Enum) s += $"\nimplements {string.Join(", ", c.Interfaces)}";
        if (!string.IsNullOrEmpty(c.Summary)) s += "\n\n" + c.Summary;
        return s;
    }

    public static string DescribeMember(PhpMember m)
    {
        var vis = m.Visibility.ToString().ToLowerInvariant();
        var s = m.Kind switch
        {
            PhpMemberKind.Method => $"{vis}{(m.IsStatic ? " static" : "")} function {m.Name}{m.Signature()}{(m.EffectiveType != null ? ": " + PhpTypes.Short(m.EffectiveType) : "")}",
            PhpMemberKind.Property => $"{vis}{(m.IsStatic ? " static" : "")}{(m.IsReadonly ? " readonly" : "")} {PhpTypes.Short(m.EffectiveType)} ${m.Name}",
            PhpMemberKind.EnumCase => $"case {m.Name}",
            _ => $"const {m.Name}",
        };
        s = m.DeclaringClass + "\n" + s;
        if (!string.IsNullOrEmpty(m.Summary)) s += "\n\n" + m.Summary;
        return s;
    }

    /// <summary>Class names used in the file that are neither imported nor declared in the current namespace.</summary>
    public List<(int start, int length, string name, List<PhpClass> candidates)> FindUnresolvedClasses(string text, PhpFileInfo info)
    {
        var list = new List<(int, int, string, List<PhpClass>)>();
        if (!_index.IsReady) return list;
        var sig = PhpLexer.Significant(PhpLexer.Tokenize(text));
        for (int i = 0; i < sig.Count; i++)
        {
            var t = sig[i];
            if (t.Kind != TokKind.Identifier || t.Text.Contains('\\') || t.Text.Length == 0 || !char.IsUpper(t.Text[0])) continue;
            if (IsInUseStatement(info, t.Start)) continue;
            var prev = i > 0 ? sig[i - 1] : default;
            var next = i + 1 < sig.Count ? sig[i + 1] : default;
            if (prev.Text != null && (prev.Is("->") || prev.Is("?->") || prev.Is("::") || prev.IsWord("function") || prev.IsWord("const")
                || prev.IsWord("class") || prev.IsWord("interface") || prev.IsWord("trait") || prev.IsWord("enum") || prev.IsWord("namespace") || prev.IsWord("case") || prev.IsWord("goto")))
                continue;
            if (next.Text != null && next.Is("(") && !(prev.Text != null && (prev.IsWord("new") || prev.Is("#[") || prev.Is(",")))) continue; // function call
            if (next.Text != null && next.Is(":") && prev.Text != null && (prev.Is("(") || prev.Is(","))) continue; // named argument
            bool classPosition = (next.Text != null && (next.Is("::") || next.Kind == TokKind.Variable || next.Is("|") || next.Is("{") && prev.Text != null && (prev.IsWord("extends") || prev.IsWord("implements"))))
                || (prev.Text != null && (prev.IsWord("new") || prev.IsWord("extends") || prev.IsWord("implements") || prev.IsWord("instanceof") || prev.Is("#[") || prev.Is(":") || prev.Is("?") || prev.Is("|") || prev.IsWord("use") || prev.Is(",") && IsInImplementsOrCatch(sig, i)));
            if (!classPosition) continue;
            if (PhpTypes.Builtin.Contains(t.Text)) continue;
            var fqn = info.ResolveClass(t.Text);
            if (_index.GetClass(fqn) != null) continue;
            if (info.Classes.Any(c => c.Name == t.Text)) continue;
            var candidates = _index.FindByShortName(t.Text);
            list.Add((t.Start, t.Text.Length, t.Text, candidates));
        }
        return list;
    }

    /// <summary>Concrete classes of the file that miss methods of their interfaces / abstract parents.</summary>
    public List<MissingMethods> FindMissingImplementations(PhpFileInfo info)
    {
        var list = new List<MissingMethods>();
        if (!_index.IsReady) return list;
        foreach (var cls in info.Classes)
        {
            if (cls.Kind != PhpClassKind.Class || cls.IsAbstract) continue;
            if (cls.Parent == null && cls.Interfaces.Count == 0) continue;
            // GetMembers shadows parent/interface declarations by the first implementation found (class, traits, parents)
            var missing = _index.GetMembers(cls.Fqn)
                .Where(r => r.Member.Kind == PhpMemberKind.Method && r.Member.IsAbstract && !r.Member.IsMagic)
                .Select(r => r.Member)
                .ToList();
            if (missing.Count > 0)
                list.Add(new MissingMethods(cls.Name, cls.Fqn, cls.Offset, cls.Name.Length, cls.BodyEnd, missing));
        }
        return list;
    }

    /// <summary>
    /// PHP source of method stubs implementing <paramref name="methods"/>. Class names are written through
    /// <paramref name="classRef"/> (which imports them) so the generated code uses short names.
    /// </summary>
    public static string BuildStubs(IEnumerable<PhpMember> methods, Func<string, string> classRef, string indent, string nl)
    {
        string Type(string? t)
        {
            if (string.IsNullOrEmpty(t)) return "";
            return System.Text.RegularExpressions.Regex.Replace(t, @"\\?([A-Za-z_][\w]*(?:\\[A-Za-z_]\w*)+|[A-Z]\w*)", m =>
            {
                var name = m.Groups[1].Value;
                if (PhpTypes.Builtin.Contains(name)) return name;
                return classRef(name.TrimStart('\\'));
            });
        }
        var sb = new System.Text.StringBuilder();
        foreach (var m in methods)
        {
            var vis = m.Visibility == Visibility.Protected ? "protected" : "public";
            var parameters = string.Join(", ", m.Parameters.Select(p =>
            {
                var s = "";
                if (!string.IsNullOrEmpty(p.Type)) s += Type(p.Type) + " ";
                if (p.IsByRef) s += "&";
                if (p.IsVariadic) s += "...";
                s += "$" + p.Name;
                if (p.Default != null && !p.IsVariadic) s += " = " + (p.Default == "…" ? "null" : p.Default);
                return s;
            }));
            var ret = string.IsNullOrEmpty(m.Type) ? "" : ": " + Type(m.Type);
            sb.Append(nl);
            sb.Append(indent).Append(vis).Append(m.IsStatic ? " static" : "").Append(" function ").Append(m.Name)
              .Append('(').Append(parameters).Append(')').Append(ret).Append(nl);
            sb.Append(indent).Append('{').Append(nl);
            sb.Append(indent).Append(indent).Append("// TODO: Implement ").Append(m.Name).Append("() method.").Append(nl);
            sb.Append(indent).Append('}').Append(nl);
        }
        return sb.ToString();
    }

    private static bool IsInImplementsOrCatch(List<Token> sig, int i)
    {
        for (int k = i - 1; k >= 0 && k > i - 20; k--)
        {
            if (sig[k].Is("{") || sig[k].Is(";")) return false;
            if (sig[k].IsWord("implements")) return true;
        }
        return false;
    }
}
