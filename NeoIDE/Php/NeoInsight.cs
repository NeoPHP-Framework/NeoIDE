using System.Text.RegularExpressions;
using NeoIDE.Editor;
using NeoIDE.Services;
using NeoIDE.Services.Diagnostics;

namespace NeoIDE.Php;

public enum NeoRefKind { Route, Template, Env, Parameter, Service, Controller }

/// <summary>A NeoPHP name written in a string: <see cref="Start"/> / <see cref="Length"/> cover the name itself.</summary>
public sealed record NeoRef(NeoRefKind Kind, string Value, int Start, int Length, bool Checked);

public sealed record NeoTarget(string File, int Offset, string Description);

/// <summary>
/// NeoPHP aware strings: route names (generateUrl, redirectToRoute, path), templates (render, extend, include),
/// .env variables (%env()%, $_ENV, getenv), parameters (%name%) and services (get('...'), '@id').
/// Navigation (Ctrl+click), documentation, completion and "unknown name" checks.
/// </summary>
public static class NeoInsight
{
    private sealed record Rule(NeoRefKind Kind, Regex Pattern, bool Checked);

    private const string Q = @"(?<q>['""])(?<v>[^'""\r\n$]*)\k<q>";

    private static readonly Rule[] PhpRules =
    {
        new(NeoRefKind.Route, new Regex(@"(?:->|::|\b)(?:generateUrl|redirectToRoute)\s*\(\s*" + Q, RegexOptions.Compiled), true),
        new(NeoRefKind.Route, new Regex(@"->(?:path|url)\s*\(\s*" + Q, RegexOptions.Compiled), true),
        new(NeoRefKind.Route, new Regex(@"->generate\s*\(\s*" + Q, RegexOptions.Compiled), false),
        new(NeoRefKind.Template, new Regex(@"->(?:render|renderView|extend|include|insert)\s*\(\s*" + Q, RegexOptions.Compiled), true),
        new(NeoRefKind.Env, new Regex(@"\$_(?:ENV|SERVER)\s*\[\s*" + Q, RegexOptions.Compiled), false),
        new(NeoRefKind.Env, new Regex(@"\b(?:getenv|env)\s*\(\s*" + Q, RegexOptions.Compiled), false),
        new(NeoRefKind.Service, new Regex(@"->(?:get|has)\s*\(\s*" + Q, RegexOptions.Compiled), false),
    };

    private static readonly Rule[] TwigRules =
    {
        new(NeoRefKind.Template, new Regex(@"\{%-?\s*(?:extends|include|embed|import|from|use)\s+" + Q, RegexOptions.Compiled), true),
        new(NeoRefKind.Template, new Regex(@"\b(?:include|source)\s*\(\s*" + Q, RegexOptions.Compiled), true),
        new(NeoRefKind.Route, new Regex(@"\b(?:path|url)\s*\(\s*" + Q, RegexOptions.Compiled), true),
    };

    /// <summary>%env(NAME)%, %env(bool:NAME)%... anywhere (YAML, and PHP strings).</summary>
    private static readonly Regex EnvPlaceholder = new(@"%env\((?:[\w-]+:)*(?<v>[A-Za-z_][A-Za-z0-9_]*)\)%", RegexOptions.Compiled);
    private static readonly Regex ParamPlaceholder = new(@"(?<!%)%(?!env\()(?<v>[A-Za-z_][\w.]*)%", RegexOptions.Compiled);
    private static readonly Regex ServiceRef = new(@"(?<=['""\s\[,])@(?<v>[A-Za-z_\\][\w\\.]*)", RegexOptions.Compiled);
    private static readonly Regex ControllerRef = new(@"\bcontroller\s*:\s*['""]?(?<v>[A-Za-z_\\][\w\\]*::\w+)", RegexOptions.Compiled);

    /// <summary>Every reference of a file of the given language ("PHP", "Twig", "YAML").</summary>
    public static List<NeoRef> All(string text, string? language)
    {
        var list = new List<NeoRef>();
        void Add(NeoRefKind kind, Match m, bool check)
        {
            var g = m.Groups["v"];
            // the string must be the whole argument: render('a/' . $x) is dynamic
            var after = g.Index + g.Length + (m.Groups["q"].Success ? 1 : 0);
            while (after < text.Length && text[after] is ' ' or '\t') after++;
            var dynamic = m.Groups["q"].Success && after < text.Length && text[after] is '.' or '~';
            list.Add(new NeoRef(kind, g.Value, g.Index, g.Length, check && !dynamic && g.Value.Length > 0));
        }
        var rules = language switch { "PHP" => PhpRules, "Twig" => TwigRules, _ => Array.Empty<Rule>() };
        foreach (var r in rules)
            foreach (Match m in r.Pattern.Matches(text)) Add(r.Kind, m, r.Checked);
        if (language is "PHP" or "YAML" or "Twig")
        {
            foreach (Match m in EnvPlaceholder.Matches(text)) Add(NeoRefKind.Env, m, true);
            if (language is "YAML" || language == "PHP")
                foreach (Match m in ParamPlaceholder.Matches(text))
                    if (language == "YAML" || InsideQuotes(text, m.Index)) Add(NeoRefKind.Parameter, m, language == "YAML");
        }
        if (language == "YAML")
        {
            foreach (Match m in ServiceRef.Matches(text)) Add(NeoRefKind.Service, m, false);
            foreach (Match m in ControllerRef.Matches(text)) Add(NeoRefKind.Controller, m, false);
        }
        // a %param% match inside a %env()% placeholder or a '@' in an email is not a reference
        return list.Where(r => !(r.Kind == NeoRefKind.Parameter && list.Any(o => o.Kind == NeoRefKind.Env && o.Start <= r.Start && r.Start <= o.Start + o.Length)))
                   .OrderBy(r => r.Start).ToList();
    }

    private static bool InsideQuotes(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var before = text[lineStart..index];
        return before.Count(c => c == '\'') % 2 == 1 || before.Count(c => c == '"') % 2 == 1;
    }

    /// <summary>The reference around <paramref name="offset"/> (the caret may be just after the name).</summary>
    public static NeoRef? At(string text, int offset, string? language) =>
        All(text, language).FirstOrDefault(r => offset >= r.Start && offset <= r.Start + r.Length);

    public static NeoTarget? Resolve(NeoRef r)
    {
        switch (r.Kind)
        {
            case NeoRefKind.Route when NeoProject.Route(r.Value) is { } route:
                return new NeoTarget(route.File, route.Offset, DescribeRoute(route));
            case NeoRefKind.Template when NeoProject.Template(r.Value) is { } file:
                return new NeoTarget(file, 0, $"Template {r.Value}\n{RelativePath(file)}");
            case NeoRefKind.Env when NeoProject.Env(r.Value) is { Count: > 0 } defs:
                var last = defs[^1];
                return new NeoTarget(last.File, last.Offset, DescribeEnv(r.Value, defs));
            case NeoRefKind.Parameter when NeoProject.Parameter(r.Value) is { } p:
                return new NeoTarget(p.File, p.Offset, $"Parameter %{p.Name}%\n{p.Value}\n\n{RelativePath(p.File)}:{p.Line}");
            case NeoRefKind.Service:
            {
                var id = r.Value.TrimStart('@', '\\');
                if (Workspace.Index.GetClass(id) is { File: not null } cls) return new NeoTarget(cls.File, cls.Offset, PhpInsight.DescribeClass(cls));
                if (NeoProject.Service(id) is { } s) return new NeoTarget(s.File, s.Offset, $"Service {s.Name}\n{s.Value}\n\n{RelativePath(s.File)}:{s.Line}");
                return null;
            }
            case NeoRefKind.Controller:
            {
                var parts = r.Value.TrimStart('\\').Split("::");
                if (parts.Length == 2 && Workspace.Index.FindMember(parts[0], parts[1], PhpMemberKind.Method) is { } m && m.Member.File != null)
                    return new NeoTarget(m.Member.File, m.Member.Offset, PhpInsight.DescribeMember(m.Member));
                if (Workspace.Index.GetClass(parts[0]) is { File: not null } c) return new NeoTarget(c.File, c.Offset, PhpInsight.DescribeClass(c));
                return null;
            }
        }
        return null;
    }

    /// <summary>Documentation shown on hover: also for parameters of the kernel (no file).</summary>
    public static string? Describe(NeoRef r)
    {
        if (Resolve(r) is { } t) return t.Description;
        if (r.Kind == NeoRefKind.Parameter && NeoProject.KernelParameters.Contains(r.Value)) return $"Parameter %{r.Value}%\nset by the kernel";
        return null;
    }

    public static string? Problem(NeoRef r)
    {
        if (!r.Checked || !NeoProject.IsLoaded) return null;
        return r.Kind switch
        {
            NeoRefKind.Route when NeoProject.Route(r.Value) == null => $"Unknown route '{r.Value}'",
            // "@namespace/..." templates may come from packages: only the project's own names are checked
            NeoRefKind.Template when !r.Value.StartsWith("@") && NeoProject.Template(r.Value) == null => $"Template '{r.Value}' not found in templates/",
            NeoRefKind.Env when NeoProject.Env(r.Value).Count == 0 => $"Environment variable '{r.Value}' is not defined in the .env files",
            NeoRefKind.Parameter when NeoProject.Parameter(r.Value) == null && !NeoProject.KernelParameters.Contains(r.Value) => $"Unknown parameter '%{r.Value}%'",
            _ => null,
        };
    }

    /// <summary>NeoPHP checks of a buffer, as editor underlines (warnings).</summary>
    public static List<Squiggle> Check(string text, string? language)
    {
        var list = new List<Squiggle>();
        if (!AppSettings.Current.NeoChecks || !NeoProject.IsLoaded) return list;
        foreach (var r in All(text, language))
            if (Problem(r) is { } msg)
            {
                var names = Names(r.Kind);
                var close = names.Where(n => Levenshtein(n, r.Value) <= Math.Max(2, r.Value.Length / 4)).OrderBy(n => Levenshtein(n, r.Value)).FirstOrDefault();
                list.Add(new Squiggle(r.Start, Math.Max(1, r.Length), msg + (close != null ? $" — did you mean '{close}'?" : ""), Severity.Warning, "neophp"));
            }
        return list;
    }

    /// <summary>Existing names close to a faulty one (quick fix "Change to ...").</summary>
    public static List<string> Suggestions(NeoRef r, int max = 3) => TwigInsight.Suggest(r.Value, Names(r.Kind), max);

    public static IReadOnlyList<string> Names(NeoRefKind kind) => kind switch
    {
        NeoRefKind.Route => NeoProject.RouteNames,
        NeoRefKind.Template => NeoProject.TemplateNames.Where(n => !n.EndsWith(".php", StringComparison.OrdinalIgnoreCase)).ToList(),
        NeoRefKind.Env => NeoProject.EnvNames,
        NeoRefKind.Parameter => NeoProject.ParameterNames,
        NeoRefKind.Service => Workspace.Index.AllClasses().Where(c => c.File != null && !c.File.Contains("\\vendor\\") && c.Kind == PhpClassKind.Class && !c.IsAbstract).Select(c => c.Fqn).OrderBy(n => n).ToList(),
        _ => Array.Empty<string>(),
    };

    /// <summary>Completion of the name at the caret (null when the caret is not in a NeoPHP string).</summary>
    public static (int start, List<CompletionEntry> entries)? Complete(string text, int caret, string? language)
    {
        var r = At(text, caret, language);
        if (r == null || r.Kind == NeoRefKind.Controller) return null;
        var entries = new List<CompletionEntry>();
        foreach (var n in Names(r.Kind))
        {
            string? detail = r.Kind switch
            {
                NeoRefKind.Route => NeoProject.Route(n) is { } route ? $"{route.Methods} {route.Path}" : null,
                NeoRefKind.Template => NeoProject.Template(n) is { } f ? RelativePath(f) : null,
                NeoRefKind.Env => NeoProject.Env(n) is { Count: > 0 } d ? Mask(n, d[^1].Value) : null,
                NeoRefKind.Parameter => NeoProject.Parameter(n)?.Value,
                _ => null,
            };
            entries.Add(new CompletionEntry
            {
                Label = n, Insert = n,
                Kind = r.Kind switch { NeoRefKind.Service => EntryKind.Class, NeoRefKind.Template => EntryKind.Template, NeoRefKind.Env or NeoRefKind.Parameter => EntryKind.Variable, _ => EntryKind.Constant },
                TypeText = detail, Priority = 10,
            });
        }
        return (r.Start, entries);
    }

    // ------------------------------------------------------------------ descriptions

    public static string DescribeRoute(NeoRoute r) =>
        $"Route '{r.Name}'\n{r.Methods} {r.Path}" + (r.Controller != null ? $"\n→ {r.Controller}" : "") + $"\n\n{RelativePath(r.File)}:{r.Line}";

    private static string DescribeEnv(string name, IReadOnlyList<NeoDefinition> defs)
    {
        var s = $"{name} = {Mask(name, defs[^1].Value)}";
        s += "\n\n" + string.Join("\n", defs.Select(d => $"{System.IO.Path.GetFileName(d.File)}:{d.Line}  {Mask(name, d.Value)}"));
        return s;
    }

    private static string Mask(string name, string value) =>
        Regex.IsMatch(name, "SECRET|PASSWORD|PASS$|TOKEN|PRIVATE|_KEY$|API_KEY", RegexOptions.IgnoreCase) && value.Length > 0 ? "••••••" : value;

    private static string RelativePath(string file) =>
        Workspace.Root != null ? System.IO.Path.GetRelativePath(Workspace.Root, file).Replace('\\', '/') : file;

    private static int Levenshtein(string a, string b)
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
