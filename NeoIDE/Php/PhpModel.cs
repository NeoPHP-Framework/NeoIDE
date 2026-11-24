namespace NeoIDE.Php;

public enum PhpClassKind { Class, Interface, Trait, Enum }
public enum PhpMemberKind { Method, Property, Constant, EnumCase }
public enum Visibility { Public, Protected, Private }

public sealed class PhpParameter
{
    public string Name { get; set; } = "";       // without $
    public string? Type { get; set; }             // resolved type string
    public string? Default { get; set; }
    public bool IsVariadic { get; set; }
    public bool IsByRef { get; set; }
    public bool IsPromoted { get; set; }

    public string Display()
    {
        var s = "";
        if (!string.IsNullOrEmpty(Type)) s += PhpTypes.Short(Type) + " ";
        if (IsByRef) s += "&";
        if (IsVariadic) s += "...";
        s += "$" + Name;
        if (Default != null) s += " = " + Default;
        return s;
    }
}

public sealed class PhpMember
{
    public string Name { get; set; } = "";
    public PhpMemberKind Kind { get; set; }
    public Visibility Visibility { get; set; } = Visibility.Public;
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsFinal { get; set; }
    public bool IsReadonly { get; set; }
    public bool IsMagic { get; set; }            // from @property / @method
    public bool IsDeprecated { get; set; }
    public string? Type { get; set; }             // native (resolved) type: return type or property type
    public string? DocType { get; set; }          // @return / @var (resolved)
    public List<PhpParameter> Parameters { get; set; } = new();
    public string? Summary { get; set; }
    public string DeclaringClass { get; set; } = "";
    public string? File { get; set; }
    public int Offset { get; set; }

    public string? EffectiveType => !string.IsNullOrEmpty(DocType) && PhpTypes.IsVague(Type) ? DocType
        : !string.IsNullOrEmpty(Type) ? Type : DocType;

    public string Signature() => "(" + string.Join(", ", Parameters.Select(p => p.Display())) + ")";
}

public sealed class PhpClass
{
    public string Fqn { get; set; } = "";
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public PhpClassKind Kind { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsFinal { get; set; }
    public bool IsAttribute { get; set; }
    public bool IsDeprecated { get; set; }
    public string? Parent { get; set; }
    public List<string> Interfaces { get; set; } = new();
    public List<string> Traits { get; set; } = new();
    public List<string> Mixins { get; set; } = new();
    public List<PhpMember> Members { get; set; } = new();
    public string? Summary { get; set; }
    public string? File { get; set; }
    public int Offset { get; set; }
    public int BodyStart { get; set; }
    public int BodyEnd { get; set; }
    public bool IsBuiltin => File == null;

    public string KindLabel => Kind switch
    {
        PhpClassKind.Interface => "interface",
        PhpClassKind.Trait => "trait",
        PhpClassKind.Enum => "enum",
        _ => IsAbstract ? "abstract" : "class",
    };
}

public sealed class PhpFunction
{
    public string Fqn { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PhpParameter> Parameters { get; set; } = new();
    public string? ReturnType { get; set; }
    public string? DocReturnType { get; set; }
    public string? Summary { get; set; }
    public string? File { get; set; }
    public int Offset { get; set; }
    public string? EffectiveReturn => !string.IsNullOrEmpty(DocReturnType) && PhpTypes.IsVague(ReturnType) ? DocReturnType : ReturnType ?? DocReturnType;
    public string Signature() => "(" + string.Join(", ", Parameters.Select(p => p.Display())) + ")";
}

public sealed class PhpUse
{
    public string Alias { get; set; } = "";
    public string Fqn { get; set; } = "";
    public string Kind { get; set; } = "class";   // class | function | const
    public int StatementStart { get; set; }        // offset of the "use" keyword
    public int StatementEnd { get; set; }          // offset just after ';'
    public bool IsGroup { get; set; }
}

public sealed class PhpFileInfo
{
    public string? Path { get; set; }
    public string Namespace { get; set; } = "";
    public int NamespaceStatementEnd { get; set; } = -1;
    public int HeaderEnd { get; set; } = -1;          // after "<?php" (+ declare)
    public List<PhpUse> Uses { get; } = new();
    public List<PhpClass> Classes { get; } = new();
    public List<PhpFunction> Functions { get; } = new();
    public List<string> Constants { get; } = new();

    public PhpClass? ClassAt(int offset) =>
        Classes.LastOrDefault(c => offset > c.BodyStart && (offset <= c.BodyEnd || c.BodyEnd <= c.BodyStart));

    public string? AliasTarget(string alias) =>
        Uses.FirstOrDefault(u => u.Kind == "class" && string.Equals(u.Alias, alias, StringComparison.OrdinalIgnoreCase))?.Fqn;

    /// <summary>Resolves a class name as written in this file to its fully qualified name.</summary>
    public string ResolveClass(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name[0] == '\\') return name.TrimStart('\\');
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("namespace\\")) return Join(Namespace, name[10..]);
        var first = name.Split('\\')[0];
        var target = AliasTarget(first);
        if (target != null) return name.Length == first.Length ? target : target + name[first.Length..];
        return Join(Namespace, name);
    }

    private static string Join(string ns, string name) => string.IsNullOrEmpty(ns) ? name : ns + "\\" + name;

    /// <summary>Resolves a whole type expression (?Foo, Foo|Bar, Foo[], array&lt;Foo&gt;...).</summary>
    public string? ResolveType(string? type) => PhpTypes.Resolve(type, ResolveClass);
}

/// <summary>Helpers around PHP type strings.</summary>
public static class PhpTypes
{
    public static readonly HashSet<string> Builtin = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "integer", "float", "double", "string", "bool", "boolean", "array", "void", "mixed", "never", "null",
        "object", "iterable", "callable", "true", "false", "self", "static", "parent", "$this", "resource",
        "list", "non-empty-array", "non-empty-list", "non-empty-string", "class-string", "positive-int",
        "negative-int", "non-negative-int", "numeric", "numeric-string", "scalar", "array-key", "callable-string",
        "key-of", "value-of", "int-mask", "literal-string", "lowercase-string",
    };

    public static bool IsVague(string? type) => string.IsNullOrEmpty(type) ||
        type.Split('|', '&').Select(t => t.Trim().TrimStart('?')).All(t =>
            t is "array" or "mixed" or "object" or "iterable" or "null" or "static" or "self" or "callable" or "\\Traversable" or "Traversable");

    public static string? Resolve(string? type, Func<string, string> resolveClass)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < type.Length)
        {
            char c = type[i];
            if (PhpLexer.IsIdentStart(c) || c == '\\')
            {
                int s = i;
                while (i < type.Length && (PhpLexer.IsIdentPart(type[i]) || type[i] == '\\' || type[i] == '-')) i++;
                var word = type[s..i];
                // keep "$this" and keywords as-is
                if (s > 0 && type[s - 1] == '$') { sb.Append(word); continue; }
                if (Builtin.Contains(word)) sb.Append(word.ToLowerInvariant());
                else sb.Append('\\').Append(resolveClass(word).TrimStart('\\'));
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString().Replace(" ", "");
    }

    /// <summary>The class names contained in a resolved type ("\A\B|null" → ["A\B"]). Collections give element types when asked.</summary>
    public static List<string> ClassNames(string? type, string? selfClass = null, bool elements = false)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(type)) return list;
        foreach (var raw in SplitTopLevel(type))
        {
            var t = raw.Trim().TrimStart('?');
            if (t.Length == 0) continue;
            if (elements)
            {
                if (t.EndsWith("[]")) { AddName(list, t[..^2], selfClass); continue; }
                int lt = t.IndexOf('<');
                if (lt > 0 && t.EndsWith(">"))
                {
                    var args = SplitTopLevel(t[(lt + 1)..^1], ',');
                    AddName(list, args.Last().Trim(), selfClass);
                    continue;
                }
                continue;
            }
            int g = t.IndexOf('<');
            if (g > 0) t = t[..g];
            if (t.EndsWith("[]")) continue;
            AddName(list, t, selfClass);
        }
        return list;
    }

    private static void AddName(List<string> list, string t, string? selfClass)
    {
        t = t.Trim().TrimStart('?');
        if (t is "static" or "self" or "$this")
        {
            if (selfClass != null) list.Add(selfClass);
            return;
        }
        if (t.StartsWith("\\")) t = t[1..];
        if (t.Length == 0 || Builtin.Contains(t)) return;
        if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) list.Add(t);
    }

    private static List<string> SplitTopLevel(string s, char sep = '|')
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '<' or '(' or '{' or '[') depth++;
            else if (c is '>' or ')' or '}' or ']') depth--;
            else if (depth == 0 && (c == sep || (sep == '|' && c == '&'))) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    /// <summary>Human readable short form: \App\Entity\User|null → ?User.</summary>
    public static string Short(string? type)
    {
        if (string.IsNullOrEmpty(type)) return "";
        var parts = SplitTopLevel(type).Select(p =>
        {
            var t = p.Trim();
            return System.Text.RegularExpressions.Regex.Replace(t, @"\\?(?:[A-Za-z_][\w]*\\)+([A-Za-z_]\w*)", "$1");
        }).ToList();
        if (parts.Count == 2 && parts.Contains("null")) return "?" + parts.First(p => p != "null");
        return string.Join("|", parts);
    }
}
