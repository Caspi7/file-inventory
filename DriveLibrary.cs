namespace FileInventory;

sealed partial class InventoryWindow
{
    readonly Button library = new() { Text = "Drive Library…", AutoSize = true };
    readonly Button searchDrive = new() { Text = "Search this drive", AutoSize = true };
    readonly CheckBox includeRemoved = new() { Text = "Show removed files", Checked = true, AutoSize = true };
    HashSet<string>? searchDrives;
    bool transferringInventory;

    void InitializeLibrary()
    {
        includeRemoved.CheckedChanged += (_, _) => { ResetTree(); QueueSearch(); };
        searchDrive.Click += (_, _) =>
        {
            if ((scope.SelectedItem as Choice)?.Id is { } id)
                ApplySearchDrives([id]);
        };
        library.Click += async (_, _) => await OpenDriveLibrary();
    }

    void ApplySearchDrives(HashSet<string>? selected)
    {
        searchDrives = selected;
        library.Text = selected is null ? "Drive Library…" : $"Drive Library ({selected.Count} selected)…";
        QueueSearch();
        query.Focus();
    }

    async Task OpenDriveLibrary()
    {
        if (transferringInventory || checkingUpdates) return;
        using var window = new Form { Text = "Drive Library", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(640, 410), MinimumSize = new Size(520, 340), Font = Font, ShowInTaskbar = false };
        var list = new CheckedListBox { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false,
            AccessibleName = "Drives to include in search" };
        var scanned = volumes.Where(v => v.Succeeded is not null || v.Id.StartsWith("import:", StringComparison.Ordinal)).ToList();
        foreach (var item in scanned)
        {
            list.Items.Add(item.DisplayLibraryLabel, searchDrives?.Contains(item.Id) ?? true);
        }
        var selectAll = new Button { Text = "Select all", AutoSize = true };
        var clearAll = new Button { Text = "Clear all", AutoSize = true };
        var import = new Button { Text = "Import inventory…", AutoSize = true, Enabled = !benchmark };
        var export = new Button { Text = "Export selected drive…", AutoSize = true, Enabled = false };
        var apply = new Button { Text = "Apply", AutoSize = true, DialogResult = DialogResult.OK };
        var dismiss = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        selectAll.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
        clearAll.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };
        list.SelectedIndexChanged += (_, _) => export.Enabled = !benchmark && list.SelectedIndex >= 0;
        string? transferPath = null, exportDrive = null;
        const string archiveFilter = "File Inventory backups (*.fiinventory)|*.fiinventory";
        import.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = archiveFilter, Title = "Import a drive inventory" };
            if (picker.ShowDialog(window) != DialogResult.OK) return;
            transferPath = picker.FileName;
            window.DialogResult = DialogResult.OK;
        };
        export.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            using var picker = new SaveFileDialog { Filter = archiveFilter, DefaultExt = "fiinventory",
                AddExtension = true, Title = "Export file and folder names, paths, and dates" };
            if (picker.ShowDialog(window) != DialogResult.OK) return;
            if (!string.Equals(Path.GetExtension(picker.FileName), ".fiinventory", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(window, "Use the .fiinventory file extension.", "Export inventory");
                return;
            }
            transferPath = picker.FileName;
            exportDrive = scanned[list.SelectedIndex].Id;
            window.DialogResult = DialogResult.OK;
        };
        var toolbar = Flow();
        toolbar.Controls.AddRange([selectAll, clearAll, import, export]);
        var actions = Flow();
        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.Controls.AddRange([apply, dismiss]);
        var layout = Stack(4);
        layout.Controls.Add(LabelFor("Choose which scanned or imported drives to search."), 0, 0);
        layout.Controls.Add(toolbar, 0, 1);
        layout.Controls.Add(list, 0, 2);
        layout.Controls.Add(actions, 0, 3);
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        window.Controls.Add(layout);
        window.AcceptButton = apply;
        window.CancelButton = dismiss;
        window.Load += (_, _) => Theme.Apply(window, currentTheme);
        if (window.ShowDialog(this) != DialogResult.OK) return;
        var selected = list.CheckedIndices.Cast<int>().Select(i => scanned[i].Id).ToHashSet(StringComparer.Ordinal);
        ApplySearchDrives(selected.Count == scanned.Count ? null : selected);
        if (transferPath is null) return;

        transferringInventory = true;
        library.Enabled = false;
        try
        {
            state.Text = exportDrive is null ? "Importing inventory…" : "Exporting inventory…";
            if (exportDrive is not null)
            {
                await Task.Run(() => catalog.ExportInventory(exportDrive, transferPath));
                if (!closing) MessageBox.Show(this,
                    $"Saved {Path.GetFileName(transferPath)} ({new FileInfo(transferPath).Length / 1_000_000.0:N2} MB).",
                    "Export complete");
            }
            else
            {
                var id = await Task.Run(() => catalog.ImportInventory(transferPath));
                if (closing) return;
                searchDrives?.Add(id);
                await ReloadVolumes();
                ResetTree();
                ApplySearchDrives(searchDrives);
                state.Text = "Inventory imported and ready to search.";
            }
        }
        catch (Exception ex)
        {
            if (!closing) MessageBox.Show(this, ex.Message, "Inventory transfer failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { transferringInventory = false; if (!closing) library.Enabled = true; }
    }
}
