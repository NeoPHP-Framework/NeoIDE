using System.IO;
using System.Text.RegularExpressions;

namespace NeoIDE.Php;

public sealed record NeoRoute(string Name, string Path, string Methods, string? Controller, string File, int Offset, int Line);
public sealed record NeoDefinition(string Name, string Value, string File, int Offset, int Line);

/// <summary>
/// What NeoPHP knows by name: routes (#[Route] attributes and config/routes*.yaml), templates (templates/ and the
/// namespaces of view.yaml), .env variables, container parameters and services of services.yaml.
/// Built from the files (no PHP process), refreshed when they change.
/// </summary>
public static class NeoProject
{
    private static readonly object Gate = new();
    private static string? _root;
    private static Dictionary<string, NeoRoute> _routes = new();
    private static List<NeoRoute> _allRoutes = new();
    private static Dictionary<string, List<NeoDefinition>> _env = new();
    private static Dictionary<string, NeoDefinition> _parameters = new();
    private static Dictionary<string, NeoDefinition> _services = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _templates = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _namespaces = new(StringComparer.OrdinalIgnoreCase);

    public static event Action? Changed;

    public static readonly string[] KernelParameters =
        { "kernel.root_path", "kernel.config_path", "kernel.public_path", "kernel.templates_path", "kernel.cache_path", "kernel.environment", "kernel.debug", "kernel.version" };

    public static bool IsLoaded => _root != null;

    public static IReadOnlyCollection<NeoRoute> Routes { get { lock (Gate) return _allRoutes.ToList(); } }
    public static NeoRoute? Route(string name) { lock (Gate) return _routes.GetValueOrDefault(name); }
    public static IReadOnlyList<string> RouteNames { get { lock (Gate) return _routes.Keys.OrderBy(k => k).ToList(); } }

    public static IReadOnlyList<NeoDefinition> Env(string name) { lock (Gate) return _env.GetValueOrDefault(name) ?? new List<NeoDefinition>(); }
    public static IReadOnlyList<string> EnvNames { get { lock (Gate) return _env.Keys.OrderBy(k => k).ToList(); } }

    public static NeoDefinition? Parameter(string name) { lock (Gate) return _parameters.GetValueOrDefault(name); }
    public static IReadOnlyList<string> ParameterNames { get { lock (Gate) return _parameters.Keys.Concat(KernelParameters).Distinct().OrderBy(k => k).ToList(); } }

    public static NeoDefinition? Service(string id) { lock (Gate) return _services.GetValueOrDefault(id.TrimStart('@', '\\')); }

    public static IReadOnlyList<string> TemplateNames { get { lock (Gate) return _templates.Keys.OrderBy(k => k).ToList(); } }

    /// <summary>File of a template name as given to render() / extend() / include(): "home/index", "base.html.twig", "@admin/dashboard".</summary>
    public static string? Template(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim().Replace('\\', '/').TrimStart('/');
        lock (Gate)
        {
            if (_templates.TryGetValue(name, out var f)) return f;
            foreach (var ext in new[] { ".php", ".html.twig", ".twig" })
                if (_templates.TryGetValue(name + ext, out f)) return f;
        }
        return null;
    }

    /// <summary>Template name of a file of templates/ (null when it is not a template).</summary>
    public static string? TemplateNameOf(string file)
    {
        lock (Gate)
            return _templates.Where(t => string.Equals(t.Value, file, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Key).OrderBy(k => k.Length).FirstOrDefault();
    }

    /// <summary>Routes whose controller is <paramref name="fqn"/>::<paramref name="method"/>.</summary>
    public static List<NeoRoute> RoutesOf(string fqn, string method)
    {
        var target = fqn.TrimStart('\\') + "::" + method;
        lock (Gate) return _allRoutes.Where(r => string.Equals(r.Controller, target, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ------------------------------------------------------------------ building

    private static System.Threading.Timer? _timer;

    /// <summary>A project file changed: rebuild in a moment (several changes are merged).</summary>
    public static void Invalidate(string? path = null)
    {
        if (_root == null) return;
        if (path != null)
        {
            var rel = Path.GetRelativePath(_root, path).Replace('\\', '/');
            bool relevant = rel.StartsWith("src/") && rel.EndsWith(".php") || rel.StartsWith("config/") || rel.StartsWith("templates/") || rel.StartsWith(".env");
            if (!relevant) return;
        }
        _timer?.Dispose();
        var root = _root;
        _timer = new System.Threading.Timer(_ => { try { Build(root); } catch { } }, null, 700, Timeout.Infinite);
    }

    public static void Build(string root)
    {
        root = Path.GetFullPath(root);
        var routes = new List<NeoRoute>();
        var env = new Dictionary<string, List<NeoDefinition>>();
        var parameters = new Dictionary<string, NeoDefinition>();
        var services = new Dictionary<string, NeoDefinition>(StringComparer.OrdinalIgnoreCase);
        var templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var namespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var config = Path.Combine(root, "config");

        // ---- routes
        var resources = new List<(string dir, string prefix, string namePrefix)>();
        foreach (var file in RouteFiles(config))
            ParseRoutesYaml(root, file, routes, resources);
        if (resources.Count == 0) resources.Add((Path.Combine(root, "src", "Controller"), "", ""));
        foreach (var (dir, prefix, namePrefix) in resources)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in SafeFiles(dir, "*.php"))
                ParseRouteAttributes(f, prefix, namePrefix, routes);
        }

        // ---- .env (load order: .env, .env.local, .env.{env}, .env.{env}.local)
        var appEnv = "dev";
        foreach (var name in new[] { ".env", ".env.local" })
            foreach (var d in ParseEnv(Path.Combine(root, name)))
            {
                if (d.Name == "APP_ENV") appEnv = d.Value;
                (env.TryGetValue(d.Name, out var l) ? l : env[d.Name] = new List<NeoDefinition>()).Add(d);
            }
        foreach (var name in new[] { $".env.{appEnv}", $".env.{appEnv}.local" })
            foreach (var d in ParseEnv(Path.Combine(root, name)))
                (env.TryGetValue(d.Name, out var l) ? l : env[d.Name] = new List<NeoDefinition>()).Add(d);

        // ---- parameters / services of the YAML configuration
        if (Directory.Exists(config))
            foreach (var f in SafeFiles(config, "*.yaml").Concat(SafeFiles(config, "*.yml")))
                ParseConfigYaml(f, parameters, services);

        // ---- templates
        var templatesDir = Path.Combine(root, "templates");
        AddTemplates(templatesDir, "", templates);
        var viewYaml = Path.Combine(config, "framework", "view.yaml");
        if (File.Exists(viewYaml))
        {
            var text = File.ReadAllText(viewYaml);
            var ns = Regex.Match(text, @"^namespaces:\s*\{(?<inline>[^}]*)\}|^namespaces:\s*\n(?<block>(?:[ \t]+.+\n?)+)", RegexOptions.Multiline);
            var body = ns.Groups["inline"].Success ? ns.Groups["inline"].Value.Replace(",", "\n") : ns.Groups["block"].Value;
            foreach (Match m in Regex.Matches(body, @"^\s*['""]?(?<k>[\w-]+)['""]?\s*:\s*['""]?(?<v>[^'""\n]+)['""]?", RegexOptions.Multiline))
            {
                var dir = ResolvePath(root, m.Groups["v"].Value.Trim());
                namespaces[m.Groups["k"].Value] = dir;
                AddTemplates(dir, "@" + m.Groups["k"].Value + "/", templates);
            }
        }

        lock (Gate)
        {
            _root = root;
            _allRoutes = routes;
            _routes = routes.Where(r => r.Name.Length > 0).GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.First());
            _env = env;
            _parameters = parameters;
            _services = services;
            _templates = templates;
            _namespaces = namespaces;
        }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _root = null;
            _allRoutes = new(); _routes = new(); _env = new(); _parameters = new(); _services = new(StringComparer.OrdinalIgnoreCase); _templates = new(StringComparer.OrdinalIgnoreCase);
        }
        Changed?.Invoke();
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static string ResolvePath(string root, string value)
    {
        value = value.Replace("%kernel.root_path%", root).Replace("%kernel.templates_path%", Path.Combine(root, "templates"))
                     .Replace("%kernel.config_path%", Path.Combine(root, "config"));
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }

    private static void AddTemplates(string dir, string prefix, Dictionary<string, string> templates)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in SafeFiles(dir, "*.*"))
        {
            if (!(f.EndsWith(".php", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".twig", StringComparison.OrdinalIgnoreCase))) continue;
            var rel = prefix + Path.GetRelativePath(dir, f).Replace('\\', '/');
            templates[rel] = f;                                                  // "home/index.html.twig"
            var bare = Regex.Replace(rel, @"(\.html)?\.twig$|\.php$", "", RegexOptions.IgnoreCase);
            templates.TryAdd(bare, f);                                           // "home/index"
        }
    }

    private static IEnumerable<string> RouteFiles(string config)
    {
        var main = Path.Combine(config, "routes.yaml");
        if (File.Exists(main)) yield return main;
        var dir = Path.Combine(config, "routes");
        if (Directory.Exists(dir))
            foreach (var f in SafeFiles(dir, "*.yaml")) yield return f;
    }

    private static int LineOf(string text, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>Top-level YAML blocks: key → (offset of the key, child "key: value" pairs).</summary>
    private static IEnumerable<(string key, int offset, Dictionary<string, string> values)> YamlBlocks(string text)
    {
        foreach (Match m in Regex.Matches(text, @"^(?<key>[^\s#][^:\n]*?):[ \t]*(?:#.*)?\r?\n(?<body>(?:(?:[ \t]+.*|[ \t]*)\r?\n?)*)", RegexOptions.Multiline))
        {
            var values = new Dictionary<string, string>();
            foreach (Match v in Regex.Matches(m.Groups["body"].Value, @"^[ \t]+(?<k>[\w.-]+)[ \t]*:[ \t]*(?<v>.*?)[ \t]*(?:#.*)?$", RegexOptions.Multiline))
                values.TryAdd(v.Groups["k"].Value, v.Groups["v"].Value.Trim().Trim('\'', '"'));
            yield return (m.Groups["key"].Value.Trim().Trim('\'', '"'), m.Index, values);
        }
    }

    private static void ParseRoutesYaml(string root, string file, List<NeoRoute> routes, List<(string, string, string)> resources)
    {
        string text;
        try { text = File.ReadAllText(file); } catch { return; }
        foreach (var (key, offset, values) in YamlBlocks(text))
        {
            if (values.TryGetValue("resource", out var resource))
            {
                var dir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, resource));
                resources.Add((dir, values.GetValueOrDefault("prefix", "").TrimEnd('/'), values.GetValueOrDefault("name_prefix", "")));
            }
            else if (values.TryGetValue("path", out var path))
            {
                var methods = values.GetValueOrDefault("methods", "").Trim('[', ']').Replace(" ", "").Replace("'", "").Replace("\"", "");
                routes.Add(new NeoRoute(key, path, methods.Length == 0 ? "ANY" : methods.Replace(",", "|").ToUpperInvariant(),
                    values.GetValueOrDefault("controller"), file, offset, LineOf(text, offset)));
            }
        }
    }

    private static readonly Regex RouteAttr = new(@"#\[\s*(?:\\?[\w\\]*\\)?Route\s*\((?<args>(?:[^()]|\((?:[^()]|\([^()]*\))*\))*)\)\s*\]", RegexOptions.Compiled);

    private static (string? path, string? name, string methods) RouteArgs(string args)
    {
        string? Str(string pattern) => Regex.Match(args, pattern) is { Success: true } m ? m.Groups[1].Value : null;
        var path = Str(@"(?:^|\bpath\s*:)\s*['""]([^'""]*)['""]");
        var name = Str(@"\bname\s*:\s*['""]([^'""]*)['""]");
        var methodsRaw = Str(@"\bmethods\s*:\s*(\[[^\]]*\]|['""][^'""]*['""])") ?? "";
        var methods = string.Join("|", Regex.Matches(methodsRaw, @"['""](\w+)['""]").Select(m => m.Groups[1].Value.ToUpperInvariant()));
        return (path, name, methods.Length == 0 ? "ANY" : methods);
    }

    private static void ParseRouteAttributes(string file, string prefix, string namePrefix, List<NeoRoute> routes)
    {
        string text;
        try { text = File.ReadAllText(file); } catch { return; }
        if (!text.Contains("Route")) return;
        var ns = Regex.Match(text, @"^\s*namespace\s+([\w\\]+)\s*;", RegexOptions.Multiline).Groups[1].Value;
        var cls = Regex.Match(text, @"\bclass\s+(\w+)");
        if (!cls.Success) return;
        var fqn = (ns.Length > 0 ? ns + "\\" : "") + cls.Groups[1].Value;
        string classPath = "", className = "";
        foreach (Match a in RouteAttr.Matches(text))
        {
            var after = text[(a.Index + a.Length)..];
            var target = Regex.Match(after, @"^\s*(?:#\[[^\]]*\]\s*)*(?:(?:final|abstract|readonly|public|protected|private|static)\s+)*(?<kind>class|function)\s+(?<name>\w+)");
            if (!target.Success) continue;
            var (path, name, methods) = RouteArgs(a.Groups["args"].Value);
            if (target.Groups["kind"].Value == "class")
            {
                classPath = (path ?? "").TrimEnd('/');
                className = name ?? "";
                continue;
            }
            var full = prefix + classPath + "/" + (path ?? "").TrimStart('/');
            if (full.Length > 1) full = full.TrimEnd('/');
            routes.Add(new NeoRoute(name != null ? namePrefix + className + name : "", full.Length == 0 ? "/" : full, methods,
                fqn + "::" + target.Groups["name"].Value, file, a.Index, LineOf(text, a.Index)));
        }
    }

    private static IEnumerable<NeoDefinition> ParseEnv(string file)
    {
        if (!File.Exists(file)) yield break;
        string text;
        try { text = File.ReadAllText(file); } catch { yield break; }
        foreach (Match m in Regex.Matches(text, @"^[ \t]*(?:export[ \t]+)?(?<k>[A-Za-z_][A-Za-z0-9_]*)[ \t]*=(?<v>.*)$", RegexOptions.Multiline))
        {
            var v = m.Groups["v"].Value.Trim();
            if (v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'')) v = v[1..^1];
            else v = Regex.Replace(v, @"\s+#.*$", "");
            yield return new NeoDefinition(m.Groups["k"].Value, v.TrimEnd('\r'), file, m.Groups["k"].Index, LineOf(text, m.Index));
        }
    }

    private static void ParseConfigYaml(string file, Dictionary<string, NeoDefinition> parameters, Dictionary<string, NeoDefinition> services)
    {
        string text;
        try { text = File.ReadAllText(file); } catch { return; }
        foreach (Match section in Regex.Matches(text, @"^(?<name>parameters|services):[ \t]*\r?\n(?<body>(?:(?:[ \t]+.*|[ \t]*)\r?\n?)*)", RegexOptions.Multiline))
        {
            var body = section.Groups["body"];
            // direct children only: the smallest indentation of the block
            var keys = Regex.Matches(body.Value, @"^(?<ind>[ \t]+)(?<k>['""]?[^\s#'""][^:\n]*?['""]?)[ \t]*:(?<v>.*)$", RegexOptions.Multiline);
            if (keys.Count == 0) continue;
            var indent = keys.Min(k => k.Groups["ind"].Length);
            foreach (Match k in keys)
            {
                if (k.Groups["ind"].Length != indent) continue;
                var key = k.Groups["k"].Value.Trim('\'', '"');
                var offset = body.Index + k.Groups["k"].Index;
                var def = new NeoDefinition(key, k.Groups["v"].Value.Trim().Trim('\'', '"'), file, offset, LineOf(text, offset));
                if (section.Groups["name"].Value == "parameters") parameters[key] = def;
                else if (!key.StartsWith("_")) services[key.TrimEnd('\\')] = def;
            }
        }
    }
}
