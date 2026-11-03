import { writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { deflateSync } from "node:zlib";

const ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const SIZES = [16, 20, 24, 32, 40, 48, 64, 256];
const BG = [7, 20, 13], EDGE = [31, 107, 67], GREEN = [93, 255, 160];

function inRoundRect(x, y, inset, r) {
  const a = inset, b = 1 - inset;
  if (x < a || x > b || y < a || y > b) return false;
  const cx = Math.min(Math.max(x, a + r), b - r), cy = Math.min(Math.max(y, a + r), b - r);
  return (x - cx) ** 2 + (y - cy) ** 2 <= r * r;
}
function seg(x, y, ax, ay, bx, by) {
  const vx = bx - ax, vy = by - ay, t = Math.max(0, Math.min(1, ((x - ax) * vx + (y - ay) * vy) / (vx * vx + vy * vy)));
  return Math.hypot(x - (ax + t * vx), y - (ay + t * vy));
}
const LINK_A = [0.25, 0.75, 0.47, 0.53], LINK_B = [0.53, 0.47, 0.75, 0.25], R = 0.16;
function ring(x, y, l, size, extra = 0) { const t = Math.max(0.085, 1.5 / size) + extra; return Math.abs(seg(x, y, ...l) - R) <= t / 2; }
function glyph(x, y, size) {
  const a = ring(x, y, LINK_A, size), b = ring(x, y, LINK_B, size);
  const upperHalf = x + y < 1;
  if (upperHalf) { if (b) return "green"; if (a && ring(x, y, LINK_B, size, Math.max(0.06, 1.2 / size))) return "gap"; return a ? "green" : null; }
  if (a) return "green"; if (b && ring(x, y, LINK_A, size, Math.max(0.06, 1.2 / size))) return "gap"; return b ? "green" : null;
}
function draw(size) {
  const px = Buffer.alloc(size * size * 4), S = 4;
  for (let j = 0; j < size; j++) for (let i = 0; i < size; i++) {
    let r = 0, g = 0, b = 0, a = 0;
    for (let sj = 0; sj < S; sj++) for (let si = 0; si < S; si++) {
      const x = (i + (si + 0.5) / S) / size, y = (j + (sj + 0.5) / S) / size;
      if (!inRoundRect(x, y, 0.03, 0.2)) continue;
      const gl = glyph(x, y, size);
      const c = gl === "green" ? GREEN : gl === "gap" ? BG : inRoundRect(x, y, 0.03 + Math.max(0.035, 1 / size), 0.17) ? BG : EDGE;
      r += c[0]; g += c[1]; b += c[2]; a += 255;
    }
    const n = S * S, o = 4 * (j * size + i);
    if (a) { px[o] = Math.round(r / (a / 255)); px[o + 1] = Math.round(g / (a / 255)); px[o + 2] = Math.round(b / (a / 255)); }
    px[o + 3] = Math.round(a / n);
  }
  return px;
}

const CRC = new Int32Array(256);
for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; CRC[n] = c; }
const crc32 = (buf) => { let c = -1; for (const x of buf) c = CRC[(c ^ x) & 0xff] ^ (c >>> 8); return (c ^ -1) >>> 0; };
function chunk(kind, body) {
  const h = Buffer.alloc(8); h.writeUInt32BE(body.length, 0); h.write(kind, 4, "latin1");
  const c = Buffer.alloc(4); c.writeUInt32BE(crc32(Buffer.concat([h.subarray(4), body])), 0);
  return Buffer.concat([h, body, c]);
}
function png(size, rgba) {
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(size, 0); ihdr.writeUInt32BE(size, 4); ihdr[8] = 8; ihdr[9] = 6;
  const raw = Buffer.alloc(size * (size * 4 + 1));
  for (let y = 0; y < size; y++) rgba.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk("IHDR", ihdr), chunk("IDAT", deflateSync(raw, { level: 9 })), chunk("IEND", Buffer.alloc(0))]);
}
function dib(size, rgba) {
  const head = Buffer.alloc(40);
  head.writeUInt32LE(40, 0); head.writeInt32LE(size, 4); head.writeInt32LE(size * 2, 8); head.writeUInt16LE(1, 12); head.writeUInt16LE(32, 14);
  const pixels = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    const s = 4 * (y * size + x), d = 4 * ((size - 1 - y) * size + x);
    pixels[d] = rgba[s + 2]; pixels[d + 1] = rgba[s + 1]; pixels[d + 2] = rgba[s]; pixels[d + 3] = rgba[s + 3];
  }
  const mask = Buffer.alloc(Math.ceil(size / 32) * 4 * size);
  return Buffer.concat([head, pixels, mask]);
}

const images = SIZES.map((s) => ({ s, data: s >= 256 ? png(s, draw(s)) : dib(s, draw(s)) }));
const header = Buffer.alloc(6); header.writeUInt16LE(1, 2); header.writeUInt16LE(images.length, 4);
let offset = 6 + 16 * images.length;
const entries = images.map(({ s, data }) => {
  const e = Buffer.alloc(16);
  e[0] = s >= 256 ? 0 : s; e[1] = s >= 256 ? 0 : s; e.writeUInt16LE(1, 4); e.writeUInt16LE(32, 6); e.writeUInt32LE(data.length, 8); e.writeUInt32LE(offset, 12);
  offset += data.length;
  return e;
});
writeFileSync(join(ROOT, "src", "commonwealth.ico"), Buffer.concat([header, ...entries, ...images.map((i) => i.data)]));
writeFileSync(join(ROOT, "tools", "icon-preview.png"), png(256, draw(256)));
console.log(`src/commonwealth.ico: ${SIZES.join(", ")} px`);
