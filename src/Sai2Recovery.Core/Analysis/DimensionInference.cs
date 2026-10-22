using Sai2Recovery.Core.Codecs;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Model;

namespace Sai2Recovery.Core.Analysis;

/// <summary>
/// Recovers the exact canvas size from a dense "dpcm" blob (the merged image),
/// without trusting the header:
///  1. tile grid: find the tile count K and columns TilesX for which the tile
///     headers (high byte = column) and row terminators (high byte = TilesX)
///     line up with the length table;
///  2. exact width/height: the codec is byte-exact, so only the true width of
///     the last column (and height of the last row) makes a tile consume its
///     bytes exactly.
/// </summary>
public static class DimensionInference
{
    public sealed record Result(uint Width, uint Height, int Channels, int TilesX, int TilesY);

    public static Result? FromDense(ReadOnlySpan<byte> blob, bool exactEnd, int[] channelCandidates, uint hintWidth = 0, uint hintHeight = 0)
    {
        if (!PixelStream.HasDpcmTag(blob)) return null;
        int maxK = (int)Math.Min(65536, (blob.Length - 4) / 4);
        for (int k = 1; k <= maxK; k++)
        {
            long s = 4 + 4L * k;
            if (s + 2 > blob.Length) break;
            if (blob[(int)s + 1] != 0) continue; // first tile must be column 0
            for (int tx = 1; tx <= Math.Min(255, k); tx++)
            {
                if (k % tx != 0) continue;
                int ty = k / tx;
                var offsets = WalkDense(blob, k, tx, ty, exactEnd);
                if (offsets == null) continue;
                var r = ExactSize(blob, offsets, k, tx, ty, channelCandidates, hintWidth, hintHeight);
                if (r != null) return r;
            }
        }
        return null;
    }

    private static long[]? WalkDense(ReadOnlySpan<byte> blob, int k, int tx, int ty, bool exactEnd)
    {
        var offsets = new long[k];
        long p = 4 + 4L * k;
        for (int y = 0; y < ty; y++)
        {
            for (int x = 0; x < tx; x++)
            {
                int i = y * tx + x;
                if (p + 2 > blob.Length || blob[(int)p + 1] != x) return null;
                uint len = Sai2Format.ReadU32(blob, 4 + 4L * i);
                if (len < 3 || p + len > blob.Length) return null;
                offsets[i] = p;
                p += len;
            }
            if (p + 2 > blob.Length || blob[(int)p + 1] != tx) return null;
            p += 2;
        }
        if (exactEnd && p != blob.Length && !(blob.Length - p < 16 && blob[(int)p..].IndexOfAnyExcept((byte)0) < 0))
            return null;
        return offsets;
    }

    private static ReadOnlySpan<byte> Tile(ReadOnlySpan<byte> blob, long[] offsets, int i) =>
        blob.Slice((int)offsets[i], (int)Sai2Format.ReadU32(blob, 4 + 4L * i));

    /// <summary>
    /// Finds the last-column width and last-row height. A candidate must make every
    /// tile of that column/row consume its bytes exactly. Strict mode (no zero run
    /// past the row end) is tried first: it removes the ambiguity of empty margins.
    /// </summary>
    private static Result? ExactSize(ReadOnlySpan<byte> blob, long[] offsets, int k, int tx, int ty, int[] channels, uint hintW, uint hintH)
    {
        foreach (int ch in channels)
        {
            foreach (bool strict in new[] { true, false })
            {
                // Width: rows of the last-column tiles must be 256 except in the last tile row.
                var widths = new List<(int tw, int lastRows)>();
                for (int tw = 1; tw <= 256; tw++)
                {
                    int rows0 = DpcmCodec.MeasureRows(Tile(blob, offsets, tx - 1), tw, ch, strict);
                    if (rows0 < 1 || (ty > 1 && rows0 != 256)) continue;
                    int lastRows = rows0;
                    bool ok = true;
                    for (int y = 1; y < ty && ok; y++)
                    {
                        int rows = DpcmCodec.MeasureRows(Tile(blob, offsets, y * tx + tx - 1), tw, ch, strict);
                        if (rows < 1 || (y < ty - 1 && rows != 256)) ok = false;
                        lastRows = rows;
                    }
                    if (ok) widths.Add((tw, lastRows));
                }
                if (widths.Count == 0) continue;

                var pick = widths[0];
                foreach (var c in widths)
                    if (hintW > 0 && (uint)((tx - 1) * 256 + c.tw) == hintW) pick = c;
                uint width = (uint)((tx - 1) * 256 + pick.tw);

                // Height: every tile of the last row must agree on the row count.
                int th = pick.lastRows;
                bool rowOk = true;
                for (int x = 0; x < tx - 1 && rowOk; x++)
                {
                    int rows = DpcmCodec.MeasureRows(Tile(blob, offsets, (ty - 1) * tx + x), 256, ch, strict);
                    if (rows != th) rowOk = false;
                }
                if (!rowOk) continue;
                uint height = (uint)((ty - 1) * 256 + th);
                return new Result(width, height, ch, tx, ty);
            }
        }
        return null;
    }
}
