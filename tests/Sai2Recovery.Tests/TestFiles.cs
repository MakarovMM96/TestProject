using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sai2Recovery.Core.Codecs;
using Sai2Recovery.Core.Format;

namespace Sai2Recovery.Tests;

/// <summary>Fixtures produced by tools/crosscheck (libsai) and a builder for synthetic files.</summary>
public static class TestFiles
{
    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static byte[] Fixture(string name) => File.ReadAllBytes(FixturePath(name + ".sai2"));

    public static string ExpectedHash(string key)
    {
        var json = File.ReadAllText(FixturePath("expected-sha256.json"));
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
        return map[key];
    }

    public static string Sha256(uint[] pixels)
    {
        var bytes = new byte[pixels.Length * 4];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static uint[] RandomImage(Random rng, int w, int h, int kind)
    {
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint b, g, r, a;
                switch (kind % 3)
                {
                    case 0: b = (uint)rng.Next(256); g = (uint)rng.Next(256); r = (uint)rng.Next(256); a = (uint)rng.Next(256); break;
                    case 1: b = (uint)(x * 3 & 255); g = (uint)(y * 5 & 255); r = (uint)((x ^ y) & 255); a = 255; break;
                    default:
                        bool inside = (x - w / 2) * (x - w / 2) + (y - h / 2) * (y - h / 2) < w * w / 9;
                        b = inside ? 30u : 0; g = inside ? 60u : 0; r = inside ? 200u : 0; a = inside ? 255u : 0; break;
                }
                px[y * w + x] = b | (g << 8) | (r << 16) | (a << 24);
            }
        return px;
    }

    /// <summary>Encodes a dpcm blob. <paramref name="present"/> selects tiles for a sparse stream (null = dense).</summary>
    public static byte[] EncodeDpcm(uint[] img, int w, int h, int channels, Func<int, int, bool>? present = null)
    {
        int tx = (w + 255) / 256, ty = (h + 255) / 256;
        var tiles = new List<byte[]>();
        var body = new List<byte>();
        for (int y = 0; y < ty; y++)
        {
            for (int x = 0; x < tx; x++)
            {
                if (present != null && !present(x, y)) continue;
                int tw = Math.Min(256, w - x * 256), th = Math.Min(256, h - y * 256);
                var tile = new uint[tw * th];
                for (int r = 0; r < th; r++)
                    Array.Copy(img, (y * 256 + r) * w + x * 256, tile, r * tw, tw);
                var enc = DpcmCodec.EncodeTile(tile, tw, th, channels, (ushort)((x << 8) | (y & 0xFF)));
                tiles.Add(enc);
                body.AddRange(enc);
            }
            body.Add((byte)y);
            body.Add((byte)tx);
        }
        var output = new List<byte>();
        output.AddRange("dpcm"u8.ToArray());
        foreach (var t in tiles) output.AddRange(BitConverter.GetBytes((uint)t.Length));
        output.AddRange(body);
        return output.ToArray();
    }

    public static byte[] Layer(uint id, uint tileCount, string name)
    {
        var o = new List<byte>();
        void U32(uint v) => o.AddRange(BitConverter.GetBytes(v));
        o.AddRange("layr"u8.ToArray());
        U32(id); U32(0); U32(0);
        o.AddRange("norm"u8.ToArray());
        U32(0); U32(0); U32(0); U32(0);
        U32(0);
        U32(tileCount);
        o.AddRange("norm"u8.ToArray());
        U32(100);
        U32(1);
        var nameBytes = Encoding.Unicode.GetBytes(name);
        o.AddRange("name"u8.ToArray());
        U32((uint)nameBytes.Length);
        o.AddRange(nameBytes);
        U32(0);
        return o.ToArray();
    }

    public static byte[] Build(uint width, uint height, int previewChannels, params (string Tag, uint Id, byte[] Data)[] chunks)
    {
        var header = Sai2Header.CreateDefault(width, height, previewChannels);
        header.TableCount = (uint)chunks.Length;
        header.SelectedLayer = chunks.FirstOrDefault(c => c.Tag == "layr").Id;
        long tableEnd = 64 + 16L * chunks.Length;
        var output = new byte[tableEnd + chunks.Sum(c => c.Data.Length)];
        header.Write(output);
        long off = tableEnd;
        for (int i = 0; i < chunks.Length; i++)
        {
            new TableEntry(Sai2Format.Tag(chunks[i].Tag), chunks[i].Id, (ulong)off).Write(output.AsSpan(64 + 16 * i));
            chunks[i].Data.CopyTo(output, off);
            off += chunks[i].Data.Length;
        }
        return output;
    }

    public static long EntryOffset(byte[] file, int index) =>
        (long)BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(64 + 16 * index + 8));
}
