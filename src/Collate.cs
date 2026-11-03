namespace CommonwealthLink;

public static class Collate
{
    static readonly Dictionary<char, string> Expand = Tables.Expansions.ToDictionary(e => e.From, e => e.To);

    readonly struct El
    {
        public readonly int P, S, T; public readonly string? Num;
        public El(int p, int s, int t, string? num = null) { P = p; S = s; T = t; Num = num; }
    }

    static (int, int, int) Rank(char c)
    {
        foreach (var r in Tables.Ranks)
            if (c >= r.From && c <= r.To) { int i = c - r.From; return (r.Base[i], r.Accent[i], r.Case[i]); }
        return (100000 + c, 1, 1);
    }

    static List<El> Elements(string s, bool numeric)
    {
        var chars = new System.Text.StringBuilder(s.Length);
        foreach (var c in s) { if (Expand.TryGetValue(c, out var x)) chars.Append(x); else chars.Append(c); }
        var t = chars.ToString();
        var out_ = new List<El>(t.Length);
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (numeric && c >= '0' && c <= '9')
            {
                int j = i;
                while (j < t.Length && t[j] >= '0' && t[j] <= '9') j++;
                var digits = t[i..j].TrimStart('0');
                out_.Add(new El(Tables.DigitRank, 1, 1, digits));
                i = j - 1;
                continue;
            }
            var (p, sec, ter) = Rank(c);
            if (p == 0) continue;
            out_.Add(new El(p, sec, ter));
        }
        return out_;
    }

    public static int Compare(string a, string b, bool baseOnly = true, bool numeric = false)
    {
        var x = Elements(a, numeric); var y = Elements(b, numeric);
        int n = Math.Min(x.Count, y.Count);
        for (int i = 0; i < n; i++)
        {
            var p = x[i]; var q = y[i];
            if (p.Num != null && q.Num != null)
            {
                if (p.Num.Length != q.Num.Length) return p.Num.Length < q.Num.Length ? -1 : 1;
                int c = string.CompareOrdinal(p.Num, q.Num);
                if (c != 0) return c < 0 ? -1 : 1;
                continue;
            }
            if (p.P != q.P) return p.P < q.P ? -1 : 1;
        }
        if (x.Count != y.Count) return x.Count < y.Count ? -1 : 1;
        if (baseOnly) return 0;
        for (int i = 0; i < n; i++) if (x[i].S != y[i].S) return x[i].S < y[i].S ? -1 : 1;
        for (int i = 0; i < n; i++) if (x[i].T != y[i].T) return x[i].T < y[i].T ? -1 : 1;
        return 0;
    }

    public static List<T> StableSort<T>(IEnumerable<T> items, Comparison<T> cmp)
    {
        var list = items.Select((v, i) => (v, i)).ToList();
        list.Sort((a, b) => { int c = cmp(a.v, b.v); return c != 0 ? c : a.i.CompareTo(b.i); });
        return list.Select(p => p.v).ToList();
    }
}
