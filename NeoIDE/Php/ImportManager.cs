using System.Text;
using System.Text.RegularExpressions;

namespace NeoIDE.Php;

public readonly record struct TextEdit(int Offset, int Length, string Text);

/// <summary>Adds, detects and optimises PHP "use" imports.</summary>
public static class ImportManager
{
    /// <summary>
    /// Decides how a class must be referenced in a file: returns the name to write and,
    /// when needed, the edit inserting the use statement.
    /// </summary>
    public static (string name, TextEdit? edit) PrepareClassReference(string text, PhpFileInfo info, string fqn)
    {
        fqn = fqn.TrimStart('\\');
        var shortName = fqn.Split('\\').Last();
        var ns = fqn.Contains('\\') ? fqn[..fqn.LastIndexOf('\\')] : "";

        // already imported (maybe under an alias)
        var existing = info.Uses.FirstOrDefault(u => u.Kind == "class" && string.Equals(u.Fqn, fqn, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return (existing.Alias, null);

        // same namespace: no import needed (unless an import shadows the short name)
        var aliasConflict = info.Uses.FirstOrDefault(u => u.Kind == "class" && string.Equals(u.Alias, shortName, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(ns, info.Namespace, StringComparison.OrdinalIgnoreCase) && aliasConflict == null)
            return (shortName, null);

        // global class in a file without namespace
        if (ns.Length == 0 && string.IsNullOrEmpty(info.Namespace) && aliasConflict == null && !DeclaresName(info, shortName)) return (shortName, null);

        // a class of the file or another import already uses the short name ("use App\Entity\X;" in a class X is a
        // fatal error): imported under an alias made from its namespace, like PhpStorm (use App\Entity\X as XEntity;)
        if (aliasConflict != null || DeclaresName(info, shortName))
        {
            var alias = ChooseAlias(info, fqn, shortName);
            return alias == null ? ("\\" + fqn, null) : (alias, BuildUseInsertion(text, info, $"use {fqn} as {alias};", fqn));
        }

        return (shortName, BuildUseInsertion(text, info, $"use {fqn};", fqn));
    }

    private static bool DeclaresName(PhpFileInfo info, string name) =>
        info.Classes.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    private static bool NameTaken(PhpFileInfo info, string name) =>
        DeclaresName(info, name) || info.Uses.Any(u => u.Kind == "class" && string.Equals(u.Alias, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>XEntity, EntityX (from the namespace segments, innermost first), then X2, X3…</summary>
    private static string? ChooseAlias(PhpFileInfo info, string fqn, string shortName)
    {
        var segments = fqn.Split('\\')[..^1].Reverse().Where(s => s.Length > 0).ToList();
        var candidates = new List<string>();
        foreach (var seg in segments)
        {
            var cap = char.ToUpperInvariant(seg[0]) + seg[1..];
            if (!shortName.EndsWith(cap, StringComparison.Ordinal)) candidates.Add(shortName + cap);
            if (!shortName.StartsWith(cap, StringComparison.Ordinal)) candidates.Add(cap + shortName);
        }
        for (int i = 2; i < 10; i++) candidates.Add(shortName + i);
        return candidates.FirstOrDefault(c => !NameTaken(info, c));
    }

    public static TextEdit BuildUseInsertion(string text, PhpFileInfo info, string statement, string sortKey)
    {
        var uses = info.Uses.Where(u => !u.IsGroup).GroupBy(u => u.StatementStart).Select(g => g.First())
            .OrderBy(u => u.StatementStart).ToList();
        if (uses.Count > 0)
        {
            foreach (var u in uses)
            {
                if (u.Kind == "class" && string.Compare(u.Fqn, sortKey, StringComparison.OrdinalIgnoreCase) > 0)
                    return new TextEdit(u.StatementStart, 0, statement + NewLine(text));
            }
            var last = uses.Last();
            return new TextEdit(last.StatementEnd, 0, NewLine(text) + statement);
        }
        var nl = NewLine(text);
        if (info.NamespaceStatementEnd > 0) return new TextEdit(info.NamespaceStatementEnd, 0, nl + nl + statement);
        var at = Math.Max(0, info.HeaderEnd);
        return new TextEdit(at, 0, nl + nl + statement);
    }

    public static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    /// <summary>Imports that are never referenced in the file.</summary>
    public static List<PhpUse> FindUnused(string text, PhpFileInfo info)
    {
        var result = new List<PhpUse>();
        if (info.Uses.Count == 0) return result;
        var tokens = PhpLexer.Significant(PhpLexer.Tokenize(text));
        var ranges = info.Uses.Select(u => (u.StatementStart, u.StatementEnd)).Distinct().ToList();
        bool InUse(int offset) => ranges.Any(r => offset >= r.StatementStart && offset < r.StatementEnd);
        // the class-level "use Trait;" statements are references, not imports
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var docText = new StringBuilder();

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokKind.DocComment) { docText.Append(t.Text).Append('\n'); continue; }
            if (t.Kind != TokKind.Identifier || InUse(t.Start)) continue;
            if (i > 0 && (tokens[i - 1].Is("->") || tokens[i - 1].Is("?->") || tokens[i - 1].Is("::"))) continue;
            var first = t.Text.TrimStart('\\');
            if (t.Text.StartsWith("\\")) continue; // fully qualified
            var seg = first.Split('\\')[0];
            used.Add(seg);
            if (i + 1 < tokens.Count && tokens[i + 1].Is("(")) usedFunctions.Add(first);
        }
        var docs = docText.ToString();
        foreach (var u in info.Uses)
        {
            bool isUsed = u.Kind switch
            {
                "function" => usedFunctions.Contains(u.Alias),
                _ => used.Contains(u.Alias) || (docs.Length > 0 && Regex.IsMatch(docs, @"(?<![\\\w$])" + Regex.Escape(u.Alias) + @"\b")),
            };
            if (!isUsed) result.Add(u);
        }
        return result;
    }

    /// <summary>Removes unused imports and sorts the remaining ones (like Ctrl+Alt+O).</summary>
    public static TextEdit? Optimize(string text, PhpFileInfo info)
    {
        var uses = info.Uses.Where(u => !u.IsGroup).ToList();
        if (uses.Count == 0) return null;
        var unused = FindUnused(text, info).ToHashSet();
        int start = uses.Min(u => u.StatementStart);
        int end = uses.Max(u => u.StatementEnd);
        // only optimise a contiguous block made of use statements
        var between = Regex.Replace(text[start..end], @"use\s+(function\s+|const\s+)?[\\\w]+(\s+as\s+\w+)?\s*(,\s*[\\\w]+(\s+as\s+\w+)?\s*)*;", "");
        if (between.Trim().Length > 0) return null;

        var nl = NewLine(text);
        var lines = uses.Where(u => !unused.Contains(u))
            .OrderBy(u => u.Kind == "class" ? 0 : u.Kind == "function" ? 1 : 2)
            .ThenBy(u => u.Fqn, StringComparer.OrdinalIgnoreCase)
            .Select(u =>
            {
                var kw = u.Kind == "class" ? "use " : $"use {u.Kind} ";
                var alias = string.Equals(u.Fqn.Split('\\').Last(), u.Alias, StringComparison.Ordinal) ? "" : $" as {u.Alias}";
                return kw + u.Fqn + alias + ";";
            })
            .Distinct()
            .ToList();
        var replacement = string.Join(nl, lines);
        if (lines.Count == 0)
        {
            // also swallow the blank line left behind
            int e = end;
            while (e < text.Length && (text[e] == '\r' || text[e] == '\n')) e++;
            int s = start;
            while (s > 0 && (text[s - 1] == '\r' || text[s - 1] == '\n')) s--;
            return new TextEdit(s, e - s, nl + nl);
        }
        if (replacement == text[start..end]) return null;
        return new TextEdit(start, end - start, replacement);
    }
}
