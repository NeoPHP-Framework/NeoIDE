using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NeoIDE.Services;

/// <summary>Formatters used by Code › Reformat Code: PHP-CS-Fixer for PHP (when installed) and JSON.</summary>
public static class CodeFormatter
{
    public static string? PhpCsFixerBinary(string root) =>
        new[] { Path.Combine(root, "vendor", "friendsofphp", "php-cs-fixer", "php-cs-fixer"), Path.Combine(root, "vendor", "bin", "php-cs-fixer") }
            .FirstOrDefault(File.Exists);

    public static string? PhpCsFixerConfig(string root) =>
        new[] { ".php-cs-fixer.php", ".php-cs-fixer.dist.php" }.Select(f => Path.Combine(root, f)).FirstOrDefault(File.Exists);

    /// <summary>Rules when the project has no .php-cs-fixer(.dist).php.</summary>
    public const string DefaultRules = "@PER-CS";

    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "neoide-format");

    /// <summary>
    /// Runs PHP-CS-Fixer on a copy of the buffer (the file itself is not touched: the result is applied to the editor,
    /// so it can be undone). Returns the fixed text, or an error.
    /// </summary>
    public static async Task<(string? text, string? error)> PhpCsFixerAsync(string file, string text)
    {
        var root = Workspace.Root;
        if (root == null || Workspace.PhpExe == null) return (null, "No PHP interpreter");
        var bin = PhpCsFixerBinary(root);
        if (bin == null) return (null, "PHP-CS-Fixer is not installed");
        var dir = Path.Combine(TempDir, $"{Environment.ProcessId}-{(uint)file.ToLowerInvariant().GetHashCode():x8}");
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, Path.GetFileName(file));
        await File.WriteAllTextAsync(tmp, text);
        try
        {
            var config = PhpCsFixerConfig(root);
            var args = $"-d memory_limit=512M \"{bin}\" fix \"{tmp}\" --using-cache=no --no-interaction --path-mode=override --show-progress=none "
                       + (config != null ? $"--config=\"{config}\"" : $"--rules={DefaultRules}");
            // PHP-CS-Fixer refuses PHP versions newer than the ones it knows without this
            var env = new Dictionary<string, string> { ["PHP_CS_FIXER_IGNORE_ENV"] = "1" };
            var (code, stdout, stderr) = await ProcessRunner.RunAsync(Workspace.PhpExe, args, root, 60000, env);
            // exit code: 0 = nothing / fixed, 8 = some files need fixing (dry-run), 4/16/32/64 = errors
            if (code is not (0 or 8))
            {
                var msg = (stderr.Trim().Length > 0 ? stderr : stdout).Trim().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("PHP CS Fixer")) ?? $"exit code {code}";
                return (null, msg);
            }
            return (await File.ReadAllTextAsync(tmp), null);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Pretty prints a JSON document (keys order kept); null when it is not valid JSON.</summary>
    public static string? FormatJson(string text, int indentSize)
    {
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (node == null) return null;
            var nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var json = node.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true, IndentSize = Math.Clamp(indentSize, 1, 8), NewLine = nl,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            return json + nl;
        }
        catch { return null; }
    }
}
