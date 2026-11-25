namespace FileInventory;

sealed partial class InventoryWindow
{
    readonly Button library = new() { Text = "Drives", AutoSize = true };
    readonly Button searchDrive = new() { Text = "Search this drive", AutoSize = true };
    readonly CheckBox includeRemoved = new() { Text = "Show removed files", Checked = true, AutoSize = true };
    readonly ToolTip inventoryTips = new();
    HashSet<string>? searchDrives;
    bool transferringInventory;

    void InitializeLibrary()
    {
        InitializeQuickLook(inventoryTips);
        Disposed += (_, _) => inventoryTips.Dispose();
        Theme.AttachTooltip(library, inventoryTips, "Manage scanned drives, drive notes, and exports.");
        Theme.AttachTooltip(searchDrive, inventoryTips, "Limit search to the current drive.");
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
        bool scopeChanged = ReloadBrowseScope();
        if (!restoring)
        {
            if (scopeChanged) SelectBrowserDrive();
            else QueueSearch();
            query.Focus();
        }
    }

    async Task OpenDriveLibrary()
    {
        if (transferringInventory || checkingUpdates) return;
        using var window = new Form { Text = "Drive Library", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(760, 455), MinimumSize = new Size(540, 375), Font = Font, ShowInTaskbar = false };
        var list = new CheckedListBox { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false,
            AccessibleName = "Drives to include in search" };
        // Use the full catalog so excluded drives can always be checked again.
        var scanned = volumes.Where(v => v.Succeeded is not null || v.Id.StartsWith("import:", StringComparison.Ordinal)).ToList();
        foreach (var item in scanned)
        {
            list.Items.Add(DriveLibraryLabel(item), searchDrives?.Contains(item.Id) ?? true);
        }
        var selectAll = new Button { Text = "Select all", AutoSize = true };
        var clearAll = new Button { Text = "Clear all", AutoSize = true };
        var import = new Button { Text = "Import inventory…", AutoSize = true, Enabled = !benchmark };
        var export = new Button { Text = "Export selected drive…", AutoSize = true, Enabled = false };
        var report = new Button { Text = "Export report…", AutoSize = true, Enabled = false };
        var note = new TextBox { PlaceholderText = "e.g. Shelf B — Cold Backup", Width = 230,
            AccessibleName = "Location note" };
        var reminder = new NumericUpDown { Minimum = 0, Maximum = int.MaxValue, Width = 90,
            AccessibleName = "Remind after (days), zero turns reminders off" };
        var saveNote = new Button { Text = "Save drive note", AutoSize = true };
        Theme.SetIcon(import, FluentIcon.Import);
        Theme.SetIcon(export, FluentIcon.Export);
        Theme.SetIcon(report, FluentIcon.Export);
        Theme.SetIcon(saveNote, FluentIcon.Save);
        var editor = Flow();
        editor.Controls.AddRange([LabelFor("Location note"), note, LabelFor("Remind after (days)"), reminder, saveNote]);
        var apply = new Button { Text = "Apply", AutoSize = true, DialogResult = DialogResult.OK };
        var dismiss = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        selectAll.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
        clearAll.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };
        void ShowDriveNote()
        {
            int index = list.SelectedIndex;
            export.Enabled = report.Enabled = !benchmark && index >= 0;
            note.Enabled = reminder.Enabled = saveNote.Enabled = index >= 0;
            note.Text = index >= 0 ? scanned[index].Note : "";
            reminder.Value = index >= 0 ? scanned[index].FreshnessDays : 0;
        }
        list.SelectedIndexChanged += (_, _) => ShowDriveNote();
        ShowDriveNote();
        saveNote.Click += async (_, _) =>
        {
            int index = list.SelectedIndex;
            if (index < 0) return;
            var drive = scanned[index];
            string location = note.Text;
            int days = (int)reminder.Value;
            try
            {
                catalog.UpdateVolumeMetadata(drive.Id, location, days);
                scanned[index] = drive with { Note = location, FreshnessDays = days };
                bool checkedDrive = list.GetItemChecked(index);
                list.Items[index] = DriveLibraryLabel(scanned[index]);
                list.SetItemChecked(index, checkedDrive);
                list.SelectedIndex = index;
                await ReloadVolumes();
                if (!closing) state.Text = "Drive note saved.";
            }
            catch (Exception ex)
            {
                if (!window.IsDisposed)
                    MessageBox.Show(window, ex.Message, "Could not save drive note", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };

        using var tips = new ToolTip();
        Theme.AttachTooltip(selectAll, tips, "Include every listed drive in search.");
        Theme.AttachTooltip(clearAll, tips, "Clear all drive checkboxes.");
        Theme.AttachTooltip(import, tips, "Import a File Inventory backup.");
        Theme.AttachTooltip(export, tips, "Export a backup of the highlighted drive.");
        Theme.AttachTooltip(report, tips, "Export an HTML, CSV, or JSON report for the highlighted drive.");
        Theme.AttachTooltip(saveNote, tips, "Save the location and reminder for the highlighted drive.");
        Theme.AttachTooltip(reminder, tips, "Days since the last successful scan. Use 0 to turn reminders off.");
        Theme.AttachTooltip(apply, tips, "Apply the checked drives to search.");
        Theme.AttachTooltip(dismiss, tips, "Close without applying changes to the search selection.");

        string? transferPath = null, exportDrive = null;
        string? reportTitle = null;
        InventoryExportFormat? reportFormat = null;
        const string archiveFilter = "File Inventory backups (*.fiinventory)|*.fiinventory";
        const string importFilter = "File Inventory backups (*.fiinventory;*.finv.gz;*.finv.enc)|*.fiinventory;*.finv.gz;*.finv.enc|All files (*.*)|*.*";
        import.Click += (_, _) =>
        {
            using var picker = new OpenFileDialog { Filter = importFilter, Title = "Import a drive inventory" };
            if (picker.ShowDialog(window) != DialogResult.OK) return;
            transferPath = picker.FileName;
            window.DialogResult = DialogResult.OK;
        };
        export.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            using var picker = new SaveFileDialog { Filter = archiveFilter, DefaultExt = "fiinventory",
                FileName = CloudSync.SnapshotDisplayName(Environment.MachineName, scanned[list.SelectedIndex]) + ".fiinventory",
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
        report.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            using var picker = new SaveFileDialog
            {
                Filter = "Standalone HTML (*.html)|*.html|CSV (*.csv)|*.csv|JSON (*.json)|*.json",
                AddExtension = true, Title = "Export drive inventory report"
            };
            if (picker.ShowDialog(window) != DialogResult.OK) return;
            var (extension, format) = picker.FilterIndex switch
            {
                2 => (".csv", InventoryExportFormat.Csv),
                3 => (".json", InventoryExportFormat.Json),
                _ => (".html", InventoryExportFormat.Html)
            };
            if (!string.Equals(Path.GetExtension(picker.FileName), extension, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(window, $"Use the {extension} file extension for the selected format.", "Export report");
                return;
            }
            var drive = scanned[list.SelectedIndex];
            transferPath = picker.FileName;
            exportDrive = drive.Id;
            reportTitle = $"File Inventory report — {drive.BaseLabel}";
            reportFormat = format;
            window.DialogResult = DialogResult.OK;
        };
        var toolbar = Flow();
        toolbar.Controls.AddRange([selectAll, clearAll, import, export, report]);
        var actions = Flow();
        actions.FlowDirection = FlowDirection.RightToLeft;
        actions.Controls.AddRange([apply, dismiss]);
        var layout = Stack(5);
        layout.Controls.Add(LabelFor("Choose which scanned or imported drives to search."), 0, 0);
        layout.Controls.Add(toolbar, 0, 1);
        layout.Controls.Add(list, 0, 2);
        layout.Controls.Add(editor, 0, 3);
        layout.Controls.Add(actions, 0, 4);
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        window.Controls.Add(layout);
        window.AcceptButton = apply;
        window.CancelButton = dismiss;
        window.Load += (_, _) => Theme.Apply(window, currentTheme);
        if (window.ShowDialog(this) != DialogResult.OK) return;
        var selected = list.CheckedIndices.Cast<int>().Select(i => scanned[i].Id).ToHashSet(StringComparer.Ordinal);
        if (reportFormat is null) ApplySearchDrives(selected.Count == scanned.Count ? null : selected);
        if (transferPath is null) return;

        transferringInventory = true;
        library.Enabled = false;
        try
        {
            state.Text = reportFormat is not null ? "Exporting report…"
                : exportDrive is null ? "Importing inventory…" : "Exporting inventory…";
            if (exportDrive is not null)
            {
                if (reportFormat is { } format)
                    await Task.Run(() => WriteDriveReport(exportDrive, transferPath, format, reportTitle!));
                else
                    await Task.Run(() => catalog.ExportInventory(exportDrive, transferPath));
                if (!closing) MessageBox.Show(this,
                    $"Saved {Path.GetFileName(transferPath)} ({new FileInfo(transferPath).Length / 1_000_000.0:N2} MB).",
                    "Export complete");
                if (!closing && reportFormat is not null) state.Text = "Drive report exported.";
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

    static string DriveLibraryLabel(Volume volume) =>
        volume.DisplayLibraryLabel + (volume.IsOverdue(Catalog.Now()) ? " · Overdue" : "");

    void WriteDriveReport(string driveId, string destination, InventoryExportFormat format, string title)
    {
        string? temporaryTemplate = null;
        try
        {
            if (format == InventoryExportFormat.Html)
            {
                var contents = UiHelpers.LoadEmbeddedText("ReportTemplate.html")
                    ?? File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ReportTemplate.html"));
                temporaryTemplate = Path.GetTempFileName();
                File.WriteAllText(temporaryTemplate, contents);
            }
            InventoryExport.Write(destination, Rows(), format, title, temporaryTemplate);
        }
        finally
        {
            if (temporaryTemplate is not null) File.Delete(temporaryTemplate);
        }

        IEnumerable<Hit> Rows()
        {
            const int batchSize = 1024;
            var ids = catalog.IndexSearch("", new Filters(Volume: driveId), CancellationToken.None);
            for (int offset = 0; offset < ids.Count; offset += batchSize)
                foreach (var hit in catalog.ReadSearchPage("", ids, offset, batchSize, CancellationToken.None))
                    if (hit is not null) yield return hit;
        }
    }
}
