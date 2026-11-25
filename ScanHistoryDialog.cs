
namespace FileInventory;

sealed class ScanHistoryDialog : Form
{
    readonly Catalog catalog;
    readonly ToolTip tips = new();
    readonly ComboBox volumeSelector = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    readonly ComboBox kindFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly TextBox search = new() { PlaceholderText = "Search paths", Width = 260 };
    readonly Button copy = UiHelpers.IconButton("Copy Path", FluentIcon.Copy);
    readonly Button reveal = UiHelpers.IconButton("Reveal in Explorer", FluentIcon.FolderOpen);
    readonly ListView scans = UiHelpers.DetailsList(("Date", 185), ("Outcome", 145), ("+Added", 85), ("~Modified", 95),
        ("-Removed", 95), ("Moved", 80), ("Net Delta", 145));
    readonly ListView changes = UiHelpers.DetailsList(("Kind", 100), ("Path", 530), ("Old Bytes", 120), ("New Bytes", 120));
    List<Volume> volumes = [];
    List<ScanChange> scanChanges = [];
    bool updatingScans;

    public ScanHistoryDialog(Catalog catalog, AppTheme theme, string? initialVolumeId = null)
    {
        this.catalog = catalog;
        Text = "Scan History";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(970, 590);
        MinimumSize = new Size(620, 420);

        var volumeLabel = new Label { Text = "Volume", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
        var kindLabel = new Label { Text = "Change kind", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
        var volumeRow = UiHelpers.FlowRow(volumeLabel, volumeSelector);
        var filterRow = UiHelpers.FlowRow(kindLabel, kindFilter, search);
        var actionRow = UiHelpers.FlowRow(copy, reveal);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (Control control in new Control[] { volumeRow, scans, filterRow, changes, actionRow })
            layout.Controls.Add(control);
        Controls.Add(layout);

        foreach (var (control, description) in new (Control, string)[]
        {
            (volumeLabel, "Scan history is stored separately for each volume."),
            (volumeSelector, "Choose the volume whose completed scans you want to see."),
            (scans, "Select a completed scan to show its recorded file changes."),
            (kindLabel, "Limit the list to a specific kind of change."),
            (kindFilter, "Show all changes or only added, modified, removed, or moved files."),
            (search, "Filter changes by a case-insensitive match anywhere in the path."),
            (changes, "Select a change to copy its path or reveal a file that is currently available."),
            (copy, "Copy the selected change's full path."),
            (reveal, "Select the file in Windows Explorer if the original volume and path are available.")
        })
            Theme.AttachTooltip(control, tips, description);

        kindFilter.Items.Add("All");
        foreach (var kind in Enum.GetValues<ScanChangeKind>()) kindFilter.Items.Add(kind);
        kindFilter.SelectedIndex = 0;
        volumeSelector.SelectedIndexChanged += (_, _) => ShowScans();
        scans.SelectedIndexChanged += (_, _) => { if (!updatingScans) LoadChanges(); };
        kindFilter.SelectedIndexChanged += (_, _) => FilterChanges();
        search.TextChanged += (_, _) => FilterChanges();
        changes.SelectedIndexChanged += (_, _) => UpdateActions();
        copy.Click += (_, _) => CopyPath();
        reveal.Click += async (_, _) => await RevealAsync();
        Load += (_, _) =>
        {
            Theme.Apply(this, theme);
            Theme.StyleToolTip(tips);
            try
            {
                volumes = catalog.Volumes();
                volumeSelector.Items.AddRange(volumes.Select(volume => volume.DisplayStatusLabel).ToArray());
                if (volumes.Count > 0)
                    volumeSelector.SelectedIndex = Math.Max(0, volumes.FindIndex(volume => volume.Id == initialVolumeId));
            }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        UpdateActions();
    }

    Volume? SelectedVolume => volumeSelector.SelectedIndex >= 0 ? volumes[volumeSelector.SelectedIndex] : null;
    ScanChange? SelectedChange => changes.SelectedItems.Count > 0 ? changes.SelectedItems[0].Tag as ScanChange : null;

    void ShowScans()
    {
        updatingScans = true;
        scans.BeginUpdate();
        try
        {
            scans.Items.Clear();
            if (SelectedVolume is { } volume)
            {
                foreach (var scan in catalog.ScanHistory(volume.Id))
                {
                    if (scan.Finished is not long finished) continue;
                    string date = DateTimeOffset.FromUnixTimeMilliseconds(finished).LocalDateTime.ToString("g");
                    scans.Items.Add(new ListViewItem([date, scan.Outcome, scan.AddedCount.ToString("N0"),
                        scan.ModifiedCount.ToString("N0"), scan.RemovedCount.ToString("N0"), scan.MovedCount.ToString("N0"),
                        scan.BytesDelta.ToString("+#,0;-#,0;0") + " bytes"]) { Tag = scan });
                }
                if (scans.Items.Count > 0) scans.Items[0].Selected = true;
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            scans.EndUpdate();
            updatingScans = false;
        }
        LoadChanges();
    }

    void LoadChanges()
    {
        scanChanges = [];
        if (scans.SelectedItems.Count > 0 && scans.SelectedItems[0].Tag is ScanSummary scan)
        {
            try { scanChanges = catalog.ScanChanges(scan.Id); }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        FilterChanges();
    }

    void FilterChanges()
    {
        string term = search.Text.Trim();
        var volume = SelectedVolume;
        changes.BeginUpdate();
        try
        {
            changes.Items.Clear();
            if (volume is null) return;
            foreach (var change in scanChanges)
            {
                if (kindFilter.SelectedItem is ScanChangeKind kind && change.Kind != kind) continue;
                string path = Path.Combine(volume.Mount, change.Rel);
                if (!path.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                changes.Items.Add(new ListViewItem([change.Kind.ToString(), path,
                    change.OldBytes?.ToString("N0") ?? "", change.NewBytes?.ToString("N0") ?? ""]) { Tag = change });
            }
        }
        finally
        {
            changes.EndUpdate();
            UpdateActions();
        }
    }

    void UpdateActions()
    {
        copy.Enabled = SelectedChange is not null;
        reveal.Enabled = copy.Enabled && SelectedVolume is { Online: true };
    }

    void CopyPath()
    {
        if (SelectedVolume is not { } volume || SelectedChange is not { } change) return;
        UiHelpers.CopyText(Path.Combine(volume.Mount, change.Rel), this);
    }

    async Task RevealAsync()
    {
        if (SelectedVolume is not { Online: true } volume || SelectedChange is not { } change) return;
        try
        {
            string path = Path.Combine(volume.Mount, change.Rel);
            bool available = await Task.Run(() => WindowsVolumes.CanReveal(volume, change.Rel, path));
            if (IsDisposed || SelectedChange != change || SelectedVolume != volume) return;
            if (!available)
            {
                ShowError("The selected path is unavailable.");
                return;
            }
            UiHelpers.RevealInExplorer(path, this);
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
    }

    void ShowError(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void Dispose(bool disposing)
    {
        if (disposing) tips.Dispose();
        base.Dispose(disposing);
    }
}
