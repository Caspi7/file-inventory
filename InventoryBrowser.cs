namespace FileInventory;

sealed partial class InventoryWindow
{
    const int PageSize = 256;
    readonly TreeView tree = new() { Dock = DockStyle.Fill, HideSelection = false,
        AccessibleName = "Inventory file tree", ShowNodeToolTips = true };
    readonly Label location = new() { AutoSize = true, Padding = new Padding(6),
        MaximumSize = new Size(1200, 0) };
    readonly Button browse = new() { Text = "Browse selected folder", AutoSize = true };
    readonly Button themeButton = new() { Text = "Theme: Light", AutoSize = true };
    AppTheme currentTheme = AppTheme.Light;
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
        scope.Width = 260;
        InitializeLibrary();
        InitializeUpdates();
        query.Width = 320;
        refresh.Dock = DockStyle.None;
        refresh.Width = 175;
        refresh.Height = 32;
        scanState.Dock = DockStyle.Top;
        scanState.UseMnemonic = false;
        location.UseMnemonic = false;
        Theme.MarkPrimaryLabel(location);
        details.Height = 140;
        var toolbar = Flow();
        toolbar.Padding = new Padding(10, 8, 10, 6);
        toolbar.Controls.AddRange([LabelFor("Browse drive"), scope, refresh, cancel, browse, library, searchDrive, updateButton, releaseNotesButton, themeButton]);
        foreach (Control item in toolbar.Controls)
            item.Margin = item is Label ? new Padding(2, 8, 6, 2) : new Padding(0, 2, 6, 2);
        currentTheme = Theme.LoadPreference(catalog.DataDirectory);
        themeButton.Text = currentTheme switch
        {
            AppTheme.Dark => "Theme: Dark",
            AppTheme.DarkBlue => "Theme: Dark Blue",
            _ => "Theme: Light"
        };
        themeButton.Click += (_, _) =>
        {
            currentTheme = currentTheme switch
            {
                AppTheme.Light => AppTheme.Dark,
                AppTheme.Dark => AppTheme.DarkBlue,
                _ => AppTheme.Light
            };
            themeButton.Text = currentTheme switch
            {
                AppTheme.Dark => "Theme: Dark",
                AppTheme.DarkBlue => "Theme: Dark Blue",
                _ => "Theme: Light"
            };
            Theme.SavePreference(catalog.DataDirectory, currentTheme);
            Theme.Apply(this, currentTheme);
        };
        Load += (_, _) => Theme.Apply(this, currentTheme);
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
        FormClosed += (_, _) => { scanUiTimer.Dispose(); pageCancellation.Dispose(); };
        var filters = Flow();
        filters.Padding = new Padding(4, 4, 4, 4);
        filters.Controls.AddRange([query, status, LabelFor("Extension"), extension,
            LabelFor("Size in bytes"), min, max, LabelFor("Modified from"), from, LabelFor("Through"), through, includeRemoved]);
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
                    scope.SelectedIndex = scope.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == branch.Volume);
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
                    hit.FullPath, hit.Label, hit.Status, hit.LastSeen.ToString(),
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

    void SelectScanVolume(string volume)
    {
        int index = drives.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == volume);
        if (index < 0) return;
        if (drives.SelectedIndex != index) { drives.ClearSelected(); drives.SelectedIndex = index; }
        selectedId = volume;
        ShowVolume();
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
            var root = MakeNode(volume.Id, "", $"{volume.Label} ({volume.Mount}){(volume.Online ? "" : " · Offline")}", true);
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
                root = MakeNode(drive.Id, "", $"{drive.Label} ({drive.Mount})", true);
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
            scope.SelectedIndex = scope.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == hit.Volume);
            restoring = false;
        }
        else if (tree.SelectedNode?.Tag is Branch branch)
        {
            browseParent = branch.Directory ? branch.Relative : Path.GetDirectoryName(branch.Relative) ?? "";
            restoring = true;
            scope.SelectedIndex = scope.Items.Cast<Choice>().ToList().FindIndex(c => c.Id == branch.Volume);
            restoring = false;
        }
        query.Clear();
        QueueSearch();
    }
}
