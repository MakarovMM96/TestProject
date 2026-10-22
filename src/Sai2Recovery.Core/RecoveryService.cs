using Sai2Recovery.Core.Analysis;
using Sai2Recovery.Core.Export;
using Sai2Recovery.Core.Model;
using Sai2Recovery.Core.Repair;

namespace Sai2Recovery.Core;

public sealed class RecoveryOptions
{
    /// <summary>Write a repaired .sai2 next to the exported images.</summary>
    public bool WriteSai2 = true;
    public ExportOptions Images = new();
    /// <summary>Write report.txt into the output folder.</summary>
    public bool WriteReport = true;
}

public sealed class RecoveryOutcome
{
    public required Sai2Analysis Analysis;
    public RepairResult? Repair;
    public string OutputFolder = "";
    public string? RepairedFile;
    public List<string> Files = [];
    public string ReportText = "";
}

/// <summary>One-call pipeline used by the GUI and the CLI.</summary>
public static class RecoveryService
{
    public static string DefaultOutputFolder(string sourcePath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        string name = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(dir, $"{name}_восстановление");
    }

    public static RecoveryOutcome Recover(string sourcePath, string? outputFolder = null, RecoveryOptions? options = null)
    {
        options ??= new RecoveryOptions();
        var bytes = File.ReadAllBytes(sourcePath);
        var analysis = Sai2Analyzer.Analyze(bytes, sourcePath);
        return Recover(analysis, outputFolder ?? DefaultOutputFolder(sourcePath), options);
    }

    public static RecoveryOutcome Recover(Sai2Analysis analysis, string outputFolder, RecoveryOptions? options = null)
    {
        options ??= new RecoveryOptions();
        var outcome = new RecoveryOutcome { Analysis = analysis, OutputFolder = outputFolder };
        if (analysis.Verdict == Verdict.Unrecoverable)
        {
            outcome.ReportText = Report.Build(analysis);
            return outcome;
        }

        Directory.CreateDirectory(outputFolder);
        string baseName = analysis.SourcePath != null ? Path.GetFileNameWithoutExtension(analysis.SourcePath) : "recovered";

        if (options.WriteSai2)
        {
            outcome.Repair = Sai2Repairer.Repair(analysis);
            if (outcome.Repair.Success)
            {
                string p = Path.Combine(outputFolder, $"{baseName}_восстановлен.sai2");
                File.WriteAllBytes(p, outcome.Repair.Output);
                outcome.RepairedFile = p;
                outcome.Files.Add(p);
            }
        }

        outcome.Files.AddRange(Sai2Exporter.Export(analysis, outputFolder, options.Images));
        outcome.ReportText = Report.Build(analysis, outcome.Repair, outcome.Files);
        if (options.WriteReport)
        {
            string rp = Path.Combine(outputFolder, "отчёт.txt");
            File.WriteAllText(rp, outcome.ReportText, new System.Text.UTF8Encoding(true));
        }
        return outcome;
    }
}
