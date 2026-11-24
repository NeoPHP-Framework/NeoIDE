using System.IO;

namespace NeoIDE.Services;

public sealed record PhpInstall(string Path, string Version)
{
    public string Display => $"PHP {Version}  —  {Path}";
    public string ShortVersion => string.Join('.', Version.Split('.').Take(2));
    public override string ToString() => Display;
}

/// <summary>Finds PHP interpreters installed on the machine (Laragon, XAMPP, WAMP, Scoop, PATH...).</summary>
public static class PhpLocator
{
    private static List<PhpInstall>? _cache;

    public static async Task<List<PhpInstall>> FindAllAsync(bool refresh = false)
    {
        if (_cache != null && !refresh) return _cache;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p) { if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) candidates.Add(Path.GetFullPath(p)); }
        void AddDirs(string root, string sub = "")
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var d in Directory.GetDirectories(root)) Add(Path.Combine(d, sub, "php.exe"));
            }
            catch { }
        }

        foreach (var drive in new[] { "C:", "D:" })
        {
            AddDirs($@"{drive}\laragon\bin\php");
            AddDirs($@"{drive}\wamp64\bin\php");
            AddDirs($@"{drive}\wamp\bin\php");
            Add($@"{drive}\xampp\php\php.exe");
            Add($@"{drive}\php\php.exe");
            AddDirs($@"{drive}\tools\php");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AddDirs(Path.Combine(home, "scoop", "apps"), "current");
        AddDirs(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PHP"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PHP", "php.exe"));

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { Add(Path.Combine(dir.Trim(), "php.exe")); } catch { }
        }
        foreach (var custom in AppSettings.Current.CustomPhpPaths) Add(custom);

        var tasks = candidates.Select(async p => new PhpInstall(p, await GetVersionAsync(p) ?? "")).ToList();
        var results = await Task.WhenAll(tasks);
        _cache = results.Where(r => r.Version.Length > 0)
            .OrderByDescending(r => ParseVersion(r.Version))
            .ToList();
        return _cache;
    }

    public static async Task<string?> GetVersionAsync(string phpExe)
    {
        var (code, stdout, _) = await ProcessRunner.RunAsync(phpExe, "-n -r \"echo PHP_VERSION;\"", null, 10000);
        var v = stdout.Trim();
        return code == 0 && v.Length > 0 ? v : null;
    }

    public static Version ParseVersion(string v)
    {
        var clean = new string(v.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');
        return Version.TryParse(clean.Count(c => c == '.') == 0 ? clean + ".0" : clean, out var ver) ? ver : new Version(0, 0);
    }

    /// <summary>The interpreter to use when nothing is configured.</summary>
    public static async Task<PhpInstall?> GetDefaultAsync()
    {
        var all = await FindAllAsync();
        var configured = AppSettings.Current.DefaultPhpPath;
        return all.FirstOrDefault(p => string.Equals(p.Path, configured, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
    }
}
