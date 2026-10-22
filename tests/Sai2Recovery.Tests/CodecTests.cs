using Sai2Recovery.Core.Analysis;
using Sai2Recovery.Core.Codecs;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Imaging;
using Sai2Recovery.Core.Model;
using Xunit;

namespace Sai2Recovery.Tests;

public class CodecTests
{
    [Theory]
    [InlineData("small_rgba")]
    [InlineData("multi_tile_rgb")]
    public void Composite_decodes_exactly_like_libsai(string name)
    {
        var a = Sai2Analyzer.Analyze(TestFiles.Fixture(name));
        var intg = a.Composite;
        Assert.NotNull(intg);
        Assert.True(intg!.Pixels!.IsHealthy);
        var img = intg.Pixels.Render(intg.Bytes(a.Data), 0u, 0u);
        Assert.Equal(TestFiles.ExpectedHash($"{name}.intg.bgra"), TestFiles.Sha256(img.Pixels));
    }

    [Theory]
    [InlineData("small_rgba")]
    [InlineData("multi_tile_rgb")]
    public void Layer_pixels_decode_exactly_like_libsai(string name)
    {
        var a = Sai2Analyzer.Analyze(TestFiles.Fixture(name));
        var px = a.PixelsOf(2);
        Assert.NotNull(px);
        Assert.True(px!.Pixels!.IsHealthy);
        Assert.Equal(4, px.Pixels.Channels);
        var img = px.Pixels.Render(px.Bytes(a.Data), 0u, 0u);
        Assert.Equal(TestFiles.ExpectedHash($"{name}.layer2.bgra"), TestFiles.Sha256(img.Pixels));
    }

    [Theory]
    [InlineData(1, 1, 4, 0)]
    [InlineData(7, 3, 3, 0)]
    [InlineData(256, 256, 4, 1)]
    [InlineData(255, 200, 3, 2)]
    [InlineData(256, 17, 4, 2)]
    [InlineData(100, 256, 4, 0)]
    public void Encoder_round_trips(int w, int h, int channels, int kind)
    {
        var rng = new Random(w * 1000 + h + channels);
        var img = TestFiles.RandomImage(rng, w, h, kind);
        if (channels == 3) for (int i = 0; i < img.Length; i++) img[i] |= 0xFF000000u;
        var tile = DpcmCodec.EncodeTile(img, w, h, channels, 0x0000);
        var back = new uint[w * h];
        var r = DpcmCodec.DecodeTile(tile, w, h, channels, back);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(img, back);
        Assert.Equal(h, DpcmCodec.MeasureRows(tile, w, channels));
    }

    [Fact]
    public void Corrupted_tile_is_detected_and_keeps_good_rows()
    {
        var rng = new Random(5);
        var img = TestFiles.RandomImage(rng, 256, 256, 1);
        var tile = DpcmCodec.EncodeTile(img, 256, 256, 4, 0);
        // A zero-filled tail is what an interrupted write leaves on NTFS.
        int cut = tile.Length * 3 / 4;
        for (int i = cut; i < tile.Length; i++) tile[i] = 0x00;
        var back = new uint[256 * 256];
        var r = DpcmCodec.DecodeTile(tile, 256, 256, 4, back);
        Assert.False(r.Ok);
        Assert.InRange(r.GoodRows, 100, 255);
        Assert.Equal(img.AsSpan(0, r.GoodRows * 256).ToArray(), back.AsSpan(0, r.GoodRows * 256).ToArray());
    }

    [Fact]
    public void Png_writer_produces_valid_chunks()
    {
        var img = new BgraImage(3, 2);
        img.Pixels[0] = 0xFF0000FFu;
        var png = PngWriter.ToBytes(img);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal(new byte[] { 0xAE, 0x42, 0x60, 0x82 }, png[^4..]); // CRC of IEND
        uint ihdrCrc = PngWriter.Crc32(png.AsSpan(12, 17));
        uint stored = (uint)(png[29] << 24 | png[30] << 16 | png[31] << 8 | png[32]);
        Assert.Equal(ihdrCrc, stored);
    }

    [Fact]
    public void Jssf_is_converted_to_jpeg_stream()
    {
        var blob = new List<byte>();
        blob.AddRange(BitConverter.GetBytes(8u));
        blob.AddRange(BitConverter.GetBytes(8u));
        blob.AddRange("jssf"u8.ToArray());
        blob.AddRange(BitConverter.GetBytes((ushort)8));
        blob.AddRange(BitConverter.GetBytes((ushort)8));
        blob.AddRange(BitConverter.GetBytes((ushort)1));
        blob.AddRange(Enumerable.Repeat((byte)1, 64));
        blob.AddRange(BitConverter.GetBytes((ushort)2));
        blob.AddRange(new byte[] { 0x00, 0x00 });
        var arr = blob.ToArray();
        Assert.Equal(arr.Length, JssfCodec.MeasureLength(arr));
        var jpg = JssfCodec.ToJpeg(arr, out int w, out int h);
        Assert.NotNull(jpg);
        Assert.Equal((8, 8), (w, h));
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, jpg![..2]);
        Assert.Equal(new byte[] { 0xFF, 0xD9 }, jpg[^2..]);
    }

    [Fact]
    public void Layer_name_is_read()
    {
        var a = Sai2Analyzer.Analyze(TestFiles.Fixture("small_rgba"));
        var l = a.Layers.Single();
        Assert.True(l.Layer!.Recognized);
        Assert.Equal("Слой1", l.Layer.Name);
        Assert.True(a.LayerTileCountConfirmed);
    }

    [Fact]
    public void Dimensions_are_inferred_from_composite()
    {
        var a = Sai2Analyzer.Analyze(TestFiles.Fixture("multi_tile_rgb"));
        var r = DimensionInference.FromDense(a.Composite!.Bytes(a.Data), true, [4, 3]);
        Assert.NotNull(r);
        Assert.Equal((600u, 300u, 3), (r!.Width, r.Height, r.Channels));
    }
}
