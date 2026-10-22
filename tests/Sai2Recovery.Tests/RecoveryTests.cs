using System.Buffers.Binary;
using Sai2Recovery.Core;
using Sai2Recovery.Core.Analysis;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Model;
using Sai2Recovery.Core.Repair;
using Sai2Recovery.Core.Scan;
using Xunit;

namespace Sai2Recovery.Tests;

public class RecoveryTests
{
    private static string CompositeHash(Sai2Analysis a)
    {
        var c = a.Composite!;
        return TestFiles.Sha256(c.Pixels!.Render(c.Bytes(a.Data), 0u, 0u).Pixels);
    }

    private static RepairResult RepairClean(byte[] file)
    {
        var a = Sai2Analyzer.Analyze(file);
        Assert.NotEqual(Verdict.Unrecoverable, a.Verdict);
        var r = Sai2Repairer.Repair(a);
        Assert.True(r.Success);
        Assert.NotNull(r.Verification);
        Assert.False(r.Verification!.HasErrors, string.Join("\n", r.Verification.Issues.Select(i => i.Message)));
        return r;
    }

    [Theory]
    [InlineData("small_rgba")]
    [InlineData("multi_tile_rgb")]
    public void Healthy_file_is_reported_healthy_and_repair_is_identity(string name)
    {
        var file = TestFiles.Fixture(name);
        var a = Sai2Analyzer.Analyze(file);
        Assert.Equal(Verdict.Healthy, a.Verdict);
        Assert.DoesNotContain(a.Issues, i => i.Severity >= Severity.Warning);
        var r = Sai2Repairer.Repair(a);
        Assert.Equal(file, r.Output);
    }

    [Fact]
    public void Damaged_signature_is_restored()
    {
        var original = TestFiles.Fixture("multi_tile_rgb");
        var file = (byte[])original.Clone();
        "SAI-CANVAS-XXXX!"u8.CopyTo(file);
        var a = Sai2Analyzer.Analyze(file);
        Assert.False(a.MagicIntact);
        Assert.Equal(Verdict.Repairable, a.Verdict);
        Assert.Equal(original, RepairClean(file).Output);
    }

    [Fact]
    public void Wrong_table_count_is_corrected()
    {
        var original = TestFiles.Fixture("small_rgba");
        var file = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), 9999);
        Assert.Equal(original, RepairClean(file).Output);
    }

    [Fact]
    public void Broken_size_in_header_is_inferred()
    {
        var original = TestFiles.Fixture("multi_tile_rgb");
        var file = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 0);
        var a = Sai2Analyzer.Analyze(file);
        Assert.Equal((600u, 300u), (a.Header.Width, a.Header.Height));
        Assert.Equal(original, RepairClean(file).Output);
    }

    [Fact]
    public void Plausible_but_wrong_size_is_corrected()
    {
        var original = TestFiles.Fixture("multi_tile_rgb");
        var file = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 590);
        var a = Sai2Analyzer.Analyze(file);
        Assert.Equal((600u, 300u), (a.Header.Width, a.Header.Height));
        Assert.Equal(original, RepairClean(file).Output);
    }

    [Fact]
    public void Damaged_table_entry_is_relocated()
    {
        var original = TestFiles.Fixture("multi_tile_rgb");
        var file = (byte[])original.Clone();
        // entry #2 (layr): garbage offset
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(64 + 16 * 2 + 8), 0xDEADBEEF_12345678);
        var a = Sai2Analyzer.Analyze(file);
        Assert.Contains(a.Chunks, c => c.Type == Sai2Format.Layr && c.Repositioned);
        Assert.Equal(original, RepairClean(file).Output);
    }

    [Fact]
    public void Damaged_entry_type_is_recovered_from_content()
    {
        var original = TestFiles.Fixture("multi_tile_rgb");
        var file = (byte[])original.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(64 + 16 * 2), 0x00FF00FF);
        Assert.Equal(original, RepairClean(file).Output);
    }

    [Theory]
    [InlineData("small_rgba")]
    [InlineData("multi_tile_rgb")]
    public void Wiped_header_and_table_are_rebuilt_by_deep_scan(string name)
    {
        var original = TestFiles.Fixture(name);
        var file = (byte[])original.Clone();
        Array.Clear(file, 0, 64 + 16 * 4);
        var a = Sai2Analyzer.Analyze(file);
        Assert.True(a.TableRebuilt);
        Assert.NotNull(a.Composite);
        Assert.Equal(TestFiles.ExpectedHash($"{name}.intg.bgra"), CompositeHash(a));
        Assert.Single(a.Layers);
        Assert.NotNull(a.PixelsOf(2));
        var r = RepairClean(file);
        var again = Sai2Analyzer.Analyze(r.Output);
        Assert.Equal(TestFiles.ExpectedHash($"{name}.intg.bgra"), CompositeHash(again));
        Assert.Equal("Слой1", again.Layers.Single().Layer!.Name);
    }

    [Theory]
    [InlineData(0.95)]
    [InlineData(0.80)]
    [InlineData(0.60)]
    public void Truncated_file_is_repaired(double keep)
    {
        var original = TestFiles.Fixture("multi_tile_rgb");
        var file = original[..(int)(original.Length * keep)];
        var a = Sai2Analyzer.Analyze(file);
        Assert.NotEqual(Verdict.Healthy, a.Verdict);
        var r = RepairClean(file);
        var again = Sai2Analyzer.Analyze(r.Output);
        Assert.NotNull(again.Composite);
    }

    [Fact]
    public void Zeroed_cluster_inside_composite_damages_only_those_tiles()
    {
        var original = TestFiles.Fixture("multi_tile_rgb");
        var file = (byte[])original.Clone();
        long intgOffset = TestFiles.EntryOffset(file, 1);
        var clean = Sai2Analyzer.Analyze(original).Composite!.Pixels!;
        var victim = clean.Tiles.OrderByDescending(t => t.Length).First();
        int count = Math.Min(200, victim.Length / 3);
        Array.Clear(file, (int)(intgOffset + victim.Offset + victim.Length / 3), count);

        var a = Sai2Analyzer.Analyze(file);
        var ps = a.Composite!.Pixels!;
        Assert.Equal(TileState.Corrupt, ps.Tiles[victim.Index].State);
        Assert.All(ps.Tiles.Where(t => t.Index != victim.Index), t => Assert.Equal(TileState.Ok, t.State));
        Assert.Equal(Verdict.PartiallyRecoverable, a.Verdict);
        RepairClean(file);
    }

    [Fact]
    public void Embedded_copy_is_found_and_extracted()
    {
        var original = TestFiles.Fixture("small_rgba");
        var junk = new byte[1000];
        new Random(1).NextBytes(junk);
        var container = junk.Concat(original).Concat(junk).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"sai2test_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, container);
            Assert.Empty(CopyScanner.ScanFile(path, deep: false));
            var hits = CopyScanner.ScanFile(path, deep: true);
            Assert.Equal(new long[] { 1000 }, hits);
            Assert.Equal(original, CopyScanner.Extract(path, 1000));

            var a = Sai2Analyzer.Analyze(container);
            Assert.Equal(1000, a.BaseOffset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Zero_and_empty_files_are_unrecoverable()
    {
        Assert.Equal(Verdict.Unrecoverable, Sai2Analyzer.Analyze(new byte[50_000]).Verdict);
        Assert.Equal(Verdict.Unrecoverable, Sai2Analyzer.Analyze([]).Verdict);
        var noise = new byte[10_000];
        new Random(3).NextBytes(noise);
        Assert.Equal(Verdict.Unrecoverable, Sai2Analyzer.Analyze(noise).Verdict);
    }

    [Fact]
    public void Sparse_layer_is_placed_by_tile_headers_and_survives_truncation()
    {
        const int w = 600, h = 300;
        var rng = new Random(9);
        var composite = TestFiles.RandomImage(rng, w, h, 1);
        var layer = TestFiles.RandomImage(rng, w, h, 2);
        var intg = TestFiles.EncodeDpcm(composite, w, h, 4);
        // tiles (1,0), (2,0) and (0,1) present
        bool Present(int x, int y) => (y == 0 && x >= 1) || (y == 1 && x == 0);
        var lpix = TestFiles.EncodeDpcm(layer, w, h, 4, Present);
        var file = TestFiles.Build(w, h, 4,
            ("intg", 0, intg),
            ("layr", 7, TestFiles.Layer(7, 3, "Sparse")),
            ("lpix", 7, lpix));

        var a = Sai2Analyzer.Analyze(file);
        Assert.Equal(Verdict.Healthy, a.Verdict);
        var ps = a.PixelsOf(7)!.Pixels!;
        Assert.False(ps.Dense);
        Assert.Equal(3, ps.LengthCount);
        Assert.Equal(new[] { (1, 0), (2, 0), (0, 1) }, ps.Tiles.Select(t => (t.X, t.Y)).ToArray());
        Assert.True(a.LayerTileCountConfirmed);

        var img = ps.Render(a.PixelsOf(7)!.Bytes(a.Data), 0u, 0u);
        Assert.Equal(layer[300], img.Pixels[300]);             // inside tile (1,0)
        Assert.Equal(0u, img.Pixels[0]);                        // tile (0,0) absent
        Assert.Equal(layer[260 * w + 10], img.Pixels[260 * w + 10]); // tile (0,1)

        // Cut inside the last tile.
        var truncated = file[..(file.Length - 500)];
        var r = RepairClean(truncated);
        var again = Sai2Analyzer.Analyze(r.Output);
        Assert.Equal(3, again.PixelsOf(7)!.Pixels!.LengthCount);
    }

    [Fact]
    public void Recovery_service_writes_outputs()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sai2out_{Guid.NewGuid():N}");
        var src = Path.Combine(Path.GetTempPath(), $"sai2src_{Guid.NewGuid():N}.sai2");
        try
        {
            var file = TestFiles.Fixture("multi_tile_rgb");
            File.WriteAllBytes(src, file[..(int)(file.Length * 0.9)]);
            var outcome = RecoveryService.Recover(src, dir);
            Assert.NotNull(outcome.RepairedFile);
            Assert.True(File.Exists(outcome.RepairedFile));
            Assert.Contains(outcome.Files, f => f.EndsWith(".png", StringComparison.Ordinal));
            Assert.Contains(outcome.Files, f => f.EndsWith(".ora", StringComparison.Ordinal));
            Assert.True(File.Exists(Path.Combine(dir, "отчёт.txt")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            File.Delete(src);
        }
    }
}
