using System.IO;
using System.Reflection;
using System.Text.Json;
using NeoIDE.Services;

namespace NeoIDE.Php;

public readonly record struct ResolvedMember(PhpMember Member, string ViaClass, int Depth);

/// <summary>Symbol index of the project (src, vendor...) plus the PHP runtime built-ins.</summary>
public sealed class PhpIndex
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PhpClass> _classes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PhpFunction> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PhpFileInfo> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<PhpClass>> _byShortName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _namespaces = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;
    public event Action<string>? Progress;
    public bool IsReady { get; private set; }
    public string? RuntimeVersion { get; private set; }

    private static readonly string[] ExcludedDirs = { ".git", ".idea", ".neoide", "node_modules", "var", "cache", ".vscode" };

    // ------------------------------------------------------------------ building

    public async Task BuildAsync(string root, string? phpExe, CancellationToken ct = default)
    {
        IsReady = false;
        if (phpExe != null) await LoadBuiltinsAsync(phpExe);

        await Task.Run(() =>
        {
            var files = EnumeratePhpFiles(root).ToList();
            int done = 0;
            Parallel.ForEach(files, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, file =>
            {
                try
                {
                    var info = PhpParser.Parse(File.ReadAllText(file), file);
                    lock (_sync) AddFileLocked(info);
                }
                catch { }
                var d = Interlocked.Increment(ref done);
                if (d % 200 == 0) Progress?.Invoke($"Indexing… {d}/{files.Count}");
            });
        }, ct);
        IsReady = true;
        Progress?.Invoke("");
        Changed?.Invoke();
    }

    public static IEnumerable<string> EnumeratePhpFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subdirs, files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir, "*.php");
            }
            catch { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in subdirs)
            {
                var name = Path.GetFileName(s);
                var rel = Path.GetRelativePath(root, s);
                if (ExcludedDirs.Contains(name, StringComparer.OrdinalIgnoreCase) && !rel.Contains(Path.DirectorySeparatorChar)) continue;
                if (name is ".git" or "node_modules") continue;
                // vendor tests/docs are noise for completion
                if (rel.StartsWith("vendor" + Path.DirectorySeparatorChar) &&
                    (name.Equals("tests", StringComparison.OrdinalIgnoreCase) || name.Equals("Tests", StringComparison.Ordinal) ||
                     name.Equals("docs", StringComparison.OrdinalIgnoreCase) || name.Equals("Fixtures", StringComparison.OrdinalIgnoreCase)))
                    continue;
                stack.Push(s);
            }
        }
    }

    public void UpdateFile(string path, string? content = null)
    {
        try
        {
            content ??= File.Exists(path) ? File.ReadAllText(path) : null;
            lock (_sync)
            {
                RemoveFileLocked(path);
                if (content != null) AddFileLocked(PhpParser.Parse(content, path));
            }
            Changed?.Invoke();
        }
        catch { }
    }

    /// <summary>Replaces the symbols of a file with an already parsed version (the editor buffer).</summary>
    public void UpdateFromBuffer(PhpFileInfo info, string? path)
    {
        var key = path ?? "<buffer>";
        info.Path ??= key;
        lock (_sync)
        {
            RemoveFileLocked(key);
            AddFileLocked(info);
        }
    }

    public void RemoveFile(string path)
    {
        lock (_sync) RemoveFileLocked(path);
        Changed?.Invoke();
    }

    private void AddFileLocked(PhpFileInfo info)
    {
        if (info.Path != null) _files[info.Path] = info;
        foreach (var c in info.Classes) AddClassLocked(c);
        foreach (var f in info.Functions) _functions[f.Fqn] = f;
        foreach (var k in info.Constants) _constants.Add(k);
        if (!string.IsNullOrEmpty(info.Namespace)) RegisterNamespace(info.Namespace);
    }

    private void RegisterNamespace(string ns)
    {
        var parts = ns.Split('\\');
        for (int i = 1; i <= parts.Length; i++) _namespaces.Add(string.Join('\\', parts.Take(i)));
    }

    private void AddClassLocked(PhpClass c)
    {
        _classes[c.Fqn] = c;
        if (!_byShortName.TryGetValue(c.Name, out var list)) _byShortName[c.Name] = list = new List<PhpClass>();
        list.RemoveAll(x => string.Equals(x.Fqn, c.Fqn, StringComparison.OrdinalIgnoreCase));
        list.Add(c);
    }

    private void RemoveFileLocked(string path)
    {
        if (!_files.Remove(path, out var old)) return;
        foreach (var c in old.Classes)
        {
            if (_classes.TryGetValue(c.Fqn, out var cur) && cur == c) _classes.Remove(c.Fqn);
            if (_byShortName.TryGetValue(c.Name, out var list)) list.Remove(c);
        }
        foreach (var f in old.Functions)
            if (_functions.TryGetValue(f.Fqn, out var cur) && cur == f) _functions.Remove(f.Fqn);
    }

    // ------------------------------------------------------------------ built-ins

    private async Task LoadBuiltinsAsync(string phpExe)
    {
        try
        {
            var version = await PhpLocator.GetVersionAsync(phpExe) ?? "unknown";
            var cache = Path.Combine(AppSettings.Directory, "stubs", $"php-{version}-{(uint)phpExe.ToLowerInvariant().GetHashCode():x}.json");
            string json;
            if (File.Exists(cache)) json = await File.ReadAllTextAsync(cache);
            else
            {
                Progress?.Invoke($"Reading PHP {version} runtime…");
                var script = Path.Combine(Path.GetTempPath(), "neoide-stubs.php");
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                           Assembly.GetExecutingAssembly().GetManifestResourceNames().First(n => n.EndsWith("stubs.php")))!)
                using (var fs = File.Create(script)) await s.CopyToAsync(fs);
                var (code, stdout, _) = await ProcessRunner.RunAsync(phpExe, $"-d memory_limit=512M {ProcessRunner.Quote(script)}", null, 60000);
                if (code != 0 || !stdout.TrimStart().StartsWith("{")) return;
                json = stdout;
                Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                await File.WriteAllTextAsync(cache, json);
            }
            await Task.Run(() => ParseBuiltins(json));
            RuntimeVersion = version;
        }
        catch { }
    }

    private void ParseBuiltins(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0;
        int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        string? Type(string? t) => PhpTypes.Resolve(t, n => n);

        List<PhpParameter> Params(JsonElement e)
        {
            var list = new List<PhpParameter>();
            if (!e.TryGetProperty("p", out var ps)) return list;
            foreach (var p in ps.EnumerateArray())
                list.Add(new PhpParameter
                {
                    Name = Str(p, "n") ?? "", Type = Type(Str(p, "t")), IsVariadic = Flag(p, "v"), IsByRef = Flag(p, "r"),
                    Default = Str(p, "d") ?? (Flag(p, "o") && !Flag(p, "v") ? "…" : null),
                });
            return list;
        }

        lock (_sync)
        {
            foreach (var f in root.GetProperty("functions").EnumerateArray())
            {
                var name = Str(f, "n")!;
                _functions[name] = new PhpFunction { Name = name, Fqn = name, Parameters = Params(f), ReturnType = Type(Str(f, "r")) };
            }
            foreach (var c in root.GetProperty("classes").EnumerateArray())
            {
                var fqn = Str(c, "n")!;
                var cls = new PhpClass
                {
                    Fqn = fqn, Name = fqn.Split('\\').Last(), Namespace = fqn.Contains('\\') ? fqn[..fqn.LastIndexOf('\\')] : "",
                    Kind = Str(c, "k") switch { "interface" => PhpClassKind.Interface, "trait" => PhpClassKind.Trait, "enum" => PhpClassKind.Enum, _ => PhpClassKind.Class },
                    IsAbstract = Flag(c, "a"), IsFinal = Flag(c, "f"), Parent = Str(c, "parent"),
                    IsAttribute = fqn is "Attribute" or "ReturnTypeWillChange" or "AllowDynamicProperties" or "SensitiveParameter" or "Override" or "Deprecated" or "NoDiscard",
                };
                if (c.TryGetProperty("i", out var ifs)) cls.Interfaces.AddRange(ifs.EnumerateArray().Select(x => x.GetString()!));
                if (c.TryGetProperty("m", out var ms))
                    foreach (var m in ms.EnumerateArray())
                        cls.Members.Add(new PhpMember
                        {
                            Name = Str(m, "n")!, Kind = PhpMemberKind.Method, IsStatic = Flag(m, "s"), IsAbstract = Flag(m, "a"),
                            Visibility = (Visibility)Int(m, "v"), Parameters = Params(m), Type = Type(Str(m, "r")), DeclaringClass = fqn,
                        });
                if (c.TryGetProperty("c", out var cs))
                    foreach (var k in cs.EnumerateArray())
                        cls.Members.Add(new PhpMember { Name = k.GetString()!, Kind = cls.Kind == PhpClassKind.Enum ? PhpMemberKind.EnumCase : PhpMemberKind.Constant, IsStatic = true, DeclaringClass = fqn });
                if (c.TryGetProperty("pr", out var prs))
                    foreach (var p in prs.EnumerateArray())
                        cls.Members.Add(new PhpMember
                        {
                            Name = Str(p, "n")!, Kind = PhpMemberKind.Property, IsStatic = Flag(p, "s"),
                            Visibility = (Visibility)Int(p, "v"), Type = Type(Str(p, "t")), DeclaringClass = fqn,
                        });
                if (cls.Namespace.Length > 0) RegisterNamespace(cls.Namespace);
                AddClassLocked(cls);
            }
            foreach (var k in root.GetProperty("constants").EnumerateArray()) _constants.Add(k.GetString()!);
        }
    }

    // ------------------------------------------------------------------ queries

    public PhpClass? GetClass(string? fqn)
    {
        if (string.IsNullOrEmpty(fqn)) return null;
        lock (_sync) return _classes.GetValueOrDefault(fqn.TrimStart('\\'));
    }

    public PhpFunction? GetFunction(string name, string? currentNamespace = null)
    {
        lock (_sync)
        {
            name = name.TrimStart('\\');
            if (!string.IsNullOrEmpty(currentNamespace) && _functions.TryGetValue(currentNamespace + "\\" + name, out var nsF)) return nsF;
            return _functions.GetValueOrDefault(name);
        }
    }

    public PhpFileInfo? GetFile(string path)
    {
        lock (_sync) return _files.GetValueOrDefault(path);
    }

    public List<PhpClass> AllClasses()
    {
        lock (_sync) return _classes.Values.ToList();
    }

    public List<PhpFunction> AllFunctions()
    {
        lock (_sync) return _functions.Values.ToList();
    }

    public List<string> AllConstants()
    {
        lock (_sync) return _constants.ToList();
    }

    public List<string> AllNamespaces()
    {
        lock (_sync) return _namespaces.ToList();
    }

    public List<PhpClass> FindByShortName(string shortName)
    {
        lock (_sync) return _byShortName.TryGetValue(shortName, out var l) ? l.ToList() : new List<PhpClass>();
    }

    /// <summary>True when <paramref name="child"/> is, extends, implements or uses <paramref name="ancestor"/>.</summary>
    public bool IsSubtypeOf(string? child, string? ancestor, int guard = 0)
    {
        if (child == null || ancestor == null || guard > 30) return false;
        child = child.TrimStart('\\');
        ancestor = ancestor.TrimStart('\\');
        if (string.Equals(child, ancestor, StringComparison.OrdinalIgnoreCase)) return true;
        var c = GetClass(child);
        if (c == null) return false;
        if (IsSubtypeOf(c.Parent, ancestor, guard + 1)) return true;
        foreach (var i in c.Interfaces) if (IsSubtypeOf(i, ancestor, guard + 1)) return true;
        foreach (var t in c.Traits) if (IsSubtypeOf(t, ancestor, guard + 1)) return true;
        return false;
    }

    /// <summary>
    /// Every member reachable from a class: own members, traits, parents, interfaces, mixins.
    /// Child declarations shadow parent ones (like PHP's method resolution).
    /// </summary>
    public List<ResolvedMember> GetMembers(string classFqn)
    {
        var result = new List<ResolvedMember>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(classFqn, classFqn, 0, result, seen, visited, viaTrait: false);
        return result;
    }

    private void Collect(string fqn, string via, int depth, List<ResolvedMember> result, HashSet<string> seen, HashSet<string> visited, bool viaTrait)
    {
        if (depth > 25 || !visited.Add(fqn + "|" + via)) return;
        var cls = GetClass(fqn);
        if (cls == null) return;

        void AddAll(IEnumerable<PhpMember> members, string viaClass)
        {
            foreach (var m in members)
            {
                var key = (m.Kind == PhpMemberKind.Method ? "m:" : m.Kind == PhpMemberKind.Property ? "p:" : "c:") + m.Name;
                if (m.Kind == PhpMemberKind.Method) key = key.ToLowerInvariant();
                if (seen.Add(key)) result.Add(new ResolvedMember(m, viaClass, depth));
                else if (m.Kind == PhpMemberKind.Method && !m.IsAbstract)
                {
                    // an abstract declaration (trait / interface) is satisfied by a concrete method found later in the hierarchy
                    var i = result.FindIndex(r => r.Member.Kind == PhpMemberKind.Method && r.Member.IsAbstract &&
                                                  string.Equals(r.Member.Name, m.Name, StringComparison.OrdinalIgnoreCase));
                    if (i >= 0) result[i] = new ResolvedMember(m, viaClass, depth);
                }
            }
        }

        // real members first, magic (@property/@method) ones only fill the gaps
        AddAll(cls.Members.Where(m => !m.IsMagic), via);
        foreach (var t in cls.Traits) Collect(t, via, depth, result, seen, visited, viaTrait: true);
        AddAll(cls.Members.Where(m => m.IsMagic), via);
        if (cls.Parent != null) Collect(cls.Parent, cls.Parent, depth + 1, result, seen, visited, false);
        foreach (var i in cls.Interfaces) Collect(i, i, depth + 1, result, seen, visited, false);
        foreach (var mx in cls.Mixins) Collect(mx, mx, depth + 1, result, seen, visited, false);
    }

    public ResolvedMember? FindMember(string classFqn, string name, PhpMemberKind? kind = null)
    {
        foreach (var m in GetMembers(classFqn))
        {
            if (kind != null && m.Member.Kind != kind && !(kind == PhpMemberKind.Constant && m.Member.Kind == PhpMemberKind.EnumCase)) continue;
            var cmp = m.Member.Kind == PhpMemberKind.Method ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(m.Member.Name, name, cmp)) return m;
        }
        return null;
    }

    /// <summary>Can code running inside <paramref name="contextClass"/> see this member?</summary>
    public bool IsAccessible(ResolvedMember rm, string? contextClass)
    {
        var m = rm.Member;
        if (m.Visibility == Visibility.Public) return true;
        if (contextClass == null) return false;
        if (m.Visibility == Visibility.Private)
            return string.Equals(rm.ViaClass, contextClass, StringComparison.OrdinalIgnoreCase);
        return IsSubtypeOf(contextClass, rm.ViaClass) || IsSubtypeOf(rm.ViaClass, contextClass);
    }
}
