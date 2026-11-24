using System.Diagnostics;
using System.IO;
using System.Text;

namespace NeoIDE.Services;

/// <summary>Runs external processes (php, composer, git) and streams their output.</summary>
public sealed class ProcessRunner
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public event Action<string, bool>? Output; // (line, isError)
    public event Action<int>? Exited;

    public static async Task<(int code, string stdout, string stderr)> RunAsync(
        string exe, string args, string? workingDir = null, int timeoutMs = 60000)
    {
        var psi = CreateStartInfo(exe, args, workingDir);
        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, so.ToString(), "Timeout");
        }
        return (p.ExitCode, so.ToString(), se.ToString());
    }

    /// <summary>Like <see cref="RunAsync"/> but returns stdout byte-exact (line endings preserved).</summary>
    public static async Task<(int code, string stdout)> RunRawAsync(string exe, string args, string? workingDir, int timeoutMs = 60000)
    {
        var psi = CreateStartInfo(exe, args, workingDir);
        using var p = new Process { StartInfo = psi };
        try { p.Start(); } catch { return (-1, ""); }
        p.StandardInput.Close();
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (-1, ""); }
        var stdout = await stdoutTask;
        await stderrTask;
        return (p.ExitCode, stdout);
    }

    private static ProcessStartInfo CreateStartInfo(string exe, string args, string? workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["COMPOSER_NO_INTERACTION"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // the project PHP comes first on PATH, so "php" / "composer" in a terminal use the project interpreter
        if (Workspace.PhpExe != null && Path.GetDirectoryName(Workspace.PhpExe) is { } phpDir)
            psi.Environment["PATH"] = phpDir + ";" + (psi.Environment.TryGetValue("PATH", out var p) ? p : "");
        return psi;
    }

    /// <summary>Starts a long-running process, streaming output through <see cref="Output"/>.</summary>
    public Task<int> StartAsync(string exe, string args, string? workingDir, IDictionary<string, string>? env = null, string? stdin = null)
    {
        var psi = CreateStartInfo(exe, args, workingDir);
        if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        var tcs = new TaskCompletionSource<int>();
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data, false); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) Output?.Invoke(e.Data, true); };
        _process.Exited += (_, _) =>
        {
            int code;
            try { _process.WaitForExit(); code = _process.ExitCode; } catch { code = -1; }
            Exited?.Invoke(code);
            tcs.TrySetResult(code);
        };
        try
        {
            _process.Start();
            if (stdin != null) _process.StandardInput.Write(stdin);
            _process.StandardInput.Close();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Output?.Invoke($"Unable to start {exe}: {ex.Message}", true);
            tcs.TrySetResult(-1);
        }
        return tcs.Task;
    }

    /// <summary>
    /// Interactive process: stdin stays open (<see cref="SendLine"/>) and the output is streamed in raw chunks,
    /// so prompts that do not end with a new line (" > ") are shown immediately.
    /// </summary>
    public event Action<string, bool>? RawOutput; // (chunk, isError)

    public async Task<int> StartInteractiveAsync(string exe, string args, string? workingDir, IDictionary<string, string>? env = null)
    {
        var psi = CreateStartInfo(exe, args, workingDir);
        psi.StandardInputEncoding = new UTF8Encoding(false);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try { _process.Start(); }
        catch (Exception ex) { RawOutput?.Invoke($"Unable to start {exe}: {ex.Message}\n", true); return -1; }
        _process.StandardInput.AutoFlush = true;
        async Task Pump(StreamReader reader, bool err)
        {
            var buffer = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0) RawOutput?.Invoke(new string(buffer, 0, n), err);
        }
        var outTask = Pump(_process.StandardOutput, false);
        var errTask = Pump(_process.StandardError, true);
        await _process.WaitForExitAsync();
        await Task.WhenAll(outTask, errTask);
        var code = _process.ExitCode;
        Exited?.Invoke(code);
        return code;
    }

    public void SendLine(string line)
    {
        try { if (IsRunning) _process!.StandardInput.WriteLine(line); } catch { }
    }

    public void Stop()
    {
        try { if (IsRunning) _process!.Kill(entireProcessTree: true); } catch { }
    }

    /// <summary>Quotes a command line argument for Windows.</summary>
    public static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"') ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";
}
