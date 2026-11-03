using System.Text;
using System.Text.Json;

namespace CommonwealthLink;

public static class Settings
{
    public static bool ShowName, TouchControls = true, MapPicture = true;
    public static string GameDir = "", PipboyHost = "";
    public static int PipboyPort = 27000;
    static string FilePath => Path.Combine(Program.Dir, "link-settings.json");

    public static void Load()
    {
        if (!File.Exists(FilePath)) Save();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            var r = doc.RootElement;
            bool B(string k, bool d) => r.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && d) : d;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
            ShowName = B("showName", false); TouchControls = B("touchControls", true); MapPicture = B("mapPicture", true);
            GameDir = S("gameDir"); PipboyHost = S("pipboyHost");
            if (r.TryGetProperty("pipboyPort", out var p) && p.TryGetInt32(out var pv) && pv > 0 && pv < 65536) PipboyPort = pv;
        }
        catch (Exception) { }
    }
    public static void Save()
    {
        var o = new JObj().Set("showName", ShowName).Set("touchControls", TouchControls).Set("mapPicture", MapPicture).Set("gameDir", GameDir).Set("pipboyHost", PipboyHost).Set("pipboyPort", PipboyPort);
        var text = "{\n" + string.Join(",\n", o.Pairs.Select(kv => $"  {Json.Stringify(kv.Key)}: {Json.Stringify(kv.Value)}")) + "\n}\n";
        try { Directory.CreateDirectory(Program.Dir); File.WriteAllText(FilePath, text); } catch (Exception) { }
    }
}

public static class Program
{
    public const string AppName = "Commonwealth Link";
    public static readonly string Version = typeof(Program).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
    public static string Dir = "";
    public static int WidgetPort = WidgetServer.DefaultPort;
    public static bool? AssumeRunning;
    public static volatile bool Quit;
    static bool console;
    static readonly object logLock = new();

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static void Log(string text)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}";
        lock (logLock)
        {
            if (console) { try { Console.Out.WriteLine(line); Console.Out.Flush(); } catch (Exception) { } }
            try
            {
                var path = Path.Combine(Dir, "link.log");
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024) File.Move(path, path + ".old", true);
                File.AppendAllText(path, line + "\r\n");
            }
            catch (Exception) { }
        }
    }

    static string? Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version")) { Console.Out.WriteLine($"{AppName} {Version}"); return 0; }
        if (Arg(args, "--sort-names") is { } sn) { SortFile(sn, false); return 0; }
        if (Arg(args, "--sort-archives") is { } sa) { SortFile(sa, true); return 0; }
        bool tray = !args.Contains("--no-tray");
        console = !tray;
        Dir = Arg(args, "--dir") ?? Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CommonwealthDeck");
        Dir = Path.GetFullPath(Dir);
        try { Directory.CreateDirectory(Dir); } catch (Exception) { }
        Mutex? single = null;
        if (tray) { single = new Mutex(true, @"Local\CommonwealthLink", out bool first); if (!first) return 1; }
        Settings.Load();
        if (Arg(args, "--pipboy-host") is { } h) Settings.PipboyHost = h.Trim();
        if (int.TryParse(Arg(args, "--pipboy-port"), out var pp) && pp > 0 && pp < 65536) Settings.PipboyPort = pp;
        if (int.TryParse(Arg(args, "--widget-port"), out var wp) && wp > 1023 && wp < 65536) WidgetPort = wp;
        if (Arg(args, "--game-dir") is { } gd) Settings.GameDir = gd;
        if (args.Contains("--show-name")) Settings.ShowName = true;
        if (args.Contains("--no-controls")) Settings.TouchControls = false;
        if (args.Contains("--no-map-picture")) Settings.MapPicture = false;
        if (Arg(args, "--assume-running") is { } ar) AssumeRunning = ar == "1";
        Log($"{AppName} {Version} started");
        Engine.Init();
        var engine = new Thread(Engine.Run) { IsBackground = true, Name = "engine" };
        engine.Start();
        if (tray) Tray.Run();
        else
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; Quit = true; };
            while (!Quit) Thread.Sleep(100);
        }
        Quit = true;
        engine.Join(2000);
        Log($"{AppName} stopped");
        GC.KeepAlive(single);
        return 0;
    }

    static void SortFile(string path, bool archives)
    {
        var names = File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Length > 0);
        var sorted = archives ? WorldPictures.ArchiveOrder(names) : Collate.StableSort(names, (a, b) => Collate.Compare(a, b));
        var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        foreach (var n in sorted) o.WriteLine(n);
        o.Flush();
    }
}
