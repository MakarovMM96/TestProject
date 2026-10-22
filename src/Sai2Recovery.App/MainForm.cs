using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Sai2Recovery.Core;
using Sai2Recovery.Core.Analysis;
using Sai2Recovery.Core.Export;
using Sai2Recovery.Core.Imaging;
using Sai2Recovery.Core.Model;

namespace Sai2Recovery.App;

internal sealed class FileEntry
{
    public required string Path;
    public Sai2Analysis? Analysis;
    public RecoveryOutcome? Outcome;
    public Image? Preview;
    public ListViewItem? Item;
    public bool Busy;
}

internal sealed class MainForm : Form
{
    private readonly List<FileEntry> _entries = [];
    private readonly ListView _list = new();
    private readonly PictureBox _preview = new();
    private readonly TextBox _report = new();
    private readonly ToolStripStatusLabel _status = new();
    private readonly ToolStripProgressBar _progress = new();
    private readonly ToolStripMenuItem _optSai2 = new("Создавать исправленный файл .sai2") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _optPng = new("Сохранять сведённое изображение (PNG)") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _optLayers = new("Сохранять каждый слой (PNG)") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _optOra = new("Сохранять слои в OpenRaster (.ora) для Krita/GIMP") { CheckOnClick = true, Checked = true };
    private readonly ToolStripMenuItem _optFolderNear = new("Папка результата: рядом с исходным файлом") { Checked = true };
    private readonly ToolStripMenuItem _optFolderPick = new("Папка результата: выбрать…");
    private string? _customOutput;
    private ToolStripButton _btnRecover = null!;
    private ToolStripButton _btnRecoverAll = null!;
    private ToolStripButton _btnOpen = null!;

    public MainForm(string[] args)
    {
        Text = "SAI2 Recovery — восстановление файлов PaintTool SAI 2";
        Icon = Program.LoadIcon();
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 560);
        Size = new Size(1180, 740);
        Font = new Font("Segoe UI", 9.5f);
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        BuildLayout();
        UpdateButtons();

        Shown += (_, _) =>
        {
            var files = args.Where(File.Exists).ToArray();
            if (files.Length > 0) AddFiles(files);
            else ShowWelcome();
        };
    }

    // ------------------------------------------------------------------ UI

    private void BuildLayout()
    {
        var tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4), ImageScalingSize = new Size(20, 20) };
        var btnAdd = new ToolStripButton("➕ Добавить файлы…") { ToolTipText = "Выбрать повреждённые файлы .sai2 (можно перетащить их в окно)" };
        btnAdd.Click += (_, _) => PickFiles();
        var btnScan = new ToolStripButton("🔍 Найти копии…") { ToolTipText = "Поиск файлов SAI2 по содержимому: переименованные, временные, внутри других файлов" };
        btnScan.Click += (_, _) => OpenScanner();
        _btnRecover = new ToolStripButton("🛠 Восстановить выбранные") { Font = new Font(Font, FontStyle.Bold) };
        _btnRecover.Click += async (_, _) => await RecoverAsync(SelectedEntries());
        _btnRecoverAll = new ToolStripButton("Восстановить все");
        _btnRecoverAll.Click += async (_, _) => await RecoverAsync(_entries.ToList());
        _btnOpen = new ToolStripButton("📂 Открыть папку результата");
        _btnOpen.Click += (_, _) => OpenResultFolder();

        var options = new ToolStripDropDownButton("⚙ Параметры");
        _optFolderNear.Click += (_, _) => { _customOutput = null; _optFolderNear.Checked = true; _optFolderPick.Checked = false; _optFolderPick.Text = "Папка результата: выбрать…"; };
        _optFolderPick.Click += (_, _) => PickOutputFolder();
        options.DropDownItems.AddRange(new ToolStripItem[]
        {
            _optSai2, _optPng, _optLayers, _optOra, new ToolStripSeparator(), _optFolderNear, _optFolderPick,
        });

        var help = new ToolStripButton("❓ Справка") { Alignment = ToolStripItemAlignment.Right };
        help.Click += (_, _) => ShowHelp();
        var clear = new ToolStripButton("Очистить список") { Alignment = ToolStripItemAlignment.Right };
        clear.Click += (_, _) => ClearList();

        tools.Items.AddRange(new ToolStripItem[]
        {
            btnAdd, btnScan, new ToolStripSeparator(), _btnRecover, _btnRecoverAll, new ToolStripSeparator(), _btnOpen, options, help, clear,
        });

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Файл", 230);
        _list.Columns.Add("Размер", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Холст", 90);
        _list.Columns.Add("Состояние", 250);
        _list.SelectedIndexChanged += (_, _) => { ShowSelected(); UpdateButtons(); };
        _list.DoubleClick += (_, _) => OpenResultFolder();
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };

        _preview.Dock = DockStyle.Fill;
        _preview.SizeMode = PictureBoxSizeMode.Zoom;
        _preview.BackColor = Color.FromArgb(48, 48, 52);

        _report.Dock = DockStyle.Fill;
        _report.Multiline = true;
        _report.ReadOnly = true;
        _report.ScrollBars = ScrollBars.Both;
        _report.WordWrap = false;
        _report.Font = new Font("Consolas", 9.5f);
        _report.BackColor = SystemColors.Window;

        var right = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        right.Panel1.Controls.Add(_preview);
        right.Panel2.Controls.Add(_report);

        var main = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        main.Panel1.Controls.Add(_list);
        main.Panel2.Controls.Add(right);

        var statusStrip = new StatusStrip();
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Text = "Перетащите файлы .sai2 в окно или нажмите «Добавить файлы…»";
        _progress.Visible = false;
        _progress.Style = ProgressBarStyle.Marquee;
        statusStrip.Items.AddRange(new ToolStripItem[] { _status, _progress });

        Controls.Add(main);
        Controls.Add(tools);
        Controls.Add(statusStrip);

        Load += (_, _) =>
        {
            main.SplitterDistance = Math.Max(300, (int)(ClientSize.Width * 0.42));
            right.SplitterDistance = Math.Max(150, (int)(right.Height * 0.48));
        };
    }

    private void UpdateButtons()
    {
        bool busy = _entries.Any(e => e.Busy);
        var sel = SelectedEntries();
        _btnRecover.Enabled = !busy && sel.Any(e => e.Analysis != null && e.Analysis.Verdict != Verdict.Unrecoverable);
        _btnRecoverAll.Enabled = !busy && _entries.Any(e => e.Analysis != null && e.Analysis.Verdict != Verdict.Unrecoverable);
        _btnOpen.Enabled = sel.Any(e => e.Outcome != null);
        _progress.Visible = busy;
    }

    private List<FileEntry> SelectedEntries() =>
        _list.SelectedItems.Cast<ListViewItem>().Select(i => (FileEntry)i.Tag!).ToList();

    // ------------------------------------------------------------- files

    private void PickFiles()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Выберите файлы SAI2",
            Filter = "Файлы PaintTool SAI 2 (*.sai2)|*.sai2|Все файлы (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddFiles(dlg.FileNames);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
        var files = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.EnumerateFiles(p, "*.sai2", SearchOption.AllDirectories));
            else if (File.Exists(p)) files.Add(p);
        }
        AddFiles(files);
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        var added = new List<FileEntry>();
        foreach (var raw in paths)
        {
            string p = Path.GetFullPath(raw);
            if (_entries.Any(e => string.Equals(e.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new FileEntry { Path = p };
            var size = new FileInfo(p).Length;
            entry.Item = new ListViewItem(new[] { Path.GetFileName(p), FormatSize(size), "", "проверка…" }) { Tag = entry, ToolTipText = p };
            _list.Items.Add(entry.Item);
            _entries.Add(entry);
            added.Add(entry);
        }
        if (added.Count == 0) return;
        _list.SelectedItems.Clear();
        added[0].Item!.Selected = true;
        _ = AnalyzeAsync(added);
    }

    private async Task AnalyzeAsync(List<FileEntry> entries)
    {
        foreach (var e in entries) e.Busy = true;
        UpdateButtons();
        foreach (var e in entries)
        {
            _status.Text = $"Проверка: {Path.GetFileName(e.Path)}";
            try
            {
                var (analysis, preview) = await Task.Run(() =>
                {
                    var a = Sai2Analyzer.AnalyzeFile(e.Path);
                    return (a, MakePreview(a));
                });
                e.Analysis = analysis;
                e.Preview = preview;
                UpdateItem(e);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                e.Item!.SubItems[3].Text = "ошибка чтения: " + ex.Message;
                e.Item.ForeColor = Color.Firebrick;
            }
            e.Busy = false;
            if (e.Item!.Selected) ShowSelected();
        }
        _status.Text = $"Проверено файлов: {entries.Count}. Выберите файлы и нажмите «Восстановить».";
        UpdateButtons();
    }

    private void UpdateItem(FileEntry e)
    {
        var a = e.Analysis!;
        var item = e.Item!;
        item.SubItems[2].Text = a.Header.Width > 0 ? $"{a.Header.Width}×{a.Header.Height}" : "?";
        item.SubItems[3].Text = e.Outcome != null ? "восстановлен ✔ — " + Report.VerdictText(a.Verdict).ToLowerInvariant() : Report.VerdictText(a.Verdict);
        item.ForeColor = a.Verdict switch
        {
            Verdict.Healthy => Color.DarkGreen,
            Verdict.Repairable => Color.DarkGoldenrod,
            Verdict.PartiallyRecoverable => Color.Chocolate,
            _ => Color.Firebrick,
        };
    }

    private static Image? MakePreview(Sai2Analysis a)
    {
        try
        {
            var img = Sai2Exporter.RenderComposite(a);
            if (img != null) return ToBitmap(OraWriter.Downscale(img, 1600));
            var jpg = Sai2Exporter.ThumbnailJpeg(a);
            if (jpg != null)
            {
                using var ms = new MemoryStream(jpg);
                using var tmp = Image.FromStream(ms);
                return new Bitmap(tmp);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            // preview is optional
        }
        return null;
    }

    private static Bitmap ToBitmap(BgraImage img)
    {
        var bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < img.Height; y++)
            {
                var row = new int[img.Width];
                Buffer.BlockCopy(img.Pixels, y * img.Width * 4, row, 0, img.Width * 4);
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, img.Width);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private void ShowSelected()
    {
        var sel = SelectedEntries();
        if (sel.Count != 1)
        {
            _preview.Image = null;
            _report.Text = sel.Count > 1 ? $"Выбрано файлов: {sel.Count}" : "";
            return;
        }
        var e = sel[0];
        _preview.Image = e.Preview;
        if (e.Outcome != null) _report.Text = Nl(e.Outcome.ReportText);
        else if (e.Analysis != null) _report.Text = Nl(Report.Build(e.Analysis));
        else _report.Text = "Идёт проверка…";
    }

    private static string Nl(string s) => s.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    private void RemoveSelected()
    {
        foreach (var e in SelectedEntries().Where(x => !x.Busy))
        {
            _list.Items.Remove(e.Item!);
            _entries.Remove(e);
        }
        ShowSelected();
        UpdateButtons();
    }

    private void ClearList()
    {
        if (_entries.Any(e => e.Busy)) return;
        _entries.Clear();
        _list.Items.Clear();
        ShowSelected();
        UpdateButtons();
    }

    // ------------------------------------------------------------ recovery

    private RecoveryOptions CurrentOptions() => new()
    {
        WriteSai2 = _optSai2.Checked,
        Images = new ExportOptions
        {
            Composite = _optPng.Checked,
            Thumbnail = _optPng.Checked,
            Layers = _optLayers.Checked,
            Ora = _optOra.Checked,
        },
    };

    private string OutputFolderFor(FileEntry e)
    {
        if (_customOutput == null) return RecoveryService.DefaultOutputFolder(e.Path);
        return Path.Combine(_customOutput, Path.GetFileNameWithoutExtension(e.Path) + "_восстановление");
    }

    private async Task RecoverAsync(List<FileEntry> entries)
    {
        entries = entries.Where(e => e.Analysis != null && e.Analysis.Verdict != Verdict.Unrecoverable && !e.Busy).ToList();
        if (entries.Count == 0) return;
        var options = CurrentOptions();
        foreach (var e in entries) e.Busy = true;
        UpdateButtons();
        int ok = 0;
        foreach (var e in entries)
        {
            _status.Text = $"Восстановление: {Path.GetFileName(e.Path)}";
            e.Item!.SubItems[3].Text = "восстановление…";
            try
            {
                string folder = OutputFolderFor(e);
                e.Outcome = await Task.Run(() => RecoveryService.Recover(e.Analysis!, folder, options));
                ok++;
                UpdateItem(e);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                e.Item.SubItems[3].Text = "ошибка: " + ex.Message;
                e.Item.ForeColor = Color.Firebrick;
            }
            e.Busy = false;
            if (e.Item.Selected) ShowSelected();
        }
        _status.Text = $"Готово: восстановлено {ok} из {entries.Count}. Результаты — в папках «…_восстановление».";
        UpdateButtons();
        if (entries.Count == 1 && entries[0].Outcome != null) OpenResultFolder();
    }

    private void OpenResultFolder()
    {
        var e = SelectedEntries().FirstOrDefault(x => x.Outcome != null);
        if (e?.Outcome == null || !Directory.Exists(e.Outcome.OutputFolder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{e.Outcome.OutputFolder}\"") { UseShellExecute = true });
    }

    private void PickOutputFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Куда сохранять результаты восстановления", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _customOutput = dlg.SelectedPath;
        _optFolderNear.Checked = false;
        _optFolderPick.Checked = true;
        _optFolderPick.Text = $"Папка результата: {dlg.SelectedPath}";
    }

    private void OpenScanner()
    {
        using var f = new ScanForm();
        if (f.ShowDialog(this) == DialogResult.OK && f.FilesToAdd.Count > 0) AddFiles(f.FilesToAdd);
    }

    // ---------------------------------------------------------------- text

    private void ShowWelcome()
    {
        _report.Text = Nl("""
            Добро пожаловать в SAI2 Recovery.

            1. Перетащите в окно повреждённые файлы .sai2 (или нажмите «Добавить файлы…»).
            2. Программа проверит каждый файл и покажет, что в нём цело, а что повреждено.
            3. Нажмите «Восстановить» — рядом с файлом появится папка «…_восстановление»:
                 • исправленный файл …_восстановлен.sai2 — откройте его в SAI2;
                 • сведённое изображение и все слои в PNG;
                 • слои.ora — все слои одним файлом для Krita / GIMP;
                 • отчёт.txt — что было сделано.

            Исходные файлы никогда не изменяются.
            """);
    }

    private void ShowHelp()
    {
        _list.SelectedItems.Clear();
        _report.Text = Nl("""
            КАК РАБОТАЕТ ВОССТАНОВЛЕНИЕ

            Файл .sai2 состоит из заголовка, таблицы блоков и самих блоков: слои, их пиксели
            (плитками 256×256), сведённое изображение, миниатюра, история. Программа:

              1. ищет заголовок (в т.ч. если он сдвинут или его сигнатура испорчена);
              2. читает таблицу блоков, исправляет число записей и повреждённые записи —
                 находит блоки по их собственным сигнатурам;
              3. если начало файла затёрто, строит таблицу заново, просматривая весь файл;
              4. восстанавливает размер холста по сведённому изображению;
              5. проверяет каждую плитку каждого слоя полным декодированием — формат сжатия
                 побайтно точен, поэтому повреждённые плитки обнаруживаются надёжно;
              6. собирает новый файл: целые данные копируются без изменений, в повреждённых
                 плитках сохраняются читаемые строки, остальное становится прозрачным;
              7. проверяет собранный файл ещё раз и экспортирует всё читаемое в PNG/ORA.

            ЕСЛИ ФАЙЛ ЦЕЛИКОМ ИЗ НУЛЕЙ ИЛИ ПУСТОЙ
              Данных в нём физически нет. Попробуйте:
                • «Найти копии…» в папке с рисунками и во временной папке (%TEMP%);
                • правый клик по файлу → Свойства → «Предыдущие версии» (если включена
                  история файлов или точки восстановления);
                • облачное хранилище (OneDrive, Яндекс Диск, Google Drive) — у них есть
                  история версий;
                • программы восстановления удалённых файлов, затем «Найти копии» с
                  глубоким поиском по восстановленным фрагментам.

            ОГРАНИЧЕНИЯ
              Формат SAI2 закрытый и описан энтузиастами не полностью. Слои с пикселями
              проверяются и восстанавливаются, но служебные блоки (история, линии,
              фигуры, текст) переносятся без проверки, а повреждённые — удаляются.
              Поэтому, кроме .sai2, всегда сохраняются PNG и ORA — они открываются в
              любом редакторе, даже если SAI2 откажется открыть исправленный файл.
            """);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} МБ" : bytes >= 1024 ? $"{bytes / 1024.0:0} КБ" : $"{bytes} Б";
}
