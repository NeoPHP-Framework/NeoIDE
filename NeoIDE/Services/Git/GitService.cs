using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NeoIDE.Services.Git;

public enum GitChangeKind { Modified, Added, Deleted, Renamed, Untracked, Conflicted }

public sealed class GitChange
{
    public string Path { get; init; } = "";          // relative, forward slashes
    public string? OldPath { get; init; }
    public char X { get; init; }                      // index status
    public char Y { get; init; }                      // work tree status
    public string FullPath { get; init; } = "";
    public GitChangeKind Kind { get; init; }
    public bool IsStaged => X != ' ' && X != '?';
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path.Replace('/', '\\')) ?? "";
}

public sealed class GitStatus
{
    public string Branch { get; set; } = "";
    public string? Upstream { get; set; }
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public bool Detached { get; set; }
    public List<GitChange> Changes { get; } = new();
}

public sealed record GitCommit(string Hash, string ShortHash, string Author, string Email, DateTime Date, string Refs, string Subject, string[] Parents);

public sealed record GitBranch(string Name, bool IsCurrent, bool IsRemote, string? Upstream);

/// <summary>Thin wrapper over the git command line for the opened project.</summary>
public static class GitService
{
    public static string? Root { get; private set; }
    public static GitStatus? Status { get; private set; }
    public static bool IsRepository => Root != null;
    public static bool IsGitInstalled { get; private set; } = true;

    /// <summary>Raised (on any thread) when the status changed.</summary>
    public static event Action? Changed;

    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    public static async Task<(int code, string stdout, string stderr)> Run(string args, string? cwd = null, int timeoutMs = 120000)
    {
        var r = await ProcessRunner.RunAsync("git", "-c core.quotepath=off -c color.ui=never " + args, cwd ?? Root ?? Workspace.Root, timeoutMs);
        if (r.code == -1 && r.stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase)) IsGitInstalled = false;
        return r;
    }

    public static string Q(string s) => ProcessRunner.Quote(s);

    /// <summary>Detects the repository containing the project.</summary>
    public static async Task OpenAsync(string projectRoot)
    {
        Root = null;
        Status = null;
        var (code, stdout, _) = await ProcessRunner.RunAsync("git", "rev-parse --show-toplevel", projectRoot, 10000);
        if (code == 0 && stdout.Trim().Length > 0) Root = Path.GetFullPath(stdout.Trim());
        await RefreshAsync();
    }

    public static async Task RefreshAsync()
    {
        if (Root == null) { Changed?.Invoke(); return; }
        if (!await RefreshLock.WaitAsync(0)) return;
        try
        {
            var (code, stdout, _) = await Run("status --porcelain=v1 -b -z --untracked-files=all");
            if (code == 0) Status = ParseStatus(stdout);
        }
        finally { RefreshLock.Release(); }
        Changed?.Invoke();
    }

    private static GitStatus ParseStatus(string raw)
    {
        var st = new GitStatus();
        var parts = raw.Split('\0');
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length < 3) continue;
            if (p.StartsWith("## "))
            {
                var head = p[3..];
                var m = Regex.Match(head, @"^(?<b>.+?)(\.\.\.(?<u>\S+))?( \[(?<ab>[^\]]+)\])?$");
                var b = m.Groups["b"].Value;
                if (b.StartsWith("No commits yet on ")) b = b[18..];
                if (b.StartsWith("HEAD (no branch)")) { st.Detached = true; b = "HEAD"; }
                st.Branch = b;
                if (m.Groups["u"].Success) st.Upstream = m.Groups["u"].Value;
                var ab = m.Groups["ab"].Value;
                var a = Regex.Match(ab, @"ahead (\d+)");
                var be = Regex.Match(ab, @"behind (\d+)");
                if (a.Success) st.Ahead = int.Parse(a.Groups[1].Value);
                if (be.Success) st.Behind = int.Parse(be.Groups[1].Value);
                continue;
            }
            char x = p[0], y = p[1];
            var path = p[3..];
            string? old = null;
            if (x == 'R' || x == 'C') { old = i + 1 < parts.Length ? parts[++i] : null; }
            var kind = (x, y) switch
            {
                ('?', '?') => GitChangeKind.Untracked,
                ('U', _) or (_, 'U') or ('A', 'A') or ('D', 'D') => GitChangeKind.Conflicted,
                ('A', _) => GitChangeKind.Added,
                ('R', _) or ('C', _) => GitChangeKind.Renamed,
                ('D', _) or (_, 'D') => GitChangeKind.Deleted,
                _ => GitChangeKind.Modified,
            };
            st.Changes.Add(new GitChange
            {
                Path = path, OldPath = old, X = x, Y = y, Kind = kind,
                FullPath = Path.GetFullPath(Path.Combine(Root!, path.Replace('/', '\\'))),
            });
        }
        return st;
    }

    public static GitChange? ChangeFor(string fullPath) =>
        Status?.Changes.FirstOrDefault(c => string.Equals(c.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the directory contains changed files (PhpStorm colours such folders).</summary>
    public static bool DirectoryHasChanges(string dir)
    {
        if (Status == null) return false;
        var prefix = dir.TrimEnd('\\') + "\\";
        return Status.Changes.Any(c => c.Kind != GitChangeKind.Untracked && c.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static string Relative(string fullPath) =>
        Root == null ? fullPath : Path.GetRelativePath(Root, fullPath).Replace('\\', '/');

    // ------------------------------------------------------------------ content

    /// <summary>Content of a file at a revision (HEAD by default); null when the file does not exist there.</summary>
    public static async Task<string?> ShowFileAsync(string fullPath, string revision = "HEAD")
    {
        if (Root == null) return null;
        var (code, stdout) = await ProcessRunner.RunRawAsync("git", $"-c core.quotepath=off show {Q(revision + ":" + Relative(fullPath))}", Root);
        return code == 0 ? stdout : null;
    }

    public static async Task<bool> IsTrackedAsync(string fullPath)
    {
        if (Root == null) return false;
        var (code, _, _) = await Run($"ls-files --error-unmatch -- {Q(Relative(fullPath))}");
        return code == 0;
    }

    // ------------------------------------------------------------------ history

    public static async Task<List<GitCommit>> LogAsync(int max = 400, string? path = null, bool all = true)
    {
        var list = new List<GitCommit>();
        if (Root == null) return list;
        var args = $"log {(all ? "--all" : "")} --date-order -n {max} --pretty=format:%H%x1f%h%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%P%x1f%s%x1e";
        if (path != null) args += $" -- {Q(Relative(path))}";
        var (code, stdout, _) = await Run(args);
        if (code != 0) return list;
        foreach (var rec in stdout.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.Trim('\n', '\r').Split('\x1f');
            if (f.Length < 8) continue;
            list.Add(new GitCommit(f[0], f[1], f[2], f[3], DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[4])).LocalDateTime, f[5], f[7],
                f[6].Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }
        return list;
    }

    public static async Task<List<(char status, string path)>> CommitFilesAsync(string hash)
    {
        var list = new List<(char, string)>();
        var (code, stdout, _) = await Run($"show --name-status --pretty=format: -M {hash}");
        if (code != 0) return list;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = line.Split('\t');
            if (cols.Length >= 2) list.Add((cols[0][0], cols[^1].Trim()));
        }
        return list;
    }

    public static async Task<string> CommitMessageAsync(string hash)
    {
        var (_, stdout, _) = await Run($"show -s --pretty=format:%B {hash}");
        return stdout.Trim();
    }

    // ------------------------------------------------------------------ branches

    public static async Task<List<GitBranch>> BranchesAsync()
    {
        var list = new List<GitBranch>();
        if (Root == null) return list;
        var (code, stdout, _) = await Run("branch -a --format=%(HEAD)%09%(refname)%09%(upstream:short)");
        if (code != 0) return list;
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var c = line.Split('\t');
            if (c.Length < 2) continue;
            var refname = c[1];
            if (refname.EndsWith("/HEAD")) continue;
            var remote = refname.StartsWith("refs/remotes/");
            var name = remote ? refname[13..] : refname.StartsWith("refs/heads/") ? refname[11..] : refname;
            list.Add(new GitBranch(name, c[0] == "*", remote, c.Length > 2 && c[2].Length > 0 ? c[2] : null));
        }
        return list;
    }

    public static async Task<List<string>> RemotesAsync()
    {
        var (code, stdout, _) = await Run("remote -v");
        return code != 0 ? new List<string>() : stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.EndsWith("(push)")).Select(l => l.Replace(" (push)", "")).ToList();
    }

    public static async Task<List<string>> OutgoingAsync()
    {
        var (code, stdout, _) = await Run("log @{u}..HEAD --pretty=format:%h%x20%s");
        return code == 0 ? stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
    }

    /// <summary>Writes a commit message to a temp file (avoids any quoting problem).</summary>
    public static string MessageFile(string message)
    {
        var f = Path.Combine(Path.GetTempPath(), "neoide-commit-msg.txt");
        File.WriteAllText(f, message, new UTF8Encoding(false));
        return f;
    }

    public static string PathArgs(IEnumerable<GitChange> changes) =>
        string.Join(" ", changes.SelectMany(c => c.OldPath != null ? new[] { c.Path, c.OldPath } : new[] { c.Path }).Distinct().Select(Q));
}
