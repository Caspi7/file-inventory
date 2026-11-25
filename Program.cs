using System.Diagnostics;
using System.Globalization;

namespace FileInventory;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        string directory = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "FileInventory");
        bool benchmark = args.Contains("--benchmark", StringComparer.Ordinal);
        if (benchmark) directory = Path.Combine(directory, "Benchmark");
        CrashDumps.Register(directory);
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            // The lock also prevents abandoned staging files from being recovered by a live instance.
            Directory.CreateDirectory(directory);
            using var singleInstance = new FileStream(Path.Combine(directory, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var catalog = new Catalog(directory);
            using (var db = catalog.Open())
            {
                Catalog.Execute(db, """
                    UPDATE scans SET outcome='Interrupted', finished=$now WHERE finished IS NULL;
                    UPDATE volumes SET outcome='Interrupted' WHERE outcome='Scanning';
                    """, ("now", Catalog.Now()));
            }
            foreach (var path in Directory.EnumerateFiles(directory, "scan-*.sqlite3*"))
                File.Delete(path);
            using var window = new InventoryWindow(catalog, benchmark);
            Application.Run(window);
            if (window.UpdateInstaller is { } installer)
            {
                singleInstance.Dispose();
                AppUpdates.RunInstaller(installer);
            }
            else
            {
                singleInstance.Dispose();
            }
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            CrashDumps.Capture(directory, ex);
            Environment.ExitCode = 1;
            MessageBox.Show(ex.Message, "File Inventory", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

sealed partial class InventoryWindow : Form
{
    readonly Catalog catalog;
    readonly bool benchmark;
    readonly SavedSearches savedSearches;
    readonly Scanner scanner;
    readonly TextBox query = new() { Width = 310, MaxLength = 500, PlaceholderText = "Search filenames and folders" };
    readonly ComboBox extension = new() { Width = 105, DropDownStyle = ComboBoxStyle.DropDown,
        AccessibleName = "Extension filter" };
    readonly TextBox min = new() { Width = 85, PlaceholderText = "Minimum" };
    readonly TextBox max = new() { Width = 85, PlaceholderText = "Maximum" };
    readonly DateTimePicker from = new() { Width = 120, ShowCheckBox = true, Checked = false, Format = DateTimePickerFormat.Short };
    readonly DateTimePicker through = new() { Width = 120, ShowCheckBox = true, Checked = false, Format = DateTimePickerFormat.Short };
    readonly ComboBox status = new() { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox scope = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ListBox drives = new()
    {
        Dock = DockStyle.Fill, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended
    };
    readonly CheckBox enabled = new() { Text = "Schedule refresh", AutoSize = true };
    readonly Button saveSchedule = new() { Text = "Save schedule", AutoSize = true };
    readonly NumericUpDown hours = new() { Minimum = 1, Maximum = 8760, Value = 24, Width = 75 };
    readonly TextBox details = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, WordWrap = true };
    readonly Label state = new() { AutoSize = true };
    readonly Button refresh = new() { Text = "Scan drives…", Dock = DockStyle.Top,
        Height = 44, BackColor = Color.FromArgb(0, 95, 184), ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false };
    readonly Button cancel = new() { Text = "Cancel scan", AutoSize = true };
    readonly Label scanState = new() { AutoSize = true, MaximumSize = new(240, 0),
        Margin = new Padding(3, 6, 3, 6) };
    readonly Button copy = new() { Text = "Copy path", AutoSize = true };
    readonly Button candidates = new() { Text = "Find possible copies", AutoSize = true };
    readonly Button reveal = new() { Text = "Reveal in Explorer", AutoSize = true };
    readonly ListView results = new()
    {
        Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, Scrollable = true,
        MultiSelect = false, FullRowSelect = true, HideSelection = false, AccessibleName = "Inventory results"
    };
    readonly System.Windows.Forms.Timer heartbeat = new() { Interval = 15000 };
    readonly Icon appIcon = LoadAppIcon();
    readonly NotifyIcon tray = new() { Text = "File Inventory" };
    readonly Dictionary<string, (CancellationTokenSource Cancellation, Task Work)> running = new();
    readonly Dictionary<string, ScanProgress> progress = new();
    List<Volume> volumes = [];
    Dictionary<string, Volume> volumesById = new(StringComparer.Ordinal);
    bool restoring, discovering, closing, readyToClose;
    int revision;
    bool savingSchedule;
    string idleSearchStatus = "", displayedIdleStatus = "";
    string? selectedId, displayedSchedule;
    (bool? Enabled, double? Hours) scheduleVersion;

    sealed record Choice(string? Id, string Text) { public override string ToString() => Text; }

    public InventoryWindow(Catalog catalog, bool benchmark)
    {
        this.catalog = catalog;
        this.benchmark = benchmark;
        savedSearches = new(catalog.DataDirectory);
        scanner = new(catalog);
        Text = benchmark ? "File Inventory 1.3.1 · Benchmark" : "File Inventory 1.3.1";
        Icon = appIcon;
        tray.Icon = appIcon;
        Width = 1370;
        Height = 830;
        MinimumSize = new(850, 630);
        Font = Theme.BodyFont;
        StartPosition = FormStartPosition.CenterScreen;

        status.Items.AddRange(["Any status", "Online", "Offline"]);
        status.SelectedIndex = 0;
        scope.Items.Add(new Choice(null, "All volumes"));
        scope.SelectedIndex = 0;
        drives.SelectedIndexChanged += (_, _) =>
        {
            if (restoring) return;
            selectedId = (drives.SelectedItem as Choice)?.Id;
            ShowVolume();
        };
        saveSchedule.Click += async (_, _) => await SaveSchedule();
        refresh.Click += (_, _) =>
        {
            var available = volumes.Where(v => v.Online && !running.ContainsKey(v.Id)).ToList();
            if (available.Count == 0) return;
            foreach (var volume in PromptDrivesToScan(available))
                StartScan(volume);
        };
        cancel.Click += (_, _) =>
        {
            var targets = SelectedVolumes().Where(v => running.ContainsKey(v.Id)).ToList();
            if (targets.Count == 0)
            {
                foreach (var scan in running.Values) scan.Cancellation.Cancel();
                return;
            }
            foreach (var volume in targets)
                if (running.TryGetValue(volume.Id, out var scan)) scan.Cancellation.Cancel();
        };
        copy.Click += (_, _) =>
        {
            if (CurrentHit() is { } hit)
                try { Clipboard.SetText(hit.FullPath); }
                catch (System.Runtime.InteropServices.ExternalException ex) { state.Text = ex.Message; }
        };
        candidates.Click += async (_, _) =>
        {
            if (CurrentHit() is not { } hit) return;
            try
            {
                var paths = await Task.Run(() => catalog.CopyCandidates(hit));
                if (closing) return;
                var message = paths.Count == 0 ? "No cross-volume matches for size and timestamps." :
                    "Unverified cross-volume matches based on size and timestamps.\r\n\r\n"
                    + string.Join("\r\n", paths);
                MessageBox.Show(this, message, "Possible copies", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { if (!closing) state.Text = ex.Message; }
        };
        reveal.Click += async (_, _) =>
        {
            if (CurrentHit() is not { CanReveal: true } hit) return;
            try
            {
                if (!await RevealFile(hit.Volume, hit.RelativePath, hit.FullPath) && !closing)
                    state.Text = "The item cannot be revealed from its current location.";
            }
            catch (Exception ex) { state.Text = ex.Message; }
        };

        extension.Items.AddRange([".csv", ".docx", ".jpg", ".mp3", ".mp4", ".pdf", ".png", ".pptx", ".txt", ".xlsx", ".zip"]);
        foreach (var box in new Control[] { query, extension, min, max }) box.TextChanged += (_, _) => QueueSearch();
        foreach (var box in new[] { from, through }) box.ValueChanged += (_, _) => QueueSearch();
        status.SelectedIndexChanged += (_, _) => QueueSearch();
        scope.SelectedIndexChanged += (_, _) => { if (!restoring) SelectBrowserDrive(); };
        results.SelectedIndexChanged += (_, _) => ShowActions();
        results.MouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left && results.HitTest(e.Location).Item is not null)
                await LocateCurrentHit();
        };
        results.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left && results.HitTest(e.Location).Item is not null
                && CurrentHit() is { IsDirectory: true }) await OpenCurrentFolder(true);
        };
        heartbeat.Tick += async (_, _) => await Poll();

        results.Columns.Add("Filename", 180);
        results.Columns.Add("Extension", 90);
        results.Columns.Add("Full path", 320);
        results.Columns.Add("Volume", 180);
        results.Columns.Add("Status", 95);
        results.Columns.Add("Last seen", 145);
        results.Columns.Add("Bytes", 90, HorizontalAlignment.Right);
        results.Columns.Add("Modified", 145);
        results.Columns.Add("Matched", 220);

        InitializeBrowser();

        tray.ContextMenuStrip = new();
        tray.ContextMenuStrip.Items.Add("Open File Inventory", null, (_, _) => OpenWindow());
        tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Close());
        tray.DoubleClick += (_, _) => OpenWindow();
        Shown += async (_, _) =>
        {
            from.Checked = through.Checked = false;
            heartbeat.Start();
            await Poll();
            QueueSearch();
        };
        FormClosing += HandleClose;
        FormClosed += (_, _) => { tray.Visible = false; tray.Dispose(); appIcon.Dispose(); heartbeat.Dispose(); };
        ShowVolume();
        ShowActions();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.K:
                CommandPalette.Show(this, currentTheme, BuildCommandPaletteActions());
                return true;
            case Keys.Control | Keys.F:
            case Keys.Control | Keys.L:
                query.Focus();
                query.SelectAll();
                return true;
            case Keys.F5:
                refresh.PerformClick();
                return true;
            case Keys.Control | Keys.D:
                library.PerformClick();
                return true;
            case Keys.Control | Keys.Shift | Keys.S:
                syncButton.PerformClick();
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    IReadOnlyList<CommandAction> BuildCommandPaletteActions()
    {
        List<CommandAction> actions =
        [
            new("Scan drives…", "Inventory", "F5", refresh.PerformClick),
            new("Sync now", "Sync", "Ctrl+Shift+S", syncButton.PerformClick),
            new("Sync settings…", "Sync", "", syncSettingsButton.PerformClick),
            new("Drive Library…", "Inventory", "Ctrl+D", library.PerformClick),
            new("Duplicates…", "Inventory", "", duplicatesButton.PerformClick),
            new("Changes…", "Inventory", "", changesButton.PerformClick),
            new("Saved searches", "Search", "", savedSearchButton.PerformClick),
            new("Check for updates", "Application", "", updateButton.PerformClick),
            new("Release notes", "Application", "", releaseNotesButton.PerformClick)
        ];
        foreach (var theme in Enum.GetValues<AppTheme>())
            actions.Add(new($"Theme: {ThemeName(theme)}", "Appearance", "", () => SetTheme(theme)));
        foreach (var choice in scope.Items.Cast<Choice>())
            actions.Add(new($"Browse {choice.Text}", "Drives", "", () =>
            {
                if (scope.Items.Cast<Choice>().FirstOrDefault(item => item.Id == choice.Id) is { } current)
                    scope.SelectedItem = current;
            }));
        return actions;
    }

    static Icon LoadAppIcon()
    {
        using var artwork = typeof(InventoryWindow).Assembly.GetManifestResourceStream("FileInventory.AppIcon.ico");
        if (artwork is null) return (Icon)SystemIcons.Application.Clone();
        using var icon = new Icon(artwork);
        return (Icon)icon.Clone();
    }

    static Label LabelFor(string text) => new() { Text = text, AutoSize = true, Margin = new(4, 7, 4, 4) };
    static TableLayoutPanel Stack(int rows) => new()
    {
        Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Padding = new Padding(8)
    };
    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

    void OpenWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        tray.Visible = false;
    }

    Volume? Selected() => volumes.FirstOrDefault(volume => volume.Id == selectedId);
    List<Volume> SelectedVolumes()
    {
        var ids = drives.SelectedItems.Cast<Choice>().Select(choice => choice.Id).ToHashSet();
        return volumes.Where(volume => ids.Contains(volume.Id)).ToList();
    }
    int SelectedResult => results.SelectedIndices.Count == 0 ? -1 : results.SelectedIndices[0];

    Hit? CurrentHit() => activeRevision == revision ? CachedResult(SelectedResult) : null;

    Hit? CachedResult(int index) => index >= 0 && pageCache.TryGetValue(index / PageSize, out var page)
        && index % PageSize < page.Count ? page[index % PageSize] : null;
    void ShowActions()
    {
        copy.Enabled = candidates.Enabled = CurrentHit() is not null;
        reveal.Enabled = !benchmark && CurrentHit() is { CanReveal: true };
    }

    void ShowVolume()
    {
        var volume = Selected();
        restoring = true;
        var selection = SelectedVolumes();
        enabled.Enabled = hours.Enabled = saveSchedule.Enabled = selection.Count == 1
            && !closing && !savingSchedule && !benchmark;
        if (displayedSchedule != volume?.Id || scheduleVersion != (volume?.Enabled, volume?.Hours))
        {
            displayedSchedule = volume?.Id;
            scheduleVersion = (volume?.Enabled, volume?.Hours);
            enabled.Checked = volume?.Enabled ?? false;
            hours.Value = Math.Clamp((decimal)(volume?.Hours ?? 24), hours.Minimum, hours.Maximum);
        }
        refresh.Enabled = !closing && !benchmark
            && volumes.Any(item => item.Online && !running.ContainsKey(item.Id));
        cancel.Enabled = !closing && selection.Any(item => running.ContainsKey(item.Id));
        UpdateScanDisplay();
        details.Text = selection.Count > 1
            ? "Select one volume to see details or edit its schedule.\r\n\r\n"
                + string.Join("\r\n", selection.Select(item =>
                    $"{item.DisplayName}  " + (progress.TryGetValue(item.Id, out var scan)
                        ? $"Scanning {scan.Entries:N0} entries"
                        : item.Online ? item.Outcome : "Offline")))
            : volume is null ? "Select a drive to see its details, or click Scan drives… to start a scan." :
            $"System  {(string.IsNullOrWhiteSpace(volume.Host) ? "Unknown" : volume.Host)}    Type  {volume.KindLabel}\r\n" +
            $"Last attempt  {DisplayTime(volume.Attempted)}\r\n" +
            $"Last successful refresh  {DisplayTime(volume.Succeeded)}\r\n" +
            (progress.TryGetValue(volume.Id, out var current)
                ? $"\r\nScanning {current.Entries:N0} entries\r\nOmitted areas  {current.Omitted:N0}\r\n{current.Directory}"
                : $"\r\n{volume.Outcome}\r\n{volume.Issues}");
        restoring = false;
    }

    static string DisplayTime(long? milliseconds) =>
        milliseconds is { } time ? Catalog.Local(time).ToString("g") : "Never";

    async Task SaveSchedule()
    {
        if (restoring || closing || savingSchedule || Selected() is not { } volume) return;
        bool scheduled = enabled.Checked;
        double interval = (double)hours.Value;
        savingSchedule = true;
        ShowVolume();
        try
        {
            await Task.Run(() => catalog.Configure(volume.Id, scheduled, interval));
            await Poll();
        }
        catch (Exception ex) { if (!closing) state.Text = ex.Message; }
        finally
        {
            savingSchedule = false;
            if (!closing) ShowVolume();
        }
    }

    async Task Poll()
    {
        if (discovering || closing) return;
        discovering = true;
        try
        {
            if (!benchmark) await Task.Run(() => catalog.Discover(WindowsVolumes.Mounted()));
            await ReloadVolumes();
            if (!closing && !benchmark)
                foreach (var volume in volumes.Where(v => v.Online && v.Enabled))
                    if (volume.Attempted is null || Catalog.Now() - volume.Attempted.Value >= volume.Hours * 3600000)
                        StartScan(volume);
            if (!closing && (state.Text.Length == 0 || state.Text == displayedIdleStatus))
                ShowIdleSearchStatus();
        }
        catch (Exception ex) { if (!closing) state.Text = ex.Message; }
        finally { discovering = false; }
    }

    void ShowIdleSearchStatus()
    {
        long now = Catalog.Now();
        int overdue = volumes.Count(volume => volume.IsOverdue(now));
        string hint = overdue == 0 ? "" : $"{overdue} {(overdue == 1 ? "drive" : "drives")} overdue for scan — open Drive Library to check.";
        displayedIdleStatus = idleSearchStatus.Length == 0 || hint.Length == 0
            ? idleSearchStatus + hint : $"{idleSearchStatus} · {hint}";
        if (state.Text != displayedIdleStatus) state.Text = displayedIdleStatus;
    }

    async Task ReloadVolumes()
    {
        var list = await Task.Run(catalog.Volumes);
        if (closing || volumes.SequenceEqual(list)) { if (!closing) ShowVolume(); return; }
        bool topologyChanged = !volumes.Select(v => (v.Id, v.Mount, v.Label, v.Online, v.Host, v.Removable))
            .SequenceEqual(list.Select(v => (v.Id, v.Mount, v.Label, v.Online, v.Host, v.Removable)));
        volumes = list;
        volumesById = list.ToDictionary(v => v.Id, StringComparer.Ordinal);
        if (!topologyChanged && browserInitialized) { ShowVolume(); return; }
        restoring = true;
        var filterId = (scope.SelectedItem as Choice)?.Id;
        var selected = drives.SelectedItems.Cast<Choice>().Select(choice => choice.Id).ToHashSet();
        drives.Items.Clear();
        scope.Items.Clear();
        scope.Items.Add(new Choice(null, "All volumes"));
        foreach (var volume in volumes)
        {
            var choice = new Choice(volume.Id, volume.DisplayStatusLabel);
            int index = drives.Items.Add(choice);
            if (selected.Contains(volume.Id)) drives.SetSelected(index, true);
            index = scope.Items.Add(choice);
            if (volume.Id == filterId) scope.SelectedIndex = index;
        }
        if (scope.SelectedIndex < 0) scope.SelectedIndex = 0;
        if (drives.SelectedIndex < 0 && drives.Items.Count > 0)
        {
            drives.SelectedIndex = 0;
        }
        selectedId = (drives.SelectedItem as Choice)?.Id;
        if (filterId is null && !browserInitialized && scope.Items.Count > 1)
            scope.SelectedIndex = 1;
        restoring = false;
        browserInitialized = true;
        ShowVolume();
        ResetTree();
        QueueSearch();
    }

    List<Volume> PromptDrivesToScan(List<Volume> available)
    {
        using var dialog = new Form
        {
            Text = "Select Drives to Scan",
            Width = 480,
            Height = 340,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent
        };
        var header = new Label
        {
            Text = "Choose one or more online drives to scan:",
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(12, 10, 12, 0)
        };
        Theme.MarkSectionTitle(header);

        var list = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            BorderStyle = BorderStyle.FixedSingle
        };
        var preselected = SelectedVolumes().Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        bool selectAllDefault = preselected.Count == 0 || preselected.Count == available.Count;
        foreach (var v in available)
        {
            bool isChecked = selectAllDefault || preselected.Contains(v.Id);
            list.Items.Add(new Choice(v.Id, v.DisplayName), isChecked);
        }
        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6) };
        listPanel.Controls.Add(list);

        var quick = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 40,
            Padding = new Padding(12, 2, 12, 4)
        };
        var selectAll = new Button { Text = "Select all", AutoSize = true };
        var selectNone = new Button { Text = "Select none", AutoSize = true };
        selectAll.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, true); };
        selectNone.Click += (_, _) => { for (int i = 0; i < list.Items.Count; i++) list.SetItemChecked(i, false); };
        quick.Controls.AddRange([selectAll, selectNone]);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(12, 6, 12, 8)
        };
        var scanBtn = new Button { Text = "Scan selected", DialogResult = DialogResult.OK, AutoSize = true };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([cancelBtn, scanBtn]);

        dialog.Controls.AddRange([listPanel, header, quick, buttons]);
        dialog.AcceptButton = scanBtn;
        dialog.CancelButton = cancelBtn;
        dialog.Load += (_, _) => Theme.Apply(dialog, currentTheme);

        if (dialog.ShowDialog(this) != DialogResult.OK) return [];
        var chosen = new List<Volume>();
        foreach (Choice item in list.CheckedItems)
            if (item.Id is not null && available.FirstOrDefault(v => v.Id == item.Id) is { } vol)
                chosen.Add(vol);
        return chosen;
    }

    void StartScan(Volume volume)
    {
        if (running.ContainsKey(volume.Id) || closing || checkingUpdates || UpdateInstaller is not null) return;
        var source = new CancellationTokenSource();
        scanClocks[volume.Id] = Stopwatch.StartNew();
        var reporter = new Progress<ScanProgress>(update =>
        {
            if (closing || !running.ContainsKey(volume.Id)) return;
            progress[volume.Id] = update;
            // Render only the latest sample on the UI timer, avoiding a queue of layouts.
        });
        progress[volume.Id] = new(0, 0, volume.Mount, Phase: "Preparing scan");
        running.Add(volume.Id, (source, scanner.ScanAsync(volume, reporter, source.Token)));
        ShowVolume();
        _ = ObserveScan(volume.Id);
    }

    async Task ObserveScan(string id)
    {
        var scan = running[id];
        bool scanFailed = false;
        try { await scan.Work; }
        catch (Exception ex) { scanFailed = true; if (!closing) state.Text = ex.Message; }
        finally
        {
            running.Remove(id);
            progress.Remove(id);
            scanClocks.Remove(id);
            scan.Cancellation.Dispose();
        }
        if (!closing)
        {
            await Poll();
            await ReloadVolumes();
            ResetTree();
            QueueSearch();
            if (!scanFailed && !benchmark)
                _ = TryAutoSyncAfterScanAsync();
        }
    }

    async void QueueSearch()
    {
        if (closing || restoring) return;
        int requested = ++revision;
        long requestedAt = Stopwatch.GetTimestamp();
        queryCancellation?.Cancel();
        pageCancellation.Cancel();
        activeRevision = -1;
        results.VirtualListSize = 0;
        Theme.SetHighlightTerms(results, null);
        ShowActions();
        searchProgress.Visible = true;
        state.Text = "Loading inventory…";

        using var cancellation = new CancellationTokenSource();
        queryCancellation = cancellation;
        try
        {
            await Task.Delay(120, cancellation.Token);
            if (closing || requested != revision) return;
            string input = query.Text;
            bool fileSearch = !string.IsNullOrWhiteSpace(input);
            var volume = (scope.SelectedItem as Choice)?.Id;
            var (text, filters) = QueryParser.Parse(input, ReadSearchFilters(fileSearch));
            if (fileSearch && filters.Volume is { } drive)
            {
                var matches = volumes.Where(v => string.Equals(v.Mount.TrimEnd('\\'), drive.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase) || string.Equals(v.DisplayName, drive,
                    StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                if (!volumesById.ContainsKey(drive) && matches.Length == 1) drive = matches[0].Id;
                filters = filters with { Volume = drive, Drives = null };
            }
            var parent = !fileSearch && volume is not null ? browseParent : null;
            var token = cancellation.Token;
            var (page, ids) = await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(text))
                    return (catalog.QueryPage(text, filters, 0, PageSize, parent, cancellationToken: token), (List<long>?)null);
                var matches = catalog.IndexSearch(text, filters, token);
                return (new CatalogPage(matches.Count, catalog.ReadSearchPage(text, matches, 0, PageSize, token)), matches);
            }, token);
            if (closing || requested != revision) return;
            pageGeneration++;
            pageCancellation.Dispose();
            pageCancellation = new CancellationTokenSource();
            pageCache.Clear(); pendingPages.Clear();
            activeInput = text; activeFilters = filters; activeParent = parent; activeRevision = requested;
            pageCache[0] = page.Rows;
            activeMatches = ids;
            Theme.SetHighlightTerms(results, text.Replace('/', '\\').Replace("\0", "")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            results.VirtualListSize = page.Total;
            results.Invalidate();
            location.Text = fileSearch
                ? $"Search results for “{input}” — click a result to show its location in the tree"
                : volume is not null && volumesById.TryGetValue(volume, out var selectedVolume)
                    ? Path.Combine(selectedVolume.Mount, browseParent) : "All indexed files and folders";
            idleSearchStatus = $"{page.Total:N0} {(fileSearch ? "matches" : "items")} · {Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds:N0} ms";
            ShowIdleSearchStatus();
            ShowActions();
        }
        catch (Exception ex)
        {
            if (closing || requested != revision || cancellation.IsCancellationRequested) return;
            pageGeneration++; pageCache.Clear(); pendingPages.Clear();
            activeMatches = null;
            results.VirtualListSize = 0;
            state.Text = ex.Message;
            ShowActions();
        }
        finally
        {
            if (queryCancellation == cancellation) queryCancellation = null;
            if (!closing && requested == revision) searchProgress.Visible = false;
        }
    }

    Filters ReadSearchFilters(bool fileSearch) => new(
        fileSearch ? null : (scope.SelectedItem as Choice)?.Id,
        string.IsNullOrWhiteSpace(extension.Text) ? null : extension.Text.Trim(),
        ParseBytes(min.Text), ParseBytes(max.Text),
        from.Checked ? Timestamp(from.Value.Date) : null,
        through.Checked ? Timestamp(through.Value.Date.AddDays(1)) : null,
        status.SelectedIndex <= 0 ? null : status.SelectedIndex == 1,
        fileSearch ? searchDrives?.ToArray() : null, includeRemoved.Checked);

    static long? ParseBytes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) && value >= 0)
            return value;
        throw new FormatException("Enter a nonnegative whole number for size in bytes.");
    }

    static long Timestamp(DateTime local) => new DateTimeOffset(local).ToUnixTimeMilliseconds();

    async void HandleClose(object? sender, FormClosingEventArgs e)
    {
        if (transferringInventory || checkingUpdates)
        {
            e.Cancel = true;
            return;
        }
        if (readyToClose) return;
        closing = true;
        tray.Visible = false;
        queryCancellation?.Cancel();
        pageCancellation.Cancel();
        syncCancellation?.Cancel();
        scanUiTimer.Stop();
        heartbeat.Stop();
        var scans = running.Values.ToArray();
        if (scans.Length == 0) return;
        e.Cancel = true;
        foreach (var scan in scans) scan.Cancellation.Cancel();
        ShowVolume();
        try { await Task.WhenAny(Task.WhenAll(scans.Select(scan => scan.Work)), Task.Delay(3000)); }
        catch (Exception) { }
        readyToClose = true;
        Close();
    }
}
