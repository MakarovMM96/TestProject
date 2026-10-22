using Sai2Recovery.Core.Format;

namespace Sai2Recovery.Core.Model;

public enum Severity { Info, Warning, Error, Fatal }

public sealed record Issue(Severity Severity, string Message);

public enum ChunkHealth
{
    /// <summary>Verified (or unverifiable but plausible).</summary>
    Ok,
    /// <summary>Partly damaged; repair keeps what is readable.</summary>
    Damaged,
    /// <summary>Unreadable or beyond the end of the file.</summary>
    Lost,
}

public sealed class ChunkInfo
{
    /// <summary>Index in the original table, -1 if the chunk was found by scanning.</summary>
    public int TableIndex = -1;
    public uint Type;
    public uint Id;
    /// <summary>Offset relative to the start of the SAI2 data.</summary>
    public long Offset;
    /// <summary>Bytes available for this chunk (to the next chunk or EOF).</summary>
    public long Length;
    /// <summary>True if Length is known exactly (next chunk offset or a self-delimiting format).</summary>
    public bool ExactLength;
    public ChunkHealth Health = ChunkHealth.Ok;
    /// <summary>Whether the content could be checked (pixel data, layer records, jssf).</summary>
    public bool Verified;
    public string? Note;
    public PixelStream? Pixels;
    public LayerRecord? Layer;
    public bool Repositioned;

    public string TypeName => Sai2Format.TagToString(Type);

    public ReadOnlySpan<byte> Bytes(byte[] data) =>
        Offset >= 0 && Offset < data.Length
            ? data.AsSpan((int)Offset, (int)Math.Min(Length, data.Length - Offset))
            : ReadOnlySpan<byte>.Empty;

    public override string ToString() => $"{TypeName}#{Id} @{Offset} ({Length} байт) {Health}";
}

public enum Verdict
{
    /// <summary>No problems found.</summary>
    Healthy,
    /// <summary>Problems found, all fixable without losing image data.</summary>
    Repairable,
    /// <summary>Repair possible, but some image data is lost.</summary>
    PartiallyRecoverable,
    /// <summary>Nothing usable in this file.</summary>
    Unrecoverable,
}

public sealed class Sai2Analysis
{
    public string? SourcePath;
    /// <summary>SAI2 data starting at its header (may be a slice of the original file).</summary>
    public byte[] Data = [];
    public long BaseOffset;
    public long OriginalLength;

    public Sai2Header? OriginalHeader;
    /// <summary>Header with every correction applied; used for repair.</summary>
    public Sai2Header Header = Sai2Header.CreateDefault(1, 1, 4);
    public bool MagicIntact;
    public bool HeaderUsable;
    public bool TableRebuilt;
    public bool DimensionsInferred;
    public uint DeclaredTableCount;
    /// <summary>Layer records keep their tile count at offset 40 (confirmed on this file).</summary>
    public bool LayerTileCountConfirmed;
    /// <summary>"layr" chunks in this file follow the documented layout.</summary>
    public bool LayerLayoutConfirmed;

    public List<ChunkInfo> Chunks = [];
    public List<Issue> Issues = [];
    public List<(long Start, long Length)> ZeroRegions = [];
    public long LastNonZero;
    public Verdict Verdict;

    public IEnumerable<ChunkInfo> Layers => Chunks.Where(c => c.Type == Sai2Format.Layr);
    public ChunkInfo? Composite => Chunks.FirstOrDefault(c => c.Type == Sai2Format.Intg);
    public ChunkInfo? Thumbnail => Chunks.FirstOrDefault(c => c.Type == Sai2Format.Thum);

    public ChunkInfo? PixelsOf(uint id) =>
        Chunks.FirstOrDefault(c => c.Id == id && (c.Type == Sai2Format.Lpix || c.Type == Sai2Format.Fpix));

    public void Add(Severity s, string message) => Issues.Add(new Issue(s, message));

    public bool HasErrors => Issues.Any(i => i.Severity >= Severity.Error);
}
