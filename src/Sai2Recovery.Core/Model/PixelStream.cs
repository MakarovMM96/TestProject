using Sai2Recovery.Core.Codecs;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Imaging;

namespace Sai2Recovery.Core.Model;

public enum TileState
{
    Ok,
    /// <summary>Header/data present but does not decode; some rows may still be usable.</summary>
    Corrupt,
    /// <summary>Data is beyond the end of the file or unreachable.</summary>
    Missing,
}

public sealed class TileRecord
{
    public int X;
    public int Y;
    /// <summary>Index in the lengths table.</summary>
    public int Index;
    /// <summary>Offset of the tile (its 2-byte header) relative to the blob start; -1 if unknown.</summary>
    public long Offset = -1;
    public int Length;
    public ushort Header;
    public TileState State;
    public int GoodRows;
    public string? Error;
}

/// <summary>
/// Parsed "dpcm" pixel blob:
///   "dpcm", u32 length[K], then for every tile row: tiles (each starts with a
///   u16 whose high byte is the tile X index), followed by a u16 row terminator
///   whose high byte equals TilesX.
/// "intg" stores every tile (dense, K = TilesX*TilesY). Layer pixels may skip
/// empty tiles (sparse); positions are then taken from the tile headers.
/// </summary>
public sealed class PixelStream
{
    public int TilesX;
    public int TilesY;
    public int Width;
    public int Height;
    public int Channels;
    public bool Dense;
    public int LengthCount;
    public List<TileRecord> Tiles = [];
    /// <summary>Row terminators as read (null = missing).</summary>
    public ushort?[] Terminators = [];
    /// <summary>End of the parsed data relative to blob start (exact if StructureOk).</summary>
    public long ParsedLength;
    public bool StructureOk;
    public string? StructureError;

    public int CorruptTiles => Tiles.Count(t => t.State == TileState.Corrupt);
    public int MissingTiles => Tiles.Count(t => t.State == TileState.Missing) + Math.Max(0, LengthCount - Tiles.Count);
    public bool IsHealthy => StructureOk && Tiles.Count == LengthCount && Tiles.All(t => t.State == TileState.Ok);

    public (int w, int h) TileSize(int x, int y) => (
        Math.Min(Sai2Format.TileSize, Width - x * Sai2Format.TileSize),
        Math.Min(Sai2Format.TileSize, Height - y * Sai2Format.TileSize));

    public static bool HasDpcmTag(ReadOnlySpan<byte> blob) =>
        blob.Length >= 4 && Sai2Format.ReadU32(blob, 0) == Sai2Format.Dpcm;

    /// <summary>
    /// Parses and validates a pixel blob.
    /// </summary>
    /// <param name="blob">Blob bytes (from the chunk offset to the next chunk or EOF).</param>
    /// <param name="exactEnd">True if blob.Length is the true chunk size (not just "until EOF").</param>
    /// <param name="expectedCount">Tile count if known (dense intg, or from the layer record).</param>
    /// <param name="channelCandidates">Channel counts to try, most likely first.</param>
    public static PixelStream Parse(ReadOnlySpan<byte> blob, int width, int height, bool exactEnd,
        int? expectedCount, int[] channelCandidates, bool decode = true)
    {
        var ps = new PixelStream
        {
            Width = width,
            Height = height,
            TilesX = Sai2Format.TilesFor((uint)width),
            TilesY = Sai2Format.TilesFor((uint)height),
        };
        ps.Terminators = new ushort?[ps.TilesY];
        int total = ps.TilesX * ps.TilesY;

        if (!HasDpcmTag(blob))
        {
            ps.StructureError = "нет сигнатуры dpcm";
            return ps;
        }

        int k = expectedCount ?? -1;
        if (k < 0 || k > total || 4 + 4L * k > blob.Length)
        {
            k = GuessLengthCount(blob, ps, exactEnd);
            if (k < 0)
            {
                ps.StructureError = "не удалось определить таблицу длин плиток";
                return ps;
            }
        }
        ps.LengthCount = k;
        ps.Dense = k == total;

        var lengths = new int[k];
        for (int i = 0; i < k; i++) lengths[i] = (int)Math.Min(int.MaxValue, Sai2Format.ReadU32(blob, 4 + 4L * i));

        if (ps.Dense) WalkDense(blob, ps, lengths, exactEnd);
        else WalkSparse(blob, ps, lengths);

        if (decode) ps.Validate(blob, channelCandidates);
        return ps;
    }

    /// <summary>Quick structural walk without decoding (used to pick K).</summary>
    private static bool WalkCheck(ReadOnlySpan<byte> blob, PixelStream ps, int k, bool exactEnd, bool allowTruncated = false)
    {
        long p = 4 + 4L * k;
        int y = 0, idx = 0, lastX = -1;
        while (y < ps.TilesY)
        {
            if (p + 2 > blob.Length) return allowTruncated && idx > 0;
            int hi = blob[(int)p + 1];
            if (hi == ps.TilesX) { y++; p += 2; lastX = -1; continue; }
            if (idx >= k || hi >= ps.TilesX || hi <= lastX) return false;
            uint len = Sai2Format.ReadU32(blob, 4 + 4L * idx);
            if (len < 3) return false;
            if (p + len > blob.Length) return allowTruncated && len < 4u * 256 * 256 * 2;
            p += len;
            idx++;
            lastX = hi;
        }
        if (idx != k) return false;
        return !exactEnd || p == blob.Length || (blob.Length - p < 16 && IsZero(blob[(int)p..]));
    }

    private static bool IsZero(ReadOnlySpan<byte> s) => s.IndexOfAnyExcept((byte)0) < 0;

    private static int GuessLengthCount(ReadOnlySpan<byte> blob, PixelStream ps, bool exactEnd)
    {
        int total = ps.TilesX * ps.TilesY;
        int maxK = (int)Math.Min(total, (blob.Length - 4) / 4);
        // Prefer the dense layout, then any count that walks cleanly.
        if (total <= maxK && WalkCheck(blob, ps, total, exactEnd)) return total;
        for (int k = 0; k <= maxK; k++)
            if (k != total && WalkCheck(blob, ps, k, exactEnd)) return k;
        if (exactEnd)
        {
            if (total <= maxK && WalkCheck(blob, ps, total, false)) return total;
            for (int k = 0; k <= maxK; k++)
                if (k != total && WalkCheck(blob, ps, k, false)) return k;
        }
        // Truncated stream: accept a table size whose walk stays consistent until the data runs out.
        if (total <= maxK && WalkCheck(blob, ps, total, false, allowTruncated: true)) return total;
        for (int k = 1; k <= maxK; k++)
            if (k != total && WalkCheck(blob, ps, k, false, allowTruncated: true)) return k;
        return -1;
    }

    /// <summary>Dense walk: positions known, lengths drive it, resync from the end if a length is broken.</summary>
    private static void WalkDense(ReadOnlySpan<byte> blob, PixelStream ps, int[] lengths, bool exactEnd)
    {
        long p = 4 + 4L * lengths.Length;
        bool lost = false;
        for (int y = 0; y < ps.TilesY; y++)
        {
            for (int x = 0; x < ps.TilesX; x++)
            {
                int i = y * ps.TilesX + x;
                var t = new TileRecord { X = x, Y = y, Index = i, Length = lengths[i] };
                ps.Tiles.Add(t);
                if (lost) { t.State = TileState.Missing; continue; }

                long len = lengths[i];
                bool lenOk = len >= 3 && p + len <= blob.Length;
                if (!lenOk && exactEnd)
                {
                    // Resync: everything after this tile has known sizes, so measure back from the end.
                    long after = 0;
                    for (int j = i + 1; j < lengths.Length; j++) after += lengths[j];
                    after += 2L * (ps.TilesY - y);
                    long end = blob.Length - after;
                    if (end - p >= 3)
                    {
                        len = end - p;
                        lenOk = true;
                        t.Length = (int)len;
                        t.Error = "длина плитки в таблице повреждена, восстановлена по соседям";
                    }
                }
                if (!lenOk)
                {
                    if (p + 2 <= blob.Length)
                    {
                        t.Offset = p;
                        t.Length = (int)Math.Max(0, blob.Length - p);
                        t.Header = Sai2Format.ReadU16(blob, p);
                        t.State = TileState.Corrupt;
                        t.Error = "плитка обрезана";
                    }
                    else t.State = TileState.Missing;
                    lost = true;
                    continue;
                }
                t.Offset = p;
                t.Header = Sai2Format.ReadU16(blob, p);
                if ((t.Header >> 8) != x)
                {
                    t.State = TileState.Corrupt;
                    t.Error = "неверный заголовок плитки";
                }
                p += len;
            }
            if (!lost && p + 2 <= blob.Length)
            {
                ushort term = Sai2Format.ReadU16(blob, p);
                ps.Terminators[y] = term;
                p += 2;
                if ((term >> 8) != ps.TilesX)
                {
                    ps.StructureError ??= $"повреждён разделитель ряда {y}";
                }
            }
            else
            {
                lost = true;
            }
        }
        ps.ParsedLength = Math.Min(p, blob.Length);
        ps.StructureOk = !lost && ps.StructureError == null;
        if (lost) ps.StructureError ??= "данные плиток обрываются (файл обрезан)";
    }

    /// <summary>Sparse walk: positions from tile headers.</summary>
    private static void WalkSparse(ReadOnlySpan<byte> blob, PixelStream ps, int[] lengths)
    {
        long p = 4 + 4L * lengths.Length;
        int y = 0, idx = 0, lastX = -1;
        while (y < ps.TilesY)
        {
            if (p + 2 > blob.Length) { ps.StructureError = "данные плиток обрываются (файл обрезан)"; break; }
            ushort hdr = Sai2Format.ReadU16(blob, p);
            int hi = hdr >> 8;
            if (hi == ps.TilesX) { ps.Terminators[y] = hdr; y++; p += 2; lastX = -1; continue; }
            if (idx >= lengths.Length || hi >= ps.TilesX || hi <= lastX)
            {
                ps.StructureError = "нарушен порядок плиток";
                break;
            }
            var t = new TileRecord { X = hi, Y = y, Index = idx, Offset = p, Length = lengths[idx], Header = hdr };
            ps.Tiles.Add(t);
            if (t.Length < 3 || p + t.Length > blob.Length)
            {
                t.State = TileState.Corrupt;
                t.Length = (int)(blob.Length - p);
                t.Error = "плитка обрезана";
                ps.StructureError = "данные плиток обрываются (файл обрезан)";
                p = blob.Length;
                idx++;
                break;
            }
            p += t.Length;
            idx++;
            lastX = hi;
        }
        ps.ParsedLength = Math.Min(p, blob.Length);
        ps.StructureOk = ps.StructureError == null && idx == lengths.Length && y == ps.TilesY;
        if (!ps.StructureOk) ps.StructureError ??= "структура плиток нарушена";
    }

    /// <summary>Decodes every tile to verify it, choosing the channel count on the first good tile.</summary>
    private void Validate(ReadOnlySpan<byte> blob, int[] channelCandidates)
    {
        Channels = channelCandidates.Length > 0 ? channelCandidates[0] : 4;
        var buf = new uint[Sai2Format.TileSize * Sai2Format.TileSize];
        bool chosen = false;
        foreach (var t in Tiles)
        {
            if (t.Offset < 0 || t.State == TileState.Missing) continue;
            var (tw, th) = TileSize(t.X, t.Y);
            var data = blob.Slice((int)t.Offset, t.Length);
            if (!chosen && t.State == TileState.Ok)
            {
                foreach (int ch in channelCandidates)
                {
                    if (DpcmCodec.DecodeTile(data, tw, th, ch, buf).Ok) { Channels = ch; chosen = true; break; }
                }
            }
            var r = DpcmCodec.DecodeTile(data, tw, th, Channels, buf);
            t.GoodRows = r.GoodRows;
            if (!r.Ok)
            {
                t.State = TileState.Corrupt;
                t.Error ??= r.Error;
            }
        }
    }

    /// <summary>Renders the stream into an image. Damaged areas get <paramref name="damageFill"/>.</summary>
    public BgraImage Render(ReadOnlySpan<byte> blob, uint emptyFill, uint damageFill)
    {
        var img = new BgraImage(Width, Height);
        if (emptyFill != 0) Array.Fill(img.Pixels, emptyFill);
        var buf = new uint[Sai2Format.TileSize * Sai2Format.TileSize];
        foreach (var t in Tiles)
        {
            var (tw, th) = TileSize(t.X, t.Y);
            if (t.X >= TilesX || t.Y >= TilesY) continue;
            int good = 0;
            if (t.Offset >= 0 && t.Length > 0 && t.Offset + t.Length <= blob.Length)
            {
                Array.Clear(buf);
                var r = DpcmCodec.DecodeTile(blob.Slice((int)t.Offset, t.Length), tw, th, Channels, buf);
                good = r.GoodRows;
                if (good > 0) img.Blit(buf, tw, good, t.X * Sai2Format.TileSize, t.Y * Sai2Format.TileSize);
            }
            if (good < th)
                img.Fill(t.X * Sai2Format.TileSize, t.Y * Sai2Format.TileSize + good, tw, th - good, damageFill);
        }
        return img;
    }

    /// <summary>
    /// Re-encodes the stream: good tiles are copied byte for byte, damaged tiles
    /// keep their decodable rows and get <paramref name="fill"/> for the rest,
    /// and tiles lost at the end are recreated as blank tiles so the tile count
    /// stays the same.
    /// </summary>
    public byte[] Rebuild(ReadOnlySpan<byte> blob, uint fill, out int replacedTiles, out int tileCount)
    {
        replacedTiles = 0;
        var records = new List<TileRecord>(Tiles);

        // Sparse streams that lost their tail: put blank tiles on the next free positions.
        if (!Dense && records.Count < LengthCount)
        {
            int last = records.Count > 0 ? records[^1].Y * TilesX + records[^1].X : -1;
            int need = LengthCount - records.Count;
            for (int pos = last + 1; pos < TilesX * TilesY && need > 0; pos++, need--)
                records.Add(new TileRecord { X = pos % TilesX, Y = pos / TilesX, Index = records.Count, State = TileState.Missing });
        }

        tileCount = records.Count;
        var tileBytes = new List<byte[]>(records.Count);
        var buf = new uint[Sai2Format.TileSize * Sai2Format.TileSize];
        foreach (var t in records)
        {
            var (tw, th) = TileSize(t.X, t.Y);
            if (t.State == TileState.Ok && t.Offset >= 0)
            {
                tileBytes.Add(blob.Slice((int)t.Offset, t.Length).ToArray());
                continue;
            }
            replacedTiles++;
            Array.Fill(buf, fill);
            if (t.State == TileState.Corrupt && t.Offset >= 0 && t.Length > 2 && t.Offset + t.Length <= blob.Length)
            {
                var tmp = new uint[tw * th];
                var r = DpcmCodec.DecodeTile(blob.Slice((int)t.Offset, t.Length), tw, th, Channels, tmp);
                Array.Fill(buf, fill, 0, tw * th);
                Array.Copy(tmp, 0, buf, 0, r.GoodRows * tw);
            }
            ushort header = (t.Header >> 8) == t.X ? t.Header : (ushort)(t.X << 8);
            tileBytes.Add(DpcmCodec.EncodeTile(buf.AsSpan(0, tw * th), tw, th, Channels, header));
        }

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Sai2Format.Dpcm);
        foreach (var tb in tileBytes) w.Write((uint)tb.Length);
        int ti = 0;
        for (int y = 0; y < TilesY; y++)
        {
            while (ti < records.Count && records[ti].Y == y)
            {
                w.Write(tileBytes[ti]);
                ti++;
            }
            ushort term = Terminators.Length > y && Terminators[y] is ushort tv && (tv >> 8) == TilesX
                ? tv
                : (ushort)((TilesX << 8) | (y & 0xFF));
            w.Write(term);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>A fresh blob with blank tiles (used when the whole pixel chunk is lost).</summary>
    public static byte[] CreateBlank(int width, int height, int channels, uint fill)
    {
        int tx = Sai2Format.TilesFor((uint)width), ty = Sai2Format.TilesFor((uint)height);
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        var tiles = new List<byte[]>();
        for (int y = 0; y < ty; y++)
            for (int x = 0; x < tx; x++)
            {
                int tw = Math.Min(256, width - x * 256), th = Math.Min(256, height - y * 256);
                tiles.Add(DpcmCodec.EncodeSolidTile(tw, th, channels, fill, (ushort)(x << 8)));
            }
        w.Write(Sai2Format.Dpcm);
        foreach (var t in tiles) w.Write((uint)t.Length);
        int i = 0;
        for (int y = 0; y < ty; y++)
        {
            for (int x = 0; x < tx; x++) w.Write(tiles[i++]);
            w.Write((ushort)((tx << 8) | (y & 0xFF)));
        }
        w.Flush();
        return ms.ToArray();
    }
}
