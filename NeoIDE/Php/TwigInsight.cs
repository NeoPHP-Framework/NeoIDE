using System.Text.RegularExpressions;
using NeoIDE.Editor;
using NeoIDE.Services;
using NeoIDE.Services.Diagnostics;

namespace NeoIDE.Php;

/// <summary>A problem of a Twig template with the names that may replace the faulty one.</summary>
public sealed record TwigProblem(int Start, int Length, string Message, IReadOnlyList<string> Suggestions);

/// <summary>What a name of a Twig expression refers to: a template variable, a loop / set variable or a member of a class.</summary>
public sealed record TwigSymbol(int Start, int Length, string? File, int Offset, string Description);

/// <summary>
/// Twig intelligence: variables of the template (from render()), {% for %} / {% set %} variables, attributes of
/// objects (<c>user.name</c> → property, getName(), isName(), hasName() or name()), filters, functions and tags.
/// Completion, navigation, hover and "unknown attribute" checks.
/// </summary>
public static class TwigInsight
{
    public static readonly string[] Filters =
    {
        "abs", "batch", "capitalize", "column", "convert_encoding", "country_name", "currency_name", "date", "date_modify", "default",
        "e", "escape", "filter", "first", "format", "format_currency", "format_date", "format_datetime", "format_number", "join",
        "json_encode", "keys", "last", "length", "lower", "map", "merge", "nl2br", "number_format", "raw", "reduce", "replace",
        "reverse", "round", "slice", "slug", "sort", "spaceless", "split", "striptags", "title", "trim", "u", "upper", "url_encode",
    };

    public static readonly string[] Functions =
    {
        "asset", "attribute", "block", "constant", "csrf_token", "cycle", "date", "dump", "include", "max", "min", "parent", "path",
        "random", "range", "source", "template_from_string", "url",
    };

    public static readonly string[] Tags =
    {
        "apply", "autoescape", "block", "cache", "deprecated", "do", "embed", "extends", "flush", "for", "from", "if", "import",
        "include", "macro", "sandbox", "set", "use", "verbatim", "with",
        "else", "elseif", "endapply", "endautoescape", "endblock", "endembed", "endfor", "endif", "endmacro", "endsandbox", "endset",
        "endverbatim", "endwith",
    };

    private static readonly string[] LoopAttributes = { "index", "index0", "revindex", "revindex0", "first", "last", "length", "parent" };

    private static readonly Regex Region = new(@"\{\{(?<e>.*?)(?:\}\}|$)|\{%(?<e>.*?)(?:%\}|$)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ForLoop = new(@"\{%-?\s*for\s+(?:(?<k>\w+)\s*,\s*)?(?<v>\w+)\s+in\s+(?<expr>.+?)\s*(?:\bif\b.*?)?-?%\}", RegexOptions.Compiled);
    private static readonly Regex SetVar = new(@"\{%-?\s*set\s+(?<v>\w+)\s*=\s*(?<expr>.+?)\s*-?%\}", RegexOptions.Compiled);
    private static readonly Regex Chain = new(@"(?<![\w.$|])(?<chain>[A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*(?:\([^()]*\))?)+)", RegexOptions.Compiled);

    private sealed record Local(string Name, string? Type, int Offset, string Description, ArrayShape? Shape = null);

    /// <summary>What a Twig expression evaluates to: a PHP type and, for an array literal of render(), its keys.</summary>
    public readonly record struct TwigValue(string? Type, ArrayShape? Shape);

    // ------------------------------------------------------------------ regions / variables

    /// <summary>The {{ }} / {% %} expressions of the text (start of the expression, end).</summary>
    private static List<(int start, int end)> Regions(string text) =>
        Region.Matches(text).Select(m => (m.Groups["e"].Index, m.Groups["e"].Index + m.Groups["e"].Length)).ToList();

    private static (int start, int end)? RegionAt(string text, int offset)
    {
        foreach (var r in Regions(text))
            if (offset >= r.start && offset <= r.end) return r;
        return null;
    }

    /// <summary>True when the offset is inside a {{ }} / {% %} expression.</summary>
    public static bool InExpression(string text, int offset) => RegionAt(text, offset) != null;

    /// <summary>{% for %} variables whose loop is still open at the offset, and the {% set %} variables before it.</summary>
    private static List<Local> Locals(string text, int offset, string? file)
    {
        var list = new List<Local>();
        foreach (Match m in ForLoop.Matches(text))
        {
            if (m.Index >= offset) break;
            // still in the loop: as many {% endfor %} as {% for %} between the loop and the offset is required
            var between = text[(m.Index + m.Length)..offset];
            if (Regex.Matches(between, @"\{%-?\s*endfor\b").Count > Regex.Matches(between, @"\{%-?\s*for\b").Count) continue;
            var expr = m.Groups["expr"].Value;
            var collection = ResolveExpression(expr, text, m.Index, file);
            var element = PhpTypes.ClassNames(collection, null, elements: true).FirstOrDefault();
            list.Add(new Local(m.Groups["v"].Value, element != null ? "\\" + element : null, m.Groups["v"].Index, $"{m.Groups["v"].Value}: {(element != null ? PhpTypes.Short("\\" + element) : "mixed")}\nelement of {expr.Trim()}"));
            if (m.Groups["k"].Success) list.Add(new Local(m.Groups["k"].Value, null, m.Groups["k"].Index, $"{m.Groups["k"].Value}: key of {expr.Trim()}"));
            list.Add(new Local("loop", null, m.Index, "loop: index, index0, first, last, length, revindex, parent"));
        }
        foreach (Match m in SetVar.Matches(text))
        {
            if (m.Index >= offset) break;
            var value = ResolveValue(m.Groups["expr"].Value, text, m.Index, file);
            var type = value.Type;
            list.Add(new Local(m.Groups["v"].Value, type, m.Groups["v"].Index, $"{m.Groups["v"].Value}: {(type != null ? PhpTypes.Short(type) : "mixed")}\nset to {m.Groups["expr"].Value.Trim()}", value.Shape));
        }
        return list;
    }

    private static TwigValue VariableValue(string name, string text, int offset, string? file)
    {
        var local = Locals(text, offset, file).LastOrDefault(l => l.Name == name);
        if (local != null) return new TwigValue(local.Type, local.Shape);
        var v = TemplateVariables.For(file).FirstOrDefault(x => x.Name == name);
        return new TwigValue(v?.Type, v?.Shape);
    }

    /// <summary>One step of "a.b": the key of an array given to render(), else the attribute of the object.</summary>
    private static TwigValue Step(TwigValue value, string name)
    {
        if (value.Shape != null)
        {
            var key = value.Shape.Keys.FirstOrDefault(k => k.Name == name);
            return key == null ? default : new TwigValue(key.Type ?? "mixed", key.Shape);
        }
        var cls = ClassOf(value.Type);
        if (cls == null) return default;
        return new TwigValue(Attribute(cls, name)?.Member.EffectiveType, null);
    }

    /// <summary>Value of a Twig expression made of a variable and attributes / keys ("post.author.name"); filters are ignored.</summary>
    public static TwigValue ResolveValue(string expr, string text, int offset, string? file)
    {
        expr = expr.Trim();
        var pipe = expr.IndexOf('|');
        if (pipe >= 0) expr = expr[..pipe].Trim();
        var m = Regex.Match(expr, @"^(?<v>[A-Za-z_]\w*)(?<rest>(?:\s*\.\s*[A-Za-z_]\w*(?:\([^()]*\))?)*)$");
        if (!m.Success) return default;
        var value = VariableValue(m.Groups["v"].Value, text, offset, file);
        foreach (Match a in Regex.Matches(m.Groups["rest"].Value, @"\.\s*(?<a>[A-Za-z_]\w*)"))
        {
            value = Step(value, a.Groups["a"].Value);
            if (value.Type == null && value.Shape == null) return default;
        }
        return value;
    }

    /// <summary>Type of a Twig expression ("post.author.name").</summary>
    public static string? ResolveExpression(string expr, string text, int offset, string? file) => ResolveValue(expr, text, offset, file).Type;

    private static List<CompletionEntry> KeyEntries(ArrayShape shape) =>
        shape.Keys.Select(k => new CompletionEntry
        {
            Label = k.Name, Insert = k.Name, Kind = EntryKind.Property, Priority = 7,
            TypeText = k.Shape != null ? "array" : PhpTypes.Short(k.Type), Tail = "  key",
        }).ToList();

    private static string? ClassOf(string? type) => PhpTypes.ClassNames(type).FirstOrDefault(c => Workspace.Index.GetClass(c) != null);

    /// <summary>The member Twig uses for <c>obj.name</c>: property, name(), getName(), isName(), hasName().</summary>
    public static ResolvedMember? Attribute(string classFqn, string name)
    {
        var members = Workspace.Index.GetMembers(classFqn).Where(m => !m.Member.IsStatic && m.Member.Visibility == Visibility.Public).ToList();
        var prop = members.FirstOrDefault(m => m.Member.Kind == PhpMemberKind.Property && m.Member.Name.TrimStart('$') == name);
        if (prop.Member != null) return prop;
        foreach (var candidate in new[] { name, "get" + name, "is" + name, "has" + name })
        {
            var method = members.FirstOrDefault(m => m.Member.Kind == PhpMemberKind.Method && string.Equals(m.Member.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (method.Member != null) return method;
        }
        return null;
    }

    /// <summary>The attribute names of a class as written in Twig (getTitle() → title).</summary>
    public static List<(string name, ResolvedMember member)> AttributeNames(string classFqn)
    {
        var list = new List<(string, ResolvedMember)>();
        foreach (var m in Workspace.Index.GetMembers(classFqn))
        {
            var mem = m.Member;
            if (mem.IsStatic || mem.Visibility != Visibility.Public) continue;
            if (mem.Kind == PhpMemberKind.Property) { list.Add((mem.Name.TrimStart('$'), m)); continue; }
            if (mem.Kind != PhpMemberKind.Method || mem.Name.StartsWith("__")) continue;
            var g = Regex.Match(mem.Name, @"^(?:get|is|has)(?<n>[A-Z]\w*)$");
            if (g.Success && mem.Parameters.All(p => p.Default != null || p.IsVariadic))
                list.Add((char.ToLowerInvariant(g.Groups["n"].Value[0]) + g.Groups["n"].Value[1..], m));
            else list.Add((mem.Name, m));
        }
        return list.GroupBy(x => x.Item1).Select(g => g.First()).ToList();
    }

    private static bool IsDynamic(string classFqn)
    {
        if (Workspace.Index.IsSubtypeOf(classFqn, "ArrayAccess")) return true;
        return Workspace.Index.GetMembers(classFqn).Any(m => m.Member.Name is "__get" or "__call" or "__isset");
    }

    // ------------------------------------------------------------------ completion

    public static (int start, List<CompletionEntry> entries)? Complete(string text, int caret, string? file)
    {
        var region = RegionAt(text, caret);
        if (region == null) return null;
        var (rs, _) = region.Value;
        int start = caret;
        while (start > rs && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        // inside a string: no completion (templates and routes are completed by NeoInsight)
        var before = text[rs..start];
        if (before.Count(c => c == '\'') % 2 == 1 || before.Count(c => c == '"') % 2 == 1) return null;
        var entries = new List<CompletionEntry>();
        var p = start - 1;
        while (p > rs && text[p] == ' ') p--;
        var isTag = text[rs - 1] == '%' && before.Trim().TrimStart('-').Trim().Length == 0;
        if (p >= rs && text[p] == '.')
        {
            var chainEnd = p;
            var cs = chainEnd;
            int depth = 0;
            while (cs > rs && (char.IsLetterOrDigit(text[cs - 1]) || text[cs - 1] is '_' or '.' || text[cs - 1] == ')' || depth > 0 && text[cs - 1] != '\n'))
            {
                if (text[cs - 1] == ')') depth++;
                else if (text[cs - 1] == '(') { if (depth == 0) break; depth--; }
                cs--;
            }
            var chain = text[cs..chainEnd];
            if (chain == "loop")
            {
                entries.AddRange(LoopAttributes.Select(a => new CompletionEntry { Label = a, Insert = a, Kind = EntryKind.Property, Priority = 5 }));
                return (start, entries);
            }
            var value = ResolveValue(chain, text, cs, file);
            if (value.Shape != null) return (start, KeyEntries(value.Shape));
            var cls = ClassOf(value.Type);
            if (cls == null) return (start, entries);
            foreach (var (name, m) in AttributeNames(cls))
                entries.Add(new CompletionEntry
                {
                    Label = name, Insert = name,
                    Kind = m.Member.Kind == PhpMemberKind.Property || name != m.Member.Name ? EntryKind.Property : EntryKind.Method,
                    Tail = name != m.Member.Name.TrimStart('$') ? $"  {m.Member.Name}()" : m.Member.Kind == PhpMemberKind.Method ? m.Member.Signature() : null,
                    TypeText = PhpTypes.Short(m.Member.EffectiveType), Priority = m.Member.DeclaringClass == cls ? 6 : 5,
                    Description = m.Member.Summary,
                });
            return (start, entries);
        }
        if (p >= rs && text[p] == '|')
        {
            entries.AddRange(Filters.Select(f => new CompletionEntry { Label = f, Insert = f, Kind = EntryKind.Function, TypeText = "filter", Priority = 5 }));
            return (start, entries);
        }
        if (isTag)
        {
            entries.AddRange(Tags.Select(t => new CompletionEntry { Label = t, Insert = t, Kind = EntryKind.Keyword, Priority = 5 }));
            return (start, entries);
        }
        foreach (var v in TemplateVariables.For(file))
            entries.Add(new CompletionEntry { Label = v.Name, Insert = v.Name, Kind = EntryKind.Variable, TypeText = PhpTypes.Short(v.Type), Tail = $"  {v.Source}", Priority = 8 });
        foreach (var l in Locals(text, start, file).GroupBy(l => l.Name).Select(g => g.Last()))
            entries.Add(new CompletionEntry { Label = l.Name, Insert = l.Name, Kind = EntryKind.Variable, TypeText = PhpTypes.Short(l.Type), Priority = 9 });
        entries.Add(new CompletionEntry { Label = "app", Insert = "app", Kind = EntryKind.Variable, TypeText = "global", Priority = 2 });
        entries.AddRange(Functions.Select(f => new CompletionEntry { Label = f, Insert = f, Kind = EntryKind.Function, Tail = "()", Priority = 3 }));
        foreach (var k in new[] { "true", "false", "null", "not", "and", "or", "in", "is", "defined", "empty", "iterable" })
            entries.Add(new CompletionEntry { Label = k, Insert = k, Kind = EntryKind.Keyword, Priority = 1 });
        return entries.Count == 0 ? null : (start, entries.GroupBy(e => e.Label).Select(g => g.First()).ToList());
    }

    // ------------------------------------------------------------------ navigation / hover

    public static TwigSymbol? SymbolAt(string text, int offset, string? file)
    {
        var region = RegionAt(text, offset);
        if (region == null) return null;
        var (rs, re) = region.Value;
        int s = offset, e = offset;
        while (s > rs && (char.IsLetterOrDigit(text[s - 1]) || text[s - 1] == '_')) s--;
        while (e < re && (char.IsLetterOrDigit(text[e]) || text[e] == '_')) e++;
        if (e <= s || char.IsDigit(text[s])) return null;
        var word = text[s..e];
        var before = text[rs..s];
        if (before.Count(c => c == '\'') % 2 == 1 || before.Count(c => c == '"') % 2 == 1) return null;
        var p = s - 1;
        while (p > rs && text[p] == ' ') p--;
        if (p >= rs && text[p] == '.')
        {
            var cs = p;
            while (cs > rs && (char.IsLetterOrDigit(text[cs - 1]) || text[cs - 1] is '_' or '.')) cs--;
            var receiver = ResolveValue(text[cs..p], text, cs, file);
            if (receiver.Shape?.Keys.FirstOrDefault(k => k.Name == word) is { } key)
                return new TwigSymbol(s, e - s, key.File, key.Offset, $"{text[cs..p].Trim()}.{word}: {(key.Shape != null ? "array {" + string.Join(", ", key.Shape.Keys.Select(k => k.Name)) + "}" : PhpTypes.Short(key.Type ?? "mixed"))}\nkey of the array given to render()");
            var cls = ClassOf(receiver.Type);
            if (cls == null) return null;
            var m = Attribute(cls, word);
            if (m == null) return null;
            return new TwigSymbol(s, e - s, m.Value.Member.File, m.Value.Member.Offset, PhpInsight.DescribeMember(m.Value.Member));
        }
        if (p >= rs && text[p] == '|') return null;
        var local = Locals(text, s, file).LastOrDefault(l => l.Name == word);
        if (local != null) return new TwigSymbol(s, e - s, file, local.Offset, local.Description);
        var v = TemplateVariables.For(file).FirstOrDefault(x => x.Name == word);
        if (v != null) return new TwigSymbol(s, e - s, v.File, v.Offset, TemplateVariables.Describe(v).TrimStart('$'));
        return null;
    }

    // ------------------------------------------------------------------ checks

    /// <summary>Attributes that do not exist on the class of the object (only when the class is known and not magic).</summary>
    public static List<TwigProblem> Problems(string text, string? file)
    {
        var list = new List<TwigProblem>();
        if (!NeoProject.IsLoaded) return list;
        foreach (var (rs, re) in Regions(text))
        {
            var expr = BlankStrings(text[rs..re]);
            foreach (Match m in Chain.Matches(expr))
            {
                var parts = Regex.Matches(m.Groups["chain"].Value, @"[A-Za-z_]\w*(?:\([^()]*\))?").ToList();
                var head = parts[0].Value;
                if (head is "loop" or "app" or "_self" or "_context") continue;
                var value = VariableValue(head, text, rs + m.Index, file);
                for (int i = 1; i < parts.Count; i++)
                {
                    var name = Regex.Match(parts[i].Value, @"^\w+").Value;
                    var at = rs + m.Groups["chain"].Index + parts[i].Index;
                    if (value.Shape != null)
                    {
                        // keys of an array literal given to render()
                        var key = value.Shape.Keys.FirstOrDefault(k => k.Name == name);
                        if (key == null)
                        {
                            if (value.Shape.Open) break;
                            var close = Suggest(name, value.Shape.Keys.Select(k => k.Name));
                            var owner = string.Join(".", parts.Take(i).Select(x => Regex.Match(x.Value, @"^\w+").Value));
                            list.Add(new TwigProblem(at, name.Length,
                                $"Unknown key '{name}' of {owner} (keys: {string.Join(", ", value.Shape.Keys.Select(k => k.Name))})" + (close.Count > 0 ? $" — did you mean '{close[0]}'?" : ""), close));
                            break;
                        }
                        value = new TwigValue(key.Type, key.Shape);
                        continue;
                    }
                    var type = value.Type;
                    var cls = ClassOf(type);
                    if (cls == null || IsDynamic(cls)) break;
                    var member = Attribute(cls, name);
                    if (member == null)
                    {
                        var names = AttributeNames(cls).Select(a => a.name).ToList();
                        var close = Suggest(name, names);
                        list.Add(new TwigProblem(at, name.Length,
                            $"Unknown attribute '{name}' of {cls.Split('\\').Last()}" + (close.Count > 0 ? $" — did you mean '{close[0]}'?" : ""), close));
                        break;
                    }
                    value = new TwigValue(member.Value.Member.EffectiveType, null);
                }
            }
        }
        return list;
    }

    public static List<Squiggle> Check(string text, string? file)
    {
        if (!AppSettings.Current.NeoChecks) return new List<Squiggle>();
        return Problems(text, file).Select(p => new Squiggle(p.Start, p.Length, p.Message + " — Alt+Enter", Severity.Warning, "neophp")).ToList();
    }

    private static string BlankStrings(string s) =>
        Regex.Replace(s, @"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""", m => new string(' ', m.Length));

    /// <summary>The closest names (edit distance), best first.</summary>
    public static List<string> Suggest(string name, IEnumerable<string> names, int max = 3) =>
        names.Select(n => (n, d: Levenshtein(n, name))).Where(x => x.d <= Math.Max(2, name.Length / 3) && x.n != name)
             .OrderBy(x => x.d).ThenBy(x => x.n.Length).Take(max).Select(x => x.n).ToList();

    public static int Levenshtein(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 6) return 99;
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int tmp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1));
                prev = tmp;
            }
        }
        return d[b.Length];
    }
}
