using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using System.Windows;

namespace NeoIDE.Services.Debug;

public enum DebugState { Idle, Listening, Running, Paused }

/// <summary>Line breakpoints of the project (saved in .neoide/project.json).</summary>
public static class Breakpoints
{
    public static event Action<string?>? Changed; // file, or null for all

    public static IReadOnlyList<BreakpointDef> All => Workspace.Settings.Breakpoints;

    public static IEnumerable<BreakpointDef> For(string file) =>
        Workspace.Settings.Breakpoints.Where(b => string.Equals(b.File, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));

    public static BreakpointDef? At(string file, int line) => For(file).FirstOrDefault(b => b.Line == line);

    public static bool Muted { get; private set; }

    public static void SetMuted(bool muted)
    {
        Muted = muted;
        Changed?.Invoke(null);
    }

    public static void Toggle(string file, int line)
    {
        var existing = At(file, line);
        if (existing != null) Remove(existing);
        else Add(new BreakpointDef { File = Path.GetFullPath(file), Line = line });
    }

    public static void Add(BreakpointDef bp)
    {
        bp.File = Path.GetFullPath(bp.File);
        Workspace.Settings.Breakpoints.Add(bp);
        Save(bp.File);
    }

    public static void Remove(BreakpointDef bp)
    {
        Workspace.Settings.Breakpoints.Remove(bp);
        Save(bp.File);
    }

    public static void Update(BreakpointDef bp) => Save(bp.File);

    public static void RemoveAll()
    {
        Workspace.Settings.Breakpoints.Clear();
        Workspace.SaveSettings();
        Changed?.Invoke(null);
    }

    /// <summary>The lines of a file moved (editing above a breakpoint): saved without notifying the editor again.</summary>
    public static void Moved(string file) { Workspace.SaveSettings(); Changed?.Invoke(Path.GetFullPath(file)); }

    private static void Save(string file)
    {
        Workspace.SaveSettings();
        Changed?.Invoke(Path.GetFullPath(file));
    }
}

/// <summary>
/// The PHP debugger: listens for Xdebug (DBGp on port 9003), one script at a time (the other requests wait,
/// like PHP's built-in server), and drives the paused script for the Debug tool window and the editors.
/// </summary>
public static class DebugService
{
    private static TcpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static DbgpConnection? _conn;
    private static readonly Dictionary<BreakpointDef, string> _remoteIds = new();
    private static readonly Channel<TcpClient> _queue = Channel.CreateUnbounded<TcpClient>();

    public static DebugState State { get; private set; } = DebugState.Idle;
    public static bool IsListening => _listener != null;
    public static int Port { get; private set; }

    /// <summary>Location of the paused script (null while running).</summary>
    public static (string file, int line)? Position { get; private set; }
    public static List<DebugFrame> Stack { get; private set; } = new();
    public static int SelectedFrame { get; set; }
    /// <summary>Incremented at every pause (the views reload the frames / variables once per pause).</summary>
    public static int PauseCount { get; private set; }
    public static string? ScriptName { get; private set; }

    public static event Action? StateChanged;
    public static event Action<string, bool>? Output;   // (text, isError)
    /// <summary>The script stopped at a line (file, line): the main window opens it.</summary>
    public static event Action<string, int>? Paused;

    private static void Raise(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    private static void SetState(DebugState s)
    {
        State = s;
        Raise(() => StateChanged?.Invoke());
    }

    // ------------------------------------------------------------------ listening

    public static bool StartListening(int port, out string? error)
    {
        error = null;
        if (_listener != null) return true;
        try
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            _listener = l;
            Port = port;
        }
        catch (SocketException ex)
        {
            try
            {
                // another IPv4 wildcard listener (or a firewall rule): loopback only
                var l = new TcpListener(IPAddress.Loopback, port);
                l.Start();
                _listener = l;
                Port = port;
            }
            catch
            {
                error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? $"Port {port} is already used (another IDE listening for Xdebug?)"
                    : ex.Message;
                return false;
            }
        }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => AcceptLoop(_listener, ct));
        _ = Task.Run(() => SessionLoop(ct));
        SetState(DebugState.Listening);
        Raise(() => Output?.Invoke($"Listening for Xdebug on port {port}", false));
        return true;
    }

    public static void StopListening()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _conn?.Close();
        SetState(DebugState.Idle);
    }

    private static async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                await _queue.Writer.WriteAsync(client, ct);
            }
        }
        catch { }
    }

    private static async Task SessionLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _queue.Reader.ReadAsync(ct);
                try { await RunSession(client, ct); }
                catch (Exception ex) { Raise(() => Output?.Invoke("Debug session error: " + ex.Message, true)); }
                finally
                {
                    _conn = null;
                    Position = null;
                    Stack = new();
                    ScriptName = null;
                    _remoteIds.Clear();
                    SetState(_listener != null ? DebugState.Listening : DebugState.Idle);
                }
            }
        }
        catch { }
    }

    private static async Task RunSession(TcpClient client, CancellationToken ct)
    {
        var conn = new DbgpConnection(client);
        var init = await conn.StartAsync(ct);
        _conn = conn;
        conn.Stream += (type, text) => Raise(() => Output?.Invoke(text, type == "stderr"));
        ScriptName = ToLocal(init.Attribute("fileuri")?.Value ?? "");
        Raise(() => Output?.Invoke($"Connection from Xdebug: {RelativeName(ScriptName)}", false));
        SetState(DebugState.Running);

        await Try(() => conn.SendAsync("feature_set", "-n max_depth -v 1", timeoutMs: 5000));
        await Try(() => conn.SendAsync("feature_set", "-n max_children -v 200", timeoutMs: 5000));
        await Try(() => conn.SendAsync("feature_set", "-n max_data -v 4096", timeoutMs: 5000));
        await Try(() => conn.SendAsync("feature_set", "-n extended_properties -v 1", timeoutMs: 5000));
        await Try(() => conn.SendAsync("stdout", "-c 1", timeoutMs: 5000));
        await Try(() => conn.SendAsync("stderr", "-c 1", timeoutMs: 5000));
        if (!Breakpoints.Muted)
            foreach (var bp in Breakpoints.All.Where(b => b.Enabled).ToList()) await SetRemoteBreakpoint(conn, bp);
        Breakpoints.Changed += OnBreakpointsChanged;
        try
        {
            await Continue(AppSettings.Current.BreakAtFirstLine ? "step_into" : "run");
            // the session lives until Xdebug closes the connection
            var closed = new TaskCompletionSource();
            conn.Closed += () => closed.TrySetResult();
            if (!conn.IsClosed) await closed.Task.WaitAsync(ct);
        }
        finally
        {
            Breakpoints.Changed -= OnBreakpointsChanged;
            conn.Close();
            Raise(() => Output?.Invoke("Debug session ended", false));
        }
    }

    private static async Task Try(Func<Task> a)
    {
        try { await a(); } catch (DbgpException) { } catch (TimeoutException) { }
    }

    private static async Task SetRemoteBreakpoint(DbgpConnection conn, BreakpointDef bp)
    {
        try
        {
            var uri = ToRemoteUri(bp.File);
            var response = string.IsNullOrWhiteSpace(bp.Condition)
                ? await conn.SendAsync("breakpoint_set", $"-t line -f {uri} -n {bp.Line}", timeoutMs: 5000)
                : await conn.SendAsync("breakpoint_set", $"-t conditional -f {uri} -n {bp.Line}", bp.Condition, timeoutMs: 5000);
            if (response.Attribute("id")?.Value is { } id) _remoteIds[bp] = id;
        }
        catch (Exception ex) { Raise(() => Output?.Invoke($"Breakpoint {Path.GetFileName(bp.File)}:{bp.Line}: {ex.Message}", true)); }
    }

    private static async void OnBreakpointsChanged(string? file)
    {
        var conn = _conn;
        if (conn == null || conn.IsClosed) return;
        // breakpoints changed during a session: remove the stale ones, add the new ones
        foreach (var (bp, id) in _remoteIds.ToList())
            if (Breakpoints.Muted || !Breakpoints.All.Contains(bp) || !bp.Enabled)
            {
                _remoteIds.Remove(bp);
                await Try(() => conn.SendAsync("breakpoint_remove", $"-d {id}", timeoutMs: 5000));
            }
        if (Breakpoints.Muted) return;
        foreach (var bp in Breakpoints.All.Where(b => b.Enabled && !_remoteIds.ContainsKey(b)).ToList())
            await SetRemoteBreakpoint(conn, bp);
    }

    // ------------------------------------------------------------------ commands

    public static Task Resume() => Continue("run");
    public static Task StepOver() => Continue("step_over");
    public static Task StepInto() => Continue("step_into");
    public static Task StepOut() => Continue("step_out");

    public static async Task Stop()
    {
        var conn = _conn;
        if (conn == null) return;
        try { await conn.SendAsync("stop", timeoutMs: 3000); } catch { }
        conn.Close();
    }

    /// <summary>Detach: the script continues without the debugger.</summary>
    public static async Task Detach()
    {
        var conn = _conn;
        if (conn == null) return;
        try { await conn.SendAsync("detach", timeoutMs: 3000); } catch { }
        conn.Close();
    }

    private static async Task Continue(string command)
    {
        var conn = _conn;
        if (conn == null || conn.IsClosed) return;
        Position = null;
        Stack = new();
        SetState(DebugState.Running);
        System.Xml.Linq.XElement response;
        try { response = await conn.SendAsync(command); }
        catch { return; }
        var (status, _, file, line) = DbgpConnection.ParseStatus(response);
        if (status == "break")
        {
            try { Stack = DbgpConnection.ParseStack(await conn.SendAsync("stack_get", timeoutMs: 10000)).Select(f => f with { File = ToLocal(f.File) }).ToList(); }
            catch { Stack = new(); }
            SelectedFrame = 0;
            var top = Stack.FirstOrDefault();
            var local = top != null ? (top.File, top.Line) : (ToLocal(file ?? ""), line);
            Position = local;
            PauseCount++;
            SetState(DebugState.Paused);
            Raise(() => Paused?.Invoke(local.Item1, local.Item2));
        }
        else if (status is "stopping" or "stopped")
        {
            // end of the script: let Xdebug finish it
            try { await conn.SendAsync("stop", timeoutMs: 3000); } catch { }
            conn.Close();
        }
    }

    /// <summary>Variables of a context (0 = locals, 1 = superglobals, 2 = constants) of a stack frame.</summary>
    public static async Task<List<DebugValue>> Variables(int depth, int context = 0)
    {
        var conn = _conn;
        if (conn == null || State != DebugState.Paused) return new();
        try { return DbgpConnection.ParseProperties(await conn.SendAsync("context_get", $"-d {depth} -c {context}", timeoutMs: 10000)); }
        catch { return new(); }
    }

    /// <summary>Children of an array / object (loaded when the tree node is expanded).</summary>
    public static async Task<List<DebugValue>> Children(DebugValue value, int depth)
    {
        var conn = _conn;
        if (conn == null || State != DebugState.Paused || value.FullName.Length == 0) return new();
        var all = new List<DebugValue>();
        try
        {
            for (int page = 0; page < 10; page++)
            {
                var response = await conn.SendAsync("property_get", $"-d {depth} -n {Quote(value.FullName)} -p {page}", timeoutMs: 10000);
                var prop = response.Elements().FirstOrDefault(e => e.Name.LocalName == "property");
                if (prop == null) break;
                var parsed = DbgpConnection.ParseProperty(prop);
                var children = parsed.Children ?? new();
                all.AddRange(children);
                if (children.Count == 0 || all.Count >= parsed.NumChildren) break;
            }
        }
        catch { }
        return all;
    }

    /// <summary>Evaluates a PHP expression in the paused frame.</summary>
    public static async Task<(DebugValue? value, string? error)> Evaluate(string expression, int depth = 0)
    {
        var conn = _conn;
        if (conn == null || State != DebugState.Paused) return (null, "The script is not paused");
        try
        {
            var response = await conn.SendAsync("eval", depth > 0 ? $"-d {depth}" : "", expression, timeoutMs: 15000);
            var prop = response.Elements().FirstOrDefault(e => e.Name.LocalName == "property");
            if (prop == null) return (null, "No result");
            var v = DbgpConnection.ParseProperty(prop);
            return (new DebugValue
            {
                Name = expression, FullName = v.FullName.Length > 0 ? v.FullName : "", Type = v.Type, ClassName = v.ClassName, Value = v.Value,
                HasChildren = v.HasChildren, NumChildren = v.NumChildren, Children = v.Children,
            }, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>Sets a variable of the paused frame (value is a PHP expression).</summary>
    public static async Task<string?> SetValue(DebugValue variable, string phpExpression, int depth)
    {
        var conn = _conn;
        if (conn == null || State != DebugState.Paused) return "The script is not paused";
        try
        {
            await conn.SendAsync("property_set", $"-d {depth} -n {Quote(variable.FullName)}", phpExpression, timeoutMs: 10000);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string Quote(string name) => "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ------------------------------------------------------------------ paths

    /// <summary>Server path → local path (DebugServerRoot is the project root on the server: Docker, VM…).</summary>
    public static string ToLocal(string uriOrPath)
    {
        var path = DbgpConnection.ToLocalPath(uriOrPath);
        var serverRoot = Workspace.Settings.DebugServerRoot?.TrimEnd('/', '\\');
        if (!string.IsNullOrEmpty(serverRoot) && Workspace.Root != null)
        {
            var raw = uriOrPath.StartsWith("file://") ? Uri.UnescapeDataString(new Uri(uriOrPath).AbsolutePath) : uriOrPath;
            if (raw.StartsWith(serverRoot + "/", StringComparison.Ordinal) || raw.StartsWith(serverRoot + "\\", StringComparison.Ordinal))
                return Path.GetFullPath(Path.Combine(Workspace.Root, raw[(serverRoot.Length + 1)..].Replace('/', '\\')));
        }
        return path;
    }

    public static string ToRemoteUri(string localPath)
    {
        var serverRoot = Workspace.Settings.DebugServerRoot?.TrimEnd('/', '\\');
        if (!string.IsNullOrEmpty(serverRoot) && Workspace.Root != null && localPath.StartsWith(Workspace.Root, StringComparison.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(Workspace.Root, localPath).Replace('\\', '/');
            return "file://" + string.Join("/", (serverRoot + "/" + rel).Split('/').Select(Uri.EscapeDataString)).Replace("%3A", ":");
        }
        return DbgpConnection.ToFileUri(localPath);
    }

    private static string RelativeName(string path) =>
        Workspace.Root != null && path.StartsWith(Workspace.Root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(Workspace.Root, path) : path;

    // ------------------------------------------------------------------ Xdebug installation

    /// <summary>Xdebug version of an interpreter (null when not loaded).</summary>
    public static async Task<string?> XdebugVersion(string php)
    {
        var (_, stdout, _) = await ProcessRunner.RunAsync(php, "-r \"echo phpversion('xdebug') ?: '';\"", null, 10000);
        var v = stdout.Trim();
        return v.Length > 0 ? v : null;
    }

    /// <summary>php arguments enabling the step debugger for one run (bin/neo serve, a command, a script).</summary>
    public static string PhpArgs(int port) =>
        $"-dxdebug.mode=debug -dxdebug.start_with_request=yes -dxdebug.client_host=127.0.0.1 -dxdebug.client_port={port} -dxdebug.idekey=NEOIDE";

    /// <summary>
    /// The same settings as environment variables: inherited by the PHP processes a command starts
    /// (the workers of "bin/neo serve", which do not get the -d options of the parent).
    /// </summary>
    public static Dictionary<string, string> XdebugEnvironment(int port) => new()
    {
        ["XDEBUG_MODE"] = "debug",
        ["XDEBUG_CONFIG"] = $"client_host=127.0.0.1 client_port={port} idekey=NEOIDE start_with_request=yes",
        ["XDEBUG_SESSION"] = "NEOIDE",
    };
}
