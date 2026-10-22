using Sai2Recovery.Core.Analysis;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Model;

namespace Sai2Recovery.Core.Repair;

public sealed class RepairResult
{
    public byte[] Output = [];
    public List<string> Actions = [];
    public List<string> Losses = [];
    /// <summary>Analysis of the repaired file (should be free of errors).</summary>
    public Sai2Analysis? Verification;
    public bool Success => Output.Length > 0;
}

/// <summary>
/// Writes a new, structurally valid .sai2 from an analysis:
/// fixed header, rebuilt table, chunks laid out back to back. Readable data is
/// copied byte for byte; damaged pixel tiles keep their readable rows; layers
/// whose description or pixels are gone are removed so SAI2 can open the rest.
/// The original file is never modified.
/// </summary>
public static class Sai2Repairer
{
    private sealed record Planned(uint Type, uint Id, byte[] Data);

    public static RepairResult Repair(Sai2Analysis a)
    {
        var result = new RepairResult();
        if (a.Verdict == Verdict.Unrecoverable || a.Chunks.Count == 0 || a.Header.Width == 0)
        {
            result.Losses.Add("Файл не содержит пригодных для восстановления данных.");
            return result;
        }

        var d = a.Data;
        var header = a.Header.Clone();
        int w = (int)header.Width, h = (int)header.Height;
        var plan = new List<Planned>();
        var lostLayers = new HashSet<uint>();
        var layerName = new Dictionary<uint, string>();
        foreach (var l in a.Layers)
            if (l.Layer?.Name is { Length: > 0 } n) layerName[l.Id] = n;
        string LayerLabel(uint id) => layerName.TryGetValue(id, out var n) ? $"#{id} «{n}»" : $"#{id}";

        if (!a.MagicIntact) result.Actions.Add("Восстановлена сигнатура SAI-CANVAS-TYPE0.");
        if (a.BaseOffset > 0) result.Actions.Add($"Отброшены {a.BaseOffset:N0} байт мусора перед заголовком.");
        if (a.DimensionsInferred) result.Actions.Add($"Записан верный размер холста {w}×{h}.");
        if (a.TableRebuilt) result.Actions.Add("Таблица блоков построена заново по найденным блокам.");

        foreach (var c in a.Chunks.OrderBy(c => c.Offset))
        {
            var blob = c.Bytes(d);
            string name = Sai2Format.TagToString(c.Type);

            if (c.Type == Sai2Format.Intg)
            {
                int total = Sai2Format.TilesFor((uint)w) * Sai2Format.TilesFor((uint)h);
                int ch = c.Pixels?.Channels ?? header.PreviewChannels;
                uint fill = ch == 3 ? 0xFFFFFFFFu : 0u;
                if (c.Health == ChunkHealth.Ok && c.Pixels != null)
                {
                    plan.Add(new Planned(c.Type, c.Id, Keep(c, blob, c.Pixels.ParsedLength)));
                }
                else if (c.Pixels != null && c.Pixels.LengthCount == total && c.Health != ChunkHealth.Lost)
                {
                    var data = c.Pixels.Rebuild(blob, fill, out int replaced, out _);
                    plan.Add(new Planned(c.Type, c.Id, data));
                    result.Actions.Add($"Сведённое изображение: заменено повреждённых плиток — {replaced} (читаемые строки сохранены).");
                }
                else
                {
                    plan.Add(new Planned(c.Type, c.Id, PixelStream.CreateBlank(w, h, ch, fill)));
                    result.Actions.Add("Сведённое изображение не читается — создано пустое (SAI2 пересчитает его при сохранении).");
                }
                continue;
            }

            if (c.Type == Sai2Format.Layr)
            {
                if (c.Health == ChunkHealth.Lost) { lostLayers.Add(c.Id); continue; }
                if (c.Health == ChunkHealth.Damaged && c.Layer is { Recognized: true })
                {
                    // Keep the fixed part and every complete parameter, then terminate the list.
                    int keep = (int)Math.Min(blob.Length, c.Layer.ParsedLength);
                    var fixedLayr = new byte[keep + 4];
                    blob[..keep].CopyTo(fixedLayr);
                    plan.Add(new Planned(c.Type, c.Id, fixedLayr));
                    result.Actions.Add($"Слой {LayerLabel(c.Id)}: восстановлен список параметров.");
                    continue;
                }
                long len = c.Layer is { Recognized: true, ParamsComplete: true } ? c.Layer.ParsedLength : blob.Length;
                plan.Add(new Planned(c.Type, c.Id, Keep(c, blob, len)));
                continue;
            }

            if (Sai2Format.IsPixelChunk(c.Type))
            {
                if (c.Health == ChunkHealth.Ok && c.Pixels != null)
                {
                    plan.Add(new Planned(c.Type, c.Id, Keep(c, blob, c.Pixels.ParsedLength)));
                    continue;
                }
                if (c.Health == ChunkHealth.Damaged && c.Pixels != null && c.Pixels.LengthCount > 0)
                {
                    var data = c.Pixels.Rebuild(blob, 0u, out int replaced, out int count);
                    if (count != c.Pixels.LengthCount)
                    {
                        // Tile count changed: only safe if we know where the layer stores it.
                        var layr = plan.FindIndex(p => p.Type == Sai2Format.Layr && p.Id == c.Id);
                        if (a.LayerTileCountConfirmed && layr >= 0)
                        {
                            plan[layr] = plan[layr] with { Data = LayerRecord.WithTileCount(plan[layr].Data, (uint)count) };
                        }
                        else
                        {
                            lostLayers.Add(c.Id);
                            continue;
                        }
                    }
                    plan.Add(new Planned(c.Type, c.Id, data));
                    result.Actions.Add($"Слой {LayerLabel(c.Id)}: заменено повреждённых плиток — {replaced}, остальное сохранено.");
                    continue;
                }
                lostLayers.Add(c.Id);
                continue;
            }

            // Everything else: keep if readable, drop otherwise.
            if (c.Health == ChunkHealth.Ok && blob.Length > 0)
            {
                long len = blob.Length;
                if (c.Type == Sai2Format.Thum)
                {
                    long jl = Codecs.JssfCodec.MeasureLength(blob);
                    if (jl > 0) len = jl;
                }
                plan.Add(new Planned(c.Type, c.Id, Keep(c, blob, len)));
            }
            else
            {
                string what = $"{name} ({Sai2Format.DescribeChunk(c.Type)})";
                result.Actions.Add($"Удалён повреждённый служебный блок {what}.");
                if (c.Type == Sai2Format.Hist) result.Losses.Add("История действий (отмена) утеряна — на рисунок это не влияет.");
                if (c.Type == Sai2Format.Thum) result.Losses.Add("Миниатюра для проводника удалена — SAI2 создаст новую при сохранении.");
            }
        }

        // Layers that lost their description or pixels are removed entirely.
        if (lostLayers.Count > 0)
        {
            foreach (uint id in lostLayers.Order())
                result.Losses.Add($"Слой {LayerLabel(id)} утерян (нет данных) и удалён из файла.");
            plan.RemoveAll(p => lostLayers.Contains(p.Id) && p.Id != 0 &&
                (p.Type == Sai2Format.Layr || Sai2Format.IsPixelChunk(p.Type) || p.Type == Sai2Format.Mask || p.Type == Sai2Format.Liwk || p.Type == Sai2Format.Shap || p.Type == Sai2Format.Text));
        }
        // Pixel chunks without a layer description cannot be opened by SAI2 as layers.
        var layerIds = plan.Where(p => p.Type == Sai2Format.Layr).Select(p => p.Id).ToHashSet();
        int orphans = plan.RemoveAll(p => (p.Type == Sai2Format.Lpix || p.Type == Sai2Format.Fpix) && !layerIds.Contains(p.Id));
        if (orphans > 0) result.Losses.Add($"Пиксели без описания слоя ({orphans}) не попадут в .sai2, но сохраняются при экспорте в PNG.");

        if (!plan.Any(p => p.Type == Sai2Format.Layr))
            result.Losses.Add("В восстановленном файле не осталось ни одного слоя — используйте экспорт в PNG.");

        if (!layerIds.Contains(header.SelectedLayer) && layerIds.Count > 0)
        {
            header.SelectedLayer = plan.First(p => p.Type == Sai2Format.Layr).Id;
            result.Actions.Add("Исправлен номер выбранного слоя.");
        }

        result.Output = Write(header, plan);
        if (a.OriginalHeader != null && a.OriginalHeader.TableCount != header.TableCount && !a.TableRebuilt)
            result.Actions.Add($"Число блоков в таблице: {a.OriginalHeader.TableCount} → {header.TableCount}.");
        result.Verification = Sai2Analyzer.Analyze(result.Output);
        return result;
    }

    /// <summary>
    /// Chunks with a known extent are copied untouched; the last chunk (which
    /// runs to EOF) is cut to its parsed size to drop trailing garbage.
    /// </summary>
    private static byte[] Keep(ChunkInfo c, ReadOnlySpan<byte> blob, long parsedLength) =>
        c.ExactLength ? blob.ToArray() : blob[..(int)Math.Clamp(parsedLength, 0, blob.Length)].ToArray();

    private static byte[] Write(Sai2Header header, List<Planned> plan)
    {
        header.TableCount = (uint)plan.Count;
        long tableEnd = Sai2Format.HeaderSize + (long)plan.Count * Sai2Format.EntrySize;
        long total = tableEnd + plan.Sum(p => (long)p.Data.Length);
        var output = new byte[total];
        header.Write(output);
        long offset = tableEnd;
        for (int i = 0; i < plan.Count; i++)
        {
            new TableEntry(plan[i].Type, plan[i].Id, (ulong)offset)
                .Write(output.AsSpan(Sai2Format.HeaderSize + i * Sai2Format.EntrySize));
            plan[i].Data.CopyTo(output.AsSpan((int)offset));
            offset += plan[i].Data.Length;
        }
        return output;
    }
}
