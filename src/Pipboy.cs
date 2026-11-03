using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CommonwealthLink;

public static class Channel { public const byte KeepAlive = 0, Accepted = 1, Busy = 2, Data = 3, LocalMap = 4, Command = 5, CommandResult = 6; }
public static class EntryType { public const byte Bool = 0, Int8 = 1, UInt8 = 2, Int32 = 3, UInt32 = 4, Float = 5, String = 6, List = 7, Dict = 8; }
public static class Command
{
    public const int UseItem = 0, SetCustomMapMarker = 6, RemoveCustomMapMarker = 7, CheckFastTravel = 8, FastTravel = 9, ToggleRadioStation = 12, RequestLocalMapSnapshot = 13;
}

public sealed class Entry
{
    public byte Type; public uint Id;
    public object? Value;
    public uint[]? List;
    public List<(uint Ref, string Key)>? Insert;
    public uint[]? Remove;
}

public sealed class LocalMapData { public int Width, Height; public double NwX, NwY, NeX, NeY, SwX, SwY; public byte[] Pixels = Array.Empty<byte>(); }

public static class Codec
{
    public const int Header = 5;
    public const int MaxMessage = 32 * 1024 * 1024;

    public static byte[] Frame(byte channel, byte[] payload)
    {
        var b = new byte[Header + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)payload.Length);
        b[4] = channel;
        payload.CopyTo(b, Header);
        return b;
    }
    public static byte[] KeepAlive() => Frame(Channel.KeepAlive, Array.Empty<byte>());

    public static byte[] CommandFrame(int type, IEnumerable<object?> args, int id)
    {
        var o = new JObj().Set("type", (double)type).Set("args", new JArr(args)).Set("id", (double)id);
        return Frame(Channel.Command, Json.Utf8(Json.Stringify(o)));
    }

    static string CString(byte[] b, ref int at)
    {
        int end = Array.IndexOf(b, (byte)0, at);
        if (end < 0) throw new FormatException($"a string at byte {at} has no end");
        var s = Encoding.UTF8.GetString(b, at, end - at);
        at = end + 1;
        return s;
    }
    static void Need(byte[] b, int at, long n, string what) { if (at + n > b.Length) throw new FormatException($"{what} at byte {at} runs past the end ({b.Length})"); }

    public static List<Entry> DecodeDataUpdate(byte[] b)
    {
        var out_ = new List<Entry>();
        int at = 0;
        while (at < b.Length)
        {
            Need(b, at, 5, "an entry header");
            var e = new Entry { Type = b[at], Id = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 1)) };
            at += 5;
            switch (e.Type)
            {
                case EntryType.Bool: Need(b, at, 1, "a bool"); e.Value = b[at] != 0; at += 1; break;
                case EntryType.Int8: Need(b, at, 1, "an int8"); e.Value = (double)(sbyte)b[at]; at += 1; break;
                case EntryType.UInt8: Need(b, at, 1, "a uint8"); e.Value = (double)b[at]; at += 1; break;
                case EntryType.Int32: Need(b, at, 4, "an int32"); e.Value = (double)BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)); at += 4; break;
                case EntryType.UInt32: Need(b, at, 4, "a uint32"); e.Value = (double)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)); at += 4; break;
                case EntryType.Float: Need(b, at, 4, "a float"); e.Value = (double)BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(at)); at += 4; break;
                case EntryType.String: e.Value = CString(b, ref at); break;
                case EntryType.List:
                    {
                        Need(b, at, 2, "a list count");
                        int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)); at += 2;
                        Need(b, at, 4L * n, "a list");
                        var list = new uint[n];
                        for (int i = 0; i < n; i++) list[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 4 * i));
                        at += 4 * n;
                        e.List = list;
                        break;
                    }
                case EntryType.Dict:
                    {
                        Need(b, at, 2, "a dict count");
                        int n = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)); at += 2;
                        var ins = new List<(uint, string)>(n);
                        for (int i = 0; i < n; i++)
                        {
                            Need(b, at, 4, "a dict reference");
                            uint r = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
                            at += 4;
                            ins.Add((r, CString(b, ref at)));
                        }
                        Need(b, at, 2, "a dict removal count");
                        int m = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)); at += 2;
                        Need(b, at, 4L * m, "dict removals");
                        var rem = new uint[m];
                        for (int i = 0; i < m; i++) rem[i] = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at + 4 * i));
                        at += 4 * m;
                        e.Insert = ins; e.Remove = rem;
                        break;
                    }
                default: throw new FormatException($"unknown entry type {e.Type} at byte {at - 5}");
            }
            out_.Add(e);
        }
        return out_;
    }

    public static LocalMapData DecodeLocalMap(byte[] b)
    {
        Need(b, 0, 32, "a local map header");
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(b), height = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4));
        if (width == 0 || height == 0 || width > 8192 || height > 8192) throw new FormatException($"a local map of {width} x {height} is not one");
        double F(int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
        var m = new LocalMapData { Width = (int)width, Height = (int)height, NwX = F(8), NwY = F(12), NeX = F(16), NeY = F(20), SwX = F(24), SwY = F(28) };
        int size = (int)(width * height);
        Need(b, 32, size, "the local map's pixels");
        int stride = (b.Length - 32) / (int)height;
        if (stride > width && (b.Length - 32) % height == 0)
        {
            var px = new byte[size];
            for (int y = 0; y < height; y++) Buffer.BlockCopy(b, 32 + y * stride, px, y * (int)width, (int)width);
            m.Pixels = px;
        }
        else m.Pixels = b.AsSpan(32, size).ToArray();
        return m;
    }
}

public sealed class PipDatabase
{
    sealed class Dict
    {
        public readonly List<string> Order = new();
        public readonly Dictionary<string, uint> Refs = new(StringComparer.Ordinal);
        public Dict Copy() { var d = new Dict(); d.Order.AddRange(Order); foreach (var kv in Refs) d.Refs[kv.Key] = kv.Value; return d; }
        public void Set(string k, uint r) { if (!Refs.ContainsKey(k)) Order.Add(k); Refs[k] = r; }
        public void Delete(string k) { if (Refs.Remove(k)) Order.Remove(k); }
    }
    readonly Dictionary<uint, (byte Type, object? Value)> entries = new();
    bool treeMade;
    const int MaxDepth = 32;

    public int Size => entries.Count;
    public bool Ready => entries.ContainsKey(0);
    public void Clear() { entries.Clear(); treeRaw = null; treeMade = false; }

    public void Apply(List<Entry> list)
    {
        foreach (var e in list)
        {
            if (e.Type == EntryType.Dict)
            {
                var keys = entries.TryGetValue(e.Id, out var old) && old.Type == EntryType.Dict ? ((Dict)old.Value!).Copy() : new Dict();
                if (e.Remove!.Length > 0)
                {
                    var gone = new HashSet<uint>(e.Remove);
                    foreach (var k in keys.Order.ToArray()) if (gone.Contains(keys.Refs[k])) keys.Delete(k);
                }
                foreach (var (r, k) in e.Insert!) keys.Set(k, r);
                entries[e.Id] = (EntryType.Dict, keys);
            }
            else if (e.Type == EntryType.List) entries[e.Id] = (EntryType.List, (uint[])e.List!.Clone());
            else entries[e.Id] = (e.Type, e.Value);
        }
        treeMade = false;
    }

    static long? IndexKey(string k)
    {
        if (k.Length == 0 || k.Length > 10) return null;
        if (k == "0") return 0;
        if (k[0] < '1' || k[0] > '9') return null;
        foreach (var c in k) if (c < '0' || c > '9') return null;
        var v = long.Parse(k, System.Globalization.CultureInfo.InvariantCulture);
        return v < 4294967295L ? v : null;
    }

    public object? Value(uint id) => Value(id, 0, new HashSet<uint>());
    object? Value(uint id, int depth, HashSet<uint> seen)
    {
        if (!entries.TryGetValue(id, out var e)) return Undefined.Value;
        if (e.Type == EntryType.Dict)
        {
            if (depth > MaxDepth || seen.Contains(id)) return Undefined.Value;
            seen.Add(id);
            var d = (Dict)e.Value!;
            var o = new JObj { PipId = id };
            var ordered = d.Order.Select(k => (k, idx: IndexKey(k))).ToList();
            foreach (var (k, _) in ordered.Where(p => p.idx != null).OrderBy(p => p.idx)) { var v = Value(d.Refs[k], depth + 1, seen); if (v is not Undefined) o.Set(k, v); }
            foreach (var (k, _) in ordered.Where(p => p.idx == null)) { var v = Value(d.Refs[k], depth + 1, seen); if (v is not Undefined) o.Set(k, v); }
            seen.Remove(id);
            return o;
        }
        if (e.Type == EntryType.List)
        {
            if (depth > MaxDepth || seen.Contains(id)) return Undefined.Value;
            seen.Add(id);
            var a = new JArr { PipId = id };
            foreach (var r in (uint[])e.Value!) { var v = Value(r, depth + 1, seen); if (v is not Undefined) a.Add(v); }
            seen.Remove(id);
            return a;
        }
        return e.Value;
    }

    public object? Tree()
    {
        if (!treeMade) { treeRaw = Ready ? (Value(0) is var v && Js.Truthy(v) ? v : new JObj()) : null; treeMade = true; }
        return treeRaw;
    }
    object? treeRaw;
}

public sealed class PipClient
{
    public static readonly int[] Backoff = { 1000, 2000, 4000, 8000, 15000, 30000 };
    public const int ConnectTimeout = 5000, Silence = 15000, RefusedMax = 10000, BusyWait = 15000;

    public string Host = "localhost";
    public int Port = 27000;
    public readonly PipDatabase Db = new();
    public volatile string State = "off";
    public long Since;
    public string GameVersion = "";
    public Action<string, string>? OnState;
    public Action? OnUpdate;
    public Action<byte[]>? OnLocalMap;
    readonly object sendLock = new();
    volatile bool running;
    volatile Socket? sock;
    Thread? thread;
    int attempt, nextId = 1;
    bool refusedLogged;
    long lastSent;
    readonly AutoResetEvent wake = new(false);

    void Set(string state, string why = "")
    {
        if (state == State) return;
        var was = State;
        State = state;
        Since = Program.Now();
        bool retry = (was == "refused" && state == "connecting") || (state == "refused" && refusedLogged);
        if (state == "refused") refusedLogged = true; else if (state != "connecting") refusedLogged = false;
        if (!retry) Program.Log($"game: {was} -> {state}{(why != "" ? $" ({why})" : "")}");
        OnState?.Invoke(state, was);
    }

    public void Start()
    {
        if (running) return;
        running = true;
        attempt = 0;
        if (thread == null || !thread.IsAlive) { thread = new Thread(Loop) { IsBackground = true, Name = "game" }; thread.Start(); }
        else wake.Set();
    }
    public void Stop()
    {
        if (!running && State == "off") return;
        running = false;
        try { sock?.Close(); } catch (Exception) { }
        wake.Set();
        lock (Engine.Gate) Set("off");
    }

    void Loop()
    {
        while (true)
        {
            if (!running) { wake.WaitOne(500); if (Program.Quit) return; continue; }
            string? next = Session(out bool welcomed, out string why);
            if (!running) continue;
            int wait = Backoff[Math.Min(attempt, Backoff.Length - 1)];
            lock (Engine.Gate)
            {
                if (!running) continue;
                bool wasLive = State == "live" || State == "nodata";
                Set(next ?? (wasLive ? "connecting" : "refused"), why);
                if (wasLive) Db.Clear();
            }
            if (State == "refused") wait = Math.Min(wait, RefusedMax);
            if (State == "busy") wait = Math.Max(wait, BusyWait);
            attempt++;
            wake.WaitOne(wait);
            if (Program.Quit) return;
        }
    }

    string? Session(out bool welcomed, out string why)
    {
        welcomed = false; why = "";
        lock (Engine.Gate) { if (!running) return null; Set("connecting"); }
        Socket s;
        try
        {
            var addrs = Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
                : IPAddress.TryParse(Host, out var ip) ? new[] { ip } : Dns.GetHostAddresses(Host);
            s = null!;
            Exception? last = null;
            foreach (var a in addrs)
            {
                var t = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    var task = t.ConnectAsync(new IPEndPoint(a, Port));
                    if (!task.Wait(ConnectTimeout)) throw new TimeoutException("no answer");
                    s = t; break;
                }
                catch (Exception e) { last = e.InnerException ?? e; try { t.Close(); } catch (Exception) { } }
            }
            if (s == null) { why = last is SocketException se ? se.SocketErrorCode.ToString() : last?.Message ?? "no address"; return "refused"; }
        }
        catch (Exception e) { why = e.Message; return "refused"; }
        sock = s;
        if (!running) { try { s.Close(); } catch (Exception) { } return null; }
        var buf = new byte[65536];
        var pending = new MemoryStream();
        long start = Program.Now(), heard = start;
        s.ReceiveTimeout = 500;
        try
        {
            while (running)
            {
                int n;
                try { n = s.Receive(buf); }
                catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
                {
                    var now = Program.Now();
                    if (!welcomed && now - start > ConnectTimeout) { why = "no welcome"; return "refused"; }
                    if (now - heard > Silence) { why = "the game went quiet"; return null; }
                    continue;
                }
                if (n <= 0) { why = "closed"; return welcomed ? null : "refused"; }
                heard = Program.Now();
                pending.Write(buf, 0, n);
                var data = pending.GetBuffer();
                int len = (int)pending.Length, at = 0;
                while (len - at >= Codec.Header)
                {
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                    byte ch = data[at + 4];
                    if (size > Codec.MaxMessage) { why = $"a message of {size} bytes is too big"; return welcomed ? null : "refused"; }
                    if (len - at < Codec.Header + size) break;
                    var payload = data.AsSpan(at + Codec.Header, (int)size).ToArray();
                    at += Codec.Header + (int)size;
                    if (!welcomed)
                    {
                        if (ch == Channel.Accepted)
                        {
                            welcomed = true;
                            GameVersion = WelcomeVersion(payload);
                            attempt = 0;
                            lock (Engine.Gate) { if (!running) return null; Db.Clear(); Set("nodata", $"game {(GameVersion != "" ? GameVersion : "?")}"); }
                            continue;
                        }
                        if (ch == Channel.Busy) { why = "another app is connected"; return "busy"; }
                        if (ch == Channel.KeepAlive) { Write(Codec.KeepAlive()); continue; }
                        why = $"unexpected channel {ch} first";
                        return "refused";
                    }
                    Frame(ch, payload);
                }
                var rest = len - at;
                var keep = new MemoryStream();
                if (rest > 0) keep.Write(data, at, rest);
                pending = keep;
            }
            return null;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or IOException)
        {
            why = e is SocketException se ? se.SocketErrorCode.ToString() : "closed";
            return welcomed ? null : "refused";
        }
        finally
        {
            sock = null;
            try { s.Close(); } catch (Exception) { }
        }
    }

    static string WelcomeVersion(byte[] payload)
    {
        try
        {
            var text = Encoding.UTF8.GetString(payload).TrimEnd('\0');
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
        }
        catch (Exception) { return ""; }
    }

    void Frame(byte ch, byte[] payload)
    {
        switch (ch)
        {
            case Channel.KeepAlive: Write(Codec.KeepAlive()); return;
            case Channel.Data:
                {
                    List<Entry> entries;
                    try { entries = Codec.DecodeDataUpdate(payload); }
                    catch (FormatException e) { Program.Log($"game: a data update did not decode ({e.Message})"); return; }
                    lock (Engine.Gate)
                    {
                        Db.Apply(entries);
                        if (running && State != "live" && Db.Ready) Set("live", $"{Db.Size} entries");
                    }
                    if (Program.Now() - Interlocked.Read(ref lastSent) > 2000) Write(Codec.KeepAlive());
                    OnUpdate?.Invoke();
                    return;
                }
            case Channel.LocalMap: OnLocalMap?.Invoke(payload); return;
            default: return;
        }
    }

    bool Write(byte[] b)
    {
        var s = sock;
        if (s == null) return false;
        lock (sendLock)
        {
            try { s.Send(b); Interlocked.Exchange(ref lastSent, Program.Now()); return true; }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { return false; }
        }
    }

    public (bool Ok, string Msg) Send(int type, params object?[] args)
    {
        if (State != "live") return (false, State == "nodata" ? "open the Pip-Boy once" : "not connected");
        int id = Interlocked.Increment(ref nextId) - 1;
        if (type != Command.RequestLocalMapSnapshot) Program.Log($"game: command {type}");
        return Write(Codec.CommandFrame(type, args, id)) ? (true, "sent") : (false, "not connected");
    }
}
