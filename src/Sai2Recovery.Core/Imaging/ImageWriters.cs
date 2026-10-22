using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Sai2Recovery.Core.Imaging;

/// <summary>A decoded image in SAI's native 32-bit BGRA layout (B in the lowest byte).</summary>
public sealed class BgraImage(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint[] Pixels { get; } = new uint[(long)width * height];

    public void Blit(ReadOnlySpan<uint> tile, int tileWidth, int tileHeight, int x0, int y0)
    {
        for (int y = 0; y < tileHeight; y++)
        {
            int dy = y0 + y;
            if (dy >= Height) break;
            int w = Math.Min(tileWidth, Width - x0);
            if (w <= 0) return;
            tile.Slice(y * tileWidth, w).CopyTo(Pixels.AsSpan((int)((long)dy * Width + x0), w));
        }
    }

    public void Fill(int x0, int y0, int w, int h, uint value)
    {
        for (int y = y0; y < Math.Min(Height, y0 + h); y++)
            Pixels.AsSpan(y * Width + x0, Math.Min(w, Width - x0)).Fill(value);
    }

    /// <summary>
    /// SAI keeps colour premultiplied by alpha. If every pixel satisfies
    /// colour &lt;= alpha we treat the data as premultiplied and convert it to
    /// straight alpha for PNG. Returns true if a conversion happened.
    /// </summary>
    public bool UnpremultiplyIfNeeded()
    {
        bool anyTranslucent = false;
        foreach (uint p in Pixels)
        {
            uint a = p >> 24;
            if (a == 255) continue;
            if ((p & 0xFF) > a || ((p >> 8) & 0xFF) > a || ((p >> 16) & 0xFF) > a) return false;
            if (a > 0) anyTranslucent = true;
        }
        if (!anyTranslucent) return false;
        for (int i = 0; i < Pixels.Length; i++)
        {
            uint p = Pixels[i];
            uint a = p >> 24;
            if (a == 0 || a == 255) continue;
            uint b = Math.Min(255u, ((p & 0xFF) * 255 + a / 2) / a);
            uint g = Math.Min(255u, (((p >> 8) & 0xFF) * 255 + a / 2) / a);
            uint r = Math.Min(255u, (((p >> 16) & 0xFF) * 255 + a / 2) / a);
            Pixels[i] = b | (g << 8) | (r << 16) | (a << 24);
        }
        return true;
    }
}

public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc ^= 0xFFFFFFFFu;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)data.Length);
        s.Write(buf);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc32(data, Crc32(typeBytes));
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    /// <summary>Writes an 8-bit RGBA PNG from BGRA pixels.</summary>
    public static void Write(Stream output, BgraImage image)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)image.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // RGBA
        Chunk(output, "IHDR", ihdr);

        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            {
                var row = new byte[1 + image.Width * 4];
                var prev = new byte[row.Length];
                var raw = new byte[row.Length];
                for (int y = 0; y < image.Height; y++)
                {
                    // Filter 2 (Up): cheap and compresses painted images well
                    raw[0] = 0;
                    for (int x = 0; x < image.Width; x++)
                    {
                        uint p = image.Pixels[(long)y * image.Width + x];
                        int o = 1 + x * 4;
                        raw[o] = (byte)(p >> 16);
                        raw[o + 1] = (byte)(p >> 8);
                        raw[o + 2] = (byte)p;
                        raw[o + 3] = (byte)(p >> 24);
                    }
                    row[0] = 2;
                    for (int i = 1; i < row.Length; i++) row[i] = (byte)(raw[i] - prev[i]);
                    z.Write(row);
                    (prev, raw) = (raw, prev);
                }
            }
            Chunk(output, "IDAT", idat.ToArray());
        }
        Chunk(output, "IEND", ReadOnlySpan<byte>.Empty);
    }

    public static void Save(string path, BgraImage image)
    {
        using var fs = File.Create(path);
        Write(fs, image);
    }

    public static byte[] ToBytes(BgraImage image)
    {
        using var ms = new MemoryStream();
        Write(ms, image);
        return ms.ToArray();
    }
}

public sealed record OraLayer(string Name, BgraImage Image, double Opacity, bool Visible);

/// <summary>
/// OpenRaster (.ora) writer: a zip with stack.xml and PNG layers. Opens in
/// Krita, GIMP, MyPaint and others with layers intact.
/// </summary>
public static class OraWriter
{
    /// <param name="layersBottomToTop">Layers in painting order (first = bottom).</param>
    public static void Save(string path, int width, int height, IReadOnlyList<OraLayer> layersBottomToTop, BgraImage merged)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var s = mime.Open()) s.Write("image/openraster"u8);

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append($"<image version=\"0.0.5\" w=\"{width}\" h=\"{height}\">\n<stack>\n");
        for (int i = layersBottomToTop.Count - 1; i >= 0; i--)
        {
            var l = layersBottomToTop[i];
            string file = $"data/layer{i:D3}.png";
            sb.Append($"  <layer name=\"{Escape(l.Name)}\" src=\"{file}\" x=\"0\" y=\"0\" opacity=\"{l.Opacity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}\" visibility=\"{(l.Visible ? "visible" : "hidden")}\"/>\n");
            var e = zip.CreateEntry(file, CompressionLevel.NoCompression);
            using var s = e.Open();
            PngWriter.Write(s, l.Image);
        }
        sb.Append("</stack>\n</image>\n");
        var stack = zip.CreateEntry("stack.xml");
        using (var s = stack.Open()) s.Write(Encoding.UTF8.GetBytes(sb.ToString()));

        var mergedEntry = zip.CreateEntry("mergedimage.png", CompressionLevel.NoCompression);
        using (var s = mergedEntry.Open()) PngWriter.Write(s, merged);

        var thumb = Downscale(merged, 256);
        var thumbEntry = zip.CreateEntry("Thumbnails/thumbnail.png", CompressionLevel.NoCompression);
        using (var s = thumbEntry.Open()) PngWriter.Write(s, thumb);
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static BgraImage Downscale(BgraImage src, int maxSide)
    {
        double k = Math.Min(1.0, (double)maxSide / Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
        var dst = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                dst.Pixels[y * w + x] = src.Pixels[(long)Math.Min(src.Height - 1, (int)(y / k)) * src.Width + Math.Min(src.Width - 1, (int)(x / k))];
        return dst;
    }
}
