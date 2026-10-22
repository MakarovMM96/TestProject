using System.Buffers.Binary;
using System.Text;

namespace Sai2Recovery.Core.Format;

/// <summary>
/// Constants and helpers for the PaintTool SAI Ver.2 (.sai2) container.
/// Sources: photopea/SAI2-specification and Wunkolo/libsai (MIT).
/// </summary>
public static class Sai2Format
{
    public const int HeaderSize = 64;
    public const int EntrySize = 16;
    public const int TileSize = 256;

    public static ReadOnlySpan<byte> Magic => "SAI-CANVAS-TYPE0"u8;

    public static uint Tag(string s)
    {
        if (s.Length != 4) throw new ArgumentException("Tag must be 4 characters", nameof(s));
        return (uint)(s[0] | (s[1] << 8) | (s[2] << 16) | (s[3] << 24));
    }

    public static readonly uint Hist = Tag("hist");
    public static readonly uint Thum = Tag("thum");
    public static readonly uint Intg = Tag("intg");
    public static readonly uint Layr = Tag("layr");
    public static readonly uint Mask = Tag("mask");
    public static readonly uint Lpix = Tag("lpix");
    public static readonly uint Fpix = Tag("fpix");
    public static readonly uint Mpix = Tag("mpix");
    public static readonly uint Liwk = Tag("liwk");
    public static readonly uint Shap = Tag("shap");
    public static readonly uint Pers = Tag("pers");
    public static readonly uint Text = Tag("text");
    public static readonly uint Grid = Tag("grid");
    public static readonly uint Symm = Tag("symm");

    public static readonly uint Dpcm = Tag("dpcm");
    public static readonly uint Jssf = Tag("jssf");
    public static readonly uint Raw = (uint)('r' | ('a' << 8) | ('w' << 16));

    private static readonly HashSet<uint> KnownChunkTypes =
    [
        Hist, Thum, Intg, Layr, Mask, Lpix, Fpix, Mpix, Liwk, Shap, Pers, Text, Grid, Symm
    ];

    public static bool IsKnownChunkType(uint tag) => KnownChunkTypes.Contains(tag);

    /// <summary>Chunk types holding tiled pixel data that belongs to a layer/mask id.</summary>
    public static bool IsPixelChunk(uint tag) => tag == Lpix || tag == Fpix || tag == Mpix;

    /// <summary>Four lowercase ASCII letters/digits: looks like a chunk tag we do not know yet.</summary>
    public static bool IsPlausibleTag(uint tag)
    {
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((tag >> (8 * i)) & 0xFF);
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))) return false;
        }
        return true;
    }

    public static string TagToString(uint tag)
    {
        var sb = new StringBuilder(4);
        for (int i = 0; i < 4; i++)
        {
            char c = (char)((tag >> (8 * i)) & 0xFF);
            if (c < 0x20 || c > 0x7E) return $"0x{tag:X8}";
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static string DescribeChunk(uint tag)
    {
        if (tag == Hist) return "история";
        if (tag == Thum) return "миниатюра";
        if (tag == Intg) return "сведённое изображение";
        if (tag == Layr) return "описание слоя";
        if (tag == Mask) return "маска";
        if (tag == Lpix) return "пиксели слоя";
        if (tag == Fpix) return "пиксели папки";
        if (tag == Mpix) return "пиксели маски";
        if (tag == Liwk) return "линии (linework)";
        if (tag == Shap) return "фигура";
        if (tag == Pers) return "перспектива";
        if (tag == Text) return "текст";
        if (tag == Grid) return "сетка";
        if (tag == Symm) return "симметрия";
        return "неизвестный блок";
    }

    public static int TilesFor(uint pixels) => (int)((pixels + TileSize - 1) / TileSize);

    public static uint ReadU32(ReadOnlySpan<byte> d, long offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(d.Slice((int)offset, 4));

    public static ushort ReadU16(ReadOnlySpan<byte> d, long offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(d.Slice((int)offset, 2));

    public static bool HasMagicAt(ReadOnlySpan<byte> data, long offset) =>
        offset >= 0 && offset + 16 <= data.Length && data.Slice((int)offset, 16).SequenceEqual(Magic);

    /// <summary>Finds every occurrence of the SAI2 magic in a buffer.</summary>
    public static List<long> FindMagic(ReadOnlySpan<byte> data, long baseOffset = 0)
    {
        var found = new List<long>();
        int start = 0;
        while (start <= data.Length - 16)
        {
            int idx = data[start..].IndexOf(Magic);
            if (idx < 0) break;
            found.Add(baseOffset + start + idx);
            start += idx + 1;
        }
        return found;
    }
}
