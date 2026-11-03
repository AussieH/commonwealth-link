using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CommonwealthLink;

public sealed class Ba2 : IDisposable
{
    public sealed class Chunk { public long Offset; public uint Packed, Size; public ushort Mip0, Mip1; }
    public sealed class File_ { public string Name = "", Ext = ""; public long Offset; public uint Packed, Size; public int Width, Height, Mips, Format; public List<Chunk> Chunks = new(); }
    public string Type = "";
    public uint Version;
    public List<File_> Files = new();
    readonly FileStream fs;
    const uint Magic = 0x58445442;

    byte[] ReadAt(long pos, long len)
    {
        var buf = new byte[len];
        int got = 0;
        while (got < len)
        {
            int n = RandomAccess.Read(fs.SafeFileHandle, buf.AsSpan(got), pos + got);
            if (n <= 0) throw new InvalidDataException($"the archive ends at byte {pos + got}");
            got += n;
        }
        return buf;
    }
    static long U64(byte[] b, int o) { var v = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o)); if (v > 9007199254740991UL) throw new InvalidDataException("an offset is too big"); return (long)v; }

    public Ba2(string path)
    {
        fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        try
        {
            long size = fs.Length;
            var h = ReadAt(0, 24);
            if (BinaryPrimitives.ReadUInt32LittleEndian(h) != Magic) throw new InvalidDataException("not a BA2 archive");
            Version = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
            Type = Encoding.Latin1.GetString(h, 8, 4);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(12));
            long names = U64(h, 16);
            if (!(Version is 1 or 2 or 3 or 7 or 8)) throw new InvalidDataException($"BA2 version {Version} is not known");
            if (Type != "GNRL" && Type != "DX10") throw new InvalidDataException("BA2 type is not known");
            if (count > 2_000_000 || names > size) throw new InvalidDataException("the BA2 header does not add up");
            long at = 24 + (Version == 2 ? 8 : Version == 3 ? 12 : 0);
            if (Type == "GNRL")
            {
                var rec = ReadAt(at, 36L * count);
                for (int i = 0; i < count; i++)
                {
                    int o = 36 * i;
                    Files.Add(new File_ { Ext = Encoding.Latin1.GetString(rec, o + 4, 4).TrimEnd('\0'), Offset = U64(rec, o + 16), Packed = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(o + 24)), Size = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(o + 28)) });
                }
            }
            else
            {
                long span = Math.Min(size, names) - at;
                var rec = ReadAt(at, Math.Min(span, 24L * count + 24L * 16 * count));
                int p = 0;
                for (int i = 0; i < count; i++)
                {
                    if (p + 24 > rec.Length) throw new InvalidDataException("the texture records run past the end");
                    int chunks = rec[p + 13], chunkHead = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(p + 14));
                    var f = new File_ { Ext = Encoding.Latin1.GetString(rec, p + 4, 4).TrimEnd('\0'), Height = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(p + 16)), Width = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(p + 18)), Mips = rec[p + 20], Format = rec[p + 21] };
                    p += 24;
                    if (chunkHead != 24 && chunks > 0) throw new InvalidDataException($"a texture chunk header of {chunkHead} bytes is not known");
                    for (int c = 0; c < chunks; c++)
                    {
                        if (p + 24 > rec.Length) throw new InvalidDataException("the texture chunks run past the end");
                        f.Chunks.Add(new Chunk { Offset = U64(rec, p), Packed = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(p + 8)), Size = BinaryPrimitives.ReadUInt32LittleEndian(rec.AsSpan(p + 12)), Mip0 = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(p + 16)), Mip1 = BinaryPrimitives.ReadUInt16LittleEndian(rec.AsSpan(p + 18)) });
                        p += 24;
                    }
                    Files.Add(f);
                }
            }
            var table = ReadAt(names, size - names);
            int q = 0;
            for (int i = 0; i < count; i++)
            {
                if (q + 2 > table.Length) throw new InvalidDataException("the name table is short");
                int n = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(q));
                int len = Math.Max(0, Math.Min(n, table.Length - q - 2));
                Files[i].Name = Encoding.Latin1.GetString(table, q + 2, len).Replace('/', '\\');
                q += 2 + n;
            }
        }
        catch { fs.Dispose(); throw; }
    }
    public void Dispose() => fs.Dispose();

    public File_? Find(string name)
    {
        var want = name.Replace('/', '\\');
        return Files.FirstOrDefault(f => string.Equals(f.Name.ToLowerInvariant(), want.ToLowerInvariant(), StringComparison.Ordinal));
    }
    byte[] Piece(long offset, uint packed, uint size)
    {
        var raw = ReadAt(offset, packed != 0 ? packed : size);
        if (packed == 0) return raw;
        using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var ms = new MemoryStream((int)size);
        z.CopyTo(ms);
        if (ms.Length != size) throw new InvalidDataException($"a packed piece came out {ms.Length} bytes, not {size}");
        return ms.ToArray();
    }
    public byte[] ReadFile(File_ f) { if (Type != "GNRL") throw new InvalidDataException("not a general archive"); return Piece(f.Offset, f.Packed, f.Size); }
    public Dds.Texture ReadTexture(File_ f)
    {
        if (Type != "DX10") throw new InvalidDataException("not a texture archive");
        if (f.Chunks.Count == 0) throw new InvalidDataException("a texture with no data");
        var first = f.Chunks.FirstOrDefault(c => c.Mip0 == 0) ?? f.Chunks[0];
        var data = Piece(first.Offset, first.Packed, first.Size);
        long need = Dds.MipBytes(f.Format, f.Width, f.Height) ?? data.Length;
        if (need > data.Length) throw new InvalidDataException($"mip 0 needs {need} bytes and the chunk has {data.Length}");
        return new Dds.Texture(f.Width, f.Height, f.Format, data.AsSpan(0, (int)need).ToArray());
    }
}

public static class Dds
{
    public sealed record Texture(int Width, int Height, int Format, byte[] Data);
    public const int R8G8B8A8 = 28, R8G8B8A8_SRGB = 29, R8 = 61, BC1 = 71, BC1_SRGB = 72, BC2 = 74, BC2_SRGB = 75, BC3 = 77, BC3_SRGB = 78, BC4 = 80, BC5 = 83, B8G8R8A8 = 87, B8G8R8X8 = 88, B8G8R8A8_SRGB = 91, BC7 = 98, BC7_SRGB = 99;
    static readonly (string Name, int F)[] Names = { ("R8G8B8A8", 28), ("R8G8B8A8_SRGB", 29), ("R8", 61), ("BC1", 71), ("BC1_SRGB", 72), ("BC2", 74), ("BC2_SRGB", 75), ("BC3", 77), ("BC3_SRGB", 78), ("BC4", 80), ("BC5", 83), ("B8G8R8A8", 87), ("B8G8R8X8", 88), ("B8G8R8A8_SRGB", 91), ("BC7", 98), ("BC7_SRGB", 99) };
    static readonly HashSet<int> Block8 = new() { BC1, BC1_SRGB, BC4 }, Block16 = new() { BC2, BC2_SRGB, BC3, BC3_SRGB, BC5, BC7, BC7_SRGB }, Pixel4 = new() { R8G8B8A8, R8G8B8A8_SRGB, B8G8R8A8, B8G8R8X8, B8G8R8A8_SRGB };
    public static string FormatName(int f) { foreach (var (n, v) in Names) if (v == f) return n; return $"DXGI {f}"; }
    public static bool CanDecode(int f) => Block8.Contains(f) || (Block16.Contains(f) && f != BC7 && f != BC7_SRGB) || Pixel4.Contains(f) || f == R8;
    public static long? MipBytes(int format, int width, int height)
    {
        long bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
        if (Block8.Contains(format)) return bw * bh * 8;
        if (Block16.Contains(format)) return bw * bh * 16;
        if (Pixel4.Contains(format)) return (long)width * height * 4;
        if (format == R8) return (long)width * height;
        return null;
    }

    static void C565(int v, byte[] o, int at) { int r = (v >> 11) & 31, g = (v >> 5) & 63, b = v & 31; o[at] = (byte)((r << 3) | (r >> 2)); o[at + 1] = (byte)((g << 2) | (g >> 4)); o[at + 2] = (byte)((b << 3) | (b >> 2)); }
    static void Bc1Block(byte[] src, int at, byte[] px, bool four = false)
    {
        int a = src[at] | (src[at + 1] << 8), b = src[at + 2] | (src[at + 3] << 8);
        var pal = new byte[16];
        C565(a, pal, 0); C565(b, pal, 4);
        pal[3] = pal[7] = 255;
        if (four || a > b)
        {
            for (int c = 0; c < 3; c++) { pal[8 + c] = (byte)((2 * pal[c] + pal[4 + c]) / 3); pal[12 + c] = (byte)((pal[c] + 2 * pal[4 + c]) / 3); }
            pal[11] = pal[15] = 255;
        }
        else
        {
            for (int c = 0; c < 3; c++) { pal[8 + c] = (byte)((pal[c] + pal[4 + c]) / 2); pal[12 + c] = 0; }
            pal[11] = 255; pal[15] = 0;
        }
        uint bits = (uint)(src[at + 4] | (src[at + 5] << 8) | (src[at + 6] << 16) | (src[at + 7] << 24));
        for (int i = 0; i < 16; i++) { int k = (int)((bits >> (2 * i)) & 3) * 4; Buffer.BlockCopy(pal, k, px, 4 * i, 4); }
    }
    static void Bc4Block(byte[] src, int at, byte[] out_, int o = 0, int stride = 4)
    {
        int a = src[at], b = src[at + 1];
        var pal = new List<int> { a, b };
        if (a > b) for (int i = 1; i <= 6; i++) pal.Add(((7 - i) * a + i * b) / 7);
        else { for (int i = 1; i <= 4; i++) pal.Add(((5 - i) * a + i * b) / 5); pal.Add(0); pal.Add(255); }
        uint lo = (uint)(src[at + 2] | (src[at + 3] << 8) | (src[at + 4] << 16)), hi = (uint)(src[at + 5] | (src[at + 6] << 8) | (src[at + 7] << 16));
        for (int i = 0; i < 16; i++) { int v = (int)(i < 8 ? (lo >> (3 * i)) & 7 : (hi >> (3 * (i - 8))) & 7); out_[o + stride * i] = (byte)pal[v]; }
    }

    public static byte[] Decode(Texture t)
    {
        int width = t.Width, height = t.Height, format = t.Format; var data = t.Data;
        if (!CanDecode(format)) throw new InvalidDataException($"the texture format {FormatName(format)} is not read");
        long need = MipBytes(format, width, height)!.Value;
        if (data.Length < need) throw new InvalidDataException($"the texture has {data.Length} bytes, {need} needed");
        var out_ = new byte[width * height * 4];
        if (Pixel4.Contains(format))
        {
            bool bgr = format is B8G8R8A8 or B8G8R8X8 or B8G8R8A8_SRGB;
            for (int i = 0; i < width * height; i++)
            {
                int o = 4 * i;
                out_[o] = data[o + (bgr ? 2 : 0)]; out_[o + 1] = data[o + 1]; out_[o + 2] = data[o + (bgr ? 0 : 2)];
                out_[o + 3] = format == B8G8R8X8 ? (byte)255 : data[o + 3];
            }
            return out_;
        }
        if (format == R8)
        {
            for (int i = 0; i < width * height; i++) { byte v = data[i]; out_[4 * i] = out_[4 * i + 1] = out_[4 * i + 2] = v; out_[4 * i + 3] = 255; }
            return out_;
        }
        int bw = (width + 3) / 4, bh = (height + 3) / 4, size = Block8.Contains(format) ? 8 : 16;
        var px = new byte[64]; var ch = new byte[64];
        for (int by = 0; by < bh; by++) for (int bx = 0; bx < bw; bx++)
            {
                int at = (by * bw + bx) * size;
                switch (format)
                {
                    case BC1: case BC1_SRGB: Bc1Block(data, at, px); break;
                    case BC2: case BC2_SRGB:
                        Bc1Block(data, at + 8, px, true);
                        for (int i = 0; i < 16; i++) { int v = (data[at + (i >> 1)] >> ((i & 1) * 4)) & 15; px[4 * i + 3] = (byte)(v * 17); }
                        break;
                    case BC3: case BC3_SRGB: Bc1Block(data, at + 8, px, true); Bc4Block(data, at, px, 3); break;
                    case BC4:
                        Bc4Block(data, at, ch, 0);
                        for (int i = 0; i < 16; i++) { px[4 * i] = px[4 * i + 1] = px[4 * i + 2] = ch[4 * i]; px[4 * i + 3] = 255; }
                        break;
                    case BC5:
                        Bc4Block(data, at, px, 0); Bc4Block(data, at + 8, px, 1);
                        for (int i = 0; i < 16; i++) { px[4 * i + 2] = 0; px[4 * i + 3] = 255; }
                        break;
                    default: throw new InvalidDataException($"the texture format {FormatName(format)} is not read");
                }
                for (int y = 0; y < 4; y++)
                {
                    int py = by * 4 + y;
                    if (py >= height) break;
                    for (int x = 0; x < 4; x++)
                    {
                        int qx = bx * 4 + x;
                        if (qx >= width) break;
                        Buffer.BlockCopy(px, 4 * (4 * y + x), out_, 4 * (py * width + qx), 4);
                    }
                }
            }
        return out_;
    }

    static readonly Dictionary<string, int> FourCC = new() { ["DXT1"] = BC1, ["DXT2"] = BC2, ["DXT3"] = BC2, ["DXT4"] = BC3, ["DXT5"] = BC3, ["ATI1"] = BC4, ["BC4U"] = BC4, ["ATI2"] = BC5, ["BC5U"] = BC5 };
    public static Texture Parse(byte[] buf)
    {
        if (buf.Length < 128 || Encoding.Latin1.GetString(buf, 0, 4) != "DDS ") throw new InvalidDataException("not a DDS file");
        uint U(int o) => BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(o));
        uint height = U(12), width = U(16), pfFlags = U(80), bits = U(88);
        string four = Encoding.Latin1.GetString(buf, 84, 4);
        int? format = null; int at = 128;
        if ((pfFlags & 4) != 0)
        {
            if (four == "DX10") { format = (int)U(128); at = 148; }
            else format = FourCC.TryGetValue(four, out var f) ? f : null;
        }
        else if ((pfFlags & 0x40) != 0 && bits == 32)
        {
            uint rMask = U(92); bool alpha = (pfFlags & 1) != 0;
            format = rMask == 0x00ff0000 ? (alpha ? B8G8R8A8 : B8G8R8X8) : rMask == 0x000000ff ? R8G8B8A8 : null;
        }
        else if ((pfFlags & 0x20000) != 0 && bits == 8) format = R8;
        if (format == null) throw new InvalidDataException("the DDS pixel format is not read");
        if (width == 0 || height == 0 || width > 16384 || height > 16384) throw new InvalidDataException("bad DDS size");
        var need = MipBytes(format.Value, (int)width, (int)height) ?? throw new InvalidDataException($"the texture format {FormatName(format.Value)} is not read");
        if (buf.Length < at + need) throw new InvalidDataException("the DDS file is cut short");
        return new Texture((int)width, (int)height, format.Value, buf.AsSpan(at, (int)need).ToArray());
    }
}

public static class Png
{
    static readonly byte[] Signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };
    static readonly uint[] Crc = MakeCrc();
    static uint[] MakeCrc() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
    static uint Crc32(ReadOnlySpan<byte> b) { uint c = 0xffffffff; foreach (var x in b) c = Crc[(c ^ x) & 0xff] ^ (c >> 8); return c ^ 0xffffffff; }
    static void Chunk(Stream o, string kind, byte[] body)
    {
        var head = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)body.Length);
        Encoding.Latin1.GetBytes(kind, 0, 4, head, 4);
        var forCrc = new byte[4 + body.Length]; Buffer.BlockCopy(head, 4, forCrc, 0, 4); Buffer.BlockCopy(body, 0, forCrc, 4, body.Length);
        var crc = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(forCrc));
        o.Write(head); o.Write(body); o.Write(crc);
    }
    public static byte[] EncodeIndexed(int width, int height, byte[] index, byte[] palette, byte[]? alpha = null, CompressionLevel level = CompressionLevel.Optimal)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height); ihdr[8] = 8; ihdr[9] = 3;
        var raw = new byte[height * (width + 1)];
        for (int y = 0; y < height; y++)
        {
            int o = y * (width + 1), row = y * width;
            if (y == 0) { raw[o] = 0; Buffer.BlockCopy(index, 0, raw, o + 1, width); continue; }
            raw[o] = 2;
            for (int x = 0; x < width; x++) raw[o + 1 + x] = (byte)(index[row + x] - index[row - width + x]);
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, level, true)) zs.Write(raw);
        var o_ = new MemoryStream();
        o_.Write(Signature);
        Chunk(o_, "IHDR", ihdr); Chunk(o_, "PLTE", palette);
        if (alpha != null) Chunk(o_, "tRNS", alpha);
        Chunk(o_, "IDAT", z.ToArray()); Chunk(o_, "IEND", Array.Empty<byte>());
        return o_.ToArray();
    }
    public sealed record Image(int Width, int Height, int Channels, int Type, byte[] Data, byte[]? Palette);
    public static Image Decode(byte[] buf, int maxSide = 8192)
    {
        if (buf.Length < 33 || !buf.AsSpan(0, 8).SequenceEqual(Signature)) throw new InvalidDataException("not a PNG");
        int at = 8, width = 0, height = 0, depth = 0, type = 0, interlace = 0; byte[]? palette = null; bool ended = false;
        var idat = new MemoryStream();
        while (at + 8 <= buf.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(at)); string kind = Encoding.Latin1.GetString(buf, at + 4, 4);
            if (len < 0 || at + 12 + (long)len > buf.Length) throw new InvalidDataException("PNG cut short");
            var body = buf.AsSpan(at + 8, len);
            if (kind == "IHDR") { width = (int)BinaryPrimitives.ReadUInt32BigEndian(body); height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]); depth = body[8]; type = body[9]; interlace = body[12]; }
            else if (kind == "PLTE") palette = body.ToArray();
            else if (kind == "IDAT") idat.Write(body);
            else if (kind == "IEND") { ended = true; break; }
            at += 12 + len;
        }
        if (!ended) throw new InvalidDataException("PNG cut short");
        if (width <= 0 || height <= 0 || width > maxSide || height > maxSide) throw new InvalidDataException("bad PNG size");
        if (depth != 8 || interlace != 0) throw new InvalidDataException("PNG depth or interlace not read");
        int channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0) throw new InvalidDataException($"PNG colour type {type} not read");
        idat.Position = 0;
        var rawMs = new MemoryStream();
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.CopyTo(rawMs);
        var raw = rawMs.ToArray();
        int stride = width * channels;
        if (raw.Length < height * (stride + 1)) throw new InvalidDataException("PNG data cut short");
        var px = new byte[height * stride];
        for (int y = 0; y < height; y++)
        {
            int f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, row = y * stride, prev = row - stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? px[row + x - channels] : 0, b = y > 0 ? px[prev + x] : 0, c = y > 0 && x >= channels ? px[prev + x - channels] : 0;
                int v = raw[src + x];
                if (f == 1) v += a;
                else if (f == 2) v += b;
                else if (f == 3) v += (a + b) >> 1;
                else if (f == 4) { int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
                else if (f != 0) throw new InvalidDataException($"PNG filter {f} not known");
                px[row + x] = (byte)(v & 0xff);
            }
        }
        return new Image(width, height, channels, type, px, palette);
    }
}

public static class Raster
{
    public static byte[] ToIndex(byte[] px, int width, int height, bool grey = false)
    {
        int n = width * height, step = grey ? 1 : 4;
        var lum = new byte[n]; var hist = new uint[256];
        for (int i = 0; i < n; i++)
        {
            int o = i * step;
            int v = step == 4 ? (px[o] * 54 + px[o + 1] * 183 + px[o + 2] * 19) >> 8 : px[o];
            lum[i] = (byte)v; hist[v]++;
        }
        int Cut(double frac) { double s = 0; for (int v = 0; v < 256; v++) { s += hist[v]; if (s >= n * frac) return v; } return 255; }
        int lo = Cut(0.005), hi = Math.Max(lo + 1, Cut(0.995));
        var map = new byte[256];
        for (int v = 0; v < 256; v++) map[v] = (byte)(1 + Js.Round(Math.Max(0, Math.Min(1, (double)(v - lo) / (hi - lo))) * 254));
        for (int i = 0; i < n; i++) lum[i] = map[lum[i]];
        return lum;
    }
    public sealed record Level(int Width, int Height, byte[] Index);
    public static List<Level> MipLevels(int width, int height, byte[] index, int min, bool max)
    {
        var out_ = new List<Level> { new(width, height, index) };
        int w = width, h = height; var src = index;
        while (w >= min * 2 && h >= min * 2)
        {
            int nw = w >> 1, nh = h >> 1; var dst = new byte[nw * nh];
            for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++)
                {
                    int o = 2 * y * w + 2 * x;
                    dst[y * nw + x] = max ? Math.Max(Math.Max(src[o], src[o + 1]), Math.Max(src[o + w], src[o + w + 1])) : (byte)((src[o] + src[o + 1] + src[o + w] + src[o + w + 1] + 2) >> 2);
                }
            out_.Add(new Level(nw, nh, dst));
            w = nw; h = nh; src = dst;
        }
        return out_;
    }
}

public static class GameDir
{
    public static bool IsGameDir(string dir) => dir != "" && (File.Exists(Path.Combine(dir, "Fallout4.exe")) || Directory.Exists(Path.Combine(dir, "Fallout4.exe"))) && (Directory.Exists(Path.Combine(dir, "Data")) || File.Exists(Path.Combine(dir, "Data")));
    static string? Reg(RegistryKey root, string key, string name) { try { using var k = root.OpenSubKey(key); return k?.GetValue(name) as string; } catch (Exception) { return null; } }
    public static List<string> VdfLibraries(string text)
    {
        var out_ = new List<string>();
        foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase | RegexOptions.ECMAScript)) out_.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        return out_;
    }
    public static (string? Dir, string? How, string? Error) Find(string over)
    {
        var o = over.Trim();
        if (o.StartsWith('"')) o = o[1..];
        if (o.EndsWith('"')) o = o[..^1];
        if (o != "") return IsGameDir(o) ? (o, "setting", null) : (null, "setting", "not a Fallout 4 folder");
        var r = Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Bethesda Softworks\Fallout4", "installed path");
        if (r != null && r.Trim() is var rt && rt != "" && IsGameDir(rt.TrimEnd('\\', '/'))) return (rt.TrimEnd('\\', '/'), "registry", null);
        var steam = (Reg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath") is string sp && sp.Trim() != "" ? sp.Trim() : "C:/Program Files (x86)/Steam").Replace('/', '\\');
        var libs = new List<string> { steam };
        try { libs.AddRange(VdfLibraries(File.ReadAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")))); } catch (Exception) { }
        foreach (var lib in libs.Distinct())
        {
            var dir = Path.Combine(lib, "steamapps", "common", "Fallout 4");
            if (IsGameDir(dir)) return (dir, "steam", null);
        }
        return (null, null, "game not found");
    }
}

public sealed class WorldPictures
{
    public const int PictureVersion = 1;
    public const int Cell = 4096;
    public const int ShiftX = -4096, ShiftY = 2048;
    static readonly Dictionary<string, string> Known = new() { ["commonwealth"] = @"Interface\Pip-Boy\WorldMap_d.dds", ["farharbor"] = @"DLC03\Interface\Map\MapFarHarbor.dds", ["nukaworld"] = @"DLC04\Interface\Map\NukaWorldMap.dds" };
    static readonly Regex SafePath = new(@"^[A-Za-z0-9 _\-().\\]+\.dds$", RegexOptions.IgnoreCase | RegexOptions.ECMAScript), DotDot = new(@"(^|\\)\.\.(\\|$)", RegexOptions.ECMAScript);

    public string Dir;
    public Func<string> GameDirSetting = () => "";
    public Action? Changed;
    bool on = true;
    (string Slug, string Texture, string? World)? target;
    sealed class Loaded { public string Slug = "", Texture = "", Key = ""; public int Width, Height; public byte[] Png = Array.Empty<byte>(); }
    Loaded? loaded;
    string? error;
    bool busy;
    double[]? ext;
    readonly Dictionary<string, string> tried = new();

    public WorldPictures(string dir) { Dir = dir; }

    public static string WorldSlug(string? name) { var s = Regex.Replace((name ?? "").ToLowerInvariant(), "[^a-z0-9]+", ""); return s.Length > 40 ? s[..40] : s; }
    public static string? TexturePath(string? texture, string? world)
    {
        var t = (texture ?? "").Trim();
        if (t == "") t = Known.TryGetValue(WorldSlug(world), out var k) ? k : "";
        if (t == "") return null;
        t = t.Replace('/', '\\').TrimStart('\\');
        if (!SafePath.IsMatch(t) || DotDot.IsMatch(t) || t.Length > 200) return null;
        return t.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? t : @"Textures\" + t;
    }
    public static List<string> ArchiveOrder(IEnumerable<string> names)
    {
        static int Rank(string n) => (Regex.IsMatch(n, "^DLCUltraHighResolution", RegexOptions.IgnoreCase) ? 0 : Regex.IsMatch(n, "^Fallout4 - ", RegexOptions.IgnoreCase) ? 2 : 1) + (Regex.IsMatch(n, "textures", RegexOptions.IgnoreCase) ? 0 : 10);
        return Collate.StableSort(names.Where(n => n.EndsWith(".ba2", StringComparison.OrdinalIgnoreCase)), (a, b) => Rank(a) - Rank(b) is var d && d != 0 ? d : Collate.Compare(b, a, baseOnly: false, numeric: true));
    }
    static IEnumerable<string> DataNames(string data) => Directory.EnumerateFileSystemEntries(data).Select(p => Path.GetFileName(p));

    public static (int Width, int Height, byte[] Rgba, string From, int Format) Extract(string gameDir, string texture)
    {
        var data = Path.Combine(gameDir, "Data");
        var loose = Path.Combine(data, texture);
        if (File.Exists(loose) || Directory.Exists(loose))
        {
            var t = Dds.Parse(File.ReadAllBytes(loose));
            return (t.Width, t.Height, Dds.Decode(t), "loose file", t.Format);
        }
        foreach (var name in ArchiveOrder(DataNames(data)))
        {
            Ba2 a;
            try { a = new Ba2(Path.Combine(data, name)); } catch (Exception) { continue; }
            using (a)
            {
                var f = a.Find(texture);
                if (f == null) continue;
                Dds.Texture t;
                if (a.Type == "DX10")
                {
                    if (!Dds.CanDecode(f.Format)) throw new InvalidDataException($"the texture format {Dds.FormatName(f.Format)} is not read");
                    t = a.ReadTexture(f);
                }
                else t = Dds.Parse(a.ReadFile(f));
                return (t.Width, t.Height, Dds.Decode(t), name, t.Format);
            }
        }
        throw new InvalidDataException("no map texture");
    }

    public static string GameKey(string gameDir, string texture)
    {
        static string St(string p)
        {
            try
            {
                FileSystemInfo i = File.Exists(p) ? new FileInfo(p) : Directory.Exists(p) ? new DirectoryInfo(p) : throw new FileNotFoundException();
                long size = i is FileInfo fi ? fi.Length : 0;
                double ms = (i.LastWriteTimeUtc.Ticks - DateTime.UnixEpoch.Ticks) / 10000.0;
                return $"{size}:{Json.Num(Js.Round(ms))}";
            }
            catch (Exception) { return "-"; }
        }
        var sb = new StringBuilder();
        sb.Append($"v{PictureVersion}|{texture.ToLowerInvariant()}");
        sb.Append($"|exe {St(Path.Combine(gameDir, "Fallout4.exe"))}|loose {St(Path.Combine(gameDir, "Data", texture))}");
        try { foreach (var n in ArchiveOrder(DataNames(Path.Combine(gameDir, "Data")))) sb.Append($"|{n.ToLowerInvariant()} {St(Path.Combine(gameDir, "Data", n))}"); } catch (Exception) { }
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()[..16];
    }

    public static double[]? TextureBox(double[]? e)
    {
        if (e == null) return null;
        double nwx = e[0], nwy = e[1], nex = e[2], swy = e[5];
        if (!new[] { nwx, nwy, nex, swy }.All(double.IsFinite) || !(nex > nwx) || !(nwy > swy)) return null;
        return new[] { nwx + ShiftX, nex + Cell + ShiftX, swy + ShiftY, nwy + Cell + ShiftY };
    }

    public void SetEnabled(bool value)
    {
        if (value == on) return;
        on = value;
        if (!on) { loaded = null; target = null; error = null; tried.Clear(); }
        Changed?.Invoke();
    }
    public void Retry()
    {
        tried.Clear(); error = null;
        if (target is { } t) { target = null; Want(t.World, Regex.Replace(t.Texture, @"^Textures\\", "", RegexOptions.IgnoreCase), ext); }
    }

    public void Want(string? world, string? texture0, double[]? extents)
    {
        if (!on) return;
        var texture = TexturePath(texture0, world);
        if (extents != null) ext = extents;
        if (texture == null) return;
        var slug = WorldSlug(world);
        if (slug == "") slug = WorldSlug(Regex.Replace(texture.Split('\\')[^1], @"\.dds$", "", RegexOptions.IgnoreCase));
        if (target is { } t0 && t0.Slug == slug && t0.Texture == texture) return;
        target = (slug, texture, world);
        if (loaded != null && loaded.Slug == slug && loaded.Texture == texture) return;
        var tag = $"{slug}|{texture}|{GameDirSetting()}";
        if (tried.TryGetValue(tag, out var e)) { error = e; return; }
        if (busy) return;
        busy = true;
        var over = GameDirSetting();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Loaded? got = null; string? err = null;
            try { got = Load(slug, texture, over); }
            catch (Exception x) { err = x is InvalidDataException or IOException or UnauthorizedAccessException ? x.Message : "cannot read the map"; Program.Log($"map picture: {err}"); }
            lock (Engine.Gate)
            {
                if (on) { if (got != null) { loaded = got; error = null; } else { error = err; tried[tag] = err!; } }
                busy = false;
                if (on && target is { } t && (loaded == null || loaded.Slug != t.Slug)) { target = null; Want(t.World, t.Texture, ext); }
            }
            Changed?.Invoke();
        });
    }

    Loaded Load(string slug, string texture, string over)
    {
        var game = GameDir.Find(over);
        if (game.Dir == null) throw new InvalidDataException(game.Error ?? "game not found");
        var key = GameKey(game.Dir, texture);
        var file = Path.Combine(Dir, $"worldmap-{slug}.png"); var meta = Path.Combine(Dir, "worldmap.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            if (doc.RootElement.TryGetProperty("worlds", out var ws) && ws.ValueKind == JsonValueKind.Object && ws.TryGetProperty(slug, out var w) && w.ValueKind == JsonValueKind.Object
                && w.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key)
            {
                var png = File.ReadAllBytes(file);
                var d = Png.Decode(png);
                if (d.Type == 3)
                {
                    Program.Log($"map picture: {slug} from the cache");
                    return new Loaded { Slug = slug, Texture = texture, Key = key, Width = d.Width, Height = d.Height, Png = png };
                }
            }
        }
        catch (Exception) { }
        var t0 = Environment.TickCount64;
        var tex = Extract(game.Dir, texture);
        var index = Raster.ToIndex(tex.Rgba, tex.Width, tex.Height);
        var bytes = Png.EncodeIndexed(tex.Width, tex.Height, index, Tables.FilePalette);
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(file + ".tmp", bytes);
            File.Move(file + ".tmp", file, true);
            var worlds = new JObj();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                if (doc.RootElement.TryGetProperty("worlds", out var ws) && ws.ValueKind == JsonValueKind.Object)
                    foreach (var p in ws.EnumerateObject()) if (p.Name != slug) worlds.Set(p.Name, new RawJson(p.Value.GetRawText()));
            }
            catch (Exception) { }
            worlds.Set(slug, new JObj().Set("key", key).Set("texture", texture).Set("width", tex.Width).Set("height", tex.Height).Set("from", tex.From).Set("format", Dds.FormatName(tex.Format)));
            File.WriteAllText(meta, Json.Stringify(new JObj().Set("worlds", worlds)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Program.Log($"map picture: cannot keep it ({e.Message})"); }
        Program.Log($"map picture: {slug} read from {tex.From} ({tex.Width} x {tex.Height} {Dds.FormatName(tex.Format)}) in {Environment.TickCount64 - t0} ms, game found by {game.How}");
        return new Loaded { Slug = slug, Texture = texture, Key = key, Width = tex.Width, Height = tex.Height, Png = bytes };
    }

    public byte[]? Png_() => on && loaded != null && (target == null || target.Value.Slug == loaded.Slug) ? loaded.Png : null;

    public object? Info()
    {
        if (!on) return null;
        var l = loaded;
        if (l != null && (target == null || target.Value.Slug == l.Slug))
        {
            var box = TextureBox(ext);
            if (box != null && box.All(double.IsFinite))
                return new JObj().Set("world", l.Slug).Set("version", l.Key).Set("width", l.Width).Set("height", l.Height)
                    .Set("box", new JObj().Set("x0", box[0]).Set("x1", box[1]).Set("y0", box[2]).Set("y1", box[3]));
        }
        if (busy) return new JObj().Set("error", "reading");
        return new JObj().Set("error", error ?? (target != null ? "reading" : "no map yet"));
    }
}

public sealed class RawJson { public readonly string Text; public RawJson(string t) { Text = t; } }

public sealed class LocalMaps
{
    public const int MinMs = 2000, IdleMs = 10000, AnswerMs = 5000, WidgetMs = 8000, Moved = 256, WidgetMax = 480;
    readonly PipClient client;
    public Func<bool> CanAsk = () => false;
    public Func<(double X, double Y)?> Player = () => null;
    public Action? Changed;
    bool enabled = true, awaiting;
    double widgetAt = double.NegativeInfinity, lastAsk = double.NegativeInfinity, lastAnswer = double.NegativeInfinity;
    (double X, double Y)? askedAt;
    sealed class Pic { public int Seq, Width, Height; public double NwX, NwY, NeX, NeY, SwX, SwY; public List<Raster.Level> Levels = new(); }
    Pic? pic;
    int seq, pngFor;
    byte[]? pngBuf;

    public LocalMaps(PipClient c) { client = c; }
    public void SetEnabled(bool on) { enabled = on; if (!enabled) Forget(); }
    public void Forget() { bool had = pic != null; pic = null; pngBuf = null; awaiting = false; if (had) Changed?.Invoke(); }
    public bool Wanted() => enabled && Program.Now() - widgetAt < WidgetMs;
    public void TouchWidget() { bool was = Wanted(); widgetAt = Program.Now(); if (!was) Tick(); }

    public bool Tick()
    {
        double t = Program.Now();
        if (!Wanted() || !CanAsk()) return false;
        if (t - lastAsk < MinMs) return false;
        if (awaiting && t - lastAsk < AnswerMs) return false;
        var p = Player(); var a = askedAt;
        bool moved = a == null || p == null || Math.Sqrt((p.Value.X - a.Value.X) * (p.Value.X - a.Value.X) + (p.Value.Y - a.Value.Y) * (p.Value.Y - a.Value.Y)) >= Moved;
        if (pic != null && !moved && t - lastAnswer < IdleMs) return false;
        lastAsk = t; awaiting = true; askedAt = p;
        client.Send(Command.RequestLocalMapSnapshot);
        return true;
    }

    public void Take(byte[] payload)
    {
        if (!enabled) return;
        LocalMapData m;
        try { m = Codec.DecodeLocalMap(payload); } catch (FormatException e) { Program.Log($"local map: did not decode ({e.Message})"); return; }
        double ax = (m.NeX - m.NwX) / m.Width, ay = (m.NeY - m.NwY) / m.Width, bx = (m.SwX - m.NwX) / m.Height, by = (m.SwY - m.NwY) / m.Height;
        double det = ax * by - ay * bx;
        if (!new[] { m.NwX, m.NwY, m.NeX, m.NeY, m.SwX, m.SwY }.All(double.IsFinite) || !double.IsFinite(det) || Math.Abs(det) < 1e-9) return;
        if (m.Width > 4096 || m.Height > 4096) return;
        var index = Raster.ToIndex(m.Pixels, m.Width, m.Height, grey: true);
        awaiting = false;
        lastAnswer = Program.Now();
        seq++;
        pic = new Pic { Seq = seq, Width = m.Width, Height = m.Height, NwX = m.NwX, NwY = m.NwY, NeX = m.NeX, NeY = m.NeY, SwX = m.SwX, SwY = m.SwY, Levels = Raster.MipLevels(m.Width, m.Height, index, 32, true) };
        pngBuf = null;
        Changed?.Invoke();
    }

    Raster.Level WidgetLevel() => pic!.Levels.FirstOrDefault(l => l.Width <= WidgetMax) ?? pic.Levels[^1];
    public byte[]? Png_()
    {
        if (pic == null) return null;
        if (pngFor != pic.Seq || pngBuf == null) { var l = WidgetLevel(); pngBuf = Png.EncodeIndexed(l.Width, l.Height, l.Index, Tables.FilePalette, Tables.LocalTrns, CompressionLevel.Fastest); pngFor = pic.Seq; }
        return pngBuf;
    }
    public object? Info()
    {
        if (!enabled || pic == null) return null;
        JObj Pt(double x, double y) => new JObj().Set("x", Js.Round(x * 10) / 10).Set("y", Js.Round(y * 10) / 10);
        var l = WidgetLevel();
        return new JObj().Set("seq", pic.Seq).Set("width", l.Width).Set("height", l.Height).Set("nw", Pt(pic.NwX, pic.NwY)).Set("ne", Pt(pic.NeX, pic.NeY)).Set("sw", Pt(pic.SwX, pic.SwY));
    }
}
