using System.Diagnostics;
using System.Text.Json;

namespace CommonwealthLink;

public static class Engine
{
    public static readonly object Gate = new();
    public const int FeedFormat = 1;
    public static readonly Dictionary<string, string> FeedText = new()
    {
        ["ok"] = "in game",
        ["menu"] = "the game is at its main menu",
        ["loading"] = "a loading screen",
        ["dead"] = "you died",
        ["nodata"] = "connected to the game's Pip-Boy app server: open the Pip-Boy once in game to start the data",
        ["no-game"] = "Fallout 4 is not running",
        ["no-app"] = "the game is running but its Pip-Boy app server does not answer",
        ["busy"] = "another Pip-Boy app is connected to the game",
    };
    const int HeartbeatMs = 1000, MinGapMs = 250, RefreshMs = 250, ProcessCheckMs = 5000, PeerCheckMs = 1000, PeerFreshMs = 5000, RetryMs = 30000;

    public static PipClient Pip = new();
    public static PipState? Data;
    public static string Status = "no-game";
    public static string? Source;
    public static bool GameRunning;
    public static bool Standby;
    public static WorldPictures World = null!;
    public static LocalMaps Local = null!;
    public static WidgetServer? Server;
    public static string? ServerError;
    public static long Seq, LastWrite, LastBuild, LastProcessCheck = long.MinValue / 2, LastPeerCheck = long.MinValue / 2, LastServerTry = long.MinValue / 2;
    static volatile bool dirty, changed = true, stateChanged;
    static long lastLocalTick, peerAt = long.MinValue / 2;

    public static void Init()
    {
        World = new WorldPictures(Program.Dir) { GameDirSetting = () => Settings.GameDir, Changed = () => changed = true };
        World.SetEnabled(Settings.MapPicture);
        Pip.Port = Settings.PipboyPort;
        Pip.Host = Settings.PipboyHost == "" ? "localhost" : Settings.PipboyHost;
        Local = new LocalMaps(Pip) { Changed = () => changed = true };
        Local.SetEnabled(Settings.MapPicture);
        Local.CanAsk = () => Settings.MapPicture && Source == "pipboy" && Pip.State == "live" && Data?.Game == "ingame";
        Local.Player = () => Status == "ok" && Data?.Map is { PlayerX: double x, PlayerY: double y } ? (x, y) : null;
        Pip.OnState = (s, was) => { stateChanged = true; if (s != "live") Local.Forget(); };
        Pip.OnUpdate = () => dirty = true;
        Pip.OnLocalMap = payload => { lock (Gate) Local.Take(payload); };
    }

    static (string, string?) Resolve()
    {
        var game = Pip.State == "live" ? Data?.Game : null;
        if (Pip.State == "live" && game != null) return (game == "ingame" ? "ok" : game, "pipboy");
        if (Pip.State is "nodata" or "live") return ("nodata", "pipboy");
        if (Pip.State == "busy") return ("busy", null);
        return (GameRunning ? "no-app" : "no-game", null);
    }

    static void Rebuild()
    {
        Data = Pip.State == "live" ? Adapter.Build(Pip.Db.Tree(), Settings.ShowName) : null;
        LastBuild = Program.Now();
        Update();
    }
    static void Update()
    {
        var (st, src) = Resolve();
        Status = st; Source = src;
        if (Settings.MapPicture && Status == "ok" && Data?.Map is { } m) World.Want(m.World, m.Texture, m.Extents);
        changed = true;
    }

    static void LookForPeer()
    {
        try
        {
            var path = Path.Combine(Program.Dir, "feed.json");
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.String || app.GetString() != "Commonwealth Deck") return;
            if (r.TryGetProperty("at", out var at) && at.TryGetInt64(out var t) && t > peerAt) peerAt = t;
        }
        catch (Exception) { }
    }

    public static void Run()
    {
        while (!Program.Quit)
        {
            Thread.Sleep(50);
            try { Tick(); } catch (Exception e) { Program.Log($"tick: {e.GetType().Name}: {e.Message}"); }
        }
        lock (Gate) { Pip.Stop(); Server?.Stop(); }
    }

    static void Tick()
    {
        var now = Program.Now();
        if (now - LastProcessCheck >= ProcessCheckMs)
        {
            LastProcessCheck = now;
            bool running = Program.AssumeRunning ?? IsGameRunning();
            if (running != GameRunning) { lock (Gate) { GameRunning = running; Update(); } }
        }
        if (now - LastPeerCheck >= PeerCheckMs)
        {
            LastPeerCheck = now;
            LookForPeer();
            bool peer = now - peerAt < (!Standby && ServerError == "port in use" ? 30000 : PeerFreshMs);
            if (peer != Standby)
            {
                lock (Gate)
                {
                    Standby = peer;
                    Program.Log(peer ? "Commonwealth Deck is running: standing by" : "Commonwealth Deck stopped writing: taking over");
                    if (peer) { Pip.Stop(); Server?.Stop(); Server = null; ServerError = null; LastServerTry = long.MinValue / 2; }
                    changed = true;
                }
            }
        }
        lock (Gate)
        {
            bool typed = Settings.PipboyHost != "" && !Settings.PipboyHost.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            bool want = !Standby && (typed || GameRunning);
            if (want && Pip.State == "off") Pip.Start();
            if (!want && Pip.State != "off") Pip.Stop();
            if (stateChanged) { stateChanged = false; dirty = false; Rebuild(); }
            else if (dirty && now - LastBuild >= RefreshMs) { dirty = false; Rebuild(); }
            bool wantServer = !Standby && Settings.TouchControls;
            if (!wantServer && (Server != null || ServerError != null)) { Server?.Stop(); Server = null; ServerError = null; changed = true; LastServerTry = long.MinValue / 2; }
            if (wantServer && Server == null && now - LastServerTry >= RetryMs)
            {
                LastServerTry = now;
                var s = new WidgetServer(Program.WidgetPort) { Run = Runner.Run, Hello = Hello, Picture = Picture };
                var err = s.Start();
                if (err == null) { Server = s; if (ServerError != null) Program.Log("widget endpoint: listening again"); ServerError = null; Program.Log($"widget endpoint: listening on localhost:{Program.WidgetPort}"); }
                else { if (ServerError != err) Program.Log($"widget endpoint: cannot listen on localhost:{Program.WidgetPort} ({err}), trying again every 30 s"); ServerError = err; }
                changed = true;
            }
            if (now - lastLocalTick >= 500) { lastLocalTick = now; Local.Tick(); }
        }
        if (Standby) return;
        if (now - LastWrite >= HeartbeatMs || (changed && now - LastWrite >= MinGapMs))
        {
            changed = false;
            string text;
            lock (Gate) text = Json.Stringify(Build(now));
            Feed.WriteWhole(Path.Combine(Program.Dir, "feed.json"), Json.Utf8(text));
            LastWrite = now;
        }
    }

    static bool IsGameRunning()
    {
        try { var p = Process.GetProcessesByName("Fallout4"); bool any = p.Length > 0; foreach (var x in p) x.Dispose(); return any; } catch (Exception) { return false; }
    }

    static JObj Hello() { lock (Gate) return new JObj().Set("version", Program.Version).Set("status", Status).Set("statusText", FeedText.GetValueOrDefault(Status, Status)); }
    static byte[]? Picture(string which)
    {
        lock (Gate)
        {
            if (!Settings.MapPicture) return null;
            if (which == "world") return World.Png_();
            Local.TouchWidget();
            return Local.Png_();
        }
    }

    public static JObj Controls()
    {
        if (Standby || !Settings.TouchControls) return new JObj().Set("on", false);
        if (Server != null) return new JObj().Set("on", true).Set("port", Program.WidgetPort).Set("key", Server.Key).Set("actions", new JArr(WidgetServer.Actions)).Set("items", new JArr(WidgetServer.ItemNames));
        return new JObj().Set("on", true).Set("port", Program.WidgetPort).Set("error", ServerError ?? "starting");
    }

    public static JObj Build(long now)
    {
        var f = new JObj().Set("feed", FeedFormat).Set("app", Program.AppName).Set("version", Program.Version).Set("seq", (double)++Seq).Set("at", (double)now)
            .Set("status", Status).Set("statusText", FeedText.GetValueOrDefault(Status, Status)).Set("source", Source)
            .Set("sources", new JObj().Set("pipboy", new JObj().Set("state", Pip.State).Set("since", Pip.Since != 0 ? (double)Pip.Since : null)).Set("bridge", new JObj().Set("state", "off").Set("version", null)))
            .Set("controls", Controls())
            .Set("mapPicture", Settings.MapPicture ? new JObj().Set("on", true).Set("world", World.Info()).Set("local", Local.Info()) : new JObj().Set("on", false));
        if (Status == "ok" && Data != null) foreach (var kv in Data.Fields.Pairs) f.Set(kv.Key, kv.Value);
        f.Set("end", true);
        return f;
    }

    public static string StatusLine()
    {
        if (Standby) return "Commonwealth Deck is running; standing by";
        var s = Status switch
        {
            "ok" => "in game", "menu" => "the game is at its main menu", "loading" => "a loading screen", "dead" => "in game (you died)",
            "nodata" => "connected: open the Pip-Boy once in game", "no-game" => "waiting for Fallout 4",
            "no-app" => "the game's companion app server does not answer", "busy" => "another app has the game's one connection",
            _ => Status,
        };
        if (Pip.State == "connecting" && Status is "no-app" or "no-game") s = "connecting to the game";
        if (Settings.TouchControls && ServerError != null) s += $" (port {Program.WidgetPort} is in use)";
        return s;
    }

    public static void SettingsChanged()
    {
        lock (Gate)
        {
            World.SetEnabled(Settings.MapPicture);
            Local.SetEnabled(Settings.MapPicture);
            World.Retry();
            Rebuild();
            LastServerTry = long.MinValue / 2;
        }
    }
}

public static class Runner
{
    const int SeenMs = 1500;
    static string? lastStation;

    static JObj Result(bool ok, string text) { lock (Engine.Gate) return new JObj().Set("ok", ok).Set(ok ? "msg" : "error", text).Set("status", Engine.Status); }

    public static JObj Run(string name, JObj args)
    {
        string status; PipState? data;
        lock (Engine.Gate) { status = Engine.Status; data = Engine.Data; }
        if (status != "ok") return new JObj().Set("ok", false).Set("error", "not in game").Set("status", status);
        if (name == "radio")
        {
            var station = (string)args["station"]!;
            if (data == null || !data.Fields.Has("radio")) return Result(false, "no stations");
            var st = data.Radio.FirstOrDefault(x => x.Name.ToLowerInvariant() == station.ToLowerInvariant());
            if (st.Name == null) return Result(false, "not found");
            if (args["on"] is bool on && st.On == on) return Result(true, st.On ? "already on" : "already off");
            Program.Log("widget: radio");
            return Send("radio", st.Name);
        }
        Program.Log($"widget: {name}{(name == "use" ? $" {args["item"]}" : "")}");
        return Send(name, name == "use" ? (string)args["item"]! : "");
    }

    static JObj Send(string cmd, string arg)
    {
        object? tree; PipState? data; double version;
        lock (Engine.Gate)
        {
            bool live = Engine.Pip.State == "live" && Engine.Data?.Game == "ingame";
            if (!live) return Result(false, Engine.Pip.State == "nodata" ? "open the Pip-Boy once" : Engine.Data != null && Engine.Data.Game != "ingame" ? "not in game" : "Pip-Boy app not on");
            tree = Engine.Pip.Db.Tree(); data = Engine.Data; version = Adapter.InventoryVersion(tree);
        }
        if (cmd == "use")
        {
            JObj? item;
            lock (Engine.Gate) item = Adapter.UseTarget(Engine.Pip.Db, tree, arg);
            if (item == null || !(item["count"] is double c0 && c0 > 0)) return Result(false, "none left");
            if (item["HandleID"] is not double handle) return Result(false, "not found");
            double before = (double)item["count"]!;
            double stack = item["StackID"] is JArr sa && sa.Count > 0 && sa[0] is double s0 ? s0 : 0;
            var r = Engine.Pip.Send(Command.UseItem, handle, stack, version);
            if (!r.Ok) return Result(false, r.Msg);
            Program.Log($"game: used {arg}");
            var t0 = Program.Now();
            while (Program.Now() - t0 < SeenMs)
            {
                Thread.Sleep(100);
                lock (Engine.Gate)
                {
                    if (Engine.Pip.State != "live") break;
                    var t = Engine.Pip.Db.Tree();
                    if (t == null) break;
                    if (Adapter.InventoryVersion(t) != version) return ResultLocked(true, "used");
                    var again = Adapter.Items(t).FirstOrDefault(it => it["HandleID"] is double h && h == handle);
                    if (again == null || (again["count"] is double c && c < before)) return ResultLocked(true, "used");
                }
            }
            return Result(true, "sent");
        }
        if (cmd == "radio")
        {
            List<Adapter.Station> st;
            lock (Engine.Gate) st = Adapter.Stations(tree);
            if (st.Count == 0) return Result(false, "no stations");
            var pick = st.FirstOrDefault(r => r.Name.ToLowerInvariant() == arg.ToLowerInvariant());
            if (pick == null) return Result(false, "not found");
            if (!pick.On && !pick.InRange) return Result(false, "out of range");
            lastStation = pick.Name;
            var r = Engine.Pip.Send(Command.ToggleRadioStation, pick.Id.HasValue ? (double)pick.Id.Value : null);
            return r.Ok ? Result(true, pick.On ? "radio off" : "tuned in") : Result(false, r.Msg);
        }
        if (cmd == "markHere")
        {
            if (data?.Map is not { PlayerX: double x, PlayerY: double y }) return Result(false, "no map yet");
            var r = Engine.Pip.Send(Command.SetCustomMapMarker, x, y, false);
            return r.Ok ? Result(true, "marked") : Result(false, r.Msg);
        }
        if (cmd == "clearMarker")
        {
            if (data?.Map is not { HasCustom: true }) return Result(false, "no marker set");
            var r = Engine.Pip.Send(Command.RemoveCustomMapMarker);
            return r.Ok ? Result(true, "cleared") : Result(false, r.Msg);
        }
        return Result(false, "use the key");
    }
    static JObj ResultLocked(bool ok, string text) => new JObj().Set("ok", ok).Set(ok ? "msg" : "error", text).Set("status", Engine.Status);
}

public static class Feed
{
    public static string WriteWhole(string file, byte[] bytes)
    {
        var tmp = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(tmp, bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "failed"; }
        for (int i = 0; i < 3; i++)
        {
            try { File.Move(tmp, file, overwrite: true); return "renamed"; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { if (i < 2) Thread.Sleep(5); }
        }
        try
        {
            using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) fs.Write(bytes);
            return "rewritten";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "held"; }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } }
    }
}
