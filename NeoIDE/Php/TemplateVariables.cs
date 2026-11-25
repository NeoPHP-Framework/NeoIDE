using System.IO;
using System.Text.RegularExpressions;
using NeoIDE.Editor;
using NeoIDE.Services;
using NeoIDE.Services.Diagnostics;

namespace NeoIDE.Php;

/// <summary>A variable a template receives: <see cref="File"/> / <see cref="Offset"/> point at the key in render().</summary>
public sealed record TemplateVar(string Name, string? Type, string File, int Offset, string Source);

/// <summary>
/// Variables of the templates: the arrays given to <c>render('home/index', ['user' => $user])</c> (and compact()) in
/// src/, with the type of each value. A layout / a partial also receives the variables of the templates that
/// extend / include it. Used by the completion, the navigation, the hover and the checks of PHP views and Twig.
/// </summary>
public static class TemplateVariables
{
    private sealed record Site(string Template, List<TemplateVar> Vars, bool Dynamic);

    private static readonly object Gate = new();
    private static List<Site>? _sites;
    private static Dictionary<string, List<string>> _receivedFrom = new(StringComparer.OrdinalIgnoreCase);

    public const string PhpTemplateClass = "NeoPHP\\Component\\View\\Template\\Template";

    static TemplateVariables()
    {
        NeoProject.Changed += () => { lock (Gate) _sites = null; };
    }

    public static void Invalidate() { lock (Gate) _sites = null; }

    /// <summary>Variables of a template file (merged when several controllers render it).</summary>
    public static IReadOnlyList<TemplateVar> For(string? templateFile)
    {
        if (templateFile == null || !NeoProject.IsLoaded) return Array.Empty<TemplateVar>();
        lock (Gate)
        {
            EnsureBuilt();
            var all = new List<TemplateVar>();
            Collect(Path.GetFullPath(templateFile), all, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return all.GroupBy(v => v.Name).Select(g =>
            {
                var types = g.Select(v => v.Type).Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();
                var first = g.First();
                return first with { Type = types.Count == 0 ? null : string.Join("|", types) };
            }).OrderBy(v => v.Name).ToList();
        }
    }

    /// <summary>
    /// True when the variables of the template are not all known: nothing renders it, or a render() gives a
    /// variable array (<c>render('x', $params)</c>) — no "variable not passed" check then.
    /// </summary>
    public static bool IsOpen(string templateFile)
    {
        lock (Gate)
        {
            EnsureBuilt();
            return IsOpen(Path.GetFullPath(templateFile), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static bool IsOpen(string file, HashSet<string> seen)
    {
        if (!seen.Add(file)) return false;
        var direct = _sites!.Where(s => string.Equals(s.Template, file, StringComparison.OrdinalIgnoreCase)).ToList();
        var parents = _receivedFrom.GetValueOrDefault(file) ?? new List<string>();
        if (direct.Count == 0 && parents.Count == 0) return true;
        return direct.Any(s => s.Dynamic) || parents.Any(p => IsOpen(p, seen));
    }

    private static void Collect(string file, List<TemplateVar> into, HashSet<string> seen)
    {
        if (!seen.Add(file)) return;
        foreach (var s in _sites!.Where(s => string.Equals(s.Template, file, StringComparison.OrdinalIgnoreCase))) into.AddRange(s.Vars);
        foreach (var parent in _receivedFrom.GetValueOrDefault(file) ?? new List<string>()) Collect(parent, into, seen);
    }

    private static void EnsureBuilt()
    {
        if (_sites != null) return;
        var sites = new List<Site>();
        var received = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var root = Workspace.Root;
        if (root != null)
        {
            var src = Path.Combine(root, "src");
            if (Directory.Exists(src))
                foreach (var f in SafeFiles(src))
                {
                    string text;
                    try { text = File.ReadAllText(f); } catch { continue; }
                    if (!text.Contains("render", StringComparison.Ordinal)) continue;
                    ScanPhp(f, text, sites, received, controller: true);
                }
            foreach (var t in NeoProject.TemplateNames.Select(NeoProject.Template).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string text;
                try { text = File.ReadAllText(t!); } catch { continue; }
                if (t!.EndsWith(".twig", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var r in NeoInsight.All(text, "Twig").Where(r => r.Kind == NeoRefKind.Template))
                        if (NeoProject.Template(r.Value) is { } target) Add(received, target, t);
                }
                else ScanPhp(t, text, sites, received, controller: false);
            }
        }
        _sites = sites;
        _receivedFrom = received;
    }

    private static void Add(Dictionary<string, List<string>> received, string target, string from)
    {
        target = Path.GetFullPath(target);
        var list = received.TryGetValue(target, out var l) ? l : received[target] = new List<string>();
        if (!list.Contains(from, StringComparer.OrdinalIgnoreCase)) list.Add(Path.GetFullPath(from));
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.php", SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>render() / renderView() of a controller, extend() / include() / insert() of a PHP view.</summary>
    private static void ScanPhp(string file, string text, List<Site> sites, Dictionary<string, List<string>> received, bool controller)
    {
        PhpFileInfo? info = null;
        List<Token>? sig = null;
        foreach (var r in NeoInsight.All(text, "PHP").Where(r => r.Kind == NeoRefKind.Template))
        {
            var target = NeoProject.Template(r.Value);
            if (target == null) continue;
            target = Path.GetFullPath(target);
            if (!controller) Add(received, target, file); // the layout / partial receives the variables of this view
            var args = ParseArguments(text, r.Start + r.Length + 1);
            if (args.Entries.Count == 0 && !args.Dynamic && !controller) continue;
            info ??= PhpParser.Parse(text, file);
            sig ??= PhpLexer.Significant(PhpLexer.Tokenize(text));
            var source = SourceLabel(text, r.Start, info, file);
            var vars = new List<TemplateVar>();
            foreach (var (name, keyOffset, exprStart, exprEnd, isCompact) in args.Entries)
            {
                string? type = null;
                try { type = controller ? TypeOf(info, sig, exprStart, exprEnd, isCompact ? "$" + name : null) : null; } catch { }
                vars.Add(new TemplateVar(name, type, file, keyOffset, source));
            }
            sites.Add(new Site(target, vars, args.Dynamic));
        }
    }

    private static string SourceLabel(string text, int offset, PhpFileInfo info, string file)
    {
        var fn = Regex.Matches(text[..offset], @"function\s+(\w+)").LastOrDefault()?.Groups[1].Value;
        var cls = info.ClassAt(offset)?.Name;
        if (cls == null) return NeoProject.TemplateNameOf(file) is { } t ? $"template {t}" : Path.GetFileName(file);
        return fn != null ? $"{cls}::{fn}()" : cls;
    }

    private static string? TypeOf(PhpFileInfo info, List<Token> all, int exprStart, int exprEnd, string? variable)
    {
        var upTo = all.TakeWhile(t => t.End <= exprEnd).ToList();
        var resolver = new TypeResolver(Workspace.Index, info, upTo, exprEnd);
        if (variable != null) return resolver.InferVariable(variable, upTo.Count);
        var from = upTo.FindIndex(t => t.Start >= exprStart);
        return from < 0 ? null : resolver.ResolveRange(from, upTo.Count);
    }

    public sealed record Arguments(List<(string name, int keyOffset, int exprStart, int exprEnd, bool compact)> Entries, bool Dynamic);

    /// <summary>The variables of the argument after the template name: <c>['a' => expr, ...]</c>, <c>array(...)</c> or <c>compact('a', 'b')</c>.</summary>
    public static Arguments ParseArguments(string text, int afterName)
    {
        var entries = new List<(string, int, int, int, bool)>();
        int i = SkipWs(text, afterName);
        if (i >= text.Length || text[i] != ',') return new Arguments(entries, false);
        i = SkipWs(text, i + 1);
        if (i >= text.Length) return new Arguments(entries, false);
        int bodyStart, bodyEnd;
        if (text[i] == '[') { bodyStart = i + 1; bodyEnd = Close(text, i, '[', ']'); }
        else if (Regex.IsMatch(text[i..Math.Min(text.Length, i + 8)], @"^array\s*\(", RegexOptions.IgnoreCase)) { var p = text.IndexOf('(', i); bodyStart = p + 1; bodyEnd = Close(text, p, '(', ')'); }
        else if (Regex.IsMatch(text[i..Math.Min(text.Length, i + 10)], @"^compact\s*\(", RegexOptions.IgnoreCase))
        {
            var p = text.IndexOf('(', i);
            var end = Close(text, p, '(', ')');
            if (end < 0) return new Arguments(entries, true);
            foreach (Match m in Regex.Matches(text[(p + 1)..end], @"['""](?<n>\w+)['""]"))
                entries.Add((m.Groups["n"].Value, p + 1 + m.Groups["n"].Index, p + 1 + m.Index, p + 1 + m.Index + m.Length, true));
            return new Arguments(entries, false);
        }
        else return new Arguments(entries, text[i] != ')');
        if (bodyEnd < 0) return new Arguments(entries, true);
        bool dynamic = false;
        foreach (var (s, e) in SplitTopLevel(text, bodyStart, bodyEnd))
        {
            var part = text[s..e];
            var m = Regex.Match(part, @"^\s*(?<q>['""])(?<k>\w+)\k<q>\s*=>\s*");
            if (m.Success) entries.Add((m.Groups["k"].Value, s + m.Groups["k"].Index, s + m.Length, e, false));
            else if (part.Trim().Length > 0) dynamic = true; // ...$spread or a computed key
        }
        return new Arguments(entries, dynamic);
    }

    private static int SkipWs(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    /// <summary>Offset of the bracket closing the one at <paramref name="open"/> (strings skipped), -1 when unclosed.</summary>
    private static int Close(string text, int open, char o, char c)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char ch = text[i];
            if (ch is '\'' or '"') { i = SkipString(text, i); continue; }
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') { depth--; if (depth == 0) return ch == c ? i : -1; }
        }
        return -1;
    }

    private static int SkipString(string text, int i)
    {
        var q = text[i];
        for (i++; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == q) return i;
        }
        return text.Length;
    }

    private static IEnumerable<(int start, int end)> SplitTopLevel(string text, int start, int end)
    {
        int depth = 0, s = start;
        for (int i = start; i < end; i++)
        {
            char ch = text[i];
            if (ch is '\'' or '"') { i = SkipString(text, i); continue; }
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            else if (ch == ',' && depth == 0) { yield return (s, i); s = i + 1; }
        }
        if (text[s..end].Trim().Length > 0) yield return (s, end);
    }

    // ------------------------------------------------------------------ PHP views

    /// <summary>True when the file is a PHP template of templates/ (variables come from render()).</summary>
    public static bool IsPhpView(string? file) =>
        file != null && file.EndsWith(".php", StringComparison.OrdinalIgnoreCase) && NeoProject.IsLoaded && NeoProject.TemplateNameOf(Path.GetFullPath(file)) != null;

    /// <summary>
    /// Doc comments declaring the variables of a PHP view (and <c>$this</c>, the Template object), put before the
    /// buffer for the completion so that <c>$user-></c> knows the class of $user.
    /// </summary>
    public static string PhpViewPrelude(string file)
    {
        var sb = new System.Text.StringBuilder("<?php ");
        if (Workspace.Index.GetClass(PhpTemplateClass) != null) sb.Append($"/** @var \\{PhpTemplateClass} $this */ ");
        foreach (var v in For(file)) sb.Append($"/** @var {(string.IsNullOrEmpty(v.Type) ? "mixed" : v.Type)} ${v.Name} */ ");
        sb.Append("?>");
        return sb.ToString();
    }

    private static readonly HashSet<string> Superglobals = new() { "this", "GLOBALS", "_SERVER", "_GET", "_POST", "_FILES", "_COOKIE", "_SESSION", "_REQUEST", "_ENV" };

    /// <summary>Hints: variables used by a PHP view that no render() passes to it.</summary>
    public static List<Squiggle> CheckPhpView(string text, string file)
    {
        var list = new List<Squiggle>();
        if (!AppSettings.Current.NeoChecks || !IsPhpView(file) || IsOpen(file)) return list;
        var known = For(file).Select(v => v.Name).ToHashSet();
        var reported = new HashSet<string>();
        var code = PhpCodeOnly(text);
        foreach (Match m in Regex.Matches(code, @"\$(?<n>[A-Za-z_]\w*)"))
        {
            var n = m.Groups["n"].Value;
            if (known.Contains(n) || Superglobals.Contains(n) || reported.Contains(n)) continue;
            reported.Add(n);
            var v = Regex.Escape("$" + n);
            // defined in the view (assignment, foreach, closure parameter) or guarded (isset / ?? / empty)
            if (Regex.IsMatch(code, v + @"\s*(?:=(?!=)|\?\?)|\bas\s+(?:\$\w+\s*=>\s*)?&?" + v + @"\b|\b(?:isset|empty)\s*\([^)]*" + v + @"\b|(?:function|fn)\s*\([^)]*" + v + @"\b|\buse\s*\([^)]*" + v + @"\b|\blist\s*\([^)]*" + v + @"\b|\[[^\]]*" + v + @"\b[^\]]*\]\s*=(?!=)"))
                continue;
            list.Add(new Squiggle(m.Index, m.Length, $"Variable '${n}' is not passed to this template by the render() calls of src/", Severity.Info, "neophp"));
        }
        return list;
    }

    /// <summary>The text with everything outside &lt;?php ?&gt; blanked (offsets kept), strings and comments blanked too.</summary>
    private static string PhpCodeOnly(string text)
    {
        var chars = text.ToCharArray();
        var tokens = PhpLexer.Tokenize(text);
        foreach (var t in tokens)
            if (t.Kind is TokKind.InlineHtml or TokKind.Comment or TokKind.DocComment || t.Kind == TokKind.String && !t.Text.StartsWith('"'))
                for (int i = t.Start; i < t.End && i < chars.Length; i++) if (chars[i] != '\n') chars[i] = ' ';
        return new string(chars);
    }

    /// <summary>The template variable written at <paramref name="offset"/> of a PHP view ($name).</summary>
    public static TemplateVar? PhpViewVariableAt(string text, int offset, string file)
    {
        if (!IsPhpView(file)) return null;
        int s = offset, e = offset;
        while (s > 0 && PhpLexer.IsIdentPart(text[s - 1])) s--;
        while (e < text.Length && PhpLexer.IsIdentPart(text[e])) e++;
        if (s == 0 || text[s - 1] != '$' || e <= s) return null;
        var name = text[s..e];
        return For(file).FirstOrDefault(v => v.Name == name);
    }

    public static string Describe(TemplateVar v) =>
        $"${v.Name}: {(string.IsNullOrEmpty(v.Type) ? "mixed" : PhpTypes.Short(v.Type))}\npassed by {v.Source}";
}
