using System.Diagnostics;

namespace FileInventory;

sealed class DeepSearchDialog : Form
{
    const string ArchiveFilter = "File Inventory Content Index (*.ficontent)|*.ficontent";
    const int ResultLimit = 1000;
    readonly Catalog catalog;
    readonly ContentIndex contentIndex;
    readonly ToolTip tips = new();
    readonly System.Windows.Forms.Timer estimateTimer = new() { Interval = 250 };
    readonly TextBox query = new() { Dock = DockStyle.Fill, AccessibleName = "Search text inside files",
        PlaceholderText = "Search inside files… e.g. (invoice OR receipt) 2025 -draft  or describe what you're looking for" };
    readonly TextBox extension = new() { Width = 100, AccessibleName = "Extension",
        PlaceholderText = "Extension" };
    readonly ComboBox searchDrive = Selector("Search drive", 155);
    readonly ComboBox matchMode = new() { AccessibleName = "Match mode", Width = 185,
        DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox fuzzy = Option("Typo-tolerant");
    readonly FlowLayoutPanel searchTools = Row();
    readonly ComboBox scanDrive = Selector("Scan drive", 175);
    readonly ComboBox resourceMode = Selector("Resource mode", 310);
    readonly CheckBox text = Option("Text & Code");
    readonly CheckBox office = Option("Office");
    readonly CheckBox pdf = Option("PDF");
    readonly CheckBox snippets = Option("Offline excerpts");
    readonly CheckBox skipSystemFolders = Option("Skip system folders");
    readonly Label status = new() { Text = "Enter text to search the content index.",
        Height = 23, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft,
        UseMnemonic = false };
    readonly Label estimate = Line("Estimating eligible files…");
    readonly Label progressText = Line("Ready to scan.");
    readonly ListView results = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        HideSelection = false, MultiSelect = false, ShowItemToolTips = true, AccessibleName = "Content search results" };
    readonly ProgressBar progress = new() { Width = 185, Height = 18, AccessibleName = "Content scan progress" };
    readonly Button search = ActionButton("Search", FluentIcon.Search);
    readonly Button copy = ActionButton("Copy Path", FluentIcon.Copy);
    readonly Button reveal = ActionButton("Reveal in Explorer", FluentIcon.FolderOpen);
    readonly Button scan = ActionButton("Scan content", FluentIcon.Scan);
    readonly Button cancel = ActionButton("Cancel", FluentIcon.Cancel);
    readonly Button sync = ActionButton("Sync content", FluentIcon.Sync);
    readonly Button export = ActionButton("Export content…", FluentIcon.Export);
    readonly Button import = ActionButton("Import content…", FluentIcon.Import);
    CancellationTokenSource? scanCancellation;
    bool busy, closeRequested, estimating;
    int estimateRevision;

    sealed record DriveChoice(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    public DeepSearchDialog(Catalog catalog, AppTheme theme, string? initialVolumeId = null)
    {
        this.catalog = catalog;
        contentIndex = new ContentIndex(catalog);
        Text = "Deep Search";
        Width = 960;
        Height = 680;
        MinimumSize = new Size(850, 600);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;

        searchDrive.Items.Add(new DriveChoice(null, "All drives"));
        scanDrive.Items.Add(new DriveChoice(null, "All online drives"));
        foreach (var volume in catalog.Volumes())
        {
            searchDrive.Items.Add(new DriveChoice(volume.Id, volume.DisplayStatusLabel));
            if (volume.Online) scanDrive.Items.Add(new DriveChoice(volume.Id, volume.DisplayName));
        }
        SelectDrive(searchDrive, initialVolumeId);
        SelectDrive(scanDrive, initialVolumeId);
        resourceMode.Items.AddRange(["Eco (Background — Low CPU & Disk I/O)", "Balanced", "Turbo (Full Resources)"]);
        resourceMode.SelectedIndex = (int)ContentScanMode.Eco;
        results.Columns.Add("Filename", 176);
        results.Columns.Add("Drive", 130);
        results.Columns.Add("Excerpt", 325);
        results.Columns.Add("Size", 82, HorizontalAlignment.Right);
        results.Columns.Add("Modified", 135);
        results.ClientSizeChanged += (_, _) =>
        {
            int reserved = results.Columns.Cast<ColumnHeader>().Where((_, index) => index != 2).Sum(column => column.Width);
            results.Columns[2].Width = Math.Max(100, results.ClientSize.Width - reserved - SystemInformation.VerticalScrollBarWidth);
        };

        var criteria = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
        criteria.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 1; i < 4; i++) criteria.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        criteria.Controls.Add(query, 0, 0);
        criteria.Controls.Add(searchDrive, 1, 0);
        criteria.Controls.Add(extension, 2, 0);
        criteria.Controls.Add(search, 3, 0);

        matchMode.Items.AddRange(["Smart (Boolean + Close)", "All keywords (AND)", "Any keyword (OR)", "Exact phrase"]);
        matchMode.SelectedIndex = (int)ContentMatchMode.Smart;
        var matchLabel = Line("Match");
        searchTools.AccessibleName = "Search Tools";
        searchTools.WrapContents = true;
        fuzzy.Padding = Padding.Empty;
        foreach (var control in new Control[] { matchLabel, matchMode, fuzzy })
        {
            control.Anchor = AnchorStyles.Left;
            searchTools.Controls.Add(control);
        }
        AddSearchTool("+ AND", "Require both sides of the AND operator.", () => InsertSyntax(" AND "));
        AddSearchTool("+ OR", "Match either side of the OR operator.", () => InsertSyntax(" OR "));
        AddSearchTool("+ Exclude", "Exclude a word, phrase, or group after a minus sign.", () => InsertSyntax(" -"));
        AddSearchTool("\"Phrase\"", "Wrap selected text in quotes to match words together in order.", () => InsertSyntax("\"", "\""));
        AddSearchTool("(Group)", "Wrap selected text in parentheses to combine conditions.", () => InsertSyntax("(", ")"));
        AddSearchTool("? Tips", "Show examples for Boolean, wildcard, typo-tolerant, and descriptive searches.", ShowSearchTips);

        var searchArea = new GroupBox { Text = "Search Inside Files", Dock = DockStyle.Fill, Padding = new Padding(9) };
        var searchLayout = Stack();
        AddRow(searchLayout, criteria);
        AddRow(searchLayout, searchTools);
        AddRow(searchLayout, status);
        AddRow(searchLayout, results, SizeType.Percent);
        AddRow(searchLayout, Row(copy, reveal));
        searchArea.Controls.Add(searchLayout);

        var scanArea = new GroupBox { Text = "Background Content Scan & Sync", Dock = DockStyle.Fill,
            AutoSize = true, Padding = new Padding(9) };
        var scanLayout = Stack();
        AddRow(scanLayout, Row(Line("Drive"), scanDrive, Line("Resource Mode"), resourceMode));
        var optionsRow = Row(text, office, pdf, snippets, skipSystemFolders);
        optionsRow.WrapContents = true;
        AddRow(scanLayout, optionsRow);
        AddRow(scanLayout, estimate);
        var scanActions = Row(scan, cancel, sync, export, import);
        scanActions.WrapContents = true;
        AddRow(scanLayout, scanActions);
        var scanProgress = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2 };
        scanProgress.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        scanProgress.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progress.Anchor = AnchorStyles.Left;
        progressText.AutoSize = false;
        progressText.Dock = DockStyle.Fill;
        progressText.AutoEllipsis = true;
        progressText.TextAlign = ContentAlignment.MiddleLeft;
        progressText.Height = 23;
        scanProgress.Controls.Add(progress, 0, 0);
        scanProgress.Controls.Add(progressText, 1, 0);
        AddRow(scanLayout, scanProgress);
        scanArea.Controls.Add(scanLayout);

        var layout = Stack();
        layout.Padding = new Padding(9);
        AddRow(layout, searchArea, SizeType.Percent);
        AddRow(layout, scanArea);
        Controls.Add(layout);
        AcceptButton = search;
        ActiveControl = query;

        (Control Control, string Help)[] descriptions =
        [
            (query, "Enter words to find inside indexed files. Press Enter to search."),
            (searchDrive, "Search all indexed drives or limit results to one drive, including offline drives."),
            (extension, "Optionally limit results to a file extension such as .pdf."),
            (search, "Search the indexed contents using the current drive and extension filters."),
            (searchTools, "Choose how search terms match and insert search operators at the cursor."),
            (matchLabel, "Controls how keywords are combined in the search."),
            (matchMode, "Smart can rank close matches. Choose all keywords, any keyword, or an exact phrase for a stricter search."),
            (fuzzy, "Look for close spellings of words the index did not find, including adjacent letters typed out of order."),
            (status, "Shows search timing, returned matches, and the size of the content index."),
            (results, "Select a match to copy its path or reveal it on an online drive."),
            (copy, "Copy the full path of the selected match."),
            (reveal, "Select the matching file in Windows File Explorer."),
            (scanDrive, "Choose which online drives to index."),
            (text, "Index supported plain text and source code files."),
            (office, "Index Microsoft Office (DOCX, XLSX, PPTX) and OpenDocument (ODT, ODS, ODP) files."),
            (pdf, "Index extractable text in PDF files."),
            (snippets, "Store Brotli-compressed text so search results can show excerpts, even offline."),
            (skipSystemFolders, "Skip drive-root Windows, Program Files, ProgramData, Recycle Bin, and System Volume Information folders, plus nested dependency, Git, AppData cache, and build-output folders."),
            (resourceMode, "Choose how much CPU and disk activity the background content scan can use."),
            (estimate, "Estimates pending work and additional index size from the current scan options."),
            (scan, "Index new or changed content with the selected options."),
            (cancel, "Request cancellation. Completed scan batches remain indexed."),
            (progress, "Shows how many pending files the scan has processed."),
            (progressText, "Shows the current scan or snapshot operation status."),
            (sync, "Exchange content snapshots through the configured Cloud Sync folder."),
            (export, "Save the current content index as a .ficontent archive."),
            (import, "Import indexed text for matching files already in this catalog.")
        ];
        foreach (var (control, help) in descriptions) Theme.AttachTooltip(control, tips, help);

        search.Click += async (_, _) => await RunAsync(SearchAsync, status);
        matchMode.SelectedIndexChanged += async (_, _) => await SearchOptionsChangedAsync();
        fuzzy.CheckedChanged += async (_, _) => await SearchOptionsChangedAsync();
        copy.Click += (_, _) => { if (SelectedHit is { } hit) UiHelpers.CopyText(hit.FullPath, this); };
        reveal.Click += (_, _) => { if (SelectedHit is { Online: true } hit) UiHelpers.RevealInExplorer(hit.FullPath, this); };
        results.SelectedIndexChanged += (_, _) => UpdateActions();
        scan.Click += async (_, _) => await RunAsync(ScanAsync, progressText);
        cancel.Click += (_, _) => CancelScan();
        sync.Click += async (_, _) => await RunAsync(SyncAsync, progressText);
        export.Click += async (_, _) => await RunAsync(ExportAsync, progressText);
        import.Click += async (_, _) => await RunAsync(ImportAsync, progressText);
        scanDrive.SelectedIndexChanged += (_, _) => QueueEstimate();
        foreach (var option in new[] { text, office, pdf, snippets, skipSystemFolders })
            option.CheckedChanged += (_, _) => QueueEstimate();
        estimateTimer.Tick += async (_, _) => await RefreshEstimateAsync();
        Load += async (_, _) =>
        {
            Theme.Apply(this, theme);
            foreach (var button in searchTools.Controls.OfType<Button>())
                button.Padding = new Padding(4, 1, 4, 1);
            foreach (var button in new[] { search, copy, reveal, scan, cancel, sync, export, import })
                button.Padding = new Padding(button.Padding.Left, 2, button.Padding.Right, 2);
            Theme.StyleToolTip(tips);
            QueueEstimate();
            await RunAsync(SearchAsync, status);
        };
        FormClosing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            closeRequested = true;
            CancelScan();
        };
        UpdateActions();
    }

    Task SearchOptionsChangedAsync() => string.IsNullOrWhiteSpace(query.Text)
        ? Task.CompletedTask : RunAsync(SearchAsync, status);

    void AddSearchTool(string label, string help, Action action)
    {
        var button = new Button { Text = label, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left, Margin = new Padding(2), UseMnemonic = false };
        button.Click += (_, _) => action();
        searchTools.Controls.Add(button);
        Theme.AttachTooltip(button, tips, help);
    }

    void InsertSyntax(string opening, string? closing = null)
    {
        int start = query.SelectionStart;
        string selection = query.SelectedText;
        if (closing is null)
        {
            query.SelectionLength = 0;
            query.SelectedText = opening;
        }
        else
        {
            query.SelectedText = opening + selection + closing;
        }
        query.SelectionStart = start + opening.Length;
        query.SelectionLength = closing is null ? 0 : selection.Length;
        query.Focus();
    }

    void ShowSearchTips() => MessageBox.Show(this, """
        All words: invoice 2025 (or invoice AND 2025)
        Any word: invoice OR receipt
        Combinations: (invoice OR receipt) AND 2025 -draft
        Exact phrase: "quarterly tax report"
        Wildcard prefix: invoic*
        Typo-tolerant: invoce finds invoice automatically
        Descriptive: quarterly tax invoice from accountant finds the closest matching documents in Smart mode
        """, "Deep Search Tips", MessageBoxButtons.OK, MessageBoxIcon.Information);

    ContentSearchHit? SelectedHit => results.SelectedItems.Count == 1
        ? results.SelectedItems[0].Tag as ContentSearchHit : null;

    static ComboBox Selector(string name, int width) => new()
    { AccessibleName = name, Width = width, DropDownWidth = Math.Max(380, width), DropDownStyle = ComboBoxStyle.DropDownList };
    static CheckBox Option(string label) => new()
    {
        Text = label, AutoSize = true, Checked = true, UseMnemonic = false, Padding = new Padding(0, 0, 8, 0)
    };
    static Label Line(string label) => new() { Text = label, AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left };

    static Button ActionButton(string label, FluentIcon icon)
    {
        var button = new Button { Text = label, AutoSize = true };
        Theme.SetIcon(button, icon);
        return button;
    }

    static TableLayoutPanel Stack() => new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };

    static void AddRow(TableLayoutPanel layout, Control control, SizeType size = SizeType.AutoSize)
    {
        int row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(size, size == SizeType.Percent ? 100 : 0));
        layout.Controls.Add(control, 0, row);
    }

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        foreach (var control in controls) control.Anchor = AnchorStyles.Left;
        row.Controls.AddRange(controls);
        return row;
    }

    static string? DriveId(ComboBox selector) => (selector.SelectedItem as DriveChoice)?.Id;

    static void SelectDrive(ComboBox selector, string? id) =>
        selector.SelectedItem = selector.Items.Cast<DriveChoice>().FirstOrDefault(choice => choice.Id == id)
            ?? selector.Items[0];

    ContentScanOptions ScanOptions() => new(
        VolumeIds: DriveId(scanDrive) is { } id ? [id] : null,
        Mode: (ContentScanMode)resourceMode.SelectedIndex, StoreSnippets: snippets.Checked,
        IncludeText: text.Checked, IncludeOffice: office.Checked, IncludePdf: pdf.Checked,
        SkipSystemFolders: skipSystemFolders.Checked);

    void QueueEstimate()
    {
        estimateRevision++;
        estimate.Text = "Estimating eligible files…";
        estimateTimer.Stop();
        if (!estimating) estimateTimer.Start();
    }

    async Task RefreshEstimateAsync()
    {
        estimateTimer.Stop();
        estimating = true;
        int revision = estimateRevision;
        var options = ScanOptions();
        try
        {
            var value = await Task.Run(() => ContentIndex.EstimateScan(catalog, options));
            if (IsDisposed || revision != estimateRevision) return;
            estimate.Text = $"Eligible: {value.EligibleFiles:N0} files · Already indexed: {value.AlreadyIndexed:N0} · "
                + $"To scan: {value.ToScan:N0} (Source: {UiHelpers.FormatBytes(value.TotalSourceBytes)}, "
                + $"Est. index: ~{UiHelpers.FormatBytes(value.EstimatedIndexBytes)})";
        }
        catch (Exception ex)
        {
            if (!IsDisposed && revision == estimateRevision) estimate.Text = $"Estimate unavailable. {ex.Message}";
        }
        finally
        {
            estimating = false;
            if (!IsDisposed && revision != estimateRevision) estimateTimer.Start();
        }
    }

    void UpdateActions()
    {
        search.Enabled = scan.Enabled = sync.Enabled = export.Enabled = import.Enabled = !busy;
        searchTools.Enabled = !busy;
        scanDrive.Enabled = resourceMode.Enabled = text.Enabled = office.Enabled = pdf.Enabled = snippets.Enabled = !busy;
        skipSystemFolders.Enabled = !busy;
        cancel.Enabled = scanCancellation is { IsCancellationRequested: false };
        copy.Enabled = SelectedHit is not null;
        reveal.Enabled = SelectedHit is { Online: true };
    }

    async Task RunAsync(Func<Task> action, Label feedback)
    {
        if (busy) return;
        busy = true;
        UpdateActions();
        try { await action(); }
        catch (Exception ex)
        {
            feedback.Text = "Operation failed.";
            if (!closeRequested)
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            busy = false;
            if (closeRequested) Close();
            else UpdateActions();
        }
    }

    async Task SearchAsync()
    {
        string input = query.Text.Trim();
        string? drive = DriveId(searchDrive);
        string ext = extension.Text.Trim().TrimStart('.');
        var options = new ContentSearchOptions((ContentMatchMode)matchMode.SelectedIndex, fuzzy.Checked);
        status.Text = "Reading the content index…";
        var (result, elapsed, stats) = await Task.Run(() =>
        {
            var watch = Stopwatch.StartNew();
            var matches = ContentIndex.SearchDetailed(catalog, input, drive, ext.Length == 0 ? null : ext,
                ResultLimit + 1, options);
            return (matches, watch.ElapsedMilliseconds, contentIndex.GetStats());
        });
        var hits = result.Hits;
        results.BeginUpdate();
        try
        {
            results.Items.Clear();
            foreach (var hit in hits.Take(ResultLimit))
            {
                string excerpt = hit.Snippet.ReplaceLineEndings(" ");
                results.Items.Add(new ListViewItem([hit.Name, hit.VolumeLabel, excerpt,
                    UiHelpers.FormatBytes(hit.Bytes), Catalog.Local(hit.Modified).ToString("g")])
                    { Tag = hit, ToolTipText = hit.FullPath + (excerpt.Length == 0 ? "" : Environment.NewLine + excerpt) });
            }
        }
        finally { results.EndUpdate(); }
        string matchesText = input.Length == 0 ? "Enter text to search" : hits.Count > ResultLimit
            ? $"Showing first {ResultLimit:N0} matches in {elapsed:N0} ms"
            : $"{hits.Count:N0} matches in {elapsed:N0} ms";
        string closeMatch = result.UsedRelaxedMatch ? " · Close match" : "";
        string typoMatch = result.FuzzyCorrections.Count > 0 ? $" · Typo match: {result.FuzzyCorrections[0]}" : "";
        if (result.FuzzyCorrections.Count > 1) typoMatch += $" (+{result.FuzzyCorrections.Count - 1})";
        status.Text = $"{matchesText}{closeMatch}{typoMatch} · {stats.IndexedFiles:N0} files indexed ({UiHelpers.FormatBytes(stats.DatabaseBytes)})";
        tips.SetToolTip(status, status.Text + (result.Summary.Length > 0 ? Environment.NewLine + result.Summary : "") +
            (result.FuzzyCorrections.Count > 1 ? Environment.NewLine + string.Join(", ", result.FuzzyCorrections) : ""));
    }

    async Task ScanAsync()
    {
        var watch = Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource();
        scanCancellation = cancellation;
        UpdateActions();
        progress.Value = 0;
        progress.Style = ProgressBarStyle.Marquee;
        progressText.Text = "Counting files to scan…";
        var reporter = new Progress<ContentScanProgress>(p =>
        {
            if (scanCancellation != cancellation || cancellation.IsCancellationRequested) return;
            var elapsed = watch.Elapsed;
            double pct = p.Total > 0 ? Math.Clamp(p.Processed * 100.0 / p.Total, 0, 100) : 0;
            string etaText = p.Total > 0 && elapsed.TotalSeconds >= 1.5 && p.Processed > 0
                ? ScanDisplay.FormatTimeLeft(TimeSpan.FromSeconds(elapsed.TotalSeconds * Math.Max(0, p.Total - p.Processed) / p.Processed))
                : "Estimating time remaining";
            progress.Style = ProgressBarStyle.Continuous;
            progress.Value = (int)pct;
            progressText.Text = $"{pct:F1}% · {p.Processed:N0} / {p.Total:N0} · {etaText} · "
                + $"{p.Indexed:N0} indexed · {p.Skipped:N0} skipped · {p.Failed:N0} failed";
            tips.SetToolTip(progressText, string.IsNullOrEmpty(p.CurrentFile)
                ? "Shows how many files the scan has processed." : p.CurrentFile);
        });
        try
        {
            await ContentIndex.ScanAsync(catalog, ScanOptions(), reporter, cancellation.Token);
            progress.Value = 100;
            progressText.Text = progressText.Text.StartsWith("Counting", StringComparison.Ordinal)
                ? "Scan complete." : $"Scan complete. {progressText.Text}";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            progressText.Text = "Scan canceled. Completed batches remain indexed.";
        }
        finally
        {
            scanCancellation = null;
            progress.Style = ProgressBarStyle.Continuous;
            if (!closeRequested) QueueEstimate();
        }
        if (!closeRequested) await SearchAsync();
    }

    void CancelScan()
    {
        if (scanCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        cancel.Enabled = false;
        progressText.Text = "Canceling content scan…";
    }

    async Task ExportAsync()
    {
        using var picker = new SaveFileDialog { Filter = ArchiveFilter, DefaultExt = "ficontent",
            FileName = $"{Environment.MachineName} - ContentIndex.ficontent" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string path = picker.FileName;
        progressText.Text = "Exporting content…";
        await Task.Run(() => contentIndex.ExportContent(path));
        progressText.Text = "Content index exported.";
    }

    async Task ImportAsync()
    {
        using var picker = new OpenFileDialog { Filter = ArchiveFilter };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string path = picker.FileName;
        progressText.Text = "Importing content…";
        int count = await Task.Run(() => contentIndex.ImportContent(path));
        progressText.Text = $"Imported content for {count:N0} files.";
        if (closeRequested) return;
        QueueEstimate();
        await SearchAsync();
    }

    async Task SyncAsync()
    {
        var config = CloudSync.LoadConfig(catalog.DataDirectory);
        if (string.IsNullOrWhiteSpace(config.Folder))
        {
            using var picker = new FolderBrowserDialog { UseDescriptionForTitle = true,
                Description = "Choose a Cloud Sync folder for content snapshots" };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            config = config with { Folder = picker.SelectedPath };
            CloudSync.SaveConfig(catalog.DataDirectory, config);
        }
        progressText.Text = "Syncing content…";
        var summary = await Task.Run(() => CloudSync.SyncContent(contentIndex, config));
        progressText.Text = $"Content sync complete. {summary.Exported:N0} exported · {summary.Imported:N0} imported · "
            + $"{summary.Skipped:N0} skipped · {summary.Errors.Count:N0} errors";
        if (closeRequested) return;
        if (summary.Errors.Count > 0)
            MessageBox.Show(this, string.Join(Environment.NewLine, summary.Errors), "Content Sync",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        QueueEstimate();
        await SearchAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            estimateTimer.Dispose();
            tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
