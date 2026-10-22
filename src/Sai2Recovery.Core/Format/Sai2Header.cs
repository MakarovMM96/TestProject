using System.Buffers.Binary;

namespace Sai2Recovery.Core.Format;

/// <summary>64-byte canvas header (layout from libsai's sai2::CanvasHeader).</summary>
public sealed class Sai2Header
{
    public byte Flags0;
    /// <summary>(flags &amp; 7) == 0 means the canvas has a transparent background (4-channel previews).</summary>
    public byte BackgroundFlags;
    public byte Flags2;
    public byte Flags3;
    public uint Width;
    public uint Height;
    public uint Resolution;
    public uint TableCount;
    public uint SelectedLayer;
    public ulong UnknownA;
    public ulong UnknownB;
    public uint BackgroundColor;
    public uint EffectTag;

    public int PreviewChannels => (BackgroundFlags & 7) == 0 ? 4 : 3;

    public static Sai2Header Read(ReadOnlySpan<byte> d)
    {
        if (d.Length < Sai2Format.HeaderSize) throw new ArgumentException("Header too short");
        return new Sai2Header
        {
            Flags0 = d[16],
            BackgroundFlags = d[17],
            Flags2 = d[18],
            Flags3 = d[19],
            Width = BinaryPrimitives.ReadUInt32LittleEndian(d[20..]),
            Height = BinaryPrimitives.ReadUInt32LittleEndian(d[24..]),
            Resolution = BinaryPrimitives.ReadUInt32LittleEndian(d[28..]),
            TableCount = BinaryPrimitives.ReadUInt32LittleEndian(d[32..]),
            SelectedLayer = BinaryPrimitives.ReadUInt32LittleEndian(d[36..]),
            UnknownA = BinaryPrimitives.ReadUInt64LittleEndian(d[40..]),
            UnknownB = BinaryPrimitives.ReadUInt64LittleEndian(d[48..]),
            BackgroundColor = BinaryPrimitives.ReadUInt32LittleEndian(d[56..]),
            EffectTag = BinaryPrimitives.ReadUInt32LittleEndian(d[60..]),
        };
    }

    public void Write(Span<byte> d)
    {
        Sai2Format.Magic.CopyTo(d);
        d[16] = Flags0;
        d[17] = BackgroundFlags;
        d[18] = Flags2;
        d[19] = Flags3;
        BinaryPrimitives.WriteUInt32LittleEndian(d[20..], Width);
        BinaryPrimitives.WriteUInt32LittleEndian(d[24..], Height);
        BinaryPrimitives.WriteUInt32LittleEndian(d[28..], Resolution);
        BinaryPrimitives.WriteUInt32LittleEndian(d[32..], TableCount);
        BinaryPrimitives.WriteUInt32LittleEndian(d[36..], SelectedLayer);
        BinaryPrimitives.WriteUInt64LittleEndian(d[40..], UnknownA);
        BinaryPrimitives.WriteUInt64LittleEndian(d[48..], UnknownB);
        BinaryPrimitives.WriteUInt32LittleEndian(d[56..], BackgroundColor);
        BinaryPrimitives.WriteUInt32LittleEndian(d[60..], EffectTag);
    }

    public Sai2Header Clone() => (Sai2Header)MemberwiseClone();

    /// <summary>Header used when the original one is destroyed beyond use.</summary>
    public static Sai2Header CreateDefault(uint width, uint height, int previewChannels) => new()
    {
        Flags0 = 0,
        BackgroundFlags = (byte)(previewChannels == 4 ? 0 : 1),
        Flags2 = 0,
        Flags3 = 0,
        Width = width,
        Height = height,
        Resolution = 350,
        TableCount = 0,
        SelectedLayer = 0,
        BackgroundColor = 0xFF808080u,
        EffectTag = Sai2Format.Tag("norm"),
    };

    public static bool IsPlausibleSize(uint w, uint h) => w >= 1 && h >= 1 && w <= 100_000 && h <= 100_000;
}

/// <summary>16-byte table entry: chunk type, object id, absolute 64-bit offset.</summary>
public readonly record struct TableEntry(uint Type, uint Id, ulong Offset)
{
    public static TableEntry Read(ReadOnlySpan<byte> d) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(d),
        BinaryPrimitives.ReadUInt32LittleEndian(d[4..]),
        BinaryPrimitives.ReadUInt64LittleEndian(d[8..]));

    public void Write(Span<byte> d)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, Type);
        BinaryPrimitives.WriteUInt32LittleEndian(d[4..], Id);
        BinaryPrimitives.WriteUInt64LittleEndian(d[8..], Offset);
    }
}
