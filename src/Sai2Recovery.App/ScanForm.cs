using Sai2Recovery.Core;
using Sai2Recovery.Core.Scan;

namespace Sai2Recovery.App;

/// <summary>Search for SAI2 data by content in a folder (and inside files with deep search).</summary>
internal sealed class ScanForm : Form
{
    private readonly TextBox _folder = new();
    private readonly CheckBox _deep = new();
    private readonly Button _start = new();
    private readonly Button _add = new();
    private readonly Button _close = new();
    private readonly ListView _list = new();
    private readonly Label _status = new();
    private CancellationTokenSource? _cts;

    public List<string> FilesToAdd { get; } = [];

    public ScanForm()
    {
        Text = "Поиск копий и фрагментов SAI2";
        Icon = Program.LoadIcon();
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(980, 600);
        MinimumSize = new Size(700, 400);
        Font = new Font("Segoe UI", 9.5f);

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 54,
            Padding = new Padding(8, 8, 8, 0),
            Text = "Программа ищет данные SAI2 по содержимому, а не по имени: переименованные и временные копии, файлы с другим " +
                   "расширением. Глубокий поиск проверяет каждый байт каждого файла — так находятся рисунки внутри образов дисков " +
                   "и фрагментов, восстановленных программами вроде PhotoRec/Recuva (медленно).",
        };

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 40, ColumnCount = 4, Padding = new Padding(6, 4, 6, 4) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _folder.Dock = DockStyle.Fill;
        _folder.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var browse = new Button { Text = "Обзор…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text, Description = "Где искать", UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
        };
        _deep.Text = "Глубокий поиск (внутри файлов)";
        _deep.AutoSize = true;
        _deep.Anchor = AnchorStyles.Left;
        _start.Text = "Искать";
        _start.AutoSize = true;
        _start.Click += async (_, _) => await StartOrCancel();
        top.Controls.Add(_folder, 0, 0);
        top.Controls.Add(browse, 1, 0);
        top.Controls.Add(_deep, 2, 0);
        top.Controls.Add(_start, 3, 0);

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.CheckBoxes = true;
        _list.Dock = DockStyle.Fill;
        _list.Columns.Add("Файл", 420);
        _list.Columns.Add("Где", 140);
        _list.Columns.Add("Размер", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Изменён", 130);
        _list.Columns.Add("Состояние", 200);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        _close.Text = "Закрыть";
        _close.AutoSize = true;
        _close.Click += (_, _) => { _cts?.Cancel(); DialogResult = FilesToAdd.Count > 0 ? DialogResult.OK : DialogResult.Cancel; };
        _add.Text = "Добавить отмеченные в список восстановления";
        _add.AutoSize = true;
        _add.Enabled = false;
        _add.Click += (_, _) => AddChecked();
        _status.AutoSize = true;
        _status.Padding = new Padding(0, 8, 20, 0);
        bottom.Controls.Add(_close);
        bottom.Controls.Add(_add);
        bottom.Controls.Add(_status);

        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(top);
        Controls.Add(hint);
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private async Task StartOrCancel()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        string folder = _folder.Text.Trim();
        if (!Directory.Exists(folder))
        {
            MessageBox.Show(this, "Папка не найдена.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _list.Items.Clear();
        _cts = new CancellationTokenSource();
        _start.Text = "Остановить";
        bool deep = _deep.Checked;
        var progress = new Progress<string>(p => _status.Text = "Проверяется: " + Shorten(p));
        List<FoundCopy> found = [];
        try
        {
            var token = _cts.Token;
            found = await Task.Run(() => CopyScanner.ScanFolder(folder, deep, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Поиск остановлен.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "Ошибка: " + ex.Message;
        }

        foreach (var c in found.OrderByDescending(c => c.Modified))
        {
            var item = new ListViewItem(new[]
            {
                c.Path,
                c.Offset > 0 ? $"внутри, смещение {c.Offset:N0}" : "весь файл",
                $"{c.Size / 1024.0:N0} КБ",
                c.Modified.ToString("dd.MM.yyyy HH:mm"),
                "…",
            }) { Tag = c, Checked = false };
            _list.Items.Add(item);
        }
        _status.Text = $"Найдено: {found.Count}";
        _start.Text = "Искать";
        _add.Enabled = found.Count > 0;
        _cts = null;

        // Health check in the background (cheap compared to the scan).
        foreach (ListViewItem item in _list.Items)
        {
            var c = (FoundCopy)item.Tag!;
            try
            {
                string health = await Task.Run(() => CopyScanner.DescribeHealth(
                    c.Offset == 0 ? File.ReadAllBytes(c.Path) : CopyScanner.Extract(c.Path, c.Offset)));
                if (item.ListView != null) item.SubItems[4].Text = health;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OutOfMemoryException)
            {
                if (item.ListView != null) item.SubItems[4].Text = "не читается";
            }
        }
    }

    private void AddChecked()
    {
        var checkedItems = _list.CheckedItems.Cast<ListViewItem>().Select(i => (FoundCopy)i.Tag!).ToList();
        if (checkedItems.Count == 0)
        {
            MessageBox.Show(this, "Отметьте галочками нужные находки.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        string? extractDir = null;
        foreach (var c in checkedItems)
        {
            if (c.Offset == 0)
            {
                FilesToAdd.Add(c.Path);
                continue;
            }
            if (extractDir == null)
            {
                using var dlg = new FolderBrowserDialog { Description = "Куда сохранить извлечённые фрагменты SAI2", UseDescriptionForTitle = true };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                extractDir = dlg.SelectedPath;
            }
            var data = CopyScanner.Extract(c.Path, c.Offset);
            string name = $"{Path.GetFileNameWithoutExtension(c.Path)}_найдено_{c.Offset}.sai2";
            string target = Path.Combine(extractDir, name);
            File.WriteAllBytes(target, data);
            FilesToAdd.Add(target);
        }
        DialogResult = DialogResult.OK;
    }

    private static string Shorten(string p) => p.Length <= 90 ? p : "…" + p[^88..];
}
