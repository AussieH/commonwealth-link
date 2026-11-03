using System.Text.RegularExpressions;

namespace CommonwealthLink;

public sealed class PipState
{
    public string Game = "loading";
    public JObj Fields = new();
    public MapInfo? Map;
    public List<(string Name, bool On)> Radio = new();
}
public sealed class MapInfo
{
    public string? World, Texture;
    public double[]? Extents;
    public double? PlayerX, PlayerY;
    public bool HasCustom;
}

public static class Adapter
{
    public const int Favorite = 1, Weapon = 2, Apparel = 4, Aid = 8, Ammo = 4096;
    public const double EquipThrown = 3;
    static readonly string[] SpecialOrder = { "Strength", "Perception", "Endurance", "Charisma", "Intelligence", "Agility", "Luck" };
    public static readonly string[] ArmorSlots = { "helmet", "torso", "leftArm", "rightArm", "leftLeg", "rightLeg" };
    static readonly Dictionary<int, string> Paperdoll = new() { [1] = "leftLeg", [2] = "rightLeg", [3] = "leftArm", [4] = "rightArm", [5] = "torso", [6] = "helmet", [7] = "helmet", [8] = "helmet" };
    const RegexOptions JsI = RegexOptions.ECMAScript | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const RegexOptions JsR = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
    static readonly Regex DigitsKey = new(@"^\d+$", JsR), RadX = new(@"^rad-?x$", JsI), FusionCore = new(@"^fusion core$", JsI), Addict = new("addict", JsI);
    static readonly Regex Health = new(@"^\s*(-?[\d.]+)\s*\/\s*([\d.]+)\s*$", JsR);
    static readonly Regex HelmetWords = new(@"helmet|helm\b|head", JsR), TorsoWords = new(@"torso|chest|body", JsR), Left = new(@"\bleft\b", JsR), Right = new(@"\bright\b", JsR), Arm = new(@"\barm\b", JsR), Leg = new(@"\bleg\b", JsR);

    static object? G(object? o, string k) => o is JObj j ? j[k] : null;
    static double D(object? v) => (double)v!;
    static int ByName(JObj a, JObj b) => Collate.Compare((string)a["name"]!, (string)b["name"]!);

    public static List<JObj> Items(object? tree)
    {
        var out_ = new List<JObj>();
        if (G(tree, "Inventory") is not JObj inv) return out_;
        foreach (var kv in inv.Pairs)
            if (DigitsKey.IsMatch(kv.Key) && kv.Value is JArr list)
                foreach (var it in list) if (it is JObj o && o["text"] is string) out_.Add(o);
        return out_;
    }
    static bool Has(JObj it, int bit) => Js.Finite(it["filterFlag"]) && (Js.ToInt32(D(it["filterFlag"])) & bit) != 0;
    static string Text(JObj it) => (string)it["text"]!;
    static JObj? AmmoCard(JObj it) => it["itemCardInfoList"] is JArr l ? l.OfType<JObj>().FirstOrDefault(c => c["damageType"] is double d && d == 10) : null;

    static string AmmoName(object? shortName, List<JObj> all)
    {
        var s = Js.Str(shortName);
        if (s == "") return "";
        var ammo = all.Where(it => Has(it, Ammo)).ToList();
        var hit = ammo.FirstOrDefault(it => Text(it) == s) ?? ammo.FirstOrDefault(it => Text(it).StartsWith(s + " ", StringComparison.Ordinal)) ?? ammo.FirstOrDefault(it => Text(it).EndsWith(" " + s, StringComparison.Ordinal));
        return hit != null ? Text(hit) : s;
    }

    static (double? U, double? V)? MapUV(double[]? ext, object? xv, object? yv)
    {
        if (ext == null || !Js.Finite(xv) || !Js.Finite(yv)) return null;
        double x = D(xv), y = D(yv);
        double exx = ext[2] - ext[0], exy = ext[3] - ext[1], eyx = ext[4] - ext[0], eyy = ext[5] - ext[1];
        double px = x - ext[0], py = y - ext[1];
        double lx = exx * exx + exy * exy, ly = eyx * eyx + eyy * eyy;
        if (lx == 0 || double.IsNaN(lx) || ly == 0 || double.IsNaN(ly)) return null;
        return (Js.RoundTo((px * exx + py * exy) / lx, 4), Js.RoundTo((px * eyx + py * eyy) / ly, 4));
    }
    static double[]? Extents(object? e)
    {
        if (e is not JObj o) return null;
        var v = new[] { o["NWX"], o["NWY"], o["NEX"], o["NEY"], o["SWX"], o["SWY"] };
        return v.All(Js.Finite) ? v.Select(D).ToArray() : null;
    }
    static JObj? Point(double[]? ext, object? o, Action<JObj>? extra = null)
    {
        if (o is not JObj p || !Js.Finite(p["X"]) || !Js.Finite(p["Y"])) return null;
        var r = new JObj().Set("x", Js.RoundTo(p["X"], 1)).Set("y", Js.RoundTo(p["Y"], 1));
        extra?.Invoke(r);
        if (MapUV(ext, p["X"], p["Y"]) is { } uv) r.Set("u", uv.U).Set("v", uv.V);
        return r;
    }

    static JObj? WorldMap(object? m, out MapInfo? info)
    {
        info = null;
        if (m is not JObj map || map["World"] is not JObj w) return null;
        var ext = Extents(w["Extents"]);
        var world = Js.Str(map["CurrWorldspace"]); var texture = Js.Str(map["WorldMapTexture"]);
        var out_ = new JObj().Set("world", world != "" ? world : null)
            .Set("extents", ext == null ? null : new JObj().Set("nw", XY(ext[0], ext[1])).Set("ne", XY(ext[2], ext[3])).Set("sw", XY(ext[4], ext[5])))
            .Set("texture", texture != "" ? texture : null);
        var player = w["Player"] is JObj pl ? Point(ext, pl, r => r.Set("rot", Js.RoundTo(pl["Rotation"], 1))) : null;
        out_.Set("player", player);
        var custom = w["Custom"] is JObj cu && Js.Truthy(cu["Visible"]) ? Point(ext, cu) : null;
        out_.Set("custom", custom);
        out_.Set("powerArmor", w["PowerArmor"] is JObj pa && Js.Truthy(pa["Visible"]) ? Point(ext, pa) : null);
        var qm = new JArr();
        foreach (var q in w["Quests"] as JArr ?? new JArr())
        {
            var name = Js.Str(G(q, "Name"));
            var pt = Point(ext, q, r => r.Set("name", name));
            if (pt != null && name != "") qm.Add(pt);
            if (qm.Count == 40) break;
        }
        out_.Set("questMarkers", qm);
        var marks = new List<JObj>();
        foreach (var l in w["Locations"] as JArr ?? new JArr())
        {
            if (l is not JObj lo || !Js.Truthy(lo["Discovered"]) || Js.Str(lo["Name"]) == "") continue;
            var pt = Point(ext, lo, r => r.Set("name", Js.Str(lo["Name"])).Set("type", Js.Finite(lo["type"]) ? lo["type"] : null).Set("cleared", lo["ClearedStatus"] is true));
            if (pt != null) marks.Add(pt);
        }
        out_.Set("markers", new JArr(Collate.StableSort(marks, ByName).Take(500)));
        info = new MapInfo { World = world != "" ? world : null, Texture = texture != "" ? texture : null, Extents = ext, PlayerX = player?["x"] as double?, PlayerY = player?["y"] as double?, HasCustom = custom != null };
        return out_;
    }
    static JObj XY(double x, double y) => new JObj().Set("x", x).Set("y", y);

    static double? Weekday(double y, double m, double d)
    {
        y = Math.Truncate(y); m = Math.Truncate(m); d = Math.Truncate(d);
        if (y >= 0 && y <= 99) y += 1900;
        double ym = y + Math.Floor(m / 12), mn = ((m % 12) + 12) % 12;
        if (Math.Abs(ym) > 400000) return null;
        long Y = (long)ym, M = (long)mn + 1;
        Y -= M <= 2 ? 1 : 0;
        long era = (Y >= 0 ? Y : Y - 399) / 400, yoe = Y - era * 400, doy = (153 * (M + (M > 2 ? -3 : 9)) + 2) / 5, doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        double days = era * 146097 + doe - 719468 + (d - 1);
        if (Math.Abs(days) > 1e8) return null;
        return ((days % 7) + 7 + 4) % 7;
    }

    static string? ArmorSlot(JObj it)
    {
        if (it["PaperdollSection"] is JArr pd)
            for (int i = 0; i < pd.Count; i++) if (pd[i] is true && Paperdoll.TryGetValue(i, out var slot)) return slot;
        var t = Js.Str(it["text"]).ToLowerInvariant();
        if (HelmetWords.IsMatch(t)) return "helmet";
        if (TorsoWords.IsMatch(t)) return "torso";
        var side = Left.IsMatch(t) ? "left" : Right.IsMatch(t) ? "right" : null;
        if (side != null && Arm.IsMatch(t)) return side + "Arm";
        if (side != null && Leg.IsMatch(t)) return side + "Leg";
        return null;
    }
    static (double Cur, double? Max)? ArmorHealth(JObj it)
    {
        var c = it["itemCardInfoList"] is JArr l ? l.OfType<JObj>().FirstOrDefault(x => x["text"] is "$health") : null;
        if (c == null) return null;
        if (c["Value"] is string s)
        {
            var m = Health.Match(s);
            if (m.Success && Js.Number(m.Groups[2].Value) > 0) return (Math.Max(0, Js.Number(m.Groups[1].Value)), Js.Number(m.Groups[2].Value));
            return null;
        }
        return Js.Finite(c["Value"]) ? (Math.Max(0, D(c["Value"])), null) : null;
    }
    static bool Equipped(JObj it) => Js.Finite(it["equipState"]) && D(it["equipState"]) > 0;

    static (bool On, double Cores, Dictionary<string, JObj?> Pieces) PowerArmor(List<JObj> all)
    {
        var pa = all.Where(it => it["isPowerArmorItem"] is true).ToList();
        bool on = pa.Any(Equipped);
        var core = all.FirstOrDefault(it => Has(it, Ammo) && FusionCore.IsMatch(Js.Str(it["text"])));
        double cores = core != null && Js.Finite(core["count"]) ? D(core["count"]) : 0;
        var pieces = ArmorSlots.ToDictionary(k => k, k => (JObj?)null);
        if (on)
            foreach (var slot in ArmorSlots)
            {
                var mine = pa.Where(it => ArmorSlot(it) == slot).ToList();
                var worn = mine.FirstOrDefault(Equipped);
                var broken = worn != null ? null : mine.FirstOrDefault(it => ArmorHealth(it) is { } h && h.Cur <= 0);
                var it = worn ?? broken;
                if (it == null) continue;
                var hh = ArmorHealth(it);
                bool hasMax = hh is { } h1 && h1.Max is double mx && mx != 0 && !double.IsNaN(mx);
                object? pct = hasMax ? Js.Round(Math.Max(0, Math.Min(1, hh!.Value.Cur / hh.Value.Max!.Value)) * 1000) / 1000 : worn != null ? null : 0.0;
                pieces[slot] = new JObj()
                    .Set("pct", pct is double pd && double.IsFinite(pd) ? pd : null)
                    .Set("cur", hh != null ? Js.RoundTo(hh.Value.Cur, 1) : null)
                    .Set("max", hasMax ? Js.RoundTo(hh!.Value.Max, 1) : null)
                    .Set("name", Js.Str(it["text"]) is var nm && nm != "" ? nm : null);
            }
        return (on, cores, pieces);
    }

    public static string GameState(object? st)
    {
        if (st is not JObj s) return "loading";
        if (Js.Truthy(s["IsPlayerDead"])) return "dead";
        if (Js.Truthy(s["IsLoading"])) return "loading";
        if (Js.Truthy(s["IsDataUnavailable"])) return "menu";
        return "ingame";
    }

    public static PipState? Build(object? treeValue, bool showName)
    {
        if (treeValue is not JObj tree) return null;
        var p = tree["PlayerInfo"] as JObj; var st = tree["Stats"] as JObj; var status = tree["Status"] as JObj;
        var all = Items(tree);
        var r = new PipState { Game = GameState(status) };
        var f = r.Fields;

        JObj? Bar(object? cur, object? max) => Js.Finite(cur) && Js.Finite(max) ? new JObj().Set("cur", Js.RoundTo(cur, 1)).Set("max", Js.RoundTo(max, 1)) : null;
        JObj? carry = null;
        if (p != null)
        {
            if (showName && Js.Str(p["PlayerName"]) != "") f.Set("player", new JObj().Set("name", Js.Str(p["PlayerName"])));
            var vitals = new JObj();
            if (Bar(p["CurrHP"], p["MaxHP"]) is { } hp) vitals.Set("health", hp);
            if (Bar(p["CurrAP"], p["MaxAP"]) is { } ap) vitals.Set("ap", ap);
            if (vitals.Count > 0) f.Set("vitals", vitals);
            if (Js.Finite(p["XPLevel"]))
            {
                double level = D(p["XPLevel"]);
                f.Set("level", level);
                double need = 200 + 75 * Math.Max(0, level - 1);
                if (Js.Finite(p["XPProgressPct"]))
                {
                    double cur = Js.Round(Math.Max(0, Math.Min(1, D(p["XPProgressPct"]))) * need);
                    if (double.IsFinite(cur) && double.IsFinite(need) && need > 0)
                        f.Set("xp", new JObj().Set("pct", Js.Round(((cur - 0) / (need - 0)) * 1000) / 1000).Set("toNext", Math.Max(0, Js.Round(need - cur))));
                }
            }
            if (Js.Finite(p["Caps"])) f.Set("caps", p["Caps"]);
            carry = Bar(p["CurrWeight"], p["MaxWeight"]);
            if (carry != null) f.Set("carry", carry);
        }
        if (st != null)
        {
            var limbs = new JObj();
            void Limb(string k, object? v) { if (Js.Finite(v)) limbs.Set(k, Math.Max(0, Math.Min(1, D(v) / 100))); }
            Limb("head", st["HeadCondition"]); Limb("torso", st["TorsoCondition"]); Limb("leftArm", st["LArmCondition"]);
            Limb("rightArm", st["RArmCondition"]); Limb("leftLeg", st["LLegCondition"]); Limb("rightLeg", st["RLegCondition"]);
            if (limbs.Count > 0) f.Set("limbs", limbs);
        }
        (bool On, double Cores, Dictionary<string, JObj?> Pieces) suit = default;
        if (all.Count > 0)
        {
            var weapons = all.Where(it => Has(it, Weapon)).ToList();
            var inHand = weapons.FirstOrDefault(it => Equipped(it) && D(it["equipState"]) != EquipThrown);
            var thrown = weapons.FirstOrDefault(it => it["equipState"] is double e && e == EquipThrown);
            if (inHand == null) f.Set("weapon", new JObj().Set("name", "Unarmed").Set("kind", "unarmed").Set("ammo", null));
            else
            {
                var card = AmmoCard(inHand);
                f.Set("weapon", new JObj().Set("name", Text(inHand)).Set("kind", card != null ? "gun" : "melee")
                    .Set("ammo", card != null ? new JObj().Set("name", AmmoName(card["text"], all)).Set("mag", null).Set("total", Js.Finite(card["Value"]) ? card["Value"] : 0.0) : null));
            }
            f.Set("grenade", thrown != null ? new JObj().Set("name", Text(thrown)).Set("count", Js.Finite(thrown["count"]) ? thrown["count"] : 0.0) : null);
            var radx = all.FirstOrDefault(it => Has(it, Aid) && RadX.IsMatch(Text(it)));
            f.Set("chems", new JObj().Set("stimpak", Js.Finite(st?["StimpakCount"]) ? st!["StimpakCount"] : 0.0)
                .Set("radaway", Js.Finite(st?["RadawayCount"]) ? st!["RadawayCount"] : 0.0)
                .Set("radx", radx != null && Js.Finite(radx["count"]) ? radx["count"] : 0.0));
        }
        else if (st != null)
            f.Set("chems", new JObj().Set("stimpak", Js.Finite(st["StimpakCount"]) ? st["StimpakCount"] : 0.0).Set("radaway", Js.Finite(st["RadawayCount"]) ? st["RadawayCount"] : 0.0));

        if (tree["Special"] is JArr sp)
        {
            int Key(string n) { int i = Array.IndexOf(SpecialOrder, n); return i + 99 * (i < 0 ? 1 : 0); }
            var list = sp.OfType<JObj>().Where(x => Js.Str(x["Name"]) != "")
                .Select(x => new JObj().Set("name", Js.Str(x["Name"])).Set("value", Js.Finite(x["Value"]) ? x["Value"] : 0.0).Set("mod", Js.Finite(x["Modifier"]) ? x["Modifier"] : 0.0));
            f.Set("special", new JArr(Collate.StableSort(list, (a, b) => Key((string)a["name"]!) - Key((string)b["name"]!))));
        }
        if (tree["Perks"] is JArr pk)
        {
            var list = pk.OfType<JObj>().Where(x => Js.Str(x["Name"]) != "" && Js.Finite(x["Rank"]) && D(x["Rank"]) > 0)
                .Select(x => new JObj().Set("name", Js.Str(x["Name"])).Set("rank", x["Rank"]).Set("max", Js.Finite(x["MaxRank"]) ? x["MaxRank"] : x["Rank"]));
            f.Set("perks", new JArr(Collate.StableSort(list, ByName).Take(80)));
        }
        if (all.Count > 0)
        {
            JArr List(int bit) => new(Collate.StableSort(all.Where(it => Has(it, bit) && Js.Finite(it["count"]) && D(it["count"]) > 0)
                .Select(it => new JObj().Set("name", Text(it)).Set("count", it["count"])), ByName).Take(200));
            f.Set("inventory", new JObj().Set("aid", List(Aid)).Set("ammo", List(Ammo)));
        }
        if (tree["Quests"] is JArr qs)
        {
            var quests = new List<(JObj Q, bool Tracked, string Name)>();
            foreach (var q in qs.OfType<JObj>().Where(q => q["enabled"] is true && Js.Str(q["text"]) != ""))
            {
                var objs = (q["objectives"] as JArr ?? new JArr()).OfType<JObj>().Where(o => Js.Str(o["text"]) != "" && o["enabled"] is true && o["failed"] is not true)
                    .Select(o => (object?)new JObj().Set("text", Js.Str(o["text"])).Set("done", o["completed"] is true));
                bool tracked = q["active"] is true;
                quests.Add((new JObj().Set("name", Js.Str(q["text"])).Set("tracked", tracked).Set("objectives", new JArr(objs)), tracked, Js.Str(q["text"])));
            }
            var sorted = Collate.StableSort(quests, (a, b) => ((b.Tracked ? 1 : 0) - (a.Tracked ? 1 : 0)) is var t && t != 0 ? t : Collate.Compare(a.Name, b.Name));
            var tq = sorted.FirstOrDefault(x => x.Tracked);
            if (tq.Q != null)
            {
                var objs = (JArr)tq.Q["objectives"]!;
                f.Set("quest", new JObj().Set("name", tq.Name).Set("objective", objs.Count > 0 ? (string)((JObj)objs[0]!)["text"]! : ""));
            }
            else f.Set("quest", null);
            f.Set("quests", new JArr(sorted.Take(40).Select(x => (object?)x.Q)));
        }
        if (p != null && Js.Finite(p["TimeHour"]))
        {
            var t = new JObj().Set("hour", Js.RoundTo(p["TimeHour"], 4));
            if (Js.Finite(p["DateDay"]) && Js.Finite(p["DateMonth"]) && Js.Finite(p["DateYear"]))
            {
                double y = D(p["DateYear"]), year = y < 1000 ? 2000 + y : y, month = D(p["DateMonth"]) - 1;
                t.Set("day", p["DateDay"]).Set("month", month).Set("year", year).Set("weekday", Weekday(year, month, D(p["DateDay"])));
            }
            f.Set("time", t);
        }
        if (tree["Map"] is JObj mp)
        {
            var loc = Js.Str(mp["CurrWorldspace"]); var cell = Js.Str(mp["CurrCell"]);
            f.Set("place", new JObj().Set("location", loc != "" ? loc : null).Set("cell", cell != "" ? cell : null));
        }
        if (st != null && st["ActiveEffects"] is JArr fxList)
        {
            var effects = new List<(string Name, double? Left, bool Bad)>(); var addictions = new JArr();
            foreach (var src in fxList.OfType<JObj>())
            {
                var name = Js.Str(src["Source"]);
                var fx = (src["Effects"] as JArr ?? new JArr()).OfType<JObj>().Where(e => e["IsActive"] is not false).ToList();
                if (name == "" || fx.Count == 0) continue;
                if (Addict.IsMatch(name)) { addictions.Add(name); continue; }
                double left = Math.Max(0, fx.Max(e => Js.Finite(e["duration"]) ? D(e["duration"]) : 0));
                effects.Add((name, left > 0 ? Js.Round(left) : null, fx.Any(e => Js.Finite(e["Value"]) && D(e["Value"]) < 0)));
            }
            var sorted = Collate.StableSort(effects, (a, b) => { double x = a.Left ?? double.PositiveInfinity, y = b.Left ?? double.PositiveInfinity; return x < y ? -1 : x > y ? 1 : 0; });
            f.Set("effects", new JArr(sorted.Take(12).Select(e => (object?)new JObj().Set("name", e.Name).Set("left", e.Left).Set("bad", e.Bad))));
            f.Set("addictions", addictions);
        }
        var flags = new JObj();
        if (status != null) flags.Set("vats", Js.Truthy(status["IsInVats"]) || Js.Truthy(status["IsInVatsPlayback"]));
        if (carry != null) flags.Set("overencumbered", D(carry["cur"]) > D(carry["max"]));
        if (all.Count > 0)
        {
            suit = PowerArmor(all);
            flags.Set("powerArmor", suit.On);
        }
        f.Set("flags", flags);
        if (all.Count > 0)
        {
            if (!suit.On) f.Set("armor", null);
            else
            {
                var parts = new JObj();
                foreach (var k in ArmorSlots) parts.Set(k, suit.Pieces![k]);
                f.Set("armor", new JObj().Set("core", null).Set("cores", double.IsFinite(suit.Cores) ? suit.Cores : null).Set("parts", parts));
            }
        }
        if (WorldMap(tree["Map"], out var info) is { } wm) { f.Set("map", wm); r.Map = info; }
        if (tree["Radio"] is JArr radio)
        {
            var list = new JArr();
            foreach (var x in radio.OfType<JObj>().Where(x => Js.Str(x["text"]) != ""))
            {
                bool on = x["active"] is true;
                list.Add(new JObj().Set("name", Js.Str(x["text"])).Set("freq", Js.RoundTo(x["frequency"], 1)).Set("on", on).Set("inRange", x["inRange"] is not false));
                r.Radio.Add((Js.Str(x["text"]), on));
            }
            f.Set("radio", list);
        }
        return r;
    }

    public sealed record Station(string Name, uint? Id, bool On, bool InRange);
    public static List<Station> Stations(object? tree) =>
        (G(tree, "Radio") as JArr ?? new JArr()).OfType<JObj>().Where(r => Js.Str(r["text"]) != "")
            .Select(r => new Station(Js.Str(r["text"]), r.PipId, r["active"] is true, r["inRange"] is not false)).ToList();
    public static double InventoryVersion(object? tree) => G(tree, "Inventory") is JObj inv && Js.Finite(inv["Version"]) ? D(inv["Version"]) : 0;
    public static JObj? UseTarget(PipDatabase db, object? tree, string item)
    {
        var inv = G(tree, "Inventory") as JObj;
        if (item == "radx") return Items(tree).FirstOrDefault(it => Has(it, Aid) && RadX.IsMatch(Text(it)));
        var (valid, id) = item == "stimpak" ? ("stimpakObjectIDIsValid", "stimpakObjectID") : ("radawayObjectIDIsValid", "radawayObjectID");
        if (inv == null || !Js.Truthy(inv[valid]) || inv[id] is not double d || d < 0 || d > uint.MaxValue || d != Math.Floor(d)) return null;
        return db.Value((uint)d) is JObj o && o["text"] is string ? o : null;
    }
}
