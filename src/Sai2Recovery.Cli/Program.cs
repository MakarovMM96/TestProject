using System.Text;
using Sai2Recovery.Core;
using Sai2Recovery.Core.Analysis;
using Sai2Recovery.Core.Model;
using Sai2Recovery.Core.Scan;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

try
{
    return args[0] switch
    {
        "analyze" => Analyze(args.Skip(1).ToArray()),
        "repair" => RepairCmd(args.Skip(1).ToArray()),
        "scan" => ScanCmd(args.Skip(1).ToArray()),
        "carve" => CarveCmd(args.Skip(1).ToArray()),
        _ => RepairCmd(args), // sai2fix file.sai2
    };
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Ошибка доступа к файлу: {ex.Message}");
    return 3;
}

static void PrintHelp()
{
    Console.WriteLine("""
        sai2fix — восстановление повреждённых файлов PaintTool SAI Ver.2 (.sai2)

        Использование:
          sai2fix analyze <файл.sai2> [...]          проверить файлы, ничего не меняя
          sai2fix repair  <файл.sai2> [-o папка] [--no-sai2] [--no-layers] [--no-ora]
                                                     восстановить: исправленный .sai2 + PNG + ORA + отчёт
          sai2fix scan    <папка> [--deep]           найти файлы SAI2 по содержимому (переименованные,
                                                     временные, внутри других файлов при --deep)
          sai2fix carve   <файл> [-o папка]          извлечь все SAI2, найденные внутри файла

        Исходные файлы никогда не изменяются.
        Коды возврата: 0 — исправен/восстановлен, 1 — восстановлен частично, 2 — невозможно, 3 — ошибка.
        """);
}

static int Analyze(string[] files)
{
    if (files.Length == 0) { PrintHelp(); return 3; }
    int worst = 0;
    foreach (var f in files)
    {
        var a = Sai2Analyzer.AnalyzeFile(f);
        Console.WriteLine(Report.Build(a));
        worst = Math.Max(worst, Code(a.Verdict));
    }
    return worst;
}

static int RepairCmd(string[] argv)
{
    string? output = null;
    var opts = new RecoveryOptions();
    var files = new List<string>();
    for (int i = 0; i < argv.Length; i++)
    {
        switch (argv[i])
        {
            case "-o": case "--out": output = argv[++i]; break;
            case "--no-sai2": opts.WriteSai2 = false; break;
            case "--no-layers": opts.Images.Layers = false; break;
            case "--no-ora": opts.Images.Ora = false; break;
            default: files.Add(argv[i]); break;
        }
    }
    if (files.Count == 0) { PrintHelp(); return 3; }
    int worst = 0;
    foreach (var f in files)
    {
        string? folder = output == null ? null : files.Count == 1 ? output : Path.Combine(output, Path.GetFileNameWithoutExtension(f));
        var r = RecoveryService.Recover(f, folder, opts);
        Console.WriteLine(r.ReportText);
        if (r.Files.Count > 0) Console.WriteLine($"Результат: {r.OutputFolder}");
        worst = Math.Max(worst, Code(r.Analysis.Verdict));
    }
    return worst;
}

static int ScanCmd(string[] argv)
{
    bool deep = argv.Contains("--deep");
    var folder = argv.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
    if (folder == null) { PrintHelp(); return 3; }
    var found = CopyScanner.ScanFolder(folder, deep);
    foreach (var c in found)
    {
        string where = c.Offset > 0 ? $" (внутри файла, смещение {c.Offset:N0})" : "";
        Console.WriteLine($"{c.Modified:yyyy-MM-dd HH:mm}  {c.Size,14:N0}  {c.Path}{where}");
    }
    Console.WriteLine($"Найдено: {found.Count}");
    return 0;
}

static int CarveCmd(string[] argv)
{
    string? output = null;
    string? file = null;
    for (int i = 0; i < argv.Length; i++)
    {
        if (argv[i] is "-o" or "--out") output = argv[++i];
        else file = argv[i];
    }
    if (file == null) { PrintHelp(); return 3; }
    output ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".", Path.GetFileNameWithoutExtension(file) + "_найдено");
    Directory.CreateDirectory(output);
    var hits = CopyScanner.ScanFile(file, deep: true);
    int n = 0;
    foreach (var off in hits)
    {
        var data = CopyScanner.Extract(file, off);
        string p = Path.Combine(output, $"найдено_{++n:D3}_смещение_{off}.sai2");
        File.WriteAllBytes(p, data);
        Console.WriteLine($"{p}  {data.Length:N0} байт  — {CopyScanner.DescribeHealth(data)}");
    }
    Console.WriteLine($"Извлечено: {n}");
    return n > 0 ? 0 : 2;
}

static int Code(Verdict v) => v switch
{
    Verdict.Healthy or Verdict.Repairable => 0,
    Verdict.PartiallyRecoverable => 1,
    _ => 2,
};
