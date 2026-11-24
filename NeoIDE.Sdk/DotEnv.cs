using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NeoIDE.Sdk;

/// <summary>Reads the .env files of a NeoPHP project like the framework does.</summary>
public static class DotEnv
{
    /// <summary>.env → .env.local → .env.{APP_ENV} → .env.{APP_ENV}.local (the last one wins).</summary>
    public static Dictionary<string, string> Load(string projectRoot)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        Merge(vars, Path.Combine(projectRoot, ".env"));
        Merge(vars, Path.Combine(projectRoot, ".env.local"));
        var env = vars.TryGetValue("APP_ENV", out var e) && e.Length > 0 ? e : "dev";
        Merge(vars, Path.Combine(projectRoot, $".env.{env}"));
        Merge(vars, Path.Combine(projectRoot, $".env.{env}.local"));
        var root = projectRoot.Replace('\\', '/').TrimEnd('/');
        foreach (var k in vars.Keys.ToList())
            vars[k] = vars[k].Replace("%kernel.root_path%", root).Replace("%kernel.project_dir%", root);
        return vars;
    }

    private static void Merge(Dictionary<string, string> vars, string file)
    {
        if (!File.Exists(file)) return;
        foreach (var (k, v) in Parse(File.ReadAllText(file), vars)) vars[k] = v;
    }

    public static List<(string key, string value)> Parse(string text, IReadOnlyDictionary<string, string>? known = null)
    {
        var result = new List<(string, string)>();
        var current = new Dictionary<string, string>(known ?? new Dictionary<string, string>());
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ")) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (!Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_]*$")) continue;
            var rest = line[(eq + 1)..].TrimStart();
            string value;
            if (rest.StartsWith('"'))
            {
                var sb = new StringBuilder();
                for (int i = 1; i < rest.Length; i++)
                {
                    var c = rest[i];
                    if (c == '\\' && i + 1 < rest.Length)
                    {
                        var n = rest[++i];
                        sb.Append(n switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => n });
                        continue;
                    }
                    if (c == '"') break;
                    sb.Append(c);
                }
                value = Interpolate(sb.ToString(), current);
            }
            else if (rest.StartsWith('\''))
            {
                var end = rest.IndexOf('\'', 1);
                value = end > 0 ? rest[1..end] : rest[1..];
            }
            else
            {
                var hash = rest.IndexOf(" #", StringComparison.Ordinal);
                value = Interpolate((hash >= 0 ? rest[..hash] : rest).Trim(), current);
            }
            current[key] = value;
            result.Add((key, value));
        }
        return result;
    }

    private static string Interpolate(string value, IReadOnlyDictionary<string, string> vars) =>
        Regex.Replace(value, @"\$\{(?<n>[A-Za-z_][A-Za-z0-9_]*)\}", m =>
            vars.TryGetValue(m.Groups["n"].Value, out var v) ? v : Environment.GetEnvironmentVariable(m.Groups["n"].Value) ?? "");
}
