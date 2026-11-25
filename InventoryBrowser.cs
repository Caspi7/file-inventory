namespace FileInventory;

sealed partial class InventoryWindow
{
    const int PageSize = 256;
    readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false,
        AccessibleName = "Inventory file tree", ShowNodeToolTips = true };
    readonly Label location = new() { AutoSize = true, Padding = new Padding(6),
        MaximumSize = new Size(1200, 0) };
    readonly Button browse = new() { Text = "Browse selected folder", AutoSize = true };
    readonly Button themeButton = new() { AutoSize = true };
    readonly Button moreButton = new() { Text = "More", AutoSize = true };
    readonly Button duplicatesButton = new() { Text = "Duplicates", AutoSize = true };
    readonly Button deepSearchButton = new() { Text = "Deep Search", AutoSize = true };
    readonly Button changesButton = new() { Text = "Changes", AutoSize = true };
    readonly Button savedSearchButton = new() { Text = "Saved searches", AutoSize = true };
    readonly Button saveSearchButton = new() { AccessibleName = "Save search", AutoSize = true };
    readonly ToolTip tips = new();
    AppTheme currentTheme = AppTheme.System;
    readonly Dictionary<int, List<Hit>> pageCache = new();
    readonly Dictionary<int, Task> pendingPages = new();
    readonly SemaphoreSlim pageGate = new(2);
    CancellationTokenSource? queryCancellation;
    CancellationTokenSource pageCancellation = new();
    int activeRevision = -1;
    IReadOnlyList<long>? activeMatches;
    readonly ProgressBar searchProgress = new() { Width = 110, Height = 18, Visible = false,
        Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 25, AccessibleName = "Search progress" };
    readonly ProgressBar scanBar = new() { Width = 250, Height = 20, MarqueeAnimationSpeed = 30,
        AccessibleName = "Scan progress" };
    readonly System.Windows.Forms.Timer scanUiTimer = new() { Interval = 500 };
    readonly Dictionary<string, System.Diagnostics.Stopwatch> scanClocks = new();
    int pageGeneration, navigationRevision;
    bool browserInitialized, locating;
    string browseParent = "", activeInput = "";
    string? activeParent;
    Filters activeFilters = new();

    sealed class Branch(string volume, string relative, bool directory)
    {
        public string Volume = volume, Relative = relative;
        public bool Directory = directory, Loaded;
        public Task? Loading;
    }
    sealed record More(Branch Parent, int Offset);

    void InitializeBrowser()
    {
        scope.Width = 185;
        scope.AccessibleName = "Browse drive";
        scope.DropDownWidth = 380;
        InitializeLibrary();
        InitializeSync();
        InitializeUpdates();
        Theme.SetIcon(refresh, FluentIcon.Scan);
        Theme.SetIcon(cancel, FluentIcon.Cancel);
        Theme.SetIcon(library, FluentIcon.Drives);
        Theme.SetIcon(duplicatesButton, FluentIcon.Duplicates);
        Theme.SetIcon(deepSearchButton, FluentIcon.Search);
        Theme.SetIcon(changesButton, FluentIcon.History);
        Theme.SetIcon(moreButton, FluentIcon.More);
        Theme.SetIcon(savedSearchButton, FluentIcon.Bookmark);
        Theme.SetIcon(saveSearchButton, FluentIcon.Star);
        Theme.SetIcon(copy, FluentIcon.Copy);
        Theme.SetIcon(reveal, FluentIcon.FolderOpen);
        Theme.SetIcon(candidates, FluentIcon.Duplicates);
        Theme.SetIcon(saveSchedule, FluentIcon.Save);
        query.Width = 320;
        scanState.Dock = DockStyle.Top;
        scanState.UseMnemonic = false;
        location.UseMnemonic = false;
        Theme.MarkPrimaryLabel(location);
        details.Height = 140;
        var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(10, 8, 10, 6), Margin = Padding.Empty };
        toolbar.ColumnStyles.Add(new(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new(SizeType.AutoSize));
        toolbar.RowStyles.Add(new(SizeType.AutoSize));
        toolbar.Controls.Add(CommandGroup(AnchorStyles.Left,
            scope, refresh, cancel, syncButton, syncSettingsButton, library, duplicatesButton, changesButton,
            deepSearchButton), 0, 0);
        toolbar.Controls.Add(CommandGroup(AnchorStyles.Right, themeButton, moreButton), 1, 0);
        InitializeMoreMenu();
        currentTheme = Theme.LoadPreference(catalog.DataDirectory);
        UpdateThemeButton(currentTheme);
        themeButton.Click += (_, _) => SetTheme(currentTheme switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            AppTheme.Dark => AppTheme.DarkBlue,
            _ => AppTheme.System
        });
        Load += (_, _) =>
        {
            Theme.Apply(this, currentTheme);
            Theme.StyleToolTip(tips);
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += FollowSystemTheme;
        };
        duplicatesButton.Click += (_, _) =>
        {
            using var dialog = new DuplicatesDialog(catalog, currentTheme, selectedId);
            dialog.ShowDialog(this);
        };
        changesButton.Click += (_, _) =>
        {
            using var dialog = new ScanHistoryDialog(catalog, currentTheme, selectedId);
            dialog.ShowDialog(this);
        };
        deepSearchButton.Click += (_, _) =>
        {
            using var dialog = new DeepSearchDialog(catalog, currentTheme, selectedId);
            dialog.ShowDialog(this);
            UpdateSyncTooltip(CloudSync.LoadConfig(catalog.DataDirectory));
        };
        savedSearchButton.Click += (_, _) => ShowSavedSearches();
        saveSearchButton.Click += (_, _) => SaveCurrentSearch();
        var scanLine = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
            RowCount = 1, Padding = new Padding(12, 2, 12, 4) };
        scanLine.ColumnStyles.Add(new(SizeType.Absolute, 265));
        scanLine.ColumnStyles.Add(new(SizeType.Percent, 100));
        scanLine.RowStyles.Add(new(SizeType.AutoSize));
        scanBar.Margin = new Padding(0, 6, 10, 4);
        scanLine.Controls.Add(scanBar, 0, 0);
        scanLine.Controls.Add(scanState, 1, 0);
        scanLine.SizeChanged += (_, _) =>
        {
            var columns = scanLine.GetColumnWidths();
            if (columns.Length < 2 || columns[1] <= scanState.Margin.Horizontal) return;
            var maximum = new Size(columns[1] - scanState.Margin.Horizontal, 0);
            if (scanState.MaximumSize != maximum) scanState.MaximumSize = maximum;
        };
        scanUiTimer.Tick += (_, _) => UpdateScanDisplay();
        scanUiTimer.Start();
        FormClosed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= FollowSystemTheme;
            tips.Dispose();
            scanUiTimer.Dispose();
            pageCancellation.Dispose();
        };
        var filters = Flow();
        filters.Padding = new Padding(4, 4, 4, 4);
        filters.Controls.AddRange([query, status, LabelFor("Extension"), extension,
            LabelFor("Size in bytes"), min, max, LabelFor("Modified from"), from, LabelFor("Through"),
            through, includeRemoved, savedSearchButton, saveSearchButton]);
        foreach (Control item in filters.Controls)
            item.Margin = item is Label ? new Padding(6, 7, 4, 4)
                : item is CheckBox ? new Padding(8, 6, 4, 4)
                : new Padding(0, 3, 6, 3);
        var actions = Flow();
        actions.Padding = new Padding(2, 4, 2, 2);
        actions.Controls.AddRange([copy, reveal, candidates, searchProgress, state]);
        foreach (Control item in actions.Controls)
            item.Margin = item is Label ? new Padding(8, 7, 4, 4)
                : item is ProgressBar ? new Padding(6, 6, 4, 4)
                : new Padding(0, 2, 6, 2);
        var right = Stack(5);
        fileDetails.Controls.Add(fileDetailsText);
        right.Controls.Add(filters, 0, 0);
        right.Controls.Add(location, 0, 1);
        right.Controls.Add(results, 0, 2);
        right.Controls.Add(actions, 0, 3);
        right.Controls.Add(fileDetails, 0, 4);
        right.RowStyles.Add(new(SizeType.AutoSize));
        right.RowStyles.Add(new(SizeType.AutoSize));
        right.RowStyles.Add(new(SizeType.Percent, 100));
        right.RowStyles.Add(new(SizeType.AutoSize));
        right.RowStyles.Add(new(SizeType.AutoSize));

        var schedule = Flow();
        schedule.Padding = new Padding(0, 4, 0, 4);
        schedule.Controls.AddRange([enabled, hours, LabelFor("hours"), saveSchedule]);
        foreach (Control item in schedule.Controls)
            item.Margin = item is CheckBox ? new Padding(0, 6, 6, 2)
                : item is Label ? new Padding(2, 7, 8, 2)
                : new Padding(0, 2, 6, 2);
        var treeTitle = LabelFor("Folders and files");
        treeTitle.Margin = new Padding(2, 2, 4, 6);
        Theme.MarkSectionTitle(treeTitle);
        var left = Stack(4);
        left.Controls.Add(treeTitle, 0, 0);
        left.Controls.Add(tree, 0, 1);
        left.Controls.Add(schedule, 0, 2);
        left.Controls.Add(details, 0, 3);
        left.RowStyles.Add(new(SizeType.AutoSize));
        left.RowStyles.Add(new(SizeType.Percent, 100));
        left.RowStyles.Add(new(SizeType.AutoSize));
        left.RowStyles.Add(new(SizeType.Absolute, 145));
        var split = new SplitContainer { Dock = DockStyle.Fill, Width = 1300, SplitterDistance = 330,
            Panel1MinSize = 220, Panel2MinSize = 400 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        var layout = Stack(3);
        layout.Padding = Padding.Empty;
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(scanLine, 0, 1);
        layout.Controls.Add(split, 0, 2);
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(layout);
        AttachBrowserTooltips();

        var icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        var folder = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(folder))
        {
            g.FillRectangle(Brushes.Goldenrod, 1, 3, 7, 4);
            g.FillRectangle(Brushes.Goldenrod, 1, 6, 14, 9);
            g.FillRectangle(Brushes.Khaki, 2, 7, 12, 6);
        }
        icons.Images.Add("folder", folder);
        var file = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(file))
        {
            g.FillRectangle(Brushes.White, 3, 1, 10, 14);
            g.DrawRectangle(Pens.SlateGray, 3, 1, 10, 14);
            g.DrawLine(Pens.SteelBlue, 5, 6, 11, 6);
            g.DrawLine(Pens.SteelBlue, 5, 9, 11, 9);
        }
        icons.Images.Add("file", file);
        tree.ImageList = icons;
        FormClosed += (_, _) => icons.Dispose();
        tree.BeforeExpand += async (_, e) =>
        {
            if (e.Node?.Tag is not Branch branch || branch.Loaded) return;
            e.Cancel = true;
            await LoadBranch(e.Node);
            if (!closing && branch.Loaded && e.Node.TreeView == tree) e.Node.Expand();
        };
        tree.AfterSelect += async (_, e) =>
        {
            if (e.Node?.Tag is More more && e.Node.Parent is { } parent)
            {
                await LoadChildrenPage(parent, more.Parent, more.Offset);
                return;
            }
            if (locating || e.Node?.Tag is not Branch branch) return;
            if (branch.Directory) ClearFileDetails();
            SelectScanVolume(branch.Volume);
            if (string.IsNullOrWhiteSpace(query.Text))
            {
                browseParent = branch.Directory ? branch.Relative : Path.GetDirectoryName(branch.Relative) ?? "";
                if ((scope.SelectedItem as Choice)?.Id != branch.Volume)
                {
                    restoring = true;
                    scope.SelectedItem = scope.Items.Cast<Choice>().FirstOrDefault(c => c.Id == branch.Volume) ?? scope.Items[0];
                    restoring = false;
                }
                QueueSearch();
            }
        };
        tree.NodeMouseClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Node.Tag is Branch branch && !branch.Directory)
                await OpenTreeFile(branch);
        };
        tree.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || tree.SelectedNode?.Tag is not Branch branch || branch.Directory) return;
            e.SuppressKeyPress = true;
            await OpenTreeFile(branch);
        };
        browse.Click += async (_, _) => await OpenCurrentFolder();
        results.RetrieveVirtualItem += (_, e) =>
        {
            int page = e.ItemIndex / PageSize;
            if (activeRevision != revision || !pageCache.ContainsKey(page))
            {
                _ = LoadResultPage(page);
                e.Item = new ListViewItem("Loading…");
                return;
            }
            e.Item = CachedResult(e.ItemIndex) is { } hit
                ? new ListViewItem([hit.Name, hit.IsDirectory ? "" : Path.GetExtension(hit.Name),
                    hit.FullPath, FormatHitVolume(hit), hit.Status, hit.LastSeen.ToString(),
                    hit.IsDirectory ? "" : hit.Bytes.ToString("N0"), hit.Modified.ToString(), hit.Matched])
                : new ListViewItem("Item no longer available");
        };
        results.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = true;
            await LocateCurrentHit();
        };
    }

    static FlowLayoutPanel CommandGroup(AnchorStyles alignment, params Control[] controls)
    {
        var group = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = alignment == AnchorStyles.Left,
            Anchor = alignment == AnchorStyles.Left ? AnchorStyles.Left | AnchorStyles.Right : alignment,
            Margin = Padding.Empty };
        foreach (var control in controls)
        {
            control.Anchor = AnchorStyles.None;
            control.Margin = new Padding(0, 2, 4, 2);
        }
        group.Controls.AddRange(controls);
        return group;
    }

    const string StorageMapCommand = "Storage Map…";

    void ShowStorageMap()
    {
        using var dialog = new StorageMapDialog(catalog, currentTheme, selectedId);
        dialog.ShowDialog(this);
    }

    void InitializeMoreMenu()
    {
        var menu = new ContextMenuStrip();
        ToolStripMenuItem AddCommand(Button button, FluentIcon icon, string? label = null)
        {
            var item = new ToolStripMenuItem(label ?? button.Text, null, (_, _) => button.PerformClick());
            menu.Opening += (_, _) =>
            {
                item.Text = label ?? button.Text;
                item.Enabled = button.Enabled;
            };
            Theme.SetMenuIcon(menu, item, icon);
            if (button.Parent is null)
                FormClosed += (_, _) => button.Dispose();
            return item;
        }

        var autoCheck = new ToolStripMenuItem("Auto-check for updates on startup")
        {
            CheckOnClick = true,
            Checked = AutoCheckUpdates
        };
        menu.Opening += (_, _) => autoCheck.Checked = AutoCheckUpdates;
        autoCheck.Click += (_, _) =>
        {
            SetAutoCheckUpdates(autoCheck.Checked);
            autoCheck.Checked = AutoCheckUpdates;
        };

        var storageMap = new ToolStripMenuItem(StorageMapCommand, null, (_, _) => ShowStorageMap())
            { ShortcutKeyDisplayString = "Ctrl+M" };
        var commandPalette = new ToolStripMenuItem("Command Palette (Ctrl+K)", null, (_, _) => ShowCommandPalette());
        Theme.SetMenuIcon(menu, storageMap, FluentIcon.StorageMap);
        Theme.SetMenuIcon(menu, commandPalette, FluentIcon.Search);
        // The auto-check command uses the standard check mark to show its toggle state.
        menu.Items.AddRange([
            AddCommand(browse, FluentIcon.FolderOpen),
            AddCommand(searchDrive, FluentIcon.Search),
            storageMap,
            new ToolStripSeparator(),
            AddCommand(syncSettingsButton, FluentIcon.Settings, "Sync settings…"),
            commandPalette,
            new ToolStripSeparator(),
            AddCommand(updateButton, FluentIcon.Update),
            autoCheck,
            AddCommand(releaseNotesButton, FluentIcon.Notes)
        ]);
        moreButton.Click += (_, _) => menu.Show(moreButton, new Point(0, moreButton.Height));
        FormClosed += (_, _) => menu.Dispose();
    }

    static string ThemeName(AppTheme theme) => theme == AppTheme.DarkBlue ? "Dark Blue" : theme.ToString();

    void UpdateThemeButton(AppTheme theme)
    {
        themeButton.Text = ThemeName(theme);
        Theme.SetIcon(themeButton, theme switch
        {
            AppTheme.Light => FluentIcon.ThemeLight,
            AppTheme.Dark => FluentIcon.ThemeDark,
            AppTheme.DarkBlue => FluentIcon.ThemeBlue,
            _ => FluentIcon.ThemeSystem
        });
    }

    void SetTheme(AppTheme theme)
    {
        currentTheme = theme;
        UpdateThemeButton(theme);
        Theme.SavePreference(catalog.DataDirectory, theme);
        Theme.Apply(this, theme);
        Theme.StyleToolTip(tips);
    }

    void FollowSystemTheme(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (currentTheme != AppTheme.System || !IsHandleCreated || IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!closing && currentTheme == AppTheme.System)
                {
                    Theme.Apply(this, currentTheme);
                    Theme.StyleToolTip(tips);
                }
            });
        }
        catch (InvalidOperationException) when (!IsHandleCreated || IsDisposed || closing) { }
    }

    void AttachBrowserTooltips()
    {
        inventoryTips.SetToolTip(library, "");
        inventoryTips.SetToolTip(searchDrive, "");
        (Control Control, string Text)[] descriptions =
        [
            (scope, "Choose a drive or browse all cataloged drives."),
            (refresh, "Choose online drives to scan or refresh (F5)."),
            (cancel, "Stop the active drive scan."),
            (browse, "Show the contents of the selected folder in the results."),
            (library, "Manage scanned drives, search scope, drive notes, and exports (Ctrl+D)."),
            (duplicatesButton, "Find matching files across drives and verify copies with SHA-256."),
            (deepSearchButton, "Deep Search & Content Scan — index and search text inside PDF, DOCX, XLSX, PPTX, and text/code files (Ctrl+Shift+F)."),
            (changesButton, "Review scan history and see which files changed between scans."),
            (searchDrive, "Limit filename searches to the drive selected above."),
            (updateButton, "Check for a newer File Inventory release."),
            (releaseNotesButton, "Read the release notes and version history."),
            (themeButton, "Cycle through System, Light, Dark, and Dark Blue themes."),
            (moreButton, "Open folder, search, sync settings, command palette, and update actions."),
            (query, "Search names across drives (Ctrl+F or Ctrl+L). Use ext:, drive:, min:, max:, after:, before:, online:, or removed: for structured filters."),
            (status, "Filter results by whether the drive is online or offline."),
            (extension, "Filter by file extension, such as .pdf."),
            (min, "Set the minimum file size in bytes."),
            (max, "Set the maximum file size in bytes."),
            (from, "Enable a lower bound for the file modification date."),
            (through, "Enable an inclusive upper bound for the file modification date."),
            (includeRemoved, "Include files marked removed by a later scan."),
            (savedSearchButton, "Apply or delete a named saved search."),
            (saveSearchButton, "Save the current query and filters under a name."),
            (tree, "Expand folders to browse the inventory. Select an item to see its details."),
            (results, "Select a result for Quick Look. Press Enter to locate it in the tree."),
            (enabled, "Enable automatic rescans for the selected drive when it is online."),
            (hours, "Set the number of hours between scheduled scan attempts."),
            (saveSchedule, "Save the selected drive’s refresh schedule."),
            (copy, "Copy the selected result’s full path to the clipboard."),
            (reveal, "Open File Explorer and select the result at its current location."),
            (candidates, "Find possible copies on other drives using file size and timestamps."),
            (scanBar, "Shows the progress of the current scan or update."),
            (searchProgress, "Indicates that the inventory search is running."),
            (details, "Shows the selected drive’s scan status and details."),
            (fileDetailsText, "Shows the selected file or folder’s metadata.")
        ];
        foreach (var (control, description) in descriptions)
            Theme.AttachTooltip(control, tips, description);
    }

    void ShowSavedSearches()
    {
        try
        {
            var searches = savedSearches.Load();
            var menu = new ContextMenuStrip();
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            if (searches.Count == 0)
                menu.Items.Add(new ToolStripMenuItem("No saved searches") { Enabled = false });
            foreach (var search in searches)
            {
                var item = new ToolStripMenuItem(search.Name.Replace("&", "&&"));
                item.DropDownItems.Add("Apply", null, (_, _) => ApplySavedSearch(search));
                item.DropDownItems.Add("Delete…", null, (_, _) =>
                {
                    if (MessageBox.Show(this, $"Delete “{search.Name}”?", "Saved searches",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    try { savedSearches.Delete(search.Name); }
                    catch (Exception ex) { state.Text = ex.Message; }
                });
                menu.Items.Add(item);
            }
            menu.Show(savedSearchButton, new Point(0, savedSearchButton.Height));
        }
        catch (Exception ex) { state.Text = ex.Message; }
    }

    void SaveCurrentSearch()
    {
        try
        {
            string input = query.Text;
            var filters = ReadSearchFilters(!string.IsNullOrWhiteSpace(input));
            QueryParser.Parse(input, filters);
            using var dialog = new Form
            {
                Text = "Save search", StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, ClientSize = new Size(340, 105)
            };
            var name = new TextBox { Width = 310, PlaceholderText = "Search name", AccessibleName = "Search name" };
            var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
            var dismiss = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            layout.Controls.AddRange([name, save, dismiss]);
            layout.SetFlowBreak(name, true);
            dialog.Controls.Add(layout);
            dialog.AcceptButton = save;
            dialog.CancelButton = dismiss;
            name.TextChanged += (_, _) => save.Enabled = !string.IsNullOrWhiteSpace(name.Text);
            Theme.Apply(dialog, currentTheme);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            string title = name.Text.Trim();
            if (savedSearches.Load().Any(search => string.Equals(search.Name, title, StringComparison.OrdinalIgnoreCase))
                && MessageBox.Show(this, $"Replace “{title}”?", "Saved searches",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            savedSearches.Save(title, input, filters);
        }
        catch (Exception ex) { state.Text = ex.Message; }
    }

    void ApplySavedSearch(SavedSearch search)
    {
        try
        {
            search.Parse();
            var filters = search.Filters;
            restoring = true;
            try
            {
                if (string.IsNullOrWhiteSpace(search.Query))
                {
                    var choice = scope.Items.Cast<Choice>().FirstOrDefault(item => item.Id == filters.Volume);
                    scope.SelectedItem = choice ?? scope.Items[0];
                }
                status.SelectedIndex = filters.Online is null ? 0 : filters.Online.Value ? 1 : 2;
                extension.Text = filters.Extension ?? "";
                min.Text = filters.Min?.ToString() ?? "";
                max.Text = filters.Max?.ToString() ?? "";
                RestoreSearchDate(from, filters.From, 0);
                RestoreSearchDate(through, filters.Through, -1);
                includeRemoved.Checked = filters.IncludeRemoved;
                query.Text = search.Query;
                if (!string.IsNullOrWhiteSpace(search.Query))
                    ApplySearchDrives(filters.Drives?.ToHashSet(StringComparer.Ordinal));
            }
            finally { restoring = false; }
            SelectBrowserDrive();
        }
        catch (Exception ex) { state.Text = ex.Message; }
    }

    static void RestoreSearchDate(DateTimePicker picker, long? timestamp, int offset)
    {
        if (timestamp is long time)
        {
            var date = Catalog.Local(time).Date.AddDays(offset);
            picker.Value = date < picker.MinDate ? picker.MinDate : date > picker.MaxDate ? picker.MaxDate : date;
        }
        picker.Checked = timestamp is not null;
    }

    string FormatHitVolume(Hit hit) => volumesById.TryGetValue(hit.Volume, out var volume)
        ? volume.DisplayName
        : hit.Label;

    void SelectScanVolume(string volume)
    {
        int index = drives.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == volume);
        if (index < 0) return;
        if (drives.SelectedIndex != index) { drives.ClearSelected(); drives.SelectedIndex = index; }
        selectedId = volume;
        ShowVolume();
    }

    bool ReloadBrowseScope(bool selectFirstDrive = false)
    {
        var previousId = (scope.SelectedItem as Choice)?.Id;
        bool wasRestoring = restoring;
        restoring = true;
        scope.BeginUpdate();
        try
        {
            scope.Items.Clear();
            scope.Items.Add(new Choice(null, "All volumes"));
            int selectedIndex = 0;
            foreach (var volume in volumes)
            {
                if (searchDrives is not null && !searchDrives.Contains(volume.Id)) continue;
                int index = scope.Items.Add(new Choice(volume.Id, volume.DisplayStatusLabel));
                if (volume.Id == previousId) selectedIndex = index;
            }
            if (selectFirstDrive && previousId is null && scope.Items.Count > 1)
                selectedIndex = 1;
            scope.SelectedIndex = selectedIndex;
        }
        finally
        {
            scope.EndUpdate();
            restoring = wasRestoring;
        }
        return previousId != (scope.SelectedItem as Choice)?.Id;
    }

    void SelectBrowserDrive()
    {
        ClearFileDetails();
        browseParent = "";
        if ((scope.SelectedItem as Choice)?.Id is { } volume) SelectScanVolume(volume);
        else { drives.ClearSelected(); selectedId = null; ShowVolume(); }
        ResetTree();
        QueueSearch();
    }

    TreeNode MakeNode(string volume, string relative, string name, bool directory)
    {
        var node = new TreeNode(name) { Tag = new Branch(volume, relative, directory),
            ImageKey = directory ? "folder" : "file", SelectedImageKey = directory ? "folder" : "file",
            ToolTipText = relative };
        if (directory) node.Nodes.Add(new TreeNode("Loading…"));
        return node;
    }

    void ResetTree()
    {
        navigationRevision++;
        tree.BeginUpdate();
        tree.Nodes.Clear();
        var selected = (scope.SelectedItem as Choice)?.Id;
        foreach (var volume in volumes.Where(v => selected is null || v.Id == selected))
        {
            var root = MakeNode(volume.Id, "", volume.DisplayStatusLabel, true);
            root.ToolTipText = volume.Mount;
            tree.Nodes.Add(root);
        }
        tree.EndUpdate();
        if (tree.Nodes.Count == 1) tree.Nodes[0].Expand();
    }

    Task LoadBranch(TreeNode node)
    {
        if (node.Tag is not Branch branch || !branch.Directory || branch.Loaded) return Task.CompletedTask;
        return branch.Loading ??= LoadChildrenPage(node, branch, 0);
    }

    async Task LoadChildrenPage(TreeNode node, Branch branch, int offset)
    {
        try
        {
            bool showRemoved = includeRemoved.Checked;
            var page = await Task.Run(() => catalog.QueryPage("", new Filters(Volume: branch.Volume, IncludeRemoved: showRemoved), offset, PageSize, branch.Relative));
            if (closing || node.TreeView != tree) return;
            tree.BeginUpdate();
            try
            {
                foreach (var extra in node.Nodes.Cast<TreeNode>().Where(n => n.Tag is not Branch).ToArray()) extra.Remove();
                var existing = node.Nodes.Cast<TreeNode>().Where(n => n.Tag is Branch)
                    .Select(n => ((Branch)n.Tag!).Relative).ToHashSet(StringComparer.Ordinal);
                var children = page.Rows.Where(h => existing.Add(h.RelativePath))
                    .Select(h => MakeNode(h.Volume, h.RelativePath, h.Name, h.IsDirectory)).ToArray();
                node.Nodes.AddRange(children);
                if (offset + page.Rows.Count < page.Total)
                    node.Nodes.Add(new TreeNode($"Show more… ({offset + page.Rows.Count:N0} of {page.Total:N0})")
                        { Tag = new More(branch, offset + page.Rows.Count) });
                branch.Loaded = true;
            }
            finally { tree.EndUpdate(); }
        }
        catch (Exception ex)
        {
            branch.Loading = null;
            if (!closing) state.Text = $"Could not load folder: {ex.Message}";
        }
    }

    Task LoadResultPage(int page)
    {
        if (closing || activeRevision != revision || pageCache.ContainsKey(page)
            || page * PageSize >= results.VirtualListSize) return Task.CompletedTask;
        if (pendingPages.TryGetValue(page, out var pending)) return pending;
        var task = LoadResultPageCore(page, pageGeneration, pageCancellation.Token);
        pendingPages[page] = task;
        return task;
    }

    async Task LoadResultPageCore(int page, int generation, CancellationToken token)
    {
        string input = activeInput;
        var filters = activeFilters;
        var parent = activeParent;
        var matches = activeMatches;
        await Task.Yield();
        bool entered = false;
        try
        {
            await pageGate.WaitAsync(token);
            entered = true;
            int offset = page * PageSize;
            var rows = await Task.Run(() => matches is null
                ? catalog.QueryPage(input, filters, offset, PageSize, parent, false, token).Rows
                : catalog.ReadSearchPage(input, matches, offset, PageSize, token), token);
            if (closing || token.IsCancellationRequested || generation != pageGeneration) return;
            pageCache[page] = rows;
            if (pageCache.Count > 24)
                foreach (int old in pageCache.Keys.OrderByDescending(p => Math.Abs(p - page)).Take(pageCache.Count - 24).ToArray())
                    pageCache.Remove(old);
            int end = offset + Math.Min(PageSize, results.VirtualListSize - offset) - 1;
            if (end >= offset) results.RedrawItems(offset, end, invalidateOnly: true);
            ShowActions();
        }
        catch (Exception ex) { if (!closing && !token.IsCancellationRequested && generation == pageGeneration) state.Text = ex.Message; }
        finally
        {
            if (entered) pageGate.Release();
            if (generation == pageGeneration) pendingPages.Remove(page);
        }
    }

    void UpdateScanDisplay()
    {
        if (checkingUpdates)
        {
            ShowUpdateProgress();
            return;
        }
        scanBar.AccessibleName = "Scan progress";
        var selected = SelectedVolumes();
        var volume = selected.FirstOrDefault(v => running.ContainsKey(v.Id));
        if (volume is not null && progress.TryGetValue(volume.Id, out var sample))
        {
            var elapsed = scanClocks.TryGetValue(volume.Id, out var clock) ? clock.Elapsed : TimeSpan.FromSeconds(sample.ElapsedSeconds);
            bool cancelling = running[volume.Id].Cancellation.IsCancellationRequested;
            var display = ScanDisplay.Create(sample, elapsed, cancelling);
            var style = display.Indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (scanBar.Style != style) scanBar.Style = style;
            scanBar.Visible = true;
            scanBar.Value = display.Percent;
            string text = volume.Mount + " " + display.Text;
            if (scanState.Text != text) scanState.Text = text;
            cancel.Enabled = !cancelling && !closing;
            string detail = $"{sample.Phase}\r\n{sample.Directory}\r\nOmitted areas: {sample.Omitted:N0}";
            if (details.Text != detail) details.Text = detail;
        }
        else
        {
            bool complete = selected.Count == 1 && selected[0].Outcome.StartsWith("Complete", StringComparison.Ordinal);
            if (scanBar.Style != ProgressBarStyle.Continuous) scanBar.Style = ProgressBarStyle.Continuous;
            scanBar.Value = complete ? 100 : 0;
            scanBar.Visible = complete;
            string text = selected.Count == 0 ? "Select a drive above, then click Scan."
                : string.Join("\r\n", selected.Select(v => !v.Online ? $"{v.Mount} Offline — reconnect to scan."
                    : v.Outcome.Length == 0 ? $"{v.Mount} Ready to scan."
                    : $"{v.Mount} {v.Outcome} · {v.Issues.Split('\r', '\n')[0].Split("NTFS identity time:")[0].Trim()}"));
            if (scanState.Text != text) scanState.Text = text;
        }
    }

    async Task LocateCurrentHit()
    {
        try
        {
            int index = SelectedResult;
            int requested = revision;
            if (index >= 0 && CurrentHit() is null) await LoadResultPage(index / PageSize);
            if (requested != revision || index != SelectedResult || CurrentHit() is not { } hit) return;
            int navigation = ++navigationRevision;
            var root = tree.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is Branch b && b.Volume == hit.Volume);
            if (root is null)
            {
                var drive = volumes.FirstOrDefault(v => v.Id == hit.Volume);
                if (drive is null) return;
                root = MakeNode(drive.Id, "", drive.DisplayStatusLabel, true);
                tree.Nodes.Add(root);
            }
            locating = true;
            var node = root;
            var parts = hit.RelativePath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            string relative = "";
            for (int i = 0; i < parts.Length; i++)
            {
                await LoadBranch(node);
                if (closing || navigation != navigationRevision) return;
                node.Expand();
                relative = Path.Combine(relative, parts[i]);
                var child = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is Branch b && b.Relative == relative);
                if (child is null)
                {
                    child = MakeNode(hit.Volume, relative, parts[i], i < parts.Length - 1 || hit.IsDirectory);
                    node.Nodes.Insert(0, child);
                }
                node = child;
            }
            tree.SelectedNode = node;
            node.EnsureVisible();
            location.Text = hit.FullPath;
            if (node.Tag is Branch branch && !branch.Directory)
                _ = OpenTreeFile(branch, openExplorer: false);
            else
                ClearFileDetails();
        }
        catch (Exception ex) { if (!closing) state.Text = $"Could not show location: {ex.Message}"; }
        finally { locating = false; }
    }

    async Task OpenCurrentFolder(bool fromList = false)
    {
        if (fromList && CurrentHit() is { IsDirectory: true } hit)
        {
            await LocateCurrentHit();
            browseParent = hit.RelativePath;
            restoring = true;
            scope.SelectedItem = scope.Items.Cast<Choice>().FirstOrDefault(c => c.Id == hit.Volume) ?? scope.Items[0];
            restoring = false;
        }
        else if (tree.SelectedNode?.Tag is Branch branch)
        {
            browseParent = branch.Directory ? branch.Relative : Path.GetDirectoryName(branch.Relative) ?? "";
            restoring = true;
            scope.SelectedItem = scope.Items.Cast<Choice>().FirstOrDefault(c => c.Id == branch.Volume) ?? scope.Items[0];
            restoring = false;
        }
        query.Clear();
        QueueSearch();
    }
}
