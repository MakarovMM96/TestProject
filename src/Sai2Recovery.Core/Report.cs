using System.Text;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Model;
using Sai2Recovery.Core.Repair;

namespace Sai2Recovery.Core;

public static class Report
{
    public static string VerdictText(Verdict v) => v switch
    {
        Verdict.Healthy => "Файл исправен",
        Verdict.Repairable => "Повреждён, восстанавливается полностью",
        Verdict.PartiallyRecoverable => "Повреждён, восстанавливается частично",
        _ => "Восстановление невозможно",
    };

    public static string SeverityMark(Severity s) => s switch
    {
        Severity.Info => "  ·",
        Severity.Warning => "  !",
        Severity.Error => "  ✖",
        _ => "  ☠",
    };

    public static string Build(Sai2Analysis a, RepairResult? repair = null, IEnumerable<string>? exported = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Файл: {a.SourcePath ?? "(из памяти)"}");
        sb.AppendLine($"Размер: {a.OriginalLength:N0} байт");
        sb.AppendLine($"Итог: {VerdictText(a.Verdict)}");
        if (a.Header.Width > 0)
            sb.AppendLine($"Холст: {a.Header.Width}×{a.Header.Height}, разрешение {a.Header.Resolution} dpi");
        int layers = a.Layers.Count(l => l.Health != ChunkHealth.Lost);
        sb.AppendLine($"Блоков: {a.Chunks.Count}, слоёв: {layers}");
        sb.AppendLine();

        sb.AppendLine("Диагностика:");
        if (a.Issues.Count == 0) sb.AppendLine("  ✔ проблем не найдено");
        foreach (var i in a.Issues) sb.AppendLine($"{SeverityMark(i.Severity)} {i.Message}");
        sb.AppendLine();

        if (a.Chunks.Count > 0)
        {
            sb.AppendLine("Блоки файла:");
            foreach (var c in a.Chunks)
            {
                string state = c.Health switch
                {
                    ChunkHealth.Ok => c.Verified ? "цел" : "цел (не проверяется)",
                    ChunkHealth.Damaged => "повреждён",
                    _ => "утерян",
                };
                string extra = c.Layer?.Name is { Length: > 0 } n ? $" «{n}»" : "";
                if (c.Pixels != null && c.Pixels.LengthCount > 0)
                    extra += $", плиток {c.Pixels.LengthCount}, каналов {c.Pixels.Channels}";
                string off = c.Offset >= 0 ? $"{c.Offset,12:N0}" : "           ?";
                sb.AppendLine($"  {Sai2Format.TagToString(c.Type),-5} #{c.Id,-6} @{off}  {state}{extra}");
            }
            sb.AppendLine();
        }

        if (repair != null)
        {
            sb.AppendLine("Восстановление:");
            foreach (var s in repair.Actions) sb.AppendLine($"  ✔ {s}");
            foreach (var s in repair.Losses) sb.AppendLine($"  ✖ {s}");
            if (repair.Verification != null)
            {
                sb.AppendLine(repair.Verification.HasErrors
                    ? "  ! Проверка восстановленного файла нашла замечания:"
                    : "  ✔ Восстановленный файл прошёл повторную проверку.");
                foreach (var i in repair.Verification.Issues.Where(i => i.Severity >= Severity.Error))
                    sb.AppendLine($"    {i.Message}");
            }
            sb.AppendLine();
        }

        if (exported != null)
        {
            var list = exported.ToList();
            if (list.Count > 0)
            {
                sb.AppendLine("Сохранённые файлы:");
                foreach (var p in list) sb.AppendLine($"  {p}");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }
}
