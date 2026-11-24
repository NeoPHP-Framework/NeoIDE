using System.IO;

namespace NeoIDE.Php;

public enum EntryKind { Method, Function, Property, Constant, EnumCase, Class, Interface, Trait, Enum, Variable, Keyword, Namespace, Template, DocTag, Parameter }

public sealed class CompletionEntry
{
    public string Label { get; init; } = "";          // text shown & used for filtering
    public string Insert { get; init; } = "";         // text inserted
    public EntryKind Kind { get; init; }
    public string? Tail { get; init; }                // gray text after the label (signature, namespace)
    public string? TypeText { get; init; }            // right aligned type
    public string? Description { get; init; }
    public bool Bold { get; init; }
    public bool Strike { get; init; }
    public double Priority { get; init; }
    public string? ImportFqn { get; init; }            // class to import when committed
    public int CaretBack { get; init; }               // move caret back after insertion (inside parentheses)
    public string? CtorFqn { get; init; }             // "new Foo()": put the caret inside () when the constructor has parameters (checked on insertion)
}

public sealed class CompletionResult
{
    public List<CompletionEntry> Entries { get; } = new();
    public int ReplaceStart { get; set; }
    public bool IsMemberAccess { get; set; }
}

/// <summary>PhpStorm-like PHP completion: members of inherited classes/traits/interfaces, classes with auto-import, variables, functions...</summary>
public sealed class PhpCompletion
{
    private readonly PhpIndex _index;
    private readonly string? _projectRoot;

    public PhpCompletion(PhpIndex index, string? projectRoot)
    {
        _index = index;
        _projectRoot = projectRoot;
    }

    private static readonly string[] StatementKeywords =
    {
        "abstract", "array", "as", "break", "case", "catch", "class", "clone", "const", "continue", "declare", "default",
        "do", "echo", "else", "elseif", "empty", "enum", "extends", "false", "final", "finally", "fn", "for", "foreach",
        "function", "global", "if", "implements", "include", "include_once", "instanceof", "interface", "isset", "list",
        "match", "namespace", "new", "null", "print", "private", "protected", "public", "readonly", "require",
        "require_once", "return", "self", "parent", "static", "switch", "throw", "trait", "true", "try", "unset", "use",
        "while", "yield", "int", "float", "string", "bool", "void", "mixed", "never", "object", "iterable", "callable",
    };

    private static readonly string[] TypeKeywords =
        { "int", "float", "string", "bool", "array", "void", "mixed", "never", "null", "object", "iterable", "callable", "self", "static", "false", "true", "?" };

    private static readonly string[] SuperGlobals =
        { "$_GET", "$_POST", "$_SERVER", "$_SESSION", "$_COOKIE", "$_FILES", "$_ENV", "$_REQUEST", "$GLOBALS" };

    private static readonly string[] DocTags =
    {
        "param", "return", "var", "throws", "deprecated", "see", "since", "author", "property", "property-read",
        "property-write", "method", "mixin", "template", "extends", "implements", "inheritDoc", "internal", "todo",
        "phpstan-param", "phpstan-return", "psalm-param", "psalm-return", "noinspection",
    };

    public CompletionResult Complete(string text, int offset, string? filePath, bool explicitInvoke)
    {
        var result = new CompletionResult();
        offset = Math.Clamp(offset, 0, text.Length);

        // ----- identify the word being typed
        int start = offset;
        while (start > 0 && (PhpLexer.IsIdentPart(text[start - 1]) || text[start - 1] == '\\')) start--;
        bool isVar = start > 0 && text[start - 1] == '$';
        var prefix = text[start..offset];
        if (isVar) start--;
        result.ReplaceStart = start;

        // ----- lexical context of the caret
        var all = PhpLexer.Tokenize(text, offset);
        var last = all.Count > 0 ? all[^1] : default;
        if (last.Text != null && last.End >= offset)
        {
            switch (last.Kind)
            {
                case TokKind.InlineHtml:
                    return result;
                case TokKind.Comment:
                    return result;
                case TokKind.DocComment:
                    CompleteDoc(text, offset, filePath, result);
                    return result;
                case TokKind.String when !IsClosedString(last, offset):
                    CompleteString(all, last, offset, result);
                    return result;
            }
        }

        var info = PhpParser.Parse(text, filePath);
        _index.UpdateFromBuffer(info, filePath);
        var sig = PhpLexer.Significant(PhpLexer.Tokenize(text, start));
        var resolver = new TypeResolver(_index, info, sig, offset);
        var current = resolver.CurrentClass;
        var prev = sig.Count > 0 ? sig[^1] : default;
        bool hasPrev = sig.Count > 0;

        // ----- declarations: the name being declared gets no completion
        //       (except "function" in a class: methods to override / implement)
        if (hasPrev && prev.Kind == TokKind.Identifier && !isVar)
        {
            var w = prev.Text.ToLowerInvariant();
            if (w == "function")
            {
                if (current != null && InClassBody(sig, current)) CompleteOverrides(text, info, current, prefix, result);
                return result;
            }
            if (w is "class" or "interface" or "trait" or "enum" or "namespace" or "const" or "goto" or "as" or "fn" || (w == "case" && current?.Kind == PhpClassKind.Enum))
                return result;
        }

        // ----- $variables (but not the name of a parameter / property being declared: "Type $na")
        if (isVar)
        {
            if (hasPrev && (prev.Kind == TokKind.Identifier && !ExpressionKeywords.Contains(prev.Text) && !PhpTypes.Builtin.Contains(prev.Text) && char.IsUpper(prev.Text.TrimStart('\\')[0])
                            || prev.Kind == TokKind.Identifier && PhpTypes.Builtin.Contains(prev.Text) && IsDeclarationType(sig)))
                return result;
            CompleteVariables(resolver, sig.Count, current, offset, info, result);
            return result;
        }

        // ----- member access
        if (hasPrev && (prev.Is("->") || prev.Is("?->") || prev.Is("::")))
        {
            result.IsMemberAccess = true;
            var isStatic = prev.Is("::");
            var receiverType = resolver.ResolveChainEndingAt(sig.Count - 1);
            var baseTok = sig.Count >= 2 ? sig[^2] : default;
            var baseWord = baseTok.Kind == TokKind.Identifier ? baseTok.Text.ToLowerInvariant() : baseTok.Text ?? "";
            CompleteMembers(receiverType, isStatic, baseWord, current, text, offset, result);
            return result;
        }

        // ----- class name contexts
        var ctx = ClassContext(sig, current);
        if (ctx != null)
        {
            CompleteClasses(text, info, prefix, ctx, result);
            if (ctx is "type") foreach (var k in TypeKeywords.Where(k => k != "?"))
                result.Entries.Add(new CompletionEntry { Label = k, Insert = k, Kind = EntryKind.Keyword, Priority = 2 });
            return result;
        }

        if (!explicitInvoke && prefix.Length == 0) return result;

        // ----- class body (member level): only modifiers / declarations
        if (current != null && InClassBody(sig, current))
        {
            foreach (var k in current.Kind == PhpClassKind.Enum ? MemberKeywords.Append("case") : MemberKeywords)
                if (Matches(k, prefix)) result.Entries.Add(new CompletionEntry { Label = k, Insert = k + " ", Kind = EntryKind.Keyword, Priority = 5 });
            // types of typed properties / constants
            if (prefix.Length > 0 && char.IsUpper(prefix[0])) CompleteClasses(text, info, prefix, "type", result);
            return result;
        }

        // ----- named arguments: foo(na|  new Foo(na|  #[Column(len|
        var call = CallAtArgumentStart(sig, resolver, info);
        if (call != null)
        {
            foreach (var p in call.Value.parameters)
            {
                if (call.Value.used.Contains(p.Name) || !Matches(p.Name, prefix)) continue;
                result.Entries.Add(new CompletionEntry
                {
                    Label = p.Name + ":", Insert = p.Name + ": ", Kind = EntryKind.Parameter, TypeText = PhpTypes.Short(p.Type),
                    Tail = p.Default != null ? " = " + p.Default : null, Priority = 30,
                    Description = $"Parameter ${p.Name} of {call.Value.callee}",
                });
            }
            if (call.Value.isAttribute) { CompleteClasses(text, info, prefix, "any", result); AddConstants(prefix, result); return result; }
        }

        // ----- general context: keywords, functions, constants, classes
        if (prefix.Contains('\\'))
        {
            CompleteClasses(text, info, prefix, IsAttributeName(sig) ? "attribute" : "qualified", result);
            return result;
        }
        var keywords = AtStatementStart(sig) ? StatementKeywords : ExpressionKeywords.ToArray();
        foreach (var k in keywords)
            if (Matches(k, prefix)) result.Entries.Add(new CompletionEntry { Label = k, Insert = k, Kind = EntryKind.Keyword, Priority = 4 });

        foreach (var f in _index.AllFunctions())
        {
            if (!Matches(f.Name, prefix)) continue;
            result.Entries.Add(new CompletionEntry
            {
                Label = f.Name, Insert = f.Name + "()", CaretBack = f.Parameters.Count > 0 ? 1 : 0,
                Kind = EntryKind.Function, Tail = f.Signature(), TypeText = PhpTypes.Short(f.EffectiveReturn),
                Description = f.Summary, Priority = f.File != null ? 3 : 2,
            });
        }
        AddConstants(prefix, result);
        CompleteClasses(text, info, prefix, "any", result);
        return result;
    }

    private static readonly string[] MemberKeywords =
        { "public", "protected", "private", "static", "abstract", "final", "readonly", "const", "function", "use", "var" };

    private static readonly HashSet<string> ExpressionKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "new", "true", "false", "null", "fn", "function", "match", "isset", "empty", "array", "list", "static", "self", "parent",
        "instanceof", "clone", "print", "yield", "and", "or", "xor", "return", "echo", "throw", "include", "require",
        "include_once", "require_once", "global", "as", "use",
    };

    private void AddConstants(string prefix, CompletionResult result)
    {
        // global constants only for an upper case prefix (PHP_EOL, E_ALL...): they would flood everything else
        if (prefix.Length < 2 || !char.IsUpper(prefix[0])) return;
        foreach (var c in _index.AllConstants())
            if (c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result.Entries.Add(new CompletionEntry { Label = c, Insert = c, Kind = EntryKind.Constant, Priority = 1 });
    }

    /// <summary>Is the caret directly in the body of the class (not inside a method)?</summary>
    private static bool InClassBody(List<Token> sig, PhpClass cls)
    {
        int depth = 0;
        bool started = false;
        foreach (var t in sig)
        {
            if (t.Start < cls.BodyStart) continue;
            if (t.Is("{")) { depth++; started = true; }
            else if (t.Is("}")) depth--;
        }
        return started && depth == 1 && (sig.Count == 0 || !sig[^1].Is("(") && !sig[^1].Is(",") && !sig[^1].Is("=") && !sig[^1].Is("#[") || sig[^1].Is(";"));
    }

    private static bool AtStatementStart(List<Token> sig)
    {
        if (sig.Count == 0) return true;
        var p = sig[^1];
        return p.Is(";") || p.Is("{") || p.Is("}") || p.Kind == TokKind.OpenTag || p.Is(":") && sig.Count > 1 && (sig[^2].Is(")") || sig[^2].IsWord("else") || sig[^2].IsWord("default"));
    }

    private static bool IsDeclarationType(List<Token> sig)
    {
        // "(int $x" / ", string $y" / "private int $z"
        if (sig.Count < 2) return false;
        var before = sig[^2];
        return before.Is("(") || before.Is(",") || before.Is("?") || before.Kind == TokKind.Identifier &&
               before.Text.ToLowerInvariant() is "public" or "protected" or "private" or "readonly" or "static" or "var";
    }

    private static bool IsAttributeName(List<Token> sig) => sig.Count > 0 && sig[^1].Is("#[");

    /// <summary>
    /// When the caret starts an argument of a call (after "(" or ","), the called function / method / constructor,
    /// its parameters and the named arguments already written.
    /// </summary>
    private (string callee, List<PhpParameter> parameters, HashSet<string> used, bool isAttribute)? CallAtArgumentStart(List<Token> sig, TypeResolver resolver, PhpFileInfo info)
    {
        if (sig.Count == 0 || !(sig[^1].Is("(") || sig[^1].Is(","))) return null;
        int d = 0, open = -1;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = sig.Count - 1; i >= 0; i--)
        {
            var t = sig[i];
            if (t.Is(")") || t.Is("]")) d++;
            else if (t.Is("[") || t.Is("#[")) { if (d == 0) return null; d--; }
            else if (t.Is("(")) { if (d == 0) { open = i; break; } d--; }
            else if (d == 0 && t.Is(":") && i > 0 && sig[i - 1].Kind == TokKind.Identifier && (sig[i - 2 >= 0 ? i - 2 : 0].Is(",") || sig[i - 2 >= 0 ? i - 2 : 0].Is("(")))
                used.Add(sig[i - 1].Text);
            else if (t.Is(";") || t.Is("{") || t.Is("}")) return null;
        }
        if (open < 1 || sig[open - 1].Kind != TokKind.Identifier) return null;
        var name = sig[open - 1].Text;
        var before = open >= 2 ? sig[open - 2] : default;
        if (before.Text != null && (before.Is("#[") || before.IsWord("new") || before.Is(",") && IsInsideAttribute(sig, open)))
        {
            var cls = info.ResolveClass(name);
            var ctor = _index.FindMember(cls, "__construct", PhpMemberKind.Method);
            return ctor == null ? null : (cls.Split('\\').Last(), ctor.Value.Member.Parameters, used, !before.IsWord("new"));
        }
        if (before.Text != null && (before.Is("->") || before.Is("?->") || before.Is("::")))
        {
            var type = resolver.ResolveChainEndingAt(open - 2);
            foreach (var c in PhpTypes.ClassNames(type, resolver.CurrentClass?.Fqn))
                if (_index.FindMember(c, name, PhpMemberKind.Method) is { } m)
                    return ($"{c.Split('\\').Last()}::{name}()", m.Member.Parameters, used, false);
            return null;
        }
        if (NonCalls.Contains(name)) return null;
        var f = _index.GetFunction(name, info.Namespace);
        return f == null ? null : (f.Name + "()", f.Parameters, used, false);
    }

    private static readonly HashSet<string> NonCalls = new(StringComparer.OrdinalIgnoreCase)
        { "if", "elseif", "while", "for", "foreach", "switch", "match", "catch", "function", "fn", "array", "list", "isset", "empty", "unset", "use", "declare" };

    private static bool IsInsideAttribute(List<Token> sig, int index)
    {
        int d = 0;
        for (int i = index - 1; i >= 0; i--)
        {
            if (sig[i].Is("]")) d++;
            else if (sig[i].Is("#[")) { if (d == 0) return true; d--; }
            else if (sig[i].Is("[")) d--;
            else if (sig[i].Is(";") || sig[i].Is("{")) return false;
        }
        return false;
    }

    /// <summary>After "function" in a class: parent / interface / trait methods not declared yet (abstract ones first).</summary>
    private void CompleteOverrides(string text, PhpFileInfo info, PhpClass current, string prefix, CompletionResult result)
    {
        var declared = current.Members.Where(m => m.Kind == PhpMemberKind.Method).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var rm in _index.GetMembers(current.Fqn))
        {
            var m = rm.Member;
            if (m.Kind != PhpMemberKind.Method || m.IsMagic || m.IsFinal || m.Visibility == Visibility.Private || rm.Depth == 0 && !m.IsAbstract) continue;
            if (declared.Contains(m.Name) && !string.Equals(m.DeclaringClass, current.Fqn, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(m.DeclaringClass, current.Fqn, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Matches(m.Name, prefix)) continue;
            var stub = PhpInsight.BuildStubs(new[] { m }, f =>
            {
                var (n, edit) = ImportManager.PrepareClassReference(text, info, f);
                return edit == null ? n : "\\" + f; // no silent import from a completion: fully qualified instead
            }, "", "\n");
            // keep only "name(params): type" from the generated stub
            var sigLine = stub.Split('\n').First(l => l.Contains(" function "));
            var signature = sigLine[(sigLine.IndexOf(" function ", StringComparison.Ordinal) + 10)..];
            result.Entries.Add(new CompletionEntry
            {
                Label = m.Name, Insert = signature, Kind = EntryKind.Method, Tail = m.Signature(),
                TypeText = (m.IsAbstract ? "implement · " : "override · ") + m.DeclaringClass.Split('\\').Last(),
                Priority = m.IsAbstract ? 20 : 10, Bold = m.IsAbstract,
            });
        }
    }

    /// <summary>PhpStorm-like matching: prefix, or camel humps ("gU" → getUser, "HoCo" → HomeController).</summary>
    public static bool Matches(string candidate, string prefix)
    {
        if (prefix.Length == 0) return true;
        if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        if (char.ToLowerInvariant(candidate[0]) != char.ToLowerInvariant(prefix[0])) return false;
        static bool HumpStart(string s, int i) => i == 0 || char.IsUpper(s[i]) && !char.IsUpper(s[i - 1]) || s[i - 1] is '_' or '\\' or '-' or ':';
        int ci = 1;
        for (int pi = 1; pi < prefix.Length; pi++)
        {
            var ch = char.ToLowerInvariant(prefix[pi]);
            if (ci < candidate.Length && char.ToLowerInvariant(candidate[ci]) == ch && !(char.IsUpper(prefix[pi]) && !HumpStart(candidate, ci))) { ci++; continue; }
            int j = ci;
            while (j < candidate.Length && !(HumpStart(candidate, j) && char.ToLowerInvariant(candidate[j]) == ch)) j++;
            if (j >= candidate.Length) return false;
            ci = j + 1;
        }
        return true;
    }

    private static bool IsClosedString(Token t, int offset)
    {
        var s = t.Text;
        if (s.Length < 2) return false;
        var q = s[0];
        if (q is not ('\'' or '"' or '`')) return true;
        if (s[^1] != q) return false;
        int backslashes = 0;
        for (int i = s.Length - 2; i > 0 && s[i] == '\\'; i--) backslashes++;
        return backslashes % 2 == 0;
    }


    // ------------------------------------------------------------------ members

    private void CompleteMembers(string? receiverType, bool isStatic, string baseWord, PhpClass? current, string text, int offset, CompletionResult result)
    {
        var classes = PhpTypes.ClassNames(receiverType, current?.Fqn);
        bool viaKeyword = baseWord is "parent" or "self" or "static";
        bool nextIsParen = offset < text.Length && text[offset] == '(';
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (isStatic)
            result.Entries.Add(new CompletionEntry { Label = "class", Insert = "class", Kind = EntryKind.Keyword, TypeText = "string", Priority = 0.5 });

        foreach (var cls in classes)
        {
            var owner = _index.GetClass(cls);
            foreach (var rm in _index.GetMembers(cls))
            {
                var m = rm.Member;
                if (!_index.IsAccessible(rm, current?.Fqn)) continue;
                if (m.Name.StartsWith("__") && !(viaKeyword && m.Name.Equals("__construct", StringComparison.OrdinalIgnoreCase))) continue;

                if (isStatic)
                {
                    // Foo::  static methods, constants, enum cases, static properties.
                    // parent::/self::/static:: also allow instance methods (parent::method()).
                    bool ok = m.Kind is PhpMemberKind.Constant or PhpMemberKind.EnumCase || m.IsStatic || (viaKeyword && m.Kind == PhpMemberKind.Method);
                    if (!ok) continue;
                }
                else
                {
                    if (m.Kind is PhpMemberKind.Constant or PhpMemberKind.EnumCase) continue;
                    if (m.IsStatic && m.Kind == PhpMemberKind.Property) continue;
                }

                var key = m.Kind + ":" + m.Name;
                if (!added.Add(key)) continue;

                var declaring = m.DeclaringClass.Split('\\').Last();
                var type = PhpTypes.Short(m.Kind == PhpMemberKind.EnumCase ? declaring : m.EffectiveType);
                var own = rm.Depth == 0;
                switch (m.Kind)
                {
                    case PhpMemberKind.Method:
                        result.Entries.Add(new CompletionEntry
                        {
                            Label = m.Name,
                            Insert = nextIsParen ? m.Name : m.Name + "()",
                            CaretBack = !nextIsParen && m.Parameters.Count > 0 ? 1 : 0,
                            Kind = EntryKind.Method, Tail = m.Signature(), TypeText = type, Bold = own,
                            Strike = m.IsDeprecated, Description = Describe(m, declaring),
                            Priority = own ? 10 : 9 - Math.Min(rm.Depth, 5) * 0.5,
                        });
                        break;
                    case PhpMemberKind.Property:
                        var pname = isStatic ? "$" + m.Name : m.Name;
                        result.Entries.Add(new CompletionEntry
                        {
                            Label = pname, Insert = pname, Kind = EntryKind.Property, TypeText = type, Bold = own,
                            Strike = m.IsDeprecated, Description = Describe(m, declaring),
                            Priority = own ? 11 : 9.5 - Math.Min(rm.Depth, 5) * 0.5,
                        });
                        break;
                    case PhpMemberKind.Constant:
                    case PhpMemberKind.EnumCase:
                        result.Entries.Add(new CompletionEntry
                        {
                            Label = m.Name, Insert = m.Name, Kind = m.Kind == PhpMemberKind.EnumCase ? EntryKind.EnumCase : EntryKind.Constant,
                            TypeText = type, Bold = own, Description = Describe(m, declaring), Priority = own ? 12 : 9,
                        });
                        break;
                }
            }
            if (owner?.Kind == PhpClassKind.Enum && isStatic)
            {
                result.Entries.Add(new CompletionEntry { Label = "cases", Insert = "cases()", Kind = EntryKind.Method, TypeText = "array", Tail = "()", Priority = 8 });
                if (owner.Interfaces.Contains("BackedEnum"))
                {
                    result.Entries.Add(new CompletionEntry { Label = "from", Insert = "from()", CaretBack = 1, Kind = EntryKind.Method, TypeText = "static", Tail = "(int|string $value)", Priority = 8 });
                    result.Entries.Add(new CompletionEntry { Label = "tryFrom", Insert = "tryFrom()", CaretBack = 1, Kind = EntryKind.Method, TypeText = "?static", Tail = "(int|string $value)", Priority = 8 });
                }
            }
        }
    }

    private static string Describe(PhpMember m, string declaring)
    {
        var vis = m.Visibility.ToString().ToLowerInvariant();
        var head = m.Kind switch
        {
            PhpMemberKind.Method => $"{vis}{(m.IsStatic ? " static" : "")} function {m.Name}{m.Signature()}{(m.EffectiveType != null ? ": " + PhpTypes.Short(m.EffectiveType) : "")}",
            PhpMemberKind.Property => $"{vis}{(m.IsStatic ? " static" : "")}{(m.IsReadonly ? " readonly" : "")} {PhpTypes.Short(m.EffectiveType)} ${m.Name}",
            _ => $"const {m.Name}",
        };
        var doc = string.IsNullOrEmpty(m.Summary) ? "" : "\n\n" + m.Summary;
        return $"{m.DeclaringClass}\n{head}{doc}";
    }

    // ------------------------------------------------------------------ variables

    private void CompleteVariables(TypeResolver resolver, int end, PhpClass? current, int offset, PhpFileInfo info, CompletionResult result)
    {
        if (current != null)
            result.Entries.Add(new CompletionEntry { Label = "$this", Insert = "$this", Kind = EntryKind.Variable, TypeText = current.Name, Priority = 10 });
        foreach (var v in resolver.VisibleVariables(end))
        {
            var t = resolver.InferVariable(v, end);
            result.Entries.Add(new CompletionEntry { Label = v, Insert = v, Kind = EntryKind.Variable, TypeText = PhpTypes.Short(t), Priority = 9 });
        }
        foreach (var g in SuperGlobals)
            result.Entries.Add(new CompletionEntry { Label = g, Insert = g, Kind = EntryKind.Variable, TypeText = "array", Priority = 1 });
    }

    // ------------------------------------------------------------------ classes

    /// <summary>Returns the expected kind of class at the caret ("new", "extends", "implements", "type", "use", "trait", "attribute") or null.</summary>
    private static string? ClassContext(List<Token> sig, PhpClass? current)
    {
        if (sig.Count == 0) return null;
        var p = sig[^1];
        if (p.Kind == TokKind.Identifier)
        {
            var w = p.Text.ToLowerInvariant();
            switch (w)
            {
                case "new": return "new";
                case "extends": return sig.Count >= 3 && sig[^3].IsWord("interface") ? "interface" : "extends";
                case "implements": return "interface";
                case "instanceof": return "any-class";
                case "insteadof": return "trait";
                case "use":
                    // inside a class body: trait; at top level: namespace import
                    return current != null && p.Start > current.BodyStart ? "trait" : "use";
                case "throw": return null;
            }
        }
        if (p.Is("#[")) return "attribute";
        if (p.Is(",") || p.Is("|") || p.Is("?") || p.Is("(") || p.Is(":") || p.Is("&"))
        {
            // implements A, B
            for (int i = sig.Count - 1; i >= 0 && i > sig.Count - 30; i--)
            {
                if (sig[i].Is("{") || sig[i].Is(";")) break;
                if (sig[i].IsWord("implements")) return "interface";
                if (sig[i].IsWord("use") && current != null && sig[i].Start > current.BodyStart && !sig[i - 1].Is(")")) return "trait";
            }
            if (IsTypePosition(sig)) return "type";
        }
        // catch (Foo | Bar
        if ((p.Is("(") && sig.Count >= 2 && sig[^2].IsWord("catch")) || (p.Is("|") && InCatch(sig))) return "any-class";
        // property type after modifiers: "private " / "public readonly "
        if (p.Kind == TokKind.Identifier && p.Text.ToLowerInvariant() is "public" or "protected" or "private" or "readonly" or "static" or "var" or "const")
            return current != null ? "type" : null;
        return null;
    }

    private static bool InCatch(List<Token> sig)
    {
        for (int i = sig.Count - 1; i > 0 && i > sig.Count - 12; i--)
            if (sig[i].Is("(")) return sig[i - 1].IsWord("catch");
        return false;
    }

    private static bool IsTypePosition(List<Token> sig)
    {
        var p = sig[^1];
        // return type: function foo(...):
        if (p.Is(":") && sig.Count >= 2 && sig[^2].Is(")"))
        {
            int d = 0;
            for (int i = sig.Count - 2; i >= 0; i--)
            {
                if (sig[i].Is(")")) d++;
                else if (sig[i].Is("(")) { d--; if (d == 0) return i >= 1 && (sig[i - 1].IsWord("function") || sig[i - 1].IsWord("fn") || (i >= 2 && sig[i - 2].IsWord("function"))); }
            }
            return false;
        }
        if (p.Is("?") || p.Is("|") || p.Is("&") || p.Is("(") || p.Is(","))
        {
            // inside a parameter list?
            int d = 0;
            for (int i = sig.Count - 1; i >= 0; i--)
            {
                if (sig[i].Is(")")) d++;
                else if (sig[i].Is("(")) { if (d == 0) return i >= 1 && (sig[i - 1].IsWord("function") || sig[i - 1].IsWord("fn") || (i >= 2 && sig[i - 2].IsWord("function"))); d--; }
                else if (sig[i].Is(";") || sig[i].Is("{") || sig[i].Is("}")) break;
            }
            if (p.Is("?") || p.Is("|"))
            {
                // property type: private ?Foo
                var before = sig.Count >= 2 ? sig[^2] : default;
                return before.Kind == TokKind.Identifier && before.Text.ToLowerInvariant() is "public" or "protected" or "private" or "readonly" or "static";
            }
        }
        return false;
    }

    private void CompleteClasses(string text, PhpFileInfo info, string prefix, string ctx, CompletionResult result)
    {
        var lowerPrefix = prefix.ToLowerInvariant();
        bool qualified = prefix.Contains('\\');

        bool Accept(PhpClass c, string context) => context switch
        {
            "new" => c.Kind == PhpClassKind.Class && !c.IsAbstract,
            "extends" => c.Kind == PhpClassKind.Class && !c.IsFinal,
            "interface" => c.Kind == PhpClassKind.Interface,
            "trait" => c.Kind == PhpClassKind.Trait,
            "attribute" => c.Kind == PhpClassKind.Class && (c.IsAttribute || c.Name.EndsWith("Attribute") || c.Fqn.Contains("\\Attribute\\")),
            "type" => c.Kind != PhpClassKind.Trait,
            "use" or "qualified" => true,
            _ => c.Kind != PhpClassKind.Trait,
        };

        if (ctx == "use" || qualified)
        {
            // Qualified name. "ORM\Col" where ORM is an import alias (use NeoPHP\...\Mapping as ORM)
            // is resolved through the alias but inserted with the alias, as written.
            var absolute = prefix.StartsWith("\\");
            var typed = prefix.TrimStart('\\');
            var writtenNs = typed.Contains('\\') ? typed[..(typed.LastIndexOf('\\') + 1)] : "";
            var nsPart = writtenNs;
            if (!absolute && ctx != "use" && writtenNs.Length > 0)
                nsPart = info.ResolveClass(writtenNs.TrimEnd('\\')).TrimStart('\\') + "\\";
            var lead = absolute ? "\\" : "";
            var namePrefix = typed[writtenNs.Length..];
            foreach (var ns in _index.AllNamespaces())
            {
                if (!ns.StartsWith(nsPart, StringComparison.OrdinalIgnoreCase) || ns.Length <= nsPart.Length) continue;
                var rest = ns[nsPart.Length..];
                if (rest.Contains('\\') || !Matches(rest, namePrefix)) continue;
                result.Entries.Add(new CompletionEntry { Label = lead + writtenNs + rest + "\\", Insert = lead + writtenNs + rest + "\\", Kind = EntryKind.Namespace, Priority = 6 });
            }
            foreach (var c in _index.AllClasses())
            {
                if (!c.Fqn.StartsWith(nsPart, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = c.Fqn[nsPart.Length..];
                if (rest.Contains('\\') && ctx != "use") continue;
                if (!Matches(rest, namePrefix) && !(ctx == "use" && rest.Contains('\\'))) continue;
                if (ctx == "use" && rest.Contains('\\')) continue;
                if (!Accept(c, ctx == "attribute" ? "attribute" : ctx)) continue;
                var label = lead + writtenNs + rest;
                result.Entries.Add(new CompletionEntry
                {
                    Label = label, Insert = ctx == "attribute" ? label + "()" : label, CaretBack = ctx == "attribute" ? 1 : 0,
                    Kind = KindOf(c), Description = DescribeClass(c), Tail = c.Namespace.Length > 0 ? $" ({c.Namespace})" : null,
                    Priority = c.IsBuiltin ? 4 : c.File?.Contains("\\vendor\\") == true ? 5 : 7, Strike = c.IsDeprecated,
                });
            }
            return;
        }

        foreach (var c in _index.AllClasses())
        {
            if (!Matches(c.Name, prefix)) continue;
            if (!Accept(c, ctx)) continue;
            var (name, edit) = ImportManager.PrepareClassReference(text, info, c.Fqn);
            bool isNew = ctx == "new";
            var inProject = c.File != null && !c.File.Contains("\\vendor\\");
            result.Entries.Add(new CompletionEntry
            {
                Label = c.Name,
                Insert = isNew || ctx == "attribute" ? name + "()" : name,
                CaretBack = 0,
                CtorFqn = isNew || ctx == "attribute" ? c.Fqn : null,
                Kind = KindOf(c),
                Tail = c.Namespace.Length > 0 ? $" ({c.Namespace})" : null,
                Description = DescribeClass(c),
                ImportFqn = edit != null ? c.Fqn : null,
                Strike = c.IsDeprecated,
                Priority = inProject ? 7 : c.IsBuiltin ? 4 : 5,
            });
        }
    }

    private static EntryKind KindOf(PhpClass c) => c.Kind switch
    {
        PhpClassKind.Interface => EntryKind.Interface,
        PhpClassKind.Trait => EntryKind.Trait,
        PhpClassKind.Enum => EntryKind.Enum,
        _ => EntryKind.Class,
    };

    private static string DescribeClass(PhpClass c)
    {
        var head = $"{c.KindLabel} {c.Fqn}";
        if (c.Parent != null) head += $"\nextends {c.Parent}";
        if (c.Interfaces.Count > 0 && c.Kind != PhpClassKind.Enum) head += $"\nimplements {string.Join(", ", c.Interfaces.Select(i => i.Split('\\').Last()))}";
        return string.IsNullOrEmpty(c.Summary) ? head : head + "\n\n" + c.Summary;
    }

    // ------------------------------------------------------------------ strings & docs

    private void CompleteString(List<Token> all, Token str, int offset, CompletionResult result)
    {
        // $this->render('home/index')  → template names
        var sig = PhpLexer.Significant(all);
        int idx = sig.FindLastIndex(t => t.Start == str.Start);
        if (idx < 2 || !sig[idx - 1].Is("(")) return;
        var fn = sig[idx - 2].Text?.ToLowerInvariant();
        if (fn is not ("render" or "renderview" or "view" or "include" or "extend" or "partial" or "renderblock" or "template")) return;
        if (_projectRoot == null) return;
        var dir = Path.Combine(_projectRoot, "templates");
        if (!Directory.Exists(dir)) return;
        result.ReplaceStart = str.Start + 1;
        foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".php" or ".twig" or ".phtml")) continue;
            var rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            var name = rel.EndsWith(".html.twig") ? rel[..^10] : rel[..rel.LastIndexOf('.')];
            result.Entries.Add(new CompletionEntry { Label = name, Insert = name, Kind = EntryKind.Template, TypeText = Path.GetExtension(f).TrimStart('.'), Priority = 5 });
        }
    }

    private void CompleteDoc(string text, int offset, string? filePath, CompletionResult result)
    {
        int start = offset;
        while (start > 0 && (PhpLexer.IsIdentPart(text[start - 1]) || text[start - 1] == '-' || text[start - 1] == '\\')) start--;
        if (start > 0 && text[start - 1] == '@')
        {
            result.ReplaceStart = start;
            foreach (var t in DocTags) result.Entries.Add(new CompletionEntry { Label = t, Insert = t + " ", Kind = EntryKind.DocTag, Priority = 5 });
            return;
        }
        // type after @var/@param/@return/@throws
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var line = text[lineStart..start];
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"@(var|param|return|throws|property(-read|-write)?|method|mixin|extends|implements|see)\s+([\w\\|?<>,\[\] ]*\|)?$"))
        {
            result.ReplaceStart = start;
            var info = PhpParser.Parse(text, filePath);
            CompleteClasses(text, info, text[start..offset], "type", result);
            foreach (var k in TypeKeywords.Where(k => k != "?"))
                result.Entries.Add(new CompletionEntry { Label = k, Insert = k, Kind = EntryKind.Keyword, Priority = 2 });
        }
    }
}
