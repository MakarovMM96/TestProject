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

    private static Result? ExactSize(ReadOnlySpan<byte> blob, long[] offsets, int k, int tx, int ty, int[] channels, uint hintW, uint hintH)
    {
        foreach (int ch in channels)
        {
            // Width from the last column of the first tile row.
            int iLast = tx - 1;
            var lastTile = blob.Slice((int)offsets[iLast], (int)Sai2Format.ReadU32(blob, 4 + 4L * iLast));
            var widthCandidates = new List<(int tw, int rows)>();
            for (int tw = 1; tw <= 256; tw++)
            {
                int rows = DpcmCodec.MeasureRows(lastTile, tw, ch);
                if (rows < 1) continue;
                if (ty > 1 && rows != 256) continue;
                widthCandidates.Add((tw, rows));
            }
            if (widthCandidates.Count == 0) continue;

            var pick = widthCandidates[0];
            if (hintW > 0)
            {
                foreach (var c in widthCandidates)
                    if ((uint)((tx - 1) * 256 + c.tw) == hintW) pick = c;
            }
            else if (widthCandidates.Count > 1)
            {
                // Prefer the widest exact fit that decodes the full tile.
                pick = widthCandidates[^1];
            }
            uint width = (uint)((tx - 1) * 256 + pick.tw);

            uint height;
            if (ty == 1)
            {
                height = (uint)pick.rows;
            }
            else
            {
                int iRow = (ty - 1) * tx;
                var rowTile = blob.Slice((int)offsets[iRow], (int)Sai2Format.ReadU32(blob, 4 + 4L * iRow));
                int tw0 = tx > 1 ? 256 : pick.tw;
                int rows = DpcmCodec.MeasureRows(rowTile, tw0, ch);
                if (rows < 1) continue;
                height = (uint)((ty - 1) * 256 + rows);
            }
            if (hintH > 0 && ty == 1 && widthCandidates.Any(c => c.tw == pick.tw && (uint)c.rows == hintH)) height = hintH;
            return new Result(width, height, ch, tx, ty);
        }
        return null;
    }
}
