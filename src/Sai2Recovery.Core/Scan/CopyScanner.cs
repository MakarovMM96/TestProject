using Sai2Recovery.Core.Analysis;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Model;

namespace Sai2Recovery.Core.Scan;

public sealed record FoundCopy(string Path, long Offset, long Size, DateTime Modified)
{
    public bool Embedded => Offset > 0 || !Path.EndsWith(".sai2", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Finds SAI2 data by content, not by name: renamed or temporary copies,
/// files with a wrong extension, and SAI2 images embedded inside other files
/// (disk images, recovered chunks from data-recovery tools).
/// </summary>
public static class CopyScanner
{
    private const int BlockSize = 8 * 1024 * 1024;

    /// <param name="deep">Search inside every file, not only at its start.</param>
    public static List<FoundCopy> ScanFolder(string folder, bool deep, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<FoundCopy>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        foreach (var path in Directory.EnumerateFiles(folder, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.Length < Sai2Format.HeaderSize) continue;
                progress?.Report(path);
                foreach (long off in ScanFile(path, deep, ct))
                    results.Add(new FoundCopy(path, off, info.Length - off, info.LastWriteTime));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // locked or unreadable file: skip
            }
        }
        return results;
    }

    public static List<long> ScanFile(string path, bool deep, CancellationToken ct = default)
    {
        var found = new List<long>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        if (!deep)
        {
            Span<byte> head = stackalloc byte[16];
            if (fs.ReadAtLeast(head, 16, false) == 16 && head.SequenceEqual(Sai2Format.Magic)) found.Add(0);
            return found;
        }

        var buffer = new byte[BlockSize + 15];
        long position = 0;
        int carry = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = fs.Read(buffer, carry, BlockSize);
            if (read <= 0) break;
            int len = carry + read;
            foreach (long hit in Sai2Format.FindMagic(buffer.AsSpan(0, len)))
                found.Add(position - carry + hit);
            carry = Math.Min(15, len);
            Array.Copy(buffer, len - carry, buffer, 0, carry);
            position += read;
        }
        return found.Distinct().ToList();
    }

    /// <summary>Reads SAI2 data starting at <paramref name="offset"/> up to the next header or EOF.</summary>
    public static byte[] Extract(string path, long offset)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long end = fs.Length;
        var next = ScanFile(path, deep: true).Where(o => o > offset).DefaultIfEmpty(end).Min();
        long size = Math.Min(end, next) - offset;
        if (size > int.MaxValue) size = int.MaxValue;
        var data = new byte[size];
        fs.Position = offset;
        fs.ReadExactly(data);
        return TrimToContent(data);
    }

    /// <summary>Cuts trailing data that does not belong to the SAI2 image.</summary>
    public static byte[] TrimToContent(byte[] data)
    {
        var a = Sai2Analyzer.Analyze(data);
        if (a.Chunks.Count == 0) return data;
        var last = a.Chunks.Where(c => c.Offset < data.Length).OrderBy(c => c.Offset).LastOrDefault();
        if (last == null) return data;
        long end = last.Offset + (last.Pixels?.StructureOk == true ? last.Pixels.ParsedLength
            : last.Layer is { ParamsComplete: true } l ? l.ParsedLength
            : last.Length);
        return end > 0 && end < data.Length ? data[..(int)end] : data;
    }

    public static string DescribeHealth(byte[] data)
    {
        var a = Sai2Analyzer.Analyze(data);
        return Report.VerdictText(a.Verdict);
    }
}
