using Sai2Recovery.Core.Codecs;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Model;

namespace Sai2Recovery.Core.Analysis;

/// <summary>
/// Lenient SAI2 reader: finds what is intact, what is damaged and what is lost.
/// It never throws on bad input; every finding becomes an <see cref="Issue"/>.
/// </summary>
public static class Sai2Analyzer
{
    private const int ZeroRegionMin = 4096;

    public static Sai2Analysis AnalyzeFile(string path) => Analyze(File.ReadAllBytes(path), path);

    public static Sai2Analysis Analyze(byte[] file, string? path = null)
    {
        var a = new Sai2Analysis { SourcePath = path, OriginalLength = file.Length };
        try
        {
            AnalyzeCore(a, file);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or InvalidOperationException)
        {
            a.Add(Severity.Fatal, $"Внутренняя ошибка разбора: {ex.Message}");
        }
        a.Verdict = DecideVerdict(a);
        return a;
    }

    private static void AnalyzeCore(Sai2Analysis a, byte[] file)
    {
        if (file.Length == 0)
        {
            a.Add(Severity.Fatal, "Файл пустой (0 байт). Программа SAI2 не успела записать данные — восстанавливать нечего. Поищите копии файла (кнопка «Найти копии») или предыдущие версии Windows.");
            return;
        }
        int lastNz = file.AsSpan().LastIndexOfAnyExcept((byte)0);
        if (lastNz < 0)
        {
            a.Add(Severity.Fatal, $"Файл ({file.Length:N0} байт) целиком заполнен нулями. Так бывает, если компьютер выключился во время сохранения: место на диске выделено, но данные не записаны. Восстановить рисунок из этого файла невозможно — поищите копии (кнопка «Найти копии») или предыдущие версии файла Windows.");
            return;
        }

        // 1. Locate the header.
        long baseOffset = 0;
        if (Sai2Format.HasMagicAt(file, 0))
        {
            a.MagicIntact = true;
        }
        else
        {
            var hits = Sai2Format.FindMagic(file);
            if (hits.Count > 0)
            {
                baseOffset = hits[0];
                a.MagicIntact = true;
                a.Add(Severity.Warning, $"Заголовок SAI2 найден не в начале файла, а со смещения {baseOffset:N0}. Данные перед ним будут отброшены.");
            }
        }
        if (Sai2Format.FindMagic(file).Count > 1)
            a.Add(Severity.Info, "В файле несколько заголовков SAI2 (возможно, внутри склеены копии). Используется первый; остальные можно извлечь через «Найти копии» с глубоким поиском.");

        a.BaseOffset = baseOffset;
        a.Data = baseOffset == 0 ? file : file[(int)baseOffset..];
        var d = a.Data;
        a.LastNonZero = d.AsSpan().LastIndexOfAnyExcept((byte)0);
        FindZeroRegions(a);

        if (d.Length < Sai2Format.HeaderSize)
        {
            a.Add(Severity.Fatal, "Файл слишком короткий: в нём нет даже заголовка SAI2.");
            return;
        }

        var hdr = Sai2Header.Read(d);
        a.OriginalHeader = hdr;
        a.Header = hdr.Clone();
        a.DeclaredTableCount = hdr.TableCount;

        // 2. Read the chunk table.
        var table = ReadTable(a, d, hdr);
        if (!a.MagicIntact)
        {
            if (table != null)
            {
                a.Add(Severity.Error, "Сигнатура файла (SAI-CANVAS-TYPE0) повреждена, но структура внутри цела — сигнатура будет восстановлена.");
                a.HeaderUsable = true;
            }
            else
            {
                a.Add(Severity.Error, "Заголовок и таблица блоков не распознаются (вероятно, начало файла затёрто). Выполняется глубокий поиск блоков по всему файлу.");
            }
        }
        else
        {
            a.HeaderUsable = true;
        }

        if (!Sai2Header.IsPlausibleSize(a.Header.Width, a.Header.Height))
            a.Header.Width = a.Header.Height = 0; // infer below

        if (table == null)
        {
            if (!a.MagicIntact)
                a.Header = Sai2Header.CreateDefault(
                    Sai2Header.IsPlausibleSize(hdr.Width, hdr.Height) ? hdr.Width : 0,
                    Sai2Header.IsPlausibleSize(hdr.Width, hdr.Height) ? hdr.Height : 0, 4);
            if (a.MagicIntact)
                a.Add(Severity.Error, "Таблица блоков повреждена и не читается. Выполняется глубокий поиск блоков по всему файлу.");
            DeepScan(a, d);
        }
        else
        {
            a.Chunks = table;
            EnsureDimensions(a, d);
            ResolveRepositionedChunks(a, d);
            ComputeLengths(a, d);
        }

        if (a.Chunks.Count == 0)
        {
            a.Add(Severity.Fatal, "Не найдено ни одного распознаваемого блока SAI2. Восстановить рисунок из этого файла не получится.");
            return;
        }

        // 3. Validate content.
        ValidateChunks(a, d);
        CheckDimensionsAgainstComposite(a, d);
        CheckLinks(a);
        CheckTail(a);
    }

    // ----------------------------------------------------------------- table

    private static List<ChunkInfo>? ReadTable(Sai2Analysis a, byte[] d, Sai2Header hdr)
    {
        long len = d.Length;
        int maxN = (int)Math.Min(1_000_000, (len - Sai2Format.HeaderSize) / Sai2Format.EntrySize);
        if (maxN <= 0) return null;

        TableEntry Entry(int i) => TableEntry.Read(d.AsSpan(Sai2Format.HeaderSize + i * Sai2Format.EntrySize, Sai2Format.EntrySize));
        bool TagOk(uint t) => Sai2Format.IsKnownChunkType(t) || Sai2Format.IsPlausibleTag(t);

        int nDecl = hdr.TableCount >= 1 && hdr.TableCount <= maxN ? (int)hdr.TableCount : -1;

        // The first chunk starts right after the table.
        int nFirst = -1;
        var e0 = Entry(0);
        if (TagOk(e0.Type) && e0.Offset >= 80 && (e0.Offset - 64) % 16 == 0 && (long)((e0.Offset - 64) / 16) <= maxN)
            nFirst = (int)((e0.Offset - 64) / 16);

        int nScan = 0;
        while (nScan < maxN && TagOk(Entry(nScan).Type)) nScan++;

        int n;
        if (nDecl > 0 && (nFirst < 0 || nFirst == nDecl)) n = nDecl;
        else if (nFirst > 0)
        {
            n = nFirst;
            a.Add(Severity.Error, $"Число записей в заголовке ({hdr.TableCount}) не совпадает с таблицей; исправлено на {n}.");
        }
        else if (nScan > 0)
        {
            n = nScan;
            a.Add(Severity.Error, $"Число записей в заголовке ({hdr.TableCount}) повреждено; по содержимому таблицы определено {n}.");
        }
        else return null;

        long tableEnd = Sai2Format.HeaderSize + (long)n * Sai2Format.EntrySize;
        var chunks = new List<ChunkInfo>();
        long prevOffset = tableEnd - 1;
        int good = 0;
        for (int i = 0; i < n; i++)
        {
            var e = Entry(i);
            bool tagOk = TagOk(e.Type);
            bool monotonic = e.Offset > (ulong)prevOffset;
            var c = new ChunkInfo { TableIndex = i, Type = e.Type, Id = e.Id, Offset = e.Offset > long.MaxValue ? long.MaxValue : (long)e.Offset };
            if (tagOk && monotonic && e.Offset < (ulong)len)
            {
                prevOffset = (long)e.Offset;
                good++;
            }
            else if (!tagOk && monotonic && e.Offset < (ulong)len)
            {
                // Offset fine, type tag damaged: the type is recovered from the content.
                c.Type = 0;
                c.Note = "тип блока в таблице повреждён";
                prevOffset = (long)e.Offset;
            }
            else if (tagOk && monotonic && e.Offset < (1UL << 40))
            {
                c.Health = ChunkHealth.Lost;
                c.Note = "данные за концом файла (файл обрезан)";
                prevOffset = (long)e.Offset;
                good++;
            }
            else
            {
                // Damaged entry: remember what we can, try to relocate later.
                c.Offset = -1;
                c.Health = ChunkHealth.Lost;
                c.Note = "запись таблицы повреждена";
                if (!tagOk) c.Type = 0;
            }
            chunks.Add(c);
        }
        if (good == 0) return null;
        int bad = n - good;
        if (bad > 0) a.Add(Severity.Error, $"В таблице блоков повреждено записей: {bad} из {n}. Будет предпринята попытка найти эти блоки по содержимому.");
        if (bad > n / 2) a.Add(Severity.Warning, "Таблица повреждена сильнее чем наполовину — результаты анализа менее надёжны.");
        return chunks;
    }

    private static void ComputeLengths(Sai2Analysis a, byte[] d)
    {
        var placed = a.Chunks.Where(c => c.Offset >= 0 && c.Offset < d.Length).OrderBy(c => c.Offset).ToList();
        for (int i = 0; i < placed.Count; i++)
        {
            var c = placed[i];
            var next = a.Chunks.Where(x => x.Offset > c.Offset).Select(x => x.Offset).DefaultIfEmpty(long.MaxValue).Min();
            if (next != long.MaxValue && next <= d.Length)
            {
                c.Length = next - c.Offset;
                c.ExactLength = true;
            }
            else
            {
                c.Length = d.Length - c.Offset;
                c.ExactLength = false;
            }
        }
    }

    /// <summary>Exact size of a chunk at <paramref name="offset"/> if its format is self-delimiting.</summary>
    private static long SelfLength(Sai2Analysis a, byte[] d, uint type, uint id, long offset)
    {
        if (offset < 0 || offset >= d.Length) return -1;
        var span = d.AsSpan((int)offset);
        if (type == Sai2Format.Layr)
        {
            var l = LayerRecord.Parse(span, id);
            return l.Recognized && l.ParamsComplete ? l.ParsedLength : -1;
        }
        if (type == Sai2Format.Thum) return JssfCodec.MeasureLength(span);
        if (PixelStream.HasDpcmTag(span) && a.Header.Width > 0)
        {
            int? expected = type == Sai2Format.Intg
                ? Sai2Format.TilesFor(a.Header.Width) * Sai2Format.TilesFor(a.Header.Height)
                : null;
            var ps = PixelStream.Parse(span, (int)a.Header.Width, (int)a.Header.Height, false, expected, [4], decode: false);
            return ps.StructureOk ? ps.ParsedLength : -1;
        }
        return -1;
    }

    private static uint GuessTypeFromContent(Sai2Analysis a, ReadOnlySpan<byte> blob, uint id)
    {
        if (blob.Length >= 4 && Sai2Format.ReadU32(blob, 0) == Sai2Format.Layr) return Sai2Format.Layr;
        if (JssfCodec.LooksLikeJssf(blob)) return Sai2Format.Thum;
        if (PixelStream.HasDpcmTag(blob))
            return id == 0 && a.Composite == null ? Sai2Format.Intg : Sai2Format.Lpix;
        return 0;
    }

    private static bool ContentMatchesType(ReadOnlySpan<byte> blob, uint type, uint id)
    {
        if (type == Sai2Format.Layr) return LayerRecord.TryPeekId(blob, out uint pid) && pid == id;
        if (type == Sai2Format.Intg || Sai2Format.IsPixelChunk(type)) return PixelStream.HasDpcmTag(blob);
        if (type == Sai2Format.Thum) return JssfCodec.LooksLikeJssf(blob);
        return false;
    }

    /// <summary>Damaged table entries: find the chunk right after its predecessor.</summary>
    private static void ResolveRepositionedChunks(Sai2Analysis a, byte[] d)
    {
        for (int i = 0; i < a.Chunks.Count; i++)
        {
            var c = a.Chunks[i];
            if (c.Offset >= 0 && c.Type == 0)
            {
                if (c.Offset >= d.Length) { c.Offset = -1; continue; }
                uint guessed = GuessTypeFromContent(a, d.AsSpan((int)c.Offset), c.Id);
                if (guessed == 0)
                {
                    a.Add(Severity.Error, $"Запись №{i + 1} таблицы: тип блока повреждён и по содержимому не определяется — блок будет исключён.");
                    c.Offset = -1;
                    continue;
                }
                c.Type = guessed;
                if (guessed == Sai2Format.Layr && LayerRecord.TryPeekId(d.AsSpan((int)c.Offset), out uint gid)) c.Id = gid;
                c.Note = null;
                c.Repositioned = true;
                a.Add(Severity.Warning, $"Запись №{i + 1} таблицы: тип блока восстановлен по содержимому («{c.TypeName}»).");
                continue;
            }
            if (c.Offset >= 0) continue;
            var prev = a.Chunks.Take(i).LastOrDefault(x => x.Offset >= 0 && x.Offset < d.Length);
            long start = -1;
            if (prev != null)
            {
                long pl = SelfLength(a, d, prev.Type, prev.Id, prev.Offset);
                if (pl > 0) start = prev.Offset + pl;
            }
            else
            {
                start = Sai2Format.HeaderSize + (long)a.Chunks.Count * Sai2Format.EntrySize;
            }
            if (start < 0 || start >= d.Length)
            {
                a.Add(Severity.Error, $"Запись №{i + 1} таблицы повреждена, найти её блок не удалось — блок будет исключён.");
                continue;
            }
            var blob = d.AsSpan((int)start);
            uint type = c.Type != 0 ? c.Type : GuessTypeFromContent(a, blob, c.Id);
            if (type == 0 || (c.Type != 0 && !ContentMatchesType(blob, type, c.Id) && SelfLength(a, d, type, c.Id, start) < 0))
            {
                a.Add(Severity.Error, $"Запись №{i + 1} таблицы повреждена, найти её блок не удалось — блок будет исключён.");
                continue;
            }
            if (type == Sai2Format.Layr && LayerRecord.TryPeekId(blob, out uint lid)) c.Id = lid;
            c.Type = type;
            c.Offset = start;
            c.Health = ChunkHealth.Ok;
            c.Note = null;
            c.Repositioned = true;
            a.Add(Severity.Warning, $"Запись №{i + 1} таблицы восстановлена: блок «{c.TypeName}» найден по смещению {start:N0}.");
        }
        a.Chunks.RemoveAll(c => c.Offset < 0);
    }

    // ------------------------------------------------------------- deep scan

    /// <summary>
    /// Header/table destroyed: find chunks by their own signatures
    /// ("layr", "dpcm", "jssf"), verify each one, then rebuild the table.
    /// </summary>
    private static void DeepScan(Sai2Analysis a, byte[] d)
    {
        a.TableRebuilt = true;
        var span = d.AsSpan();
        var layrPos = FindAll(span, "layr"u8);
        var dpcmPos = FindAll(span, "dpcm"u8);
        var jssfPos = FindAll(span, "jssf"u8).Select(p => p - 8).Where(p => p >= Sai2Format.HeaderSize).ToList();

        // Canvas size: header if plausible, otherwise from the first dense pixel stream.
        int channels = a.Header.PreviewChannels;
        if (a.Header.Width == 0 || !a.HeaderUsable)
        {
            foreach (long p in dpcmPos)
            {
                var r = DimensionInference.FromDense(span[(int)p..], false, [channels, 7 - channels]);
                if (r == null) continue;
                SetInferredSize(a, r);
                channels = r.Channels;
                break;
            }
        }
        if (a.Header.Width == 0)
        {
            a.Add(Severity.Error, "Размер холста определить не удалось: в файле не найдено целого сведённого изображения.");
        }

        var candidates = layrPos.Select(p => (p, Sai2Format.Layr))
            .Concat(dpcmPos.Select(p => (p, Sai2Format.Dpcm)))
            .Concat(jssfPos.Select(p => (p, Sai2Format.Thum)))
            .Where(t => t.p >= Sai2Format.HeaderSize)
            .OrderBy(t => t.p)
            .ToList();

        var found = new List<ChunkInfo>();
        long cursor = Sai2Format.HeaderSize;
        foreach (var (p, kind) in candidates)
        {
            if (p < cursor) continue;
            var blob = span[(int)p..];
            if (kind == Sai2Format.Layr)
            {
                if (!LayerRecord.TryPeekId(blob, out uint id)) continue;
                var l = LayerRecord.Parse(blob, id);
                if (!l.ParamsComplete) continue;
                found.Add(new ChunkInfo { Type = Sai2Format.Layr, Id = id, Offset = p, Length = l.ParsedLength, ExactLength = true });
                cursor = p + l.ParsedLength;
            }
            else if (kind == Sai2Format.Dpcm)
            {
                if (a.Header.Width == 0) continue;
                var ps = PixelStream.Parse(blob, (int)a.Header.Width, (int)a.Header.Height, false, null, [4, 3, 1], decode: false);
                if (!ps.StructureOk || ps.LengthCount == 0) continue;
                found.Add(new ChunkInfo { Type = Sai2Format.Dpcm, Offset = p, Length = ps.ParsedLength, ExactLength = true, Pixels = ps });
                cursor = p + ps.ParsedLength;
            }
            else
            {
                long jl = JssfCodec.MeasureLength(blob);
                if (jl <= 0) continue;
                found.Add(new ChunkInfo { Type = Sai2Format.Thum, Offset = p, Length = jl, ExactLength = true });
                cursor = p + jl;
            }
        }

        // Assign pixel streams: the first dense one before any layer is the merged image.
        var layers = found.Where(c => c.Type == Sai2Format.Layr).ToList();
        var pix = found.Where(c => c.Type == Sai2Format.Dpcm).ToList();
        var firstLayerOffset = layers.Count > 0 ? layers[0].Offset : long.MaxValue;
        var intg = pix.FirstOrDefault(c => c.Offset < firstLayerOffset && c.Pixels!.Dense);
        if (intg != null) { intg.Type = Sai2Format.Intg; intg.Id = 0; pix.Remove(intg); }

        if (pix.Count == layers.Count)
        {
            for (int i = 0; i < pix.Count; i++) { pix[i].Type = Sai2Format.Lpix; pix[i].Id = layers[i].Id; }
        }
        else
        {
            var used = new HashSet<ChunkInfo>();
            foreach (var px in pix)
            {
                var owner = layers.LastOrDefault(l => l.Offset < px.Offset && !used.Contains(l));
                if (owner == null) { px.Type = 0; continue; }
                used.Add(owner);
                px.Type = Sai2Format.Lpix;
                px.Id = owner.Id;
            }
            if (pix.Any(p => p.Type == 0))
                a.Add(Severity.Warning, "Часть найденных пиксельных данных не удалось сопоставить со слоями; они будут сохранены только как отдельные изображения.");
        }
        foreach (var c in found) c.Pixels = null; // re-parsed with decoding during validation
        a.Chunks = found.Where(c => c.Type != 0).ToList();

        long covered = a.Chunks.Sum(c => c.Length);
        long gaps = Math.Max(0, (a.LastNonZero + 1) - Sai2Format.HeaderSize - covered);
        a.Add(Severity.Warning, $"Таблица блоков восстановлена по содержимому: найдено слоёв — {layers.Count}, пиксельных блоков — {pix.Count + (intg != null ? 1 : 0)}{(a.Thumbnail != null ? ", миниатюра" : "")}.");
        if (gaps > 0)
            a.Add(Severity.Info, $"{gaps:N0} байт не удалось отнести ни к одному блоку (обычно это история действий или служебные данные — для рисунка они не нужны).");
        if (intg == null && a.Chunks.Count > 0)
            a.Add(Severity.Warning, "Сведённое изображение не найдено — при восстановлении будет создано пустое.");
    }

    private static List<long> FindAll(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle)
    {
        var r = new List<long>();
        int start = 0;
        while (start <= data.Length - needle.Length)
        {
            int i = data[start..].IndexOf(needle);
            if (i < 0) break;
            r.Add(start + i);
            start += i + 1;
        }
        return r;
    }

    // ------------------------------------------------------------ dimensions

    private static void SetInferredSize(Sai2Analysis a, DimensionInference.Result r)
    {
        a.Header.Width = r.Width;
        a.Header.Height = r.Height;
        a.Header.BackgroundFlags = (byte)(r.Channels == 4 ? (a.Header.BackgroundFlags & ~7) : ((a.Header.BackgroundFlags & ~7) | 1));
        a.DimensionsInferred = true;
        a.Add(Severity.Warning, $"Размер холста определён по сведённому изображению: {r.Width}×{r.Height}.");
    }

    private static void EnsureDimensions(Sai2Analysis a, byte[] d)
    {
        if (a.Header.Width > 0) return;
        a.Add(Severity.Error, $"Размер холста в заголовке повреждён ({a.OriginalHeader?.Width}×{a.OriginalHeader?.Height}).");
        var intg = a.Chunks.FirstOrDefault(c => c.Type == Sai2Format.Intg && c.Offset >= 0 && c.Offset < d.Length);
        if (intg == null) return;
        long next = a.Chunks.Where(x => x.Offset > intg.Offset).Select(x => x.Offset).DefaultIfEmpty((long)d.Length).Min();
        bool exact = next <= d.Length;
        var blob = d.AsSpan((int)intg.Offset, (int)(Math.Min(next, d.Length) - intg.Offset));
        int ch = a.Header.PreviewChannels;
        var r = DimensionInference.FromDense(blob, exact, [ch, 7 - ch]);
        if (r != null) SetInferredSize(a, r);
    }

    private static void CheckDimensionsAgainstComposite(Sai2Analysis a, byte[] d)
    {
        var intg = a.Composite;
        if (intg?.Pixels == null || intg.Health == ChunkHealth.Lost) return;
        if (intg.Pixels.IsHealthy) return;
        // The composite does not fit the header size: maybe the size is what got damaged.
        int ch = a.Header.PreviewChannels;
        var r = DimensionInference.FromDense(intg.Bytes(d), intg.ExactLength, [ch, 7 - ch]);
        if (r == null || (r.Width == a.Header.Width && r.Height == a.Header.Height)) return;
        var test = PixelStream.Parse(intg.Bytes(d), (int)r.Width, (int)r.Height, intg.ExactLength,
            (int)(Sai2Format.TilesFor(r.Width) * Sai2Format.TilesFor(r.Height)), [r.Channels]);
        if (!test.IsHealthy) return;
        a.Add(Severity.Error, $"Размер холста в заголовке ({a.Header.Width}×{a.Header.Height}) не совпадает с данными; верный размер — {r.Width}×{r.Height}.");
        a.Header.Width = r.Width;
        a.Header.Height = r.Height;
        a.DimensionsInferred = true;
        a.Issues.RemoveAll(i => i.Message.StartsWith("Сведённое изображение", StringComparison.Ordinal) || i.Message.StartsWith("Слой", StringComparison.Ordinal) || i.Message.StartsWith("Блок", StringComparison.Ordinal));
        ValidateChunks(a, d);
    }

    // ------------------------------------------------------------ validation

    private static void ValidateChunks(Sai2Analysis a, byte[] d)
    {
        int w = (int)a.Header.Width, h = (int)a.Header.Height;
        int previewCh = a.Header.PreviewChannels;

        foreach (var c in a.Chunks)
        {
            if (c.Offset < 0 || c.Offset >= d.Length)
            {
                c.Health = ChunkHealth.Lost;
                c.Note ??= "данные за концом файла (файл обрезан)";
                continue;
            }
            c.Health = ChunkHealth.Ok;
            c.Note = null;
            var blob = c.Bytes(d);
            if (blob.IndexOfAnyExcept((byte)0) < 0)
            {
                c.Health = ChunkHealth.Lost;
                c.Note = "блок заполнен нулями";
                continue;
            }

            if (c.Type == Sai2Format.Layr)
            {
                c.Layer = LayerRecord.Parse(blob, c.Id);
                c.Verified = c.Layer.Recognized;
                if (c.Layer.Recognized && !c.Layer.ParamsComplete)
                {
                    c.Health = ChunkHealth.Damaged;
                    c.Note = "повреждён список параметров слоя";
                }
            }
            else if (c.Type == Sai2Format.Intg)
            {
                if (w == 0) { c.Health = ChunkHealth.Damaged; c.Note = "размер холста неизвестен"; continue; }
                int total = Sai2Format.TilesFor((uint)w) * Sai2Format.TilesFor((uint)h);
                c.Pixels = PixelStream.Parse(blob, w, h, c.ExactLength, total, [previewCh, 7 - previewCh]);
                c.Verified = true;
                ApplyPixelHealth(c);
            }
            else if (Sai2Format.IsPixelChunk(c.Type))
            {
                if (w == 0) { c.Health = ChunkHealth.Damaged; c.Note = "размер холста неизвестен"; continue; }
                int[] chans = c.Type == Sai2Format.Mpix ? new[] { 1, 4, 3 } : new[] { 4, 3, 1 };
                c.Pixels = PixelStream.Parse(blob, w, h, c.ExactLength, null, chans);
                c.Verified = true;
                ApplyPixelHealth(c);
            }
            else if (c.Type == Sai2Format.Thum)
            {
                if (JssfCodec.LooksLikeJssf(blob))
                {
                    c.Verified = true;
                    if (JssfCodec.MeasureLength(blob) < 0) { c.Health = ChunkHealth.Damaged; c.Note = "миниатюра повреждена"; }
                }
            }
        }

        // Layer layout and tile-count field: confirm on this very file before relying on them.
        var layrs = a.Chunks.Where(c => c.Type == Sai2Format.Layr && c.Layer != null).ToList();
        a.LayerLayoutConfirmed = layrs.Any(c => c.Layer!.Recognized);
        foreach (var c in layrs)
        {
            if (!c.Layer!.Recognized && a.LayerLayoutConfirmed && c.Health != ChunkHealth.Lost)
            {
                c.Health = ChunkHealth.Lost;
                c.Note = "описание слоя повреждено";
            }
        }
        var pairs = layrs
            .Where(x => x.Layer!.Recognized)
            .Select(x => (lay: x, px: a.PixelsOf(x.Id)))
            .Where(t => t.px?.Pixels != null && t.px.Pixels.IsHealthy)
            .ToList();
        a.LayerTileCountConfirmed = pairs.Count > 0 && pairs.All(t => t.lay.Layer!.TileCount == (uint)t.px!.Pixels!.LengthCount);
        if (a.LayerTileCountConfirmed)
        {
            foreach (var l in layrs.Where(x => x.Layer!.Recognized))
            {
                var px = a.PixelsOf(l.Id);
                if (px?.Pixels == null || px.Pixels.IsHealthy || px.Health == ChunkHealth.Lost) continue;
                int expected = (int)l.Layer!.TileCount;
                if (expected == px.Pixels.LengthCount) continue;
                px.Pixels = PixelStream.Parse(px.Bytes(d), w, h, px.ExactLength, expected, [px.Pixels.Channels, 4, 3, 1]);
                ApplyPixelHealth(px);
            }
        }

        // Issues per chunk.
        foreach (var c in a.Chunks)
        {
            string what = c.Type == Sai2Format.Intg ? "Сведённое изображение"
                : c.Type == Sai2Format.Layr ? $"Слой #{c.Id}" + (c.Layer?.Name is { Length: > 0 } n ? $" «{n}»" : "")
                : Sai2Format.IsPixelChunk(c.Type) ? $"Слой #{c.Id}{LayerNameSuffix(a, c.Id)} — пиксели"
                : $"Блок «{c.TypeName}» ({Sai2Format.DescribeChunk(c.Type)})";
            if (c.Health == ChunkHealth.Lost)
                a.Add(Severity.Error, $"{what}: утерян — {c.Note}.");
            else if (c.Health == ChunkHealth.Damaged)
            {
                string detail = c.Pixels != null
                    ? $"повреждено плиток {c.Pixels.CorruptTiles}, отсутствует {c.Pixels.MissingTiles} из {c.Pixels.LengthCount} (плитка = 256×256 пикселей)"
                    : c.Note ?? "повреждён";
                a.Add(Severity.Error, $"{what}: {detail}.");
            }
        }
    }

    private static string LayerNameSuffix(Sai2Analysis a, uint id)
    {
        var l = a.Chunks.FirstOrDefault(c => c.Type == Sai2Format.Layr && c.Id == id);
        return l?.Layer?.Name is { Length: > 0 } n ? $" «{n}»" : "";
    }

    private static void ApplyPixelHealth(ChunkInfo c)
    {
        var ps = c.Pixels!;
        if (ps.LengthCount == 0 && !ps.StructureOk)
        {
            c.Health = ChunkHealth.Lost;
            c.Note = ps.StructureError ?? "данные не читаются";
        }
        else if (!ps.IsHealthy)
        {
            c.Health = ChunkHealth.Damaged;
            c.Note = ps.StructureError;
        }
    }

    private static void CheckLinks(Sai2Analysis a)
    {
        foreach (var l in a.Layers)
        {
            if (l.Health == ChunkHealth.Lost || l.Layer == null || !l.Layer.Recognized) continue;
            if (l.Layer.Kind == "norm" && a.PixelsOf(l.Id) == null)
                a.Add(Severity.Warning, $"Слой #{l.Id}{LayerNameSuffix(a, l.Id)}: нет блока пикселей.");
        }
        foreach (var p in a.Chunks.Where(c => c.Type == Sai2Format.Lpix))
        {
            if (!a.Chunks.Any(c => c.Type == Sai2Format.Layr && c.Id == p.Id && c.Health != ChunkHealth.Lost))
                a.Add(Severity.Warning, $"Пиксели слоя #{p.Id} есть, а описание слоя утеряно — слой будет сохранён только как картинка.");
        }
    }

    private static void CheckTail(Sai2Analysis a)
    {
        if (a.Chunks.Count == 0) return;
        if (a.Chunks.Any(c => c.Note?.Contains("за концом файла") == true))
            a.Add(Severity.Warning, $"Файл обрезан: часть блоков указывает за его конец ({a.Data.Length:N0} байт). Обычно это сбой при сохранении или копировании.");
        foreach (var (start, length) in a.ZeroRegions)
            a.Add(Severity.Info, $"Обнаружена область нулей: {length:N0} байт со смещения {start:N0} (признак сбоя записи на диск).");
    }

    private static void FindZeroRegions(Sai2Analysis a)
    {
        var d = a.Data.AsSpan();
        int i = 0;
        while (i < d.Length)
        {
            int z = d[i..].IndexOf((byte)0);
            if (z < 0) break;
            int start = i + z;
            int nz = d[start..].IndexOfAnyExcept((byte)0);
            int end = nz < 0 ? d.Length : start + nz;
            if (end - start >= ZeroRegionMin) a.ZeroRegions.Add((start, end - start));
            i = end;
        }
        if (a.ZeroRegions.Count > 20)
            a.ZeroRegions = a.ZeroRegions.OrderByDescending(r => r.Length).Take(20).OrderBy(r => r.Start).ToList();
    }

    // --------------------------------------------------------------- verdict

    private static Verdict DecideVerdict(Sai2Analysis a)
    {
        if (a.Issues.Any(i => i.Severity == Severity.Fatal) || a.Chunks.Count == 0) return Verdict.Unrecoverable;
        bool anyData = a.Chunks.Any(c => c.Health != ChunkHealth.Lost &&
            (c.Type == Sai2Format.Intg || Sai2Format.IsPixelChunk(c.Type) || c.Type == Sai2Format.Thum));
        if (!anyData) return Verdict.Unrecoverable;
        bool imageLoss = a.Chunks.Any(c =>
            (c.Type == Sai2Format.Layr || c.Type == Sai2Format.Intg || Sai2Format.IsPixelChunk(c.Type)) && c.Health != ChunkHealth.Ok);
        if (imageLoss) return Verdict.PartiallyRecoverable;
        return a.HasErrors || a.Issues.Any(i => i.Severity == Severity.Warning) || a.TableRebuilt
            ? Verdict.Repairable
            : Verdict.Healthy;
    }
}
