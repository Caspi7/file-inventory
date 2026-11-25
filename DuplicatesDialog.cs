using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FileInventory;

sealed class DuplicatesDialog : Form
{
    readonly Catalog catalog;
    readonly ToolTip tips = new();
    readonly ComboBox scope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly NumericUpDown minimum = new() { Value = 1, DecimalPlaces = 2, Maximum = 1_000_000_000, Width = 100 };
    readonly Button find = new() { Text = "Find Duplicates", AutoSize = true };
    readonly Button verify = new() { Text = "Verify online copies (SHA-256)", AutoSize = true };
    readonly Button cancel = new() { Text = "Cancel", AutoSize = true };
    readonly Button copy = new() { Text = "Copy Path", AutoSize = true };
    readonly Button reveal = new() { Text = "Reveal in Explorer", AutoSize = true };
    readonly Button export = new() { Text = "Export CSV", AutoSize = true };
    readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 9 };
    readonly Label status = new() { AutoSize = true, Text = "Choose a scope and find duplicates." };
    readonly ListView groups = CreateList("Group #", "Reclaimable Space", "File Size", "Copies", "Verification Status");
    readonly ListView members = CreateList("Member path", "Drive", "Availability");
    List<Volume> volumes = [];
    List<DuplicateGroup> results = [];
    CancellationTokenSource? verification;
    bool resultsAreCrossDrive;
    bool busy;

    public DuplicatesDialog(Catalog catalog, AppTheme theme, string? initialVolumeId = null)
    {
        this.catalog = catalog;
        Text = "Duplicates";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(960, 590);
        MinimumSize = new Size(740, 460);

        var scopeLabel = new Label { Text = "Scope", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
        var sizeLabel = new Label { Text = "Min size (MB)", AutoSize = true, Margin = new Padding(8, 7, 4, 0) };
        var filters = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        filters.Controls.AddRange([scopeLabel, scope, sizeLabel, minimum, find, verify, cancel]);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.AddRange([copy, reveal, export]);
        members.Columns[0].Width = 540;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (Control control in new Control[] { filters, progress, status, groups, members, actions })
            layout.Controls.Add(control);
        Controls.Add(layout);

        foreach (var (control, description) in new (Control, string)[]
        {
            (scopeLabel, "Choose which drives to compare."),
            (scope, "All drives includes every candidate. Cross-drive only requires copies on different drives."),
            (sizeLabel, "Minimum file size in megabytes (1 MB = 1,048,576 bytes)."),
            (minimum, "Ignore files below this size. Enter 0 to include empty files."),
            (find, "Find candidate copies by catalog size and timestamps, including offline entries."),
            (verify, "Compare current online copies using SHA-256. The results show groups with matching full hashes."),
            (cancel, "Stop verification and keep the previous results."),
            (progress, "Shows when a search, verification, or export is running."),
            (status, "Reclaimable space assumes one copy per listed group is retained."),
            (groups, "Select a group to list its member paths below."),
            (members, "Select a member path to copy or reveal it."),
            (copy, "Copy the selected member's full path."),
            (reveal, "Select the current online copy in Windows Explorer."),
            (export, "Export every member in the displayed results to a CSV file.")
        })
            Theme.AttachTooltip(control, tips, description);

        find.Click += async (_, _) => await FindAsync();
        verify.Click += async (_, _) => await VerifyAsync();
        cancel.Click += (_, _) => verification?.Cancel();
        groups.SelectedIndexChanged += (_, _) => ShowMembers();
        members.SelectedIndexChanged += (_, _) => UpdateActions();
        copy.Click += (_, _) => CopyPath();
        reveal.Click += async (_, _) => await RevealAsync();
        export.Click += async (_, _) => await ExportAsync();

        Load += (_, _) =>
        {
            Theme.Apply(this, theme);
            Theme.StyleToolTip(tips);
            scope.Items.AddRange(["All drives", "Cross-drive only"]);
            scope.SelectedIndex = 0;
            try
            {
                volumes = catalog.Volumes();
                scope.Items.AddRange(volumes.Select(volume => volume.DisplayStatusLabel).ToArray());
                int initial = volumes.FindIndex(volume => volume.Id == initialVolumeId);
                if (initial >= 0) scope.SelectedIndex = initial + 2;
            }
            catch (Exception ex) { ShowError(ex); }
        };
        UpdateActions();
    }

    static ListView CreateList(params string[] columns)
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            MultiSelect = false, HideSelection = false };
        foreach (string column in columns) list.Columns.Add(column, 150);
        return list;
    }

    async Task FindAsync()
    {
        int selectedScope = scope.SelectedIndex;
        string? volumeId = selectedScope >= 2 ? volumes[selectedScope - 2].Id : null;
        long minimumBytes = (long)Math.Ceiling(minimum.Value * 1_048_576);
        await RunAsync("Finding duplicate candidates…", async () =>
        {
            var found = await Task.Run(() => DuplicateAnalyzer.FindDuplicateGroups(catalog, new(minimumBytes))
                .Select(group => volumeId is null ? group : group with
                {
                    Files = group.Files.Where(file => file.Volume == volumeId).ToArray()
                })
                .Where(group => group.Files.Count > 1 && (selectedScope != 1 || IsCrossDrive(group)))
                .ToList());
            if (IsDisposed) return;
            resultsAreCrossDrive = selectedScope == 1;
            ShowResults(found);
        });
    }

    async Task VerifyAsync()
    {
        using var source = new CancellationTokenSource();
        verification = source;
        try
        {
            await RunAsync("Verifying online copies…", async () =>
            {
                var verified = await DuplicateAnalyzer.VerifyGroupsAsync(catalog, results, source.Token);
                source.Token.ThrowIfCancellationRequested();
                if (IsDisposed) return;
                if (resultsAreCrossDrive) verified.RemoveAll(group => !IsCrossDrive(group));
                ShowResults(verified, "Verification complete");
            });
        }
        finally
        {
            verification = null;
            if (!IsDisposed) UpdateActions();
        }
    }

    static bool IsCrossDrive(DuplicateGroup group) =>
        group.Files.Any(file => file.Volume != group.Files[0].Volume);

    void ShowResults(List<DuplicateGroup> found, string? message = null)
    {
        results = found;
        groups.BeginUpdate();
        try
        {
            groups.Items.Clear();
            groups.Items.AddRange(results.Select((group, index) => new ListViewItem(
                [(index + 1).ToString("N0"), FormatSize(Reclaimable(group)), FormatSize(group.Bytes),
                    group.Files.Count.ToString("N0"), VerificationStatus(group)]) { Tag = group }).ToArray());
            if (groups.Items.Count > 0) groups.Items[0].Selected = true;
        }
        finally { groups.EndUpdate(); }
        ShowMembers();
        ShowTotal(message);
    }

    static decimal Reclaimable(DuplicateGroup group) => (decimal)group.Bytes * (group.Files.Count - 1);
    static string FormatSize(decimal bytes) => bytes >= 1_048_576
        ? $"{bytes / 1_048_576:N2} MB" : $"{bytes:N0} bytes";
    static string VerificationStatus(DuplicateGroup group) =>
        group.FullHash is null ? "Metadata only" : "Verified SHA-256";

    void ShowTotal(string? message = null)
    {
        string totals = $"{results.Count:N0} groups · {FormatSize(results.Sum(Reclaimable))} reclaimable";
        status.Text = message is null ? totals : $"{message} · {totals}";
    }

    Volume? FindVolume(string id) => volumes.Find(volume => volume.Id == id);

    void ShowMembers()
    {
        members.Items.Clear();
        if (groups.SelectedItems.Count > 0 && groups.SelectedItems[0].Tag is DuplicateGroup group)
            members.Items.AddRange(group.Files.Select(file => new ListViewItem(
                [file.FullPath, FindVolume(file.Volume)?.DisplayName ?? file.Volume, Availability(file)])
                { Tag = file }).ToArray());
        if (members.Items.Count > 0) members.Items[0].Selected = true;
        UpdateActions();
    }

    static string Availability(DuplicateFile file) => !file.Online ? "Offline" : file.Stale ? "Stale" : "Online";

    DuplicateFile? SelectedFile => members.SelectedItems.Count > 0
        ? members.SelectedItems[0].Tag as DuplicateFile : null;

    void UpdateActions()
    {
        scope.Enabled = minimum.Enabled = find.Enabled = !busy;
        verify.Enabled = !busy && results.Any(group => group.FullHash is null
            && group.Files.Count(file => file.Online && !file.Stale) >= 2);
        cancel.Enabled = busy && verification is not null;
        copy.Enabled = !busy && SelectedFile is not null;
        reveal.Enabled = !busy && SelectedFile is { Online: true };
        export.Enabled = !busy && results.Count > 0;
    }

    void CopyPath()
    {
        if (SelectedFile is not { } file) return;
        try { Clipboard.SetText(file.FullPath); }
        catch (Exception ex) { ShowError(ex); }
    }

    async Task RevealAsync()
    {
        if (SelectedFile is not { } file || FindVolume(file.Volume) is not { Online: true } volume) return;
        try
        {
            bool available = await Task.Run(() => WindowsVolumes.CanReveal(volume, file.Rel, file.FullPath));
            if (IsDisposed || SelectedFile != file) return;
            if (!available)
            {
                ShowTotal("The selected path is unavailable");
                return;
            }
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file.FullPath}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex); }
    }

    async Task ExportAsync()
    {
        using var dialog = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "duplicates.csv" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string destination = dialog.FileName;
        await RunAsync("Exporting duplicates…", async () =>
        {
            await Task.Run(() => WriteCsv(destination));
            if (!IsDisposed) ShowTotal("CSV exported");
        });
    }

    void WriteCsv(string destination)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".duplicates-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
            {
                writer.WriteLine("Group #,Reclaimable Bytes,File Size Bytes,Copies,Verification Status,Full Path,Volume ID,Availability");
                for (int index = 0; index < results.Count; index++)
                {
                    var group = results[index];
                    foreach (var file in group.Files)
                    {
                        writer.WriteLine(string.Join(',', new[]
                        {
                            (index + 1).ToString(CultureInfo.InvariantCulture),
                            Reclaimable(group).ToString(CultureInfo.InvariantCulture),
                            group.Bytes.ToString(CultureInfo.InvariantCulture),
                            group.Files.Count.ToString(CultureInfo.InvariantCulture),
                            VerificationStatus(group), file.FullPath, file.Volume, Availability(file)
                        }.Select(CsvCell)));
                    }
                }
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    static string CsvCell(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if ((trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')
            || (value.Length > 0 && value[0] is '\t' or '\r' or '\n'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    async Task RunAsync(string message, Func<Task> work)
    {
        busy = true;
        UpdateActions();
        progress.Style = ProgressBarStyle.Marquee;
        status.Text = message;
        try { await work(); }
        catch (OperationCanceledException) { if (!IsDisposed) ShowTotal("Verification cancelled"); }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                ShowTotal("Operation failed");
                ShowError(ex);
            }
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                progress.Style = ProgressBarStyle.Blocks;
                UpdateActions();
            }
        }
    }

    void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            verification?.Cancel();
            tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
