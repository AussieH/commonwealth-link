import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const RANGES = [[0x0000, 0x024f], [0x1e00, 0x1eff], [0x2000, 0x206f], [0x20a0, 0x20bf], [0x2100, 0x214f], [0x2190, 0x21ff]];

const chars = [];
for (const [a, b] of RANGES) for (let c = a; c <= b; c++) if (c < 0xd800 || c > 0xdfff) chars.push(String.fromCharCode(c));

const base = new Intl.Collator("en", { sensitivity: "base" });
const accent = new Intl.Collator("en", { sensitivity: "accent" });
const variant = new Intl.Collator("en", { sensitivity: "variant" });

const ignorable = (c) => base.compare(c, "") === 0;
const LETTERS = "abcdefghijklmnopqrstuvwxyz";
const PAIRS = [];
for (const x of LETTERS) for (const y of LETTERS) PAIRS.push(x + y);
const expand = new Map();
for (const c of chars) {
  if (ignorable(c)) continue;
  const nfkd = c.normalize("NFKD");
  const tries = (nfkd.length > 1 && nfkd !== c ? [nfkd] : []).concat(PAIRS);
  for (const t of tries) if (t.length > 1 && base.compare(c, t) === 0 && [...t].every((x) => x.charCodeAt(0) < 0x250 || chars.includes(x))) { expand.set(c, t); break; }
}
const sorted = chars.filter((c) => !ignorable(c) && !expand.has(c)).sort((x, y) => variant.compare(x, y) || x.charCodeAt(0) - y.charCodeAt(0));
const prim = new Map(), sec = new Map(), ter = new Map();
let p = 0, s = 0, t = 0, prev = null;
for (const c of sorted) {
  if (prev === null || base.compare(prev, c) !== 0) { p++; s = 0; t = 0; }
  if (prev === null || accent.compare(prev, c) !== 0) { s++; t = 0; }
  if (prev === null || variant.compare(prev, c) !== 0) t++;
  prim.set(c, p); sec.set(c, s); ter.set(c, t);
  prev = c;
}
const digitRank = prim.get("0");

const esc = (x) => `\\u${x.charCodeAt(0).toString(16).padStart(4, "0")}`;
const fmt = (list) => list.map((v, i) => (i % 32 === 0 ? "\n        " : "") + v).join(",");
const rows = [];
for (const [a, b] of RANGES) {
  const P = [], S = [], T = [];
  for (let c = a; c <= b; c++) {
    const ch = String.fromCharCode(c);
    P.push(prim.get(ch) || 0); S.push(sec.get(ch) || 0); T.push(ter.get(ch) || 0);
  }
  rows.push({ a, b, P, S, T });
}

const hex = (x) => [parseInt(x.slice(1, 3), 16), parseInt(x.slice(3, 5), 16), parseInt(x.slice(5, 7), 16)];
function ramp(lo, hi, { gamma = 1, none = "#000000" } = {}) {
  const a = hex(lo), b = hex(hi), n = hex(none), out = [];
  out.push(...n);
  for (let i = 1; i < 256; i++) {
    const k = Math.pow((i - 1) / 254, gamma);
    for (let c = 0; c < 3; c++) out.push(Math.round(a[c] + (b[c] - a[c]) * k));
  }
  return out;
}
const FILE_PALETTE = ramp("#000704", "#5dffa0", { gamma: 1.1 });
const LOCAL_DARK = 40, LOCAL_LIT = 110;
const localAlpha = (v) => { if (v <= LOCAL_DARK) return 0; if (v >= LOCAL_LIT) return 1; const k = (v - LOCAL_DARK) / (LOCAL_LIT - LOCAL_DARK); return k * k * (3 - 2 * k); };
const LOCAL_TRNS = Array.from({ length: 256 }, (_, i) => Math.round(localAlpha(i) * 255));

const cs = `// Made by tools/make-tables.mjs (Node ${process.versions.node}, ICU ${process.versions.icu}). Do not edit by hand.
namespace CommonwealthLink;

static class Tables
{
    /// <summary>The rank of the digits at the base strength (numeric sorting puts a number where "0" sorts).</summary>
    public const int DigitRank = ${digitRank};
    /// <summary>Character ranges with their ranks: base (0 = ignorable), accent and case within the same base.</summary>
    public static readonly (int From, int To, ushort[] Base, byte[] Accent, byte[] Case)[] Ranks =
    {
${rows.map((r) => `        (0x${r.a.toString(16)}, 0x${r.b.toString(16)}, new ushort[] {${fmt(r.P)} },
         new byte[] {${fmt(r.S)} },
         new byte[] {${fmt(r.T)} }),`).join("\n")}
    };
    /// <summary>Characters that sort as two or three: (character, what it sorts as).</summary>
    public static readonly (char From, string To)[] Expansions =
    {
${[...expand].map(([c, t]) => `        ('${esc(c)}', "${[...t].map(esc).join("")}"),`).join("\n")}
    };
    /// <summary>The cached file's and the widget's palette: index 0 none, 1-255 the green ramp (RGB).</summary>
    public static readonly byte[] FilePalette = {${fmt(FILE_PALETTE)} };
    /// <summary>The local map's opacity by palette index (brightness as opacity).</summary>
    public static readonly byte[] LocalTrns = {${fmt(LOCAL_TRNS)} };
}
`;
writeFileSync(join(ROOT, "src", "Tables.cs"), cs);
console.log(`src/Tables.cs: ${prim.size} ranked characters, ${expand.size} expansions, ${p} base ranks, digits at ${digitRank}`);
