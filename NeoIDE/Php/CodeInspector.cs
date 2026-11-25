using System.IO;
using System.Text.RegularExpressions;
using NeoIDE.Services;
using NeoIDE.Services.Diagnostics;

namespace NeoIDE.Php;

/// <summary>A symbol declared but never used (shown greyed).</summary>
public sealed record UnusedSymbol(int Start, int Length, string Message);

/// <summary>A member accessed but not declared (self::NOPE, $this->nope()…) with the names that may replace it.</summary>
public sealed record UndefinedMember(int Start, int Length, string Message, Severity Severity, IReadOnlyList<string> Suggestions);

/// <summary>Where a method is called.</summary>
public sealed record Usage(string File, int Offset, int Line, string Preview);

/// <summary>
/// Code inspections of a PHP file: unused private properties / methods / constants and unused parameters,
/// undefined constants / methods / properties of self, static, parent, $this and named classes, and the usages
/// of the methods in the project.
/// </summary>
public static class CodeInspector
{
    private static List<Token> Code(string text) =>
        PhpLexer.Significant(PhpLexer.Tokenize(text)).Where(t => t.Kind != TokKind.DocComment).ToList();

    // ------------------------------------------------------------------ unused

    public static List<UnusedSymbol> FindUnused(string text, PhpFileInfo info)
    {
        var list = new List<UnusedSymbol>();
        var tokens = Code(text);
        foreach (var cls in info.Classes.Where(c => c.Kind is PhpClassKind.Class or PhpClassKind.Enum && c.BodyEnd > c.BodyStart))
        {
            var body = tokens.Where(t => t.Start > cls.BodyStart && t.Start < cls.BodyEnd).ToList();
            var strings = string.Join("\n", body.Where(t => t.Kind == TokKind.String).Select(t => t.Text));
            foreach (var m in cls.Members.Where(m => m.Visibility == Visibility.Private && !m.IsMagic))
            {
                bool used;
                int length;
                string what;
                switch (m.Kind)
                {
                    case PhpMemberKind.Property:
                        length = m.Name.Length + 1;
                        what = $"Property '${m.Name}'";
                        used = Accessed(body, m.Name, method: false) || Regex.IsMatch(strings, @"->" + Regex.Escape(m.Name) + @"\b|::\$" + Regex.Escape(m.Name) + @"\b");
                        break;
                    case PhpMemberKind.Method:
                        if (m.Name.StartsWith("__")) continue;
                        length = m.Name.Length;
                        what = $"Method '{m.Name}()'";
                        used = Accessed(body, m.Name, method: true) || Regex.IsMatch(strings, @"^['""]" + Regex.Escape(m.Name) + @"['""]$|->" + Regex.Escape(m.Name) + @"\(", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                        break;
                    case PhpMemberKind.Constant:
                        length = m.Name.Length;
                        what = $"Constant '{m.Name}'";
                        used = body.Where((t, i) => t.Kind == TokKind.Identifier && t.Text == m.Name && i > 0 && body[i - 1].Is("::")).Any();
                        break;
                    default: continue;
                }
                if (used) continue;
                var start = m.Offset;
                var expected = m.Kind == PhpMemberKind.Property ? "$" + m.Name : m.Name;
                if (start < 0 || start + length > text.Length || text.Substring(start, length) != expected) continue;
                list.Add(new UnusedSymbol(start, length, $"{what} is never used"));
            }
        }
        list.AddRange(UnusedParameters(text, info, tokens));
        return list;
    }

    /// <summary>"->name" / "?->name" (property or method), "::name(" / "::$name" in the tokens of a class.</summary>
    private static bool Accessed(List<Token> body, string name, bool method)
    {
        for (int i = 1; i < body.Count; i++)
        {
            var t = body[i];
            var prev = body[i - 1];
            if (method)
            {
                if (t.Kind == TokKind.Identifier && string.Equals(t.Text, name, StringComparison.OrdinalIgnoreCase) && (prev.Is("->") || prev.Is("?->") || prev.Is("::"))) return true;
            }
            else
            {
                if (t.Kind == TokKind.Identifier && t.Text == name && (prev.Is("->") || prev.Is("?->"))) return true;
                if (t.Kind == TokKind.Variable && t.Text == "$" + name && prev.Is("::")) return true;
            }
        }
        return false;
    }

    private static readonly HashSet<string> Modifiers = new(StringComparer.OrdinalIgnoreCase) { "public", "protected", "private", "readonly" };

    /// <summary>Parameters of functions / methods / closures that their body never reads.</summary>
    private static IEnumerable<UnusedSymbol> UnusedParameters(string text, PhpFileInfo info, List<Token> t)
    {
        for (int i = 0; i < t.Count; i++)
        {
            if (!t[i].IsWord("function")) continue;
            int j = i + 1;
            if (j < t.Count && t[j].Is("&")) j++;
            string? name = null;
            if (j < t.Count && t[j].Kind == TokKind.Identifier) { name = t[j].Text; j++; }
            // closures are callbacks: their signature is imposed by the code calling them
            if (name == null || j >= t.Count || !t[j].Is("(")) continue;
            // parameters
            var parameters = new List<Token>();
            int depth = 0, k = j;
            int itemStart = j + 1;
            for (; k < t.Count; k++)
            {
                if (t[k].Is("(") || t[k].Is("[")) depth++;
                else if (t[k].Is(")") || t[k].Is("]")) { depth--; if (depth == 0) break; }
                else if (depth == 1 && t[k].Is(",")) itemStart = k + 1;
                else if (depth == 1 && t[k].Kind == TokKind.Variable && k + 1 < t.Count && (t[k + 1].Is(",") || t[k + 1].Is(")") || t[k + 1].Is("=")))
                {
                    bool promoted = false;
                    for (int x = itemStart; x < k; x++) if (t[x].Kind == TokKind.Identifier && Modifiers.Contains(t[x].Text)) promoted = true;
                    if (!promoted) parameters.Add(t[k]);
                }
            }
            if (k >= t.Count || parameters.Count == 0) continue;
            // body
            int b = k + 1;
            if (b < t.Count && t[b].IsWord("use"))
            {
                int d = 0;
                for (; b < t.Count; b++) { if (t[b].Is("(")) d++; else if (t[b].Is(")")) { d--; if (d == 0) { b++; break; } } }
            }
            while (b < t.Count && !t[b].Is("{") && !t[b].Is(";") && !t[b].Is("=>")) b++;
            if (b >= t.Count || !t[b].Is("{")) continue; // abstract / interface method
            int e = b, bd = 0;
            for (; e < t.Count; e++) { if (t[e].Is("{")) bd++; else if (t[e].Is("}")) { bd--; if (bd == 0) break; } }
            var body = t.Skip(b + 1).Take(Math.Max(0, e - b - 1)).ToList();
            // an empty hook / a default implementation ("{}", "return false;") is meant to be overridden
            if (!body.Any(x => x.Kind == TokKind.Variable && x.Text != "$this")) continue;
            if (body.Any(x => x.Kind == TokKind.Identifier && x.Text.ToLowerInvariant() is "func_get_args" or "get_defined_vars" or "compact" or "extract" or "func_get_arg") || body.Any(x => x.Is("$")))
                continue;
            // a method whose signature comes from a parent class / an interface keeps its parameters
            if (name != null && info.ClassAt(t[i].Start) is { } cls)
            {
                if (cls.Kind == PhpClassKind.Interface) continue;
                if (InheritedSignature(cls, name)) continue;
            }
            var interpolated = string.Join("\n", body.Where(x => x.Kind == TokKind.String && !x.Text.StartsWith('\'')).Select(x => x.Text));
            foreach (var p in parameters)
            {
                if (body.Any(x => x.Kind == TokKind.Variable && x.Text == p.Text)) continue;
                if (Regex.IsMatch(interpolated, Regex.Escape(p.Text) + @"\b")) continue;
                yield return new UnusedSymbol(p.Start, p.Text.Length, $"Parameter '{p.Text}' is never used");
            }
        }
    }

    private static bool InheritedSignature(PhpClass cls, string method)
    {
        var ancestors = new List<string>();
        if (cls.Parent != null) ancestors.Add(cls.Parent);
        ancestors.AddRange(cls.Interfaces);
        ancestors.AddRange(cls.Traits);
        foreach (var a in ancestors)
        {
            if (Workspace.Index.GetClass(a) == null) return true; // unknown parent: maybe
            if (Workspace.Index.FindMember(a, method, PhpMemberKind.Method) != null) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ undefined members

    public static List<UndefinedMember> FindUndefined(string text, PhpFileInfo info)
    {
        var list = new List<UndefinedMember>();
        var t = Code(text);
        for (int i = 1; i + 1 < t.Count; i++)
        {
            var op = t[i];
            bool isStatic = op.Is("::");
            if (!isStatic && !op.Is("->") && !op.Is("?->")) continue;
            var receiver = t[i - 1];
            var target = t[i + 1];
            string? fqn = null;
            var current = info.ClassAt(op.Start);
            if (isStatic && receiver.Kind == TokKind.Identifier)
            {
                // "new class" / "Foo::class" chains are not receivers; a "::" after ")" is a call result
                var w = receiver.Text.ToLowerInvariant();
                if (i >= 2 && (t[i - 2].Is("->") || t[i - 2].Is("?->") || t[i - 2].Is("::") || t[i - 2].IsWord("new") || t[i - 2].IsWord("function") || t[i - 2].IsWord("const"))) continue;
                if (w is "self" or "static") fqn = current?.Kind == PhpClassKind.Trait ? null : current?.Fqn;
                else if (w == "parent") fqn = current?.Parent;
                else if (!PhpTypes.Builtin.Contains(w)) fqn = info.ResolveClass(receiver.Text).TrimStart('\\');
            }
            else if (!isStatic && receiver.Kind == TokKind.Variable && receiver.Text == "$this" && current is { Kind: not PhpClassKind.Trait })
                fqn = current.Fqn;
            if (fqn == null) continue;
            var cls = Workspace.Index.GetClass(fqn);
            if (cls == null || !FullyKnown(fqn) || cls.IsBuiltin && cls.Members.Count == 0) continue;
            // members PHP gives to every enum
            if (cls.Kind == PhpClassKind.Enum && target.Text is "cases" or "from" or "tryFrom" or "name" or "value") continue;
            var members = Workspace.Index.GetMembers(fqn);
            bool Has(string magic) => members.Any(m => m.Member.Kind == PhpMemberKind.Method && string.Equals(m.Member.Name, magic, StringComparison.OrdinalIgnoreCase));
            var call = i + 2 < t.Count && t[i + 2].Is("(");
            var shortName = cls.Name;
            if (target.Kind == TokKind.Identifier)
            {
                if (isStatic && target.Text.Equals("class", StringComparison.OrdinalIgnoreCase)) continue;
                if (call)
                {
                    if (Workspace.Index.FindMember(fqn, target.Text, PhpMemberKind.Method) != null) continue;
                    if (Has(isStatic ? "__callStatic" : "__call") || isStatic && Has("__call") && receiver.Text is "parent" or "self" or "static") continue;
                    var names = members.Where(m => m.Member.Kind == PhpMemberKind.Method && !m.Member.Name.StartsWith("__")).Select(m => m.Member.Name);
                    var close = Twig(target.Text, names);
                    list.Add(new UndefinedMember(target.Start, target.Text.Length, $"Method '{target.Text}()' not found in {shortName}" + Hint(close), Severity.Error, close));
                }
                else if (isStatic)
                {
                    if (Workspace.Index.FindMember(fqn, target.Text, PhpMemberKind.Constant) != null) continue;
                    var names = members.Where(m => m.Member.Kind is PhpMemberKind.Constant or PhpMemberKind.EnumCase).Select(m => m.Member.Name);
                    var close = Twig(target.Text, names);
                    list.Add(new UndefinedMember(target.Start, target.Text.Length, $"Constant '{target.Text}' not found in {shortName}" + Hint(close), Severity.Error, close));
                }
                else
                {
                    if (Workspace.Index.FindMember(fqn, target.Text, PhpMemberKind.Property) != null) continue;
                    if (Has("__get") || Has("__set")) continue;
                    // a property created by an assignment in the class ($this->x = ...) is a dynamic property: accepted
                    if (Regex.IsMatch(text, @"\$this->" + Regex.Escape(target.Text) + @"\s*(?:=(?!=)|\[[^\]]*\]\s*=(?!=))")) continue;
                    var names = members.Where(m => m.Member.Kind == PhpMemberKind.Property && !m.Member.IsStatic).Select(m => m.Member.Name);
                    var close = Twig(target.Text, names);
                    list.Add(new UndefinedMember(target.Start, target.Text.Length, $"Property '{target.Text}' not found in {shortName}" + Hint(close), Severity.Warning, close));
                }
            }
            else if (isStatic && target.Kind == TokKind.Variable && !call)
            {
                var name = target.Text[1..];
                if (Workspace.Index.FindMember(fqn, name, PhpMemberKind.Property) is { Member.IsStatic: true }) continue;
                var names = members.Where(m => m.Member.Kind == PhpMemberKind.Property && m.Member.IsStatic).Select(m => "$" + m.Member.Name);
                var close = Twig(target.Text, names);
                list.Add(new UndefinedMember(target.Start, target.Text.Length, $"Static property '{target.Text}' not found in {shortName}" + Hint(close), Severity.Error, close));
            }
        }
        return list;
    }

    /// <summary>Close names (edit distance), else the names sharing a prefix (HOME_INDEX → HOME_ROUTE).</summary>
    private static List<string> Twig(string name, IEnumerable<string> names)
    {
        var all = names.Distinct().ToList();
        var close = TwigInsight.Suggest(name, all);
        if (close.Count > 0) return close;
        static int Common(string a, string b)
        {
            int n = 0;
            while (n < a.Length && n < b.Length && char.ToLowerInvariant(a[n]) == char.ToLowerInvariant(b[n])) n++;
            return n;
        }
        var min = Math.Max(3, name.Length / 3);
        return all.Select(n => (n, c: Common(n, name))).Where(x => x.c >= min && x.n != name)
                  .OrderByDescending(x => x.c).ThenBy(x => x.n.Length).Take(3).Select(x => x.n).ToList();
    }

    private static string Hint(List<string> close) => close.Count > 0 ? $" — did you mean '{close[0]}'? (Alt+Enter)" : "";

    /// <summary>The class and all its ancestors are indexed (otherwise a member may come from an unknown parent).</summary>
    private static bool FullyKnown(string fqn, int depth = 0)
    {
        if (depth > 20) return false;
        var c = Workspace.Index.GetClass(fqn);
        if (c == null) return false;
        if (c.Mixins.Count > 0) return false;
        if (c.Parent != null && !FullyKnown(c.Parent, depth + 1)) return false;
        return c.Traits.All(x => FullyKnown(x, depth + 1)) && c.Interfaces.All(x => FullyKnown(x, depth + 1));
    }

    // ------------------------------------------------------------------ usages

    /// <summary>
    /// Calls of a method in the project (src/, tests/, templates/, config/, public/): every "name(" after "->" / "::"
    /// whose receiver resolves to the method (the same class, a parent or a child of it).
    /// </summary>
    public static List<Usage> MethodUsages(PhpMember method, PhpClass owner, CancellationToken ct = default)
    {
        var list = new List<Usage>();
        var root = Workspace.Root;
        if (root == null) return list;
        var insight = Workspace.Insight;
        // a call through a parent class / an interface declaring the method is a usage too
        var declarations = new List<(string file, int offset)> { (Path.GetFullPath(method.File ?? ""), method.Offset) };
        foreach (var a in new[] { owner.Parent }.Concat(owner.Interfaces).Where(a => a != null))
            if (Workspace.Index.FindMember(a!, method.Name, PhpMemberKind.Method) is { Member.File: not null } inherited)
                declarations.Add((Path.GetFullPath(inherited.Member.File), inherited.Member.Offset));
        var pattern = new Regex(@"(?:->|::)\s*" + Regex.Escape(method.Name) + @"\s*\(", RegexOptions.IgnoreCase);
        foreach (var file in ProjectFiles(root))
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { text = FileText(file); } catch { continue; }
            if (text.IndexOf(method.Name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (Match m in pattern.Matches(text))
            {
                var nameOffset = m.Index + m.Value.IndexOf(method.Name, StringComparison.OrdinalIgnoreCase);
                SymbolTarget? target = null;
                try { target = insight.Resolve(text, nameOffset + 1, file); } catch { }
                if (target?.File == null) continue;
                // the method itself, or an override / the parent declaration of the same method
                var targetFile = Path.GetFullPath(target.File);
                if (!declarations.Any(d => d.offset == target.Offset && string.Equals(d.file, targetFile, StringComparison.OrdinalIgnoreCase))) continue;
                var line = text[..nameOffset].Count(c => c == '\n') + 1;
                var ls = text.LastIndexOf('\n', Math.Max(0, nameOffset - 1)) + 1;
                var le = text.IndexOf('\n', nameOffset);
                list.Add(new Usage(file, nameOffset, line, text[ls..(le < 0 ? text.Length : le)].Trim()));
            }
        }
        return list;
    }

    private static readonly Dictionary<string, (DateTime time, string text)> TextCache = new(StringComparer.OrdinalIgnoreCase);

    private static string FileText(string file)
    {
        var time = File.GetLastWriteTimeUtc(file);
        lock (TextCache)
            if (TextCache.TryGetValue(file, out var c) && c.time == time) return c.text;
        var text = File.ReadAllText(file);
        lock (TextCache) TextCache[file] = (time, text);
        return text;
    }

    private static IEnumerable<string> ProjectFiles(string root)
    {
        foreach (var dir in new[] { "src", "tests", "templates", "config", "public", "bin" })
        {
            var d = Path.Combine(root, dir);
            if (!Directory.Exists(d)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(d, "*.php", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var f in files) yield return f;
        }
    }
}
