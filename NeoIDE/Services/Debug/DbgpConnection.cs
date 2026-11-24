using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace NeoIDE.Services.Debug;

/// <summary>A value of the debugged script (variable, property, array item, evaluation result).</summary>
public sealed class DebugValue
{
    public string Name { get; init; } = "";
    public string FullName { get; init; } = "";
    public string Type { get; init; } = "";
    public string? ClassName { get; init; }
    public string Value { get; init; } = "";
    public bool HasChildren { get; init; }
    public int NumChildren { get; init; }
    public List<DebugValue>? Children { get; set; }
    public int Page { get; init; }

    /// <summary>Text shown after the name: value for scalars, "array(3)" / class name for compound values.</summary>
    public string Display => Type switch
    {
        "string" => "\"" + (Value.Length > 500 ? Value[..500] + "…" : Value).Replace("\r", "\\r").Replace("\n", "\\n") + "\"",
        "array" => $"array({NumChildren})",
        "object" => $"{ClassName ?? "object"}" + (NumChildren > 0 ? $" ({NumChildren})" : ""),
        "null" or "uninitialized" => "null",
        "bool" => Value == "1" ? "true" : "false",
        _ => Value,
    };
}

public sealed record DebugFrame(int Level, string File, int Line, string Where);

/// <summary>
/// One DBGp connection (Xdebug 3): commands "name -i id args -- base64data\0", answers "length\0xml\0".
/// Every command returns its &lt;response&gt; element; run / step_* answer when the script breaks or stops.
/// </summary>
public sealed class DbgpConnection : IDisposable
{
    private static readonly XNamespace Ns = "urn:debugger_protocol_v1";
    private static readonly XNamespace XdebugNs = "https://xdebug.org/dbgp/xdebug";

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<XElement>> _pending = new();
    private int _nextId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public XElement? Init { get; private set; }
    public event Action<string, string>? Stream;   // (stdout|stderr, text)
    public event Action? Closed;
    public bool IsClosed { get; private set; }

    public DbgpConnection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    /// <summary>Reads the init packet then keeps reading the answers in the background.</summary>
    public async Task<XElement> StartAsync(CancellationToken ct)
    {
        Init = await ReadPacketAsync(ct) ?? throw new IOException("No init packet");
        _ = Task.Run(() => ReadLoop(ct));
        return Init;
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await ReadPacketAsync(ct);
                if (packet == null) break;
                if (packet.Name.LocalName == "stream")
                {
                    var text = packet.Attribute("encoding")?.Value == "base64" ? Decode(packet.Value) : packet.Value;
                    Stream?.Invoke(packet.Attribute("type")?.Value ?? "stdout", text);
                    continue;
                }
                if (packet.Name.LocalName == "notify") continue;
                if (int.TryParse(packet.Attribute("transaction_id")?.Value, out var id) && _pending.TryRemove(id, out var tcs))
                    tcs.TrySetResult(packet);
            }
        }
        catch { }
        Close();
    }

    private async Task<XElement?> ReadPacketAsync(CancellationToken ct)
    {
        // length (ASCII digits) NUL xml NUL
        var lengthBytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await _stream.ReadAsync(one, ct);
            if (n == 0) return null;
            if (one[0] == 0) break;
            lengthBytes.Add(one[0]);
            if (lengthBytes.Count > 12) throw new IOException("Bad DBGp packet");
        }
        var length = int.Parse(Encoding.ASCII.GetString(lengthBytes.ToArray()));
        var data = new byte[length + 1];
        int read = 0;
        while (read < data.Length)
        {
            var n = await _stream.ReadAsync(data.AsMemory(read), ct);
            if (n == 0) return null;
            read += n;
        }
        var xml = Encoding.UTF8.GetString(data, 0, length);
        return XElement.Parse(xml);
    }

    /// <summary>Sends a command and waits for its answer.</summary>
    public async Task<XElement> SendAsync(string command, string args = "", string? data = null, int timeoutMs = 0)
    {
        if (IsClosed) throw new IOException("The debug connection is closed");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<XElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var line = $"{command} -i {id}" + (args.Length > 0 ? " " + args : "") + (data != null ? " -- " + Convert.ToBase64String(Encoding.UTF8.GetBytes(data)) : "");
        var bytes = Encoding.UTF8.GetBytes(line + "\0");
        await _writeLock.WaitAsync();
        try { await _stream.WriteAsync(bytes); }
        catch { _pending.TryRemove(id, out _); Close(); throw; }
        finally { _writeLock.Release(); }
        if (timeoutMs > 0)
        {
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (done != tcs.Task) { _pending.TryRemove(id, out _); throw new TimeoutException($"{command}: no answer from Xdebug"); }
        }
        var response = await tcs.Task;
        if (response.Element(Ns + "error") is { } error)
            throw new DbgpException(error.Element(Ns + "message")?.Value ?? $"error {error.Attribute("code")?.Value}", int.TryParse(error.Attribute("code")?.Value, out var c) ? c : 0);
        return response;
    }

    public void Close()
    {
        if (IsClosed) return;
        IsClosed = true;
        foreach (var p in _pending.Values) p.TrySetException(new IOException("The debug connection was closed"));
        _pending.Clear();
        try { _client.Close(); } catch { }
        Closed?.Invoke();
    }

    public void Dispose() => Close();

    // ------------------------------------------------------------------ parsing helpers

    public static string Decode(string base64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim())); }
        catch { return base64; }
    }

    public static (string status, string reason, string? file, int line) ParseStatus(XElement response)
    {
        var status = response.Attribute("status")?.Value ?? "";
        var reason = response.Attribute("reason")?.Value ?? "";
        var msg = response.Element(XdebugNs + "message");
        var file = msg?.Attribute("filename")?.Value;
        var line = int.TryParse(msg?.Attribute("lineno")?.Value, out var l) ? l : 0;
        return (status, reason, file, line);
    }

    public static List<DebugFrame> ParseStack(XElement response) =>
        response.Elements(Ns + "stack").Select(s => new DebugFrame(
            int.TryParse(s.Attribute("level")?.Value, out var lv) ? lv : 0,
            s.Attribute("filename")?.Value ?? "",
            int.TryParse(s.Attribute("lineno")?.Value, out var ln) ? ln : 0,
            s.Attribute("where")?.Value ?? "")).ToList();

    public static List<DebugValue> ParseProperties(XElement parent) =>
        parent.Elements(Ns + "property").Select(ParseProperty).ToList();

    public static DebugValue ParseProperty(XElement p)
    {
        var encoding = p.Attribute("encoding")?.Value;
        // the value is the element's own text (not the text of nested properties); Xdebug 3 may wrap it in <value>
        var valueElement = p.Element(Ns + "value");
        var raw = valueElement != null ? valueElement.Value : string.Concat(p.Nodes().OfType<XText>().Select(t => t.Value));
        var enc = valueElement?.Attribute("encoding")?.Value ?? encoding;
        var nameElement = p.Element(Ns + "name");
        var fullElement = p.Element(Ns + "fullname");
        string Attr(string n, XElement? e) => e != null ? (e.Attribute("encoding")?.Value == "base64" ? Decode(e.Value) : e.Value) : p.Attribute(n)?.Value ?? "";
        var v = new DebugValue
        {
            Name = Attr("name", nameElement),
            FullName = Attr("fullname", fullElement),
            Type = p.Attribute("type")?.Value ?? "",
            ClassName = p.Attribute("classname")?.Value,
            Value = enc == "base64" ? Decode(raw) : raw.Trim(),
            HasChildren = p.Attribute("children")?.Value == "1",
            NumChildren = int.TryParse(p.Attribute("numchildren")?.Value, out var n) ? n : 0,
            Page = int.TryParse(p.Attribute("page")?.Value, out var pg) ? pg : 0,
        };
        var children = p.Elements(Ns + "property").ToList();
        if (children.Count > 0) v.Children = children.Select(ParseProperty).ToList();
        return v;
    }

    /// <summary>"file:///C:/x/y%20z.php" → "C:\x\y z.php" (and the reverse).</summary>
    public static string ToLocalPath(string uri)
    {
        try
        {
            if (uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(new Uri(uri).LocalPath);
        }
        catch { }
        return uri;
    }

    public static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;
}

public sealed class DbgpException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}
