using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>A fake Xdebug 3: connects to the IDE like PHP does, answers the DBGp commands, breaks once at a line.</summary>
public static class FakeXdebug
{
    public static async Task ServeAsync(int port, string projectDir, string file, int line, List<string> commands, bool stayPaused = false)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port);
        var s = client.GetStream();
        var uri = new Uri(file).AbsoluteUri;
        const string NS = "xmlns=\"urn:debugger_protocol_v1\" xmlns:xdebug=\"https://xdebug.org/dbgp/xdebug\"";
        async Task Send(string xml)
        {
            var body = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"iso-8859-1\"?>\n" + xml);
            await s.WriteAsync(Encoding.ASCII.GetBytes(body.Length.ToString()));
            s.WriteByte(0);
            await s.WriteAsync(body);
            s.WriteByte(0);
        }
        async Task<string?> Read()
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                if (await s.ReadAsync(one) == 0) return null;
                if (one[0] == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(one[0]);
            }
        }
        string B64(string v) => Convert.ToBase64String(Encoding.UTF8.GetBytes(v));
        var runs = 0;
        await Send($"<init {NS} fileuri=\"{new Uri(Path.Combine(projectDir, "public", "index.php")).AbsoluteUri}\" language=\"PHP\" protocol_version=\"1.0\" appid=\"1\" idekey=\"NEOIDE\"><engine version=\"3.4.0\"><![CDATA[Xdebug]]></engine></init>");
        while (true)
        {
            var cmd = await Read();
            if (cmd == null) return;
            lock (commands) commands.Add(cmd);
            var name = cmd.Split(' ')[0];
            var id = Regex.Match(cmd, @"-i (\d+)").Groups[1].Value;
            switch (name)
            {
                case "breakpoint_set": await Send($"<response {NS} command=\"breakpoint_set\" transaction_id=\"{id}\" id=\"1000\"/>"); break;
                case "run" when ++runs == 1:
                    await Send($"<response {NS} command=\"run\" transaction_id=\"{id}\" status=\"break\" reason=\"ok\"><xdebug:message filename=\"{uri}\" lineno=\"{line}\"></xdebug:message></response>");
                    break;
                case "run": await Send($"<response {NS} command=\"run\" transaction_id=\"{id}\" status=\"stopping\" reason=\"ok\"/>"); break;
                case "stop": await Send($"<response {NS} command=\"stop\" transaction_id=\"{id}\" status=\"stopped\" reason=\"ok\"/>"); client.Close(); return;
                case "stack_get":
                    await Send($"<response {NS} command=\"stack_get\" transaction_id=\"{id}\"><stack where=\"App\\Controller\\HomeController-&gt;index\" level=\"0\" type=\"file\" filename=\"{uri}\" lineno=\"{line}\"></stack>" +
                               $"<stack where=\"NeoPHP\\Component\\Kernel\\HttpKernel-&gt;handle\" level=\"1\" type=\"file\" filename=\"file:///C:/x/vendor/neophp/HttpKernel.php\" lineno=\"112\"></stack>" +
                               $"<stack where=\"{{main}}\" level=\"2\" type=\"file\" filename=\"file:///C:/x/public/index.php\" lineno=\"5\"></stack></response>");
                    break;
                case "context_get" when cmd.Contains("-c 0"):
                    await Send($"<response {NS} command=\"context_get\" transaction_id=\"{id}\" context=\"0\">" +
                               $"<property name=\"$title\" fullname=\"$title\" type=\"string\" size=\"7\" encoding=\"base64\"><![CDATA[{B64("Welcome to NeoIDE")}]]></property>" +
                               "<property name=\"$items\" fullname=\"$items\" type=\"array\" children=\"1\" numchildren=\"2\" page=\"0\" pagesize=\"200\"></property>" +
                               "<property name=\"$this\" fullname=\"$this\" type=\"object\" classname=\"App\\Controller\\HomeController\" children=\"1\" numchildren=\"4\"></property>" +
                               "<property name=\"$n\" fullname=\"$n\" type=\"int\"><![CDATA[3]]></property></response>");
                    break;
                case "context_get": await Send($"<response {NS} command=\"context_get\" transaction_id=\"{id}\" context=\"1\"><property name=\"$_GET\" fullname=\"$_GET\" type=\"array\" children=\"0\" numchildren=\"0\"></property></response>"); break;
                case "property_get":
                    await Send($"<response {NS} command=\"property_get\" transaction_id=\"{id}\"><property name=\"$items\" fullname=\"$items\" type=\"array\" children=\"1\" numchildren=\"2\" page=\"0\" pagesize=\"200\">" +
                               "<property name=\"0\" fullname=\"$items[0]\" type=\"string\" encoding=\"base64\"><![CDATA[" + B64("a") + "]]></property>" +
                               "<property name=\"1\" fullname=\"$items[1]\" type=\"bool\"><![CDATA[1]]></property></property></response>");
                    break;
                case "eval":
                    var expr = Encoding.UTF8.GetString(Convert.FromBase64String(cmd[(cmd.IndexOf(" -- ") + 4)..]));
                    await Send($"<response {NS} command=\"eval\" transaction_id=\"{id}\"><property type=\"int\"><![CDATA[{(expr == "6*7" ? 42 : expr.Length)}]]></property></response>");
                    break;
                default: await Send($"<response {NS} command=\"{name}\" transaction_id=\"{id}\" success=\"1\"/>"); break;
            }
        }
    }
}
