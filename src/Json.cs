using System.Globalization;
using System.Text;

namespace CommonwealthLink;

public sealed class JObj
{
    readonly List<string> keys = new();
    readonly Dictionary<string, object?> values = new(StringComparer.Ordinal);
    public uint? PipId;
    public int Count => keys.Count;
    public IEnumerable<string> Keys => keys;
    public IEnumerable<KeyValuePair<string, object?>> Pairs { get { foreach (var k in keys) yield return new(k, values[k]); } }
    public JObj Set(string key, object? value) { if (!values.ContainsKey(key)) keys.Add(key); values[key] = value; return this; }
    public bool Has(string key) => values.ContainsKey(key);
    public object? this[string key] => values.TryGetValue(key, out var v) ? v : null;
    public bool TryGet(string key, out object? value) => values.TryGetValue(key, out value);
    public void Remove(string key) { if (values.Remove(key)) keys.Remove(key); }
}

public sealed class JArr : List<object?>
{
    public uint? PipId;
    public JArr() { }
    public JArr(IEnumerable<object?> items) : base(items) { }
}

public static class Json
{
    public static string Stringify(object? v) { var sb = new StringBuilder(4096); Write(sb, v); return sb.ToString(); }

    public static void Write(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case string s: Str(sb, s); break;
            case double d: sb.Append(Num(d)); break;
            case float f: sb.Append(Num(f)); break;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case long l: sb.Append(Num(l)); break;
            case uint u: sb.Append(u.ToString(CultureInfo.InvariantCulture)); break;
            case RawJson raw: sb.Append(raw.Text); break;
            case JObj o:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in o.Pairs)
                    {
                        if (kv.Value is Undefined) continue;
                        if (!first) sb.Append(',');
                        first = false;
                        Str(sb, kv.Key); sb.Append(':'); Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                }
            case System.Collections.IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var x in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, x is Undefined ? null : x);
                    }
                    sb.Append(']');
                    break;
                }
            default: sb.Append("null"); break;
        }
    }

    public static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); continue;
                case '\\': sb.Append("\\\\"); continue;
                case '\b': sb.Append("\\b"); continue;
                case '\f': sb.Append("\\f"); continue;
                case '\n': sb.Append("\\n"); continue;
                case '\r': sb.Append("\\r"); continue;
                case '\t': sb.Append("\\t"); continue;
            }
            if (c < 0x20) { sb.Append("\\u").Append(((int)c).ToString("x4")); continue; }
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; continue; }
                sb.Append("\\u").Append(((int)c).ToString("x4")); continue;
            }
            if (char.IsLowSurrogate(c)) { sb.Append("\\u").Append(((int)c).ToString("x4")); continue; }
            sb.Append(c);
        }
        sb.Append('"');
    }

    public static string Num(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
        if (d == 0) return "0";
        var r = d.ToString("R", CultureInfo.InvariantCulture);
        bool neg = r[0] == '-';
        if (neg) r = r[1..];
        int e = 0, ei = r.IndexOfAny(new[] { 'E', 'e' });
        if (ei >= 0) { e = int.Parse(r[(ei + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture); r = r[..ei]; }
        int dot = r.IndexOf('.');
        string intPart = dot >= 0 ? r[..dot] : r, frac = dot >= 0 ? r[(dot + 1)..] : "";
        string digits = (intPart + frac).TrimStart('0');
        int lead = (intPart + frac).Length - digits.Length;
        int n = intPart.Length + e - lead;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) return "0";
        int k = digits.Length;
        var sb = new StringBuilder(24);
        if (neg) sb.Append('-');
        if (k <= n && n <= 21) { sb.Append(digits).Append('0', n - k); }
        else if (0 < n && n <= 21) { sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n); }
        else if (-6 < n && n <= 0) { sb.Append("0.").Append('0', -n).Append(digits); }
        else
        {
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(n - 1 >= 0 ? "+" : "-").Append(Math.Abs(n - 1).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);
}

public sealed class Undefined { public static readonly Undefined Value = new(); Undefined() { } }

public static class Js
{
    public static double Round(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return v;
        var f = Math.Floor(v);
        return v - f >= 0.5 ? f + 1 : f;
    }
    static readonly double[] Pow10 = { 1, 10, 100, 1000, 10000, 100000 };
    public static double? RoundTo(object? v, int d = 0) => v is double x && double.IsFinite(x) ? Round(x * Pow10[d]) / Pow10[d] : null;
    public static bool Finite(object? v) => v is double d && double.IsFinite(d);
    public static bool IsObj(object? v) => v is JObj;
    public static bool Truthy(object? v) => v switch
    {
        null => false, Undefined => false, bool b => b, double d => d != 0 && !double.IsNaN(d), string s => s.Length > 0, _ => true,
    };
    public static int ToInt32(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return 0;
        var t = Math.Truncate(d);
        var m = t % 4294967296.0;
        if (m < 0) m += 4294967296.0;
        return unchecked((int)(uint)m);
    }
    public static string Trim(string s) => s.Trim().Trim('﻿').Trim();
    public static string Str(object? v) => v is string s ? Trim(s) : "";
    public static double Number(string text)
    {
        var t = Trim(text);
        if (t.Length == 0) return 0;
        return double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }
}
