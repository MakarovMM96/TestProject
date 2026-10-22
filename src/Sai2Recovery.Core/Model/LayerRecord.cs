using System.Text;
using Sai2Recovery.Core.Format;

namespace Sai2Recovery.Core.Model;

/// <summary>
/// "layr" chunk, layout per photopea/SAI2-specification:
///   0  "layr"           4  u32 id          8  8 zero bytes
///   16 type tag ("norm","fold","text","shap","liwk")
///   20 4 x s32 (unknown, likely bounds)   36 u32 unknown
///   40 u32 tile count   44 blend tag       48 u32 opacity 0..100
///   52 u32 flags        56 params: (tag, u32 len, bytes)* then u32 0
/// </summary>
public sealed class LayerRecord
{
    public uint Id;
    public string Kind = "norm";
    public uint TileCount;
    public string Blend = "norm";
    public uint Opacity = 100;
    public uint Flags;
    public string? Name;
    public long ParsedLength;
    /// <summary>The blob starts with "layr" + matching id.</summary>
    public bool Recognized;
    /// <summary>The parameter list ends cleanly with a zero tag.</summary>
    public bool ParamsComplete;

    public const int FixedPartSize = 56;

    public static LayerRecord Parse(ReadOnlySpan<byte> blob, uint expectedId)
    {
        var r = new LayerRecord { Id = expectedId };
        if (blob.Length < FixedPartSize) return r;
        if (Sai2Format.ReadU32(blob, 0) != Sai2Format.Layr || Sai2Format.ReadU32(blob, 4) != expectedId) return r;
        r.Recognized = true;
        r.Kind = Sai2Format.TagToString(Sai2Format.ReadU32(blob, 16));
        r.TileCount = Sai2Format.ReadU32(blob, 40);
        r.Blend = Sai2Format.TagToString(Sai2Format.ReadU32(blob, 44));
        r.Opacity = Sai2Format.ReadU32(blob, 48);
        r.Flags = Sai2Format.ReadU32(blob, 52);

        long p = FixedPartSize;
        for (int guard = 0; guard < 256; guard++)
        {
            if (p + 4 > blob.Length) break;
            uint tag = Sai2Format.ReadU32(blob, p);
            if (tag == 0) { r.ParamsComplete = true; p += 4; break; }
            if (p + 8 > blob.Length) break;
            uint len = Sai2Format.ReadU32(blob, p + 4);
            if (!Sai2Format.IsPlausibleTag(tag) || p + 8 + len > blob.Length) break;
            if (tag == Sai2Format.Tag("name")) r.Name = DecodeName(blob.Slice((int)p + 8, (int)len));
            p += 8 + len;
        }
        r.ParsedLength = p;
        return r;
    }

    /// <summary>Reads the id from a blob that starts with "layr" (used when the table is lost).</summary>
    public static bool TryPeekId(ReadOnlySpan<byte> blob, out uint id)
    {
        id = 0;
        if (blob.Length < FixedPartSize || Sai2Format.ReadU32(blob, 0) != Sai2Format.Layr) return false;
        if (Sai2Format.ReadU32(blob, 8) != 0 || Sai2Format.ReadU32(blob, 12) != 0) return false;
        uint kind = Sai2Format.ReadU32(blob, 16);
        if (!Sai2Format.IsPlausibleTag(kind)) return false;
        id = Sai2Format.ReadU32(blob, 4);
        return true;
    }

    private static string DecodeName(ReadOnlySpan<byte> raw)
    {
        // UTF-8 never has NUL bytes inside text, UTF-16LE of Latin/Cyrillic always does,
        // and UTF-16LE of CJK text is almost never valid UTF-8.
        int end = raw.Length;
        while (end > 0 && raw[end - 1] == 0) end--;
        var body = raw[..end];
        if (body.IndexOf((byte)0) < 0)
        {
            try
            {
                string utf8 = new UTF8Encoding(false, true).GetString(body);
                if (!utf8.Any(char.IsControl)) return utf8.Trim();
            }
            catch (DecoderFallbackException)
            {
                // not UTF-8
            }
        }
        var even = raw[..(raw.Length & ~1)];
        return Encoding.Unicode.GetString(even).TrimEnd('\0').Trim();
    }

    /// <summary>Returns a copy of the blob with a new tile count (offset 40).</summary>
    public static byte[] WithTileCount(ReadOnlySpan<byte> blob, uint count)
    {
        var copy = blob.ToArray();
        BitConverter.TryWriteBytes(copy.AsSpan(40, 4), count);
        return copy;
    }
}
