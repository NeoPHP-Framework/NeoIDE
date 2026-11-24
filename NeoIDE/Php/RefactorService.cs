using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NeoIDE.Php;

public sealed record FileChange(string Path, string OldText, string NewText, int Occurrences);

/// <summary>
/// Rename refactorings: classes (file rename), namespaces (directory rename/move in a PSR-4 root), templates.
/// Computes the new content of every affected project file; nothing is written here.
/// </summary>
public static class RefactorService
{
    private static readonly string[] Excluded = { "vendor", "var", "node_modules", ".git", ".idea", ".neoide" };

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "and", "array", "as", "break", "callable", "case", "catch", "class", "clone", "const", "continue", "declare",
        "default", "do", "echo", "else", "elseif", "empty", "enddeclare", "endfor", "endforeach", "endif", "endswitch", "endwhile",
        "enum", "eval", "exit", "die", "extends", "final", "finally", "fn", "for", "foreach", "function", "global", "goto", "if",
        "implements", "include", "include_once", "instanceof", "insteadof", "interface", "isset", "list", "match", "namespace",
        "new", "or", "print", "private", "protected", "public", "readonly", "require", "require_once", "return", "static",
        "switch", "throw", "trait", "try", "unset", "use", "var", "while", "xor", "yield", "from", "self", "parent",
    };

    public static IEnumerable<string> ProjectFiles(string root, params string[] extensions)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); } catch { continue; }
            foreach (var f in files)
                if (extensions.Contains(Path.GetExtension(f).ToLowerInvariant())) yield return f;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (string.Equals(Path.GetDirectoryName(d), root, StringComparison.OrdinalIgnoreCase) && Excluded.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (name is ".git" or "node_modules") continue;
                stack.Push(d);
            }
        }
    }

    /// <summary>
    /// Renames / moves classes: <paramref name="map"/> old FQN → new FQN. <paramref name="movedFiles"/> maps the files whose
    /// namespace changes to their new namespace (directory move).
    /// </summary>
    public static List<FileChange> RenameClasses(string root, Dictionary<string, string> map, Dictionary<string, string> movedFiles, Func<string, string> read)
    {
        var changes = new List<FileChange>();
        var oldMap = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        foreach (var file in ProjectFiles(root, ".php"))
        {
            var text = read(file);
            var updated = RewritePhp(text, file, oldMap, movedFiles.TryGetValue(file, out var ns) ? ns : null, out var count);
            if (count > 0 && updated != text) changes.Add(new FileChange(file, text, updated, count));
        }
        // FQNs in configuration files (services.yaml, routes.yaml "App\Controller\X::index"...)
        foreach (var file in ProjectFiles(root, ".yaml", ".yml", ".xml", ".neon"))
        {
            var text = read(file);
            var updated = text;
            int count = 0;
            foreach (var (oldFqn, newFqn) in oldMap.OrderByDescending(k => k.Key.Length))
            {
                foreach (var (o, n) in new[] { (oldFqn, newFqn), (oldFqn.Replace("\\", "\\\\"), newFqn.Replace("\\", "\\\\")) })
                {
                    var re = new Regex(@"(?<![\w\\])" + Regex.Escape(o) + @"(?![\w])");
                    count += re.Matches(updated).Count;
                    updated = re.Replace(updated, n.Replace("$", "$$"));
                }
            }
            if (count > 0 && updated != text) changes.Add(new FileChange(file, text, updated, count));
        }
        return changes;
    }

    private static string RewritePhp(string text, string path, Dictionary<string, string> map, string? newNamespace, out int occurrences)
    {
        var result = RewritePhpCore(text, path, map, newNamespace);
        occurrences = result.count;
        return result.text;
    }

    private static (string text, int count) RewritePhpCore(string text, string path, Dictionary<string, string> map, string? newNamespace)
    {
        int count = 0;
        var info = PhpParser.Parse(text, path);
        var tokens = PhpLexer.Significant(PhpLexer.Tokenize(text));
        var edits = new List<(int start, int length, string text)>();
        var imports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileMoves = newNamespace != null && !string.Equals(newNamespace, info.Namespace, StringComparison.OrdinalIgnoreCase);

        // imports without explicit alias whose short name changes
        var aliasRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in info.Uses.Where(u => u.Kind == "class" && !u.IsGroup))
        {
            if (!map.TryGetValue(u.Fqn, out var nf)) continue;
            var explicitAlias = Regex.IsMatch(text[u.StatementStart..u.StatementEnd], @"\bas\s+" + Regex.Escape(u.Alias) + @"\b", RegexOptions.IgnoreCase);
            if (!explicitAlias) aliasRenames[u.Alias] = nf.Split('\\').Last();
        }

        string? Reference(string written, bool inUse, out bool needsImport, out string? importFqn)
        {
            needsImport = false;
            importFqn = null;
            if (inUse)
            {
                var f = written.TrimStart('\\');
                return map.TryGetValue(f, out var nf) ? (written.StartsWith("\\") ? "\\" : "") + nf : null;
            }
            string fqn;
            bool viaImport = false;
            if (written.StartsWith("\\")) fqn = written[1..];
            else
            {
                var first = written.Split('\\')[0];
                viaImport = info.AliasTarget(first) != null;
                fqn = info.ResolveClass(written);
            }
            if (map.TryGetValue(fqn, out var newFqn))
            {
                if (written.Contains('\\')) return "\\" + newFqn;
                if (viaImport) return aliasRenames.TryGetValue(written, out var na) ? na : null;
                // same namespace reference
                var newNs = newFqn.Contains('\\') ? newFqn[..newFqn.LastIndexOf('\\')] : "";
                var fileNs = newNamespace ?? info.Namespace;
                if (string.Equals(newNs, fileNs, StringComparison.OrdinalIgnoreCase)) return newFqn.Split('\\').Last();
                needsImport = true;
                importFqn = newFqn;
                return newFqn.Split('\\').Last();
            }
            // a moved file referencing (by short name) a class that stayed in its old namespace
            if (fileMoves && !written.Contains('\\') && !written.StartsWith("\\") && !viaImport && info.Namespace.Length > 0 &&
                char.IsUpper(written[0]) && !Keywords.Contains(written) && !info.Classes.Any(c => c.Name == written) &&
                fqn.StartsWith(info.Namespace + "\\", StringComparison.OrdinalIgnoreCase) && !PhpTypes.Builtin.Contains(written))
            {
                needsImport = true;
                importFqn = fqn;
                return null;
            }
            return null;
        }

        bool InUse(int offset) => info.Uses.Any(u => !u.IsGroup && offset >= u.StatementStart && offset < u.StatementEnd);

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var prev = i > 0 ? tokens[i - 1] : default;
            var next = i + 1 < tokens.Count ? tokens[i + 1] : default;
            if (t.Kind == TokKind.DocComment)
            {
                var doc = t.Text;
                var newDoc = Regex.Replace(doc, @"(?<![\w$\\])\\?[A-Z][\w]*(\\[A-Za-z_]\w*)*", m =>
                {
                    var r = Reference(m.Value, false, out var ni, out var imp);
                    if (ni && imp != null) imports.Add(imp);
                    if (r == null || r == m.Value) return m.Value;
                    count++;
                    return r;
                });
                if (newDoc != doc) edits.Add((t.Start, t.Text.Length, newDoc));
                continue;
            }
            if (t.Kind != TokKind.Identifier) continue;
            if (prev.Text != null && prev.IsWord("namespace"))
            {
                if (fileMoves) { edits.Add((t.Start, t.Text.Length, newNamespace!)); count++; }
                continue;
            }
            if (prev.Text != null && (prev.IsWord("class") || prev.IsWord("interface") || prev.IsWord("trait") || prev.IsWord("enum")) && !(i >= 2 && tokens[i - 2].Is("::")))
            {
                var declFqn = string.IsNullOrEmpty(info.Namespace) ? t.Text : info.Namespace + "\\" + t.Text;
                if (map.TryGetValue(declFqn, out var nf)) { edits.Add((t.Start, t.Text.Length, nf.Split('\\').Last())); count++; }
                continue;
            }
            if (prev.Text != null && (prev.Is("->") || prev.Is("?->") || prev.Is("::") || prev.IsWord("function") || prev.IsWord("const") || prev.IsWord("goto"))) continue;
            if (next.Text != null && next.Is("(") && !(prev.Text != null && (prev.IsWord("new") || prev.Is("#[")))) continue; // function call
            if (next.Text != null && next.Is(":") && prev.Text != null && (prev.Is("(") || prev.Is(","))) continue;        // named argument
            if (PhpTypes.Builtin.Contains(t.Text) || t.Text is "true" or "false" or "null") continue;
            var inUse = InUse(t.Start);
            if (inUse && (prev.IsWord("as") || prev.IsWord("function") || prev.IsWord("const"))) continue;
            var replacement = Reference(t.Text, inUse, out var needsImport, out var importFqn);
            if (needsImport && importFqn != null) imports.Add(importFqn);
            if (replacement != null && replacement != t.Text) { edits.Add((t.Start, t.Text.Length, replacement)); count++; }
        }

        if (edits.Count == 0 && imports.Count == 0) return (text, 0);
        var sb = new StringBuilder(text);
        foreach (var e in edits.OrderByDescending(e => e.start)) sb.Remove(e.start, e.length).Insert(e.start, e.text);
        var result = sb.ToString();
        foreach (var fqn in imports)
        {
            var newInfo = PhpParser.Parse(result, path);
            if (newInfo.Uses.Any(u => string.Equals(u.Fqn, fqn, StringComparison.OrdinalIgnoreCase))) continue;
            // the moved file now lives in the namespace of the class: no import
            if (newNamespace != null && fqn.Contains('\\') && string.Equals(fqn[..fqn.LastIndexOf('\\')], newNamespace, StringComparison.OrdinalIgnoreCase)) continue;
            var (_, edit) = ImportManager.PrepareClassReference(result, newInfo, fqn);
            if (edit is { } ed) { result = result.Insert(ed.Offset, ed.Text); count++; }
        }
        return (result, count);
    }

    /// <summary>Template rename: render('blog/index') → render('blog/list').</summary>
    public static List<FileChange> RenameTemplate(string root, string oldName, string newName, Func<string, string> read)
    {
        var changes = new List<FileChange>();
        var re = new Regex(@"(['""])" + Regex.Escape(oldName) + @"(\.html\.twig|\.php|\.twig)?\1");
        foreach (var file in ProjectFiles(root, ".php", ".twig"))
        {
            var text = read(file);
            int count = 0;
            var updated = re.Replace(text, m => { count++; return m.Groups[1].Value + newName + m.Groups[2].Value + m.Groups[1].Value; });
            if (count > 0) changes.Add(new FileChange(file, text, updated, count));
        }
        return changes;
    }
}
