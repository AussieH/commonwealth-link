using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommonwealthLink;

public sealed class WidgetServer
{
    public const int DefaultPort = 52827;
    public const string KeyHeader = "X-Commonwealth-Deck";
    public const int Protocol = 1, MaxBody = 1024, Rate = 5, PictureRate = 6, MaxConnections = 16, RunTimeoutMs = 5000;
    public static readonly string[] Actions = { "radio", "use", "markHere", "clearMarker" };
    public static readonly string[] ItemNames = { "stimpak", "radaway", "radx" };
    public static readonly string[] Pictures = { "/map/world.png", "/map/local.png" };

    public readonly int Port;
    public readonly string Key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public Func<string, JObj, JObj>? Run;
    public Func<JObj>? Hello;
    public Func<string, byte[]?>? Picture;
    readonly List<TcpListener> listeners = new();
    readonly Queue<long> recent = new(), recentPictures = new();
    int connections;
    long lastRefusal;
    volatile bool stopped;

    public WidgetServer(int port) { Port = port; }
    public bool Listening => listeners.Count > 0;

    public string? Start()
    {
        foreach (var ip in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var l = new TcpListener(ip, Port) { ExclusiveAddressUse = true };
            try
            {
                if (ip.AddressFamily == AddressFamily.InterNetworkV6) l.Server.DualMode = false;
                l.Start(32);
                listeners.Add(l);
            }
            catch (SocketException e)
            {
                if (ip.AddressFamily == AddressFamily.InterNetworkV6 && e.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported) { Program.Log("widget endpoint: no IPv6 loopback, 127.0.0.1 only"); continue; }
                Stop();
                return e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied ? "port in use" : e.SocketErrorCode.ToString();
            }
        }
        stopped = false;
        foreach (var l in listeners) new Thread(() => Accept(l)) { IsBackground = true, Name = "widget" }.Start();
        return null;
    }
    public void Stop()
    {
        stopped = true;
        foreach (var l in listeners) { try { l.Stop(); } catch (Exception) { } }
        listeners.Clear();
    }

    void Accept(TcpListener l)
    {
        while (!stopped && !Program.Quit)
        {
            TcpClient c;
            try { c = l.AcceptTcpClient(); } catch (Exception) { if (stopped) return; continue; }
            var remote = (c.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote == null || !(IPAddress.IsLoopback(remote) || (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4()))) || Interlocked.Increment(ref connections) > MaxConnections)
            {
                if (remote != null && IPAddress.IsLoopback(remote)) Interlocked.Decrement(ref connections);
                try { c.Close(); } catch (Exception) { }
                continue;
            }
            new Thread(() => { try { Serve(c); } finally { Interlocked.Decrement(ref connections); try { c.Close(); } catch (Exception) { } } }) { IsBackground = true }.Start();
        }
    }

    sealed class Request
    {
        public string Method = "", Path = "";
        public readonly Dictionary<string, List<string>> Headers = new(StringComparer.OrdinalIgnoreCase);
        public string? One(string name) => Headers.TryGetValue(name, out var v) ? v[0] : null;
        public int Count(string name) => Headers.TryGetValue(name, out var v) ? v.Count : 0;
    }

    void Serve(TcpClient c)
    {
        var s = c.GetStream();
        c.ReceiveTimeout = 5000; c.SendTimeout = 5000;
        try
        {
            var head = ReadHead(s, out var leftover);
            if (head == null) { RawReply(s, "400 Bad Request"); return; }
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || first[0].Length == 0) { RawReply(s, "400 Bad Request"); return; }
            var req = new Request { Method = first[0], Path = first[1].Split('?')[0] };
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                int i = line.IndexOf(':');
                if (i <= 0 || line[..i].Any(ch => ch <= ' ')) { RawReply(s, "400 Bad Request"); return; }
                var name = line[..i]; var value = line[(i + 1)..].Trim(' ', '\t');
                if (!req.Headers.TryGetValue(name, out var list)) req.Headers[name] = list = new List<string>();
                list.Add(value);
            }
            var cl = req.Headers.TryGetValue("content-length", out var cls) ? cls : null;
            if ((first[2] == "HTTP/1.1" && req.Count("host") == 0) || (cl != null && (cl.Count > 1 || !cl[0].All(char.IsAsciiDigit) || cl[0].Length == 0 || req.Count("transfer-encoding") > 0)))
            { RawReply(s, "400 Bad Request"); return; }
            Handle(s, req, leftover);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException) { }
    }

    static string? ReadHead(NetworkStream s, out byte[] leftover)
    {
        leftover = Array.Empty<byte>();
        var buf = new byte[8192]; int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) return null;
            got += n;
            for (int i = Math.Max(3, got - n - 3); i < got; i++)
                if (buf[i] == '\n' && buf[i - 1] == '\r' && buf[i - 2] == '\n' && buf[i - 3] == '\r')
                {
                    leftover = buf.AsSpan(i + 1, got - i - 1).ToArray();
                    return Encoding.Latin1.GetString(buf, 0, i - 3);
                }
        }
        return null;
    }

    static bool HostAllowed(string? host, int port)
    {
        if (host == null) return false;
        var h = host.Trim().ToLowerInvariant();
        return h == $"localhost:{port}" || h == $"127.0.0.1:{port}" || h == $"[::1]:{port}";
    }
    static bool OriginAllowed(string? origin) => origin == null || origin == "null" || origin.Equals("file://", StringComparison.OrdinalIgnoreCase) || origin.Equals("file:///", StringComparison.OrdinalIgnoreCase);
    bool KeyMatches(string? given) => given != null && given.Length == Key.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(Key));

    static void RawReply(NetworkStream s, string status) { var b = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"); s.Write(b); }

    void Send(NetworkStream s, int status, JObj? body, bool cors = true, bool preflight = false, Request? req = null)
    {
        var text = body == null ? Array.Empty<byte>() : Json.Utf8(Json.Stringify(body));
        var h = new StringBuilder();
        h.Append($"HTTP/1.1 {status} {Reason(status)}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n");
        if (body != null) h.Append("Content-Type: application/json; charset=utf-8\r\n");
        if (cors)
        {
            h.Append("Access-Control-Allow-Origin: *\r\nVary: Origin\r\n");
            if (preflight)
            {
                h.Append($"Access-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type, {KeyHeader}\r\nAccess-Control-Max-Age: 600\r\n");
                if (req?.One("Access-Control-Request-Private-Network") == "true") h.Append("Access-Control-Allow-Private-Network: true\r\n");
            }
        }
        h.Append($"Content-Length: {text.Length}\r\n\r\n");
        s.Write(Encoding.ASCII.GetBytes(h.ToString()));
        s.Write(text);
    }
    static string Reason(int status) => status switch
    {
        200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 411 => "Length Required",
        413 => "Payload Too Large", 415 => "Unsupported Media Type", 429 => "Too Many Requests", 504 => "Gateway Timeout", _ => "Error",
    };

    void Refuse(NetworkStream s, int status, string error, bool cors)
    {
        var now = Program.Now();
        if (status == 403 && now - Interlocked.Read(ref lastRefusal) > 10000) { Interlocked.Exchange(ref lastRefusal, now); Program.Log($"widget endpoint refused a request: {error}"); }
        Send(s, status, new JObj().Set("error", error), cors);
    }

    bool Allow(Queue<long> q, int max)
    {
        lock (q)
        {
            var now = Program.Now();
            while (q.Count > 0 && now - q.Peek() >= 1000) q.Dequeue();
            if (q.Count >= max) return false;
            q.Enqueue(now);
            return true;
        }
    }

    void Handle(NetworkStream s, Request req, byte[] leftover)
    {
        foreach (var h in new[] { "host", "origin", "content-length", "content-type", KeyHeader }) if (req.Count(h) > 1) { Refuse(s, 400, $"more than one {h.ToLowerInvariant()}", false); return; }
        if (!HostAllowed(req.One("host"), Port)) { Refuse(s, 403, "host not allowed", false); return; }
        if (!OriginAllowed(req.One("origin"))) { Refuse(s, 403, "origin not allowed", false); return; }
        var path = req.Path;
        bool isPicture = Array.IndexOf(Pictures, path) >= 0;
        if (path != "/hello" && path != "/command" && !isPicture) { Refuse(s, 404, "not found", true); return; }
        if (req.Method == "OPTIONS") { Send(s, 204, null, preflight: true, req: req); return; }
        if (isPicture) { PictureRequest(s, req, path == "/map/world.png" ? "world" : "local"); return; }
        if (req.Method == "GET" && path == "/hello")
        {
            var o = new JObj().Set("type", "hello").Set("protocol", Protocol).Set("app", Program.AppName).Set("controls", true)
                .Set("actions", new JArr(Actions)).Set("items", new JArr(ItemNames)).Set("pictures", new JArr(Pictures));
            if (Hello != null) foreach (var kv in Hello().Pairs) o.Set(kv.Key, kv.Value);
            Send(s, 200, o);
            return;
        }
        if (req.Method != "POST" || path != "/command") { Refuse(s, 405, "method not allowed", true); return; }
        if (!KeyMatches(req.One(KeyHeader))) { Refuse(s, 403, "key", true); return; }
        if (req.One("transfer-encoding") != null) { Refuse(s, 411, "send a Content-Length, not chunks", true); return; }
        var lenText = req.One("content-length");
        if (lenText == null) { Refuse(s, 411, "Content-Length required", true); return; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(lenText, @"^\d{1,6}$", System.Text.RegularExpressions.RegexOptions.ECMAScript)) { Refuse(s, 400, "bad Content-Length", true); return; }
        int length = int.Parse(lenText, System.Globalization.CultureInfo.InvariantCulture);
        if (length > MaxBody) { Refuse(s, 413, "body too large", true); return; }
        if (length == 0) { Refuse(s, 400, "empty body", true); return; }
        var type = (req.One("content-type") ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (type != "application/json" && type != "text/plain") { Refuse(s, 415, "send application/json or text/plain", true); return; }
        if (!Allow(recent, Rate)) { Refuse(s, 429, "too many commands", true); return; }
        var body = new byte[length];
        int got = Math.Min(leftover.Length, length);
        Buffer.BlockCopy(leftover, 0, body, 0, got);
        while (got < length) { int n = s.Read(body, got, length - got); if (n <= 0) return; got += n; }
        CommandRequest(s, body);
    }

    void PictureRequest(NetworkStream s, Request req, string which)
    {
        if (req.Method != "GET") { Refuse(s, 405, "method not allowed", true); return; }
        if (!KeyMatches(req.One(KeyHeader))) { Refuse(s, 403, "key", true); return; }
        if (!Allow(recentPictures, PictureRate)) { Refuse(s, 429, "too many requests", true); return; }
        byte[]? png = null;
        try { png = Picture?.Invoke(which); } catch (Exception) { png = null; }
        if (png == null || png.Length == 0) { Send(s, 404, new JObj().Set("error", "no picture")); return; }
        var h = $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\nVary: Origin\r\n\r\n";
        s.Write(Encoding.ASCII.GetBytes(h));
        s.Write(png);
    }

    public static (bool Ok, double Id, string Name, JObj Args, string Error) ParseCommand(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 }); } catch (Exception) { return (false, 0, "", new JObj(), "bad message"); }
        using (doc)
        {
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object) return (false, 0, "", new JObj(), "bad message");
            JsonElement P(string k) { JsonElement last = default; foreach (var p in m.EnumerateObject()) if (p.Name == k) last = p.Value; return last; }
            var idEl = P("id");
            double id = idEl.ValueKind == JsonValueKind.Number && idEl.TryGetDouble(out var dv) && double.IsFinite(dv) && Math.Floor(dv) == dv && Math.Abs(dv) <= 9007199254740991 ? dv : 0;
            if (id == 0) id = 0;
            var typeEl = P("type");
            if (typeEl.ValueKind != JsonValueKind.String || typeEl.GetString() != "command") return (false, id, "", new JObj(), "not a command");
            var nameEl = P("name");
            var name = nameEl.ValueKind == JsonValueKind.String ? nameEl.GetString()! : null;
            if (name == null || Array.IndexOf(Actions, name) < 0) return (false, id, "", new JObj(), "unknown command");
            var a = P("args");
            if (a.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object)) return (false, id, "", new JObj(), "bad args");
            JsonElement A(string k) { JsonElement last = default; if (a.ValueKind == JsonValueKind.Object) foreach (var p in a.EnumerateObject()) if (p.Name == k) last = p.Value; return last; }
            if (name == "radio")
            {
                var st = A("station");
                if (st.ValueKind != JsonValueKind.String || Js.Trim(st.GetString()!) == "" || st.GetString()!.Length > 80) return (false, id, "", new JObj(), "no station");
                var on = A("on");
                if (on.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False)) return (false, id, "", new JObj(), "bad args");
                var args = new JObj().Set("station", Js.Trim(st.GetString()!));
                if (on.ValueKind != JsonValueKind.Undefined) args.Set("on", on.ValueKind == JsonValueKind.True);
                return (true, id, "radio", args, "");
            }
            if (name == "use")
            {
                var it = A("item");
                if (it.ValueKind != JsonValueKind.String || Array.IndexOf(ItemNames, it.GetString()) < 0) return (false, id, "", new JObj(), "unknown item");
                return (true, id, "use", new JObj().Set("item", it.GetString()), "");
            }
            return (true, id, name, new JObj(), "");
        }
    }

    void CommandRequest(NetworkStream s, byte[] body)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(body); } catch (Exception) { Send(s, 400, new JObj().Set("type", "result").Set("id", 0).Set("ok", false).Set("error", "bad message")); return; }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var c = ParseCommand(text);
        if (!c.Ok) { Send(s, 400, new JObj().Set("type", "result").Set("id", c.Id).Set("ok", false).Set("error", c.Error)); return; }
        JObj? r = null;
        var task = Task.Run(() => { try { return Run?.Invoke(c.Name, c.Args) ?? new JObj().Set("ok", false).Set("error", "failed"); } catch (Exception) { return new JObj().Set("ok", false).Set("error", "failed"); } });
        bool late = !task.Wait(RunTimeoutMs);
        r = late ? new JObj().Set("ok", false).Set("error", "game not answering") : task.Result;
        bool ok = r["ok"] is true;
        var out_ = new JObj().Set("type", "result").Set("id", c.Id).Set("ok", ok);
        if (ok) out_.Set("msg", r["msg"] as string is { Length: > 0 } msg ? msg : "done");
        else out_.Set("error", r["error"] as string is { Length: > 0 } err ? err : "failed");
        if (r["status"] is string st && st.Length > 0) out_.Set("status", st);
        Send(s, late ? 504 : 200, out_);
    }
}
