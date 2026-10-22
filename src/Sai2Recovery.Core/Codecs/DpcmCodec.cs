using System.Buffers.Binary;
using System.Numerics;

namespace Sai2Recovery.Core.Codecs;

/// <summary>
/// SAI2 "dpcm" tile codec: per-row, per-channel variable-length delta codes
/// over a gradient (left + up - upleft) predictor.
///
/// The decoder is a port of sai2::UnpackDeltaRLE16 / sai2::DeltaUnpackRow16Bpc
/// from Wunkolo/libsai (MIT), made strict so corrupted data is detected
/// instead of being read past. The encoder is its exact inverse; both were
/// cross-checked against the original libsai decoder (tools/crosscheck).
///
/// Pixels are 32-bit BGRA (B in the lowest byte), as SAI stores them.
/// </summary>
public static class DpcmCodec
{
    public const int MaxTileSide = 256;

    /// <summary>
    /// Decodes one row's delta stream. Returns consumed bytes (row streams are
    /// byte aligned), or -1 if the stream is invalid.
    /// </summary>
    /// <param name="strict">Reject zero runs that overshoot the row end (an encoder never emits them).</param>
    internal static int UnpackRow(ReadOnlySpan<byte> src, Span<short> deltas, int pixelCount, int channels, bool strict = false)
    {
        deltas[..(pixelCount * 4)].Clear();
        int pos = 0;
        int remainingBits = 0;
        ulong mask = 0;

        for (int ch = 0; ch < channels; ch++)
        {
            int count = 0;
            while (true)
            {
                while (remainingBits < 32 && pos < src.Length)
                {
                    ulong word;
                    int left = src.Length - pos;
                    if (left >= 4) { word = BinaryPrimitives.ReadUInt32LittleEndian(src[pos..]); pos += 4; mask |= word << remainingBits; remainingBits += 32; }
                    else if (left >= 2) { word = BinaryPrimitives.ReadUInt16LittleEndian(src[pos..]); pos += 2; mask |= word << remainingBits; remainingBits += 16; }
                    else { word = src[pos]; pos += 1; mask |= word << remainingBits; remainingBits += 8; }
                }

                if (mask == 0) return -1;
                int first = BitOperations.TrailingZeroCount(mask);
                if (first > 7) return -1; // opcode > 15
                ulong next = mask >> (first + 1);
                int op = (2 * first) | (int)(next & 1);
                remainingBits -= first + 2;
                mask = next >> 1;
                if (remainingBits < 0) return -1;

                if (op == 0)
                {
                    deltas[count * 4 + ch] = 0;
                    count++;
                }
                else if (op <= 14)
                {
                    int n = op;
                    bool negative = ((mask >> n) & 1) != 0;
                    int v = (int)(mask & ((1UL << n) - 1));
                    int magnitude = ((1 << n) | v) - 1;
                    remainingBits -= n + 1;
                    mask >>= n + 1;
                    if (remainingBits < 0) return -1;
                    deltas[count * 4 + ch] = (short)(negative ? -magnitude : magnitude);
                    count++;
                }
                else
                {
                    int zeros = (int)(mask & 0x7F) + 8;
                    remainingBits -= 7;
                    mask >>= 7;
                    if (remainingBits < 0) return -1;
                    if (strict && count + zeros > pixelCount) return -1;
                    for (int i = 0; i < zeros && count + i < pixelCount; i++) deltas[(count + i) * 4 + ch] = 0;
                    count += zeros;
                }

                if (count >= pixelCount) break;
            }
        }

        int consumed = pos - remainingBits / 8;
        return consumed > 0 ? consumed : -1;
    }

    /// <summary>Applies the predictor exactly like libsai's DeltaUnpackRow16Bpc.</summary>
    internal static void ReconstructRow(Span<uint> dst, ReadOnlySpan<uint> previousRow, ReadOnlySpan<short> deltas, int pixelCount)
    {
        Span<int> sum = stackalloc int[4];
        Span<int> prevUp = stackalloc int[4];
        sum.Clear();
        prevUp.Clear();
        for (int i = 0; i < pixelCount; i++)
        {
            uint up = previousRow[i];
            uint result = 0;
            for (int c = 0; c < 4; c++)
            {
                int upC = (int)((up >> (8 * c)) & 0xFF);
                int t = (sum[c] + upC) & 0xFFFF;   // wrapping add
                t -= prevUp[c];                      // saturating sub
                if (t < 0) t = 0;
                if (t > 0xFF) t = 0xFF;              // adds/subs 0xFF00 => clamp
                int s = (t + deltas[i * 4 + c]) & 0xFFFF;
                sum[c] = s;
                uint b = s > 0xFF ? 0xFFu : (uint)s;
                result |= b << (8 * c);
                prevUp[c] = upC;
            }
            dst[i] = result;
        }
    }

    /// <summary>
    /// Decodes a tile (bytes include its 2-byte tile header). Rows that decode
    /// are written to <paramref name="dest"/> (row-major, stride = width).
    /// Returns the number of good rows; equals height only if the whole tile is
    /// valid and consumed exactly.
    /// </summary>
    public static TileDecodeResult DecodeTile(ReadOnlySpan<byte> tile, int width, int height, int channels, Span<uint> dest)
    {
        if (tile.Length < 3) return new TileDecodeResult(0, false, "плитка короче заголовка");
        if (width < 1 || width > MaxTileSide || height < 1 || height > MaxTileSide)
            return new TileDecodeResult(0, false, "неверный размер плитки");

        Span<short> deltas = stackalloc short[MaxTileSide * 4];
        Span<uint> zeroRow = stackalloc uint[MaxTileSide];
        zeroRow.Clear();
        int pos = 2;
        for (int row = 0; row < height; row++)
        {
            if (pos >= tile.Length)
                return Fail(dest, width, row, channels, $"данные закончились на строке {row}");
            int used = UnpackRow(tile[pos..], deltas, width, channels);
            if (used <= 0)
                return Fail(dest, width, row, channels, $"ошибка кода в строке {row}");
            ReadOnlySpan<uint> prev = row == 0 ? zeroRow[..width] : dest.Slice((row - 1) * width, width);
            ReconstructRow(dest.Slice(row * width, width), prev, deltas, width);
            if (channels == 3)
                for (int x = 0; x < width; x++) dest[row * width + x] &= 0x00FFFFFFu; // alpha decodes as 0 (fixed later)
            pos += used;
        }

        if (channels == 3)
            for (int i = 0; i < width * height; i++) dest[i] |= 0xFF000000u;

        if (pos != tile.Length)
            return new TileDecodeResult(height, false, $"лишние {tile.Length - pos} байт в конце плитки");
        return new TileDecodeResult(height, true, null);
    }

    private static TileDecodeResult Fail(Span<uint> dest, int width, int goodRows, int channels, string error)
    {
        if (channels == 3)
            for (int i = 0; i < width * goodRows; i++) dest[i] |= 0xFF000000u;
        return new TileDecodeResult(goodRows, false, error);
    }

    /// <summary>
    /// Counts how many rows of the given width a tile holds, by decoding until the
    /// stream is consumed exactly. Returns -1 if no row count fits.
    /// </summary>
    public static int MeasureRows(ReadOnlySpan<byte> tile, int width, int channels, bool strict = false)
    {
        if (tile.Length < 3) return -1;
        Span<short> deltas = stackalloc short[MaxTileSide * 4];
        int pos = 2;
        for (int row = 0; row < MaxTileSide; row++)
        {
            int used = UnpackRow(tile[pos..], deltas, width, channels, strict);
            if (used <= 0) return -1;
            pos += used;
            if (pos == tile.Length) return row + 1;
            if (pos > tile.Length) return -1;
        }
        return -1;
    }

    // ---------------------------------------------------------------- encoder

    private ref struct BitWriter
    {
        private readonly List<byte> _out;
        private uint _acc;
        private int _bits;

        public BitWriter(List<byte> output) { _out = output; _acc = 0; _bits = 0; }

        public void Put(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _acc |= ((value >> i) & 1u) << _bits;
                if (++_bits == 8) { _out.Add((byte)_acc); _acc = 0; _bits = 0; }
            }
        }

        public void PutOpcode(int op)
        {
            Put(0, op >> 1);
            Put(1, 1);
            Put((uint)(op & 1), 1);
        }

        public void Align()
        {
            if (_bits > 0) { _out.Add((byte)_acc); _acc = 0; _bits = 0; }
        }
    }

    private static void EncodeChannel(ref BitWriter w, ReadOnlySpan<int> d)
    {
        int i = 0;
        while (i < d.Length)
        {
            if (d[i] == 0)
            {
                int run = 0;
                while (i + run < d.Length && d[i + run] == 0) run++;
                while (run >= 8)
                {
                    int n = Math.Min(run, 135);
                    w.PutOpcode(15);
                    w.Put((uint)(n - 8), 7);
                    run -= n;
                    i += n;
                }
                while (run > 0) { w.PutOpcode(0); run--; i++; }
                continue;
            }
            int m = Math.Abs(d[i]);
            int bits = 0;
            while ((1 << (bits + 1)) <= m + 1) bits++;
            int v = (m + 1) - (1 << bits);
            w.PutOpcode(bits);
            w.Put((uint)v, bits);
            w.Put(d[i] < 0 ? 1u : 0u, 1);
            i++;
        }
    }

    /// <summary>Encodes a tile (BGRA pixels, row-major, stride = width) including its 2-byte header.</summary>
    public static byte[] EncodeTile(ReadOnlySpan<uint> pixels, int width, int height, int channels, ushort header)
    {
        var output = new List<byte>(width * height / 2 + 16) { (byte)header, (byte)(header >> 8) };
        var prev = new int[width * 4];
        var cur = new int[width * 4];
        var d = new int[width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint p = pixels[y * width + x];
                for (int c = 0; c < 4; c++) cur[x * 4 + c] = (int)((p >> (8 * c)) & 0xFF);
            }
            var w = new BitWriter(output);
            for (int c = 0; c < channels; c++)
            {
                int sum = 0;
                for (int x = 0; x < width; x++)
                {
                    int up = prev[x * 4 + c];
                    int upLeft = x > 0 ? prev[(x - 1) * 4 + c] : 0;
                    int pred = Math.Clamp(sum + up - upLeft, 0, 255);
                    d[x] = cur[x * 4 + c] - pred;
                    sum = cur[x * 4 + c];
                }
                EncodeChannel(ref w, d);
            }
            w.Align();
            if (channels == 3) for (int x = 0; x < width; x++) cur[x * 4 + 3] = 0;
            (prev, cur) = (cur, prev);
        }
        return output.ToArray();
    }

    /// <summary>A tile with every pixel set to <paramref name="fill"/>.</summary>
    public static byte[] EncodeSolidTile(int width, int height, int channels, uint fill, ushort header)
    {
        var px = new uint[width * height];
        Array.Fill(px, fill);
        return EncodeTile(px, width, height, channels, header);
    }
}

public readonly record struct TileDecodeResult(int GoodRows, bool Ok, string? Error);
