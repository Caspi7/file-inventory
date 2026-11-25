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
    readonly Button refresh = new() { Text = "Scan selected drive", Dock = DockStyle.Top,
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
    bool restoring, discovering, closing, readyToClose;
    int revision;
    bool savingSchedule;
    string? selectedId, displayedSchedule;
    (bool? Enabled, double? Hours) scheduleVersion;

    sealed record Choice(string? Id, string Text) { public override string ToString() => Text; }

    public InventoryWindow(Catalog catalog, bool benchmark)
    {
        this.catalog = catalog;
        this.benchmark = benchmark;
        scanner = new(catalog);
        Text = benchmark ? "File Inventory 1.2.13 · Benchmark" : "File Inventory 1.2.13";
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
            foreach (var volume in SelectedVolumes().Where(volume => volume.Online))
                StartScan(volume);
        };
        cancel.Click += (_, _) =>
        {
            foreach (var volume in SelectedVolumes())
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
        results.Columns.Add("Volume", 100);
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
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) return;
            tray.Visible = true;
            Hide();
        };
        Shown += async (_, _) =>
        {
            from.Checked = through.Checked = false;
            heartbeat.Start();
            await Poll();
            QueueSearch();
        };
        FormClosing += HandleClose;
        FormClosed += (_, _) => { tray.Dispose(); appIcon.Dispose(); heartbeat.Dispose(); };
        ShowVolume();
        ShowActions();
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
            && selection.Any(item => item.Online && !running.ContainsKey(item.Id));
        cancel.Enabled = !closing && selection.Any(item => running.ContainsKey(item.Id));
        refresh.Text = cancel.Enabled ? "Scan in progress…"
            : selection.Count > 1 ? "Scan selected drives" : "Scan selected drive";
        UpdateScanDisplay();
        details.Text = selection.Count > 1
            ? "Select one volume to see details or edit its schedule.\r\n\r\n"
                + string.Join("\r\n", selection.Select(item =>
                    $"{item.Label} ({item.Mount})  " + (progress.TryGetValue(item.Id, out var scan)
                        ? $"Scanning {scan.Entries:N0} entries"
                        : item.Online ? item.Outcome : "Offline")))
            : volume is null ? "Select a drive above, then click Scan selected drive." :
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
        }
        catch (Exception ex) { if (!closing) state.Text = ex.Message; }
        finally { discovering = false; }
    }

    async Task ReloadVolumes()
    {
        var list = await Task.Run(catalog.Volumes);
        if (closing || volumes.SequenceEqual(list)) { if (!closing) ShowVolume(); return; }
        bool topologyChanged = !volumes.Select(v => (v.Id, v.Mount, v.Label, v.Online))
            .SequenceEqual(list.Select(v => (v.Id, v.Mount, v.Label, v.Online)));
        volumes = list;
        if (!topologyChanged && browserInitialized) { ShowVolume(); return; }
        restoring = true;
        var filterId = (scope.SelectedItem as Choice)?.Id;
        var selected = drives.SelectedItems.Cast<Choice>().Select(choice => choice.Id).ToHashSet();
        drives.Items.Clear();
        scope.Items.Clear();
        scope.Items.Add(new Choice(null, "All volumes"));
        foreach (var volume in volumes)
        {
            string label = volume.Label == volume.Mount ? volume.Mount : $"{volume.Label} ({volume.Mount})";
            if (volume.Id.StartsWith("import:", StringComparison.Ordinal)) label += " · Imported";
            else if (!volume.Online) label += " · Offline";
            var choice = new Choice(volume.Id, label);
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
        try { await scan.Work; }
        catch (Exception ex) { if (!closing) state.Text = ex.Message; }
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
        }
    }

    async void QueueSearch()
    {
        if (closing) return;
        int requested = ++revision;
        long requestedAt = Stopwatch.GetTimestamp();
        queryCancellation?.Cancel();
        pageCancellation.Cancel();
        activeRevision = -1;
        results.VirtualListSize = 0;
        ShowActions();
        searchProgress.Visible = true;
        state.Text = "Loading inventory…";

        using var cancellation = new CancellationTokenSource();
        queryCancellation = cancellation;
        try
        {
            await Task.Delay(120, cancellation.Token);
            if (closing || requested != revision) return;
            string text = query.Text;
            long? low = ParseBytes(min.Text), high = ParseBytes(max.Text);
            if (low > high) throw new FormatException("The maximum size must be at least the minimum.");
            long? start = from.Checked ? Timestamp(from.Value.Date) : null;
            long? end = through.Checked ? Timestamp(through.Value.Date.AddDays(1)) : null;
            if (start >= end) throw new FormatException("The end date must be on or after the start date.");
            var volume = (scope.SelectedItem as Choice)?.Id;
            var parent = string.IsNullOrWhiteSpace(text) && volume is not null ? browseParent : null;
            bool fileSearch = !string.IsNullOrWhiteSpace(text);
            var filters = new Filters(fileSearch ? null : volume,
                string.IsNullOrWhiteSpace(extension.Text) ? null : extension.Text.Trim(),
                low, high, start, end, status.SelectedIndex == 0 ? null : status.SelectedIndex == 1,
                fileSearch ? searchDrives?.ToArray() : null, includeRemoved.Checked);
            var token = cancellation.Token;
            var (page, ids) = await Task.Run(() =>
            {
                if (!fileSearch)
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
            results.VirtualListSize = page.Total;
            results.Invalidate();
            location.Text = string.IsNullOrWhiteSpace(text)
                ? volume is null ? "All indexed files and folders" : Path.Combine(volumes.First(v => v.Id == volume).Mount, browseParent)
                : $"Search results for “{text}” — click a result to show its location in the tree";
            state.Text = $"{page.Total:N0} {(string.IsNullOrWhiteSpace(text) ? "items" : "matches")} · {Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds:N0} ms";
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
        queryCancellation?.Cancel();
        pageCancellation.Cancel();
        scanUiTimer.Stop();
        heartbeat.Stop();
        var scans = running.Values.ToArray();
        if (scans.Length == 0) return;
        e.Cancel = true;
        foreach (var scan in scans) scan.Cancellation.Cancel();
        ShowVolume();
        try { await Task.WhenAll(scans.Select(scan => scan.Work)); }
        catch (Exception) { }
        readyToClose = true;
        Close();
    }
}
