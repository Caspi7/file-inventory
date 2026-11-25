using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace FileInventory;

sealed class StorageMapDialog : Form
{
    const int ItemLimit = 250;
    static readonly (string Name, Color Color)[] Categories =
    [
        ("Folders", Color.FromArgb(82, 104, 164)),
        ("Video", Color.FromArgb(132, 77, 167)),
        ("Images", Color.FromArgb(26, 124, 128)),
        ("Audio", Color.FromArgb(53, 123, 75)),
        ("Archives", Color.FromArgb(151, 100, 30)),
        ("Code / Text / Docs", Color.FromArgb(43, 106, 169)),
        ("Executables", Color.FromArgb(166, 73, 66)),
        ("Other", Color.FromArgb(91, 102, 118))
    ];

    readonly Catalog catalog;
    readonly ToolTip tips = new() { ShowAlways = true };
    readonly ComboBox drives = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 270, DisplayMember = nameof(Volume.DisplayLibraryLabel) };
    readonly Button up = new() { Text = "Up", AutoSize = true, Enabled = false };
    readonly LinkLabel breadcrumb = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label total = new() { AutoSize = true, Anchor = AnchorStyles.Right };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ListView breakdown = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    readonly Treemap map = new() { Dock = DockStyle.Fill, AccessibleName = "Storage treemap" };
    readonly ContextMenuStrip actions = new();
    readonly Dictionary<StorageMapNode, ListViewItem> rows = new();
    string parentRel = "";
    string folderStatus = "";
    long totalBytes;
    int request;
    StorageMapNode? selection;
    StorageMapNode? hover;
    bool syncingSelection;

    Volume? SelectedVolume => drives.SelectedItem as Volume;

    public StorageMapDialog(Catalog catalog, AppTheme theme, string? initialVolumeId = null)
    {
        this.catalog = catalog;
        Text = "Storage Map";
        Theme.SetIcon(up, FluentIcon.Up);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new(835, 440);
        Size = new(1150, 720);
        MinimizeBox = false;
        Font = Theme.BodyFont;
        drives.AccessibleName = "Drive";
        breakdown.AccessibleName = "Storage breakdown";

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1 };
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.AutoSize));
        header.Controls.AddRange([drives, up, breadcrumb, total]);

        breakdown.Columns.Add("Name", 120);
        breakdown.Columns.Add("Size", 79, HorizontalAlignment.Right);
        breakdown.Columns.Add("%", 59, HorizontalAlignment.Right);
        breakdown.Columns.Add("Files", 65, HorizontalAlignment.Right);
        breakdown.Resize += (_, _) =>
        {
            int fixedWidth = breakdown.Columns.Cast<ColumnHeader>().Skip(1).Sum(column => column.Width);
            breakdown.Columns[0].Width = Math.Max(50, breakdown.ClientSize.Width - fixedWidth - SystemInformation.VerticalScrollBarWidth);
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, Size = new(1100, 500), Panel1MinSize = 240, Panel2MinSize = 280 };
        split.Panel1.Controls.Add(breakdown);
        split.Panel2.Controls.Add(map);
        void SizeSplit() => split.SplitterDistance = Math.Max(split.Panel1MinSize, (int)((split.ClientSize.Width - split.SplitterWidth) * 0.32));
        split.SizeChanged += (_, _) =>
        {
            if (split.ClientSize.Width >= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth) SizeSplit();
        };
        SizeSplit();

        var legend = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        foreach (var (name, color) in Categories)
        {
            var swatch = new PictureBox { Size = new(12, 12), Margin = new(5, 5, 2, 2) };
            swatch.Paint += (_, e) =>
            {
                using var brush = new SolidBrush(color);
                e.Graphics.FillRectangle(brush, swatch.ClientRectangle);
            };
            legend.Controls.Add(swatch);
            legend.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new(1, 2, 6, 2) });
        }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(8) };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        status.MinimumSize = new(0, 25);
        layout.Controls.AddRange([header, split, legend, status]);
        Controls.Add(layout);

        var open = new ToolStripMenuItem("Open / Drill into folder", null, async (_, _) => await OpenSelection());
        var copy = new ToolStripMenuItem("Copy path", null, (_, _) => CopyPath());
        var reveal = new ToolStripMenuItem("Reveal in Explorer", null, async (_, _) => await OpenOnDrive(reveal: true));
        actions.Items.AddRange([open, copy, reveal]);
        actions.Opening += (_, e) =>
        {
            e.Cancel = selection is null;
            open.Enabled = selection?.IsDirectory == true || SelectedVolume?.Online == true;
            copy.Enabled = selection is not null;
            reveal.Enabled = selection is not null && SelectedVolume?.Online == true;
        };
        Theme.SetMenuIcon(actions, open, FluentIcon.FolderOpen);
        Theme.SetMenuIcon(actions, copy, FluentIcon.Copy);
        Theme.SetMenuIcon(actions, reveal, FluentIcon.FolderOpen);
        map.ContextMenuStrip = breakdown.ContextMenuStrip = actions;

        up.Click += async (_, _) => await ShowFolder(Path.GetDirectoryName(parentRel) ?? "");
        breadcrumb.LinkClicked += async (_, e) =>
        {
            if (e.Link?.LinkData is string relative) await ShowFolder(relative);
        };
        drives.SelectedIndexChanged += async (_, _) => await ShowFolder("");
        breakdown.SelectedIndexChanged += (_, _) =>
        {
            if (!syncingSelection) SelectNode(breakdown.SelectedItems.Count == 0 ? null : (StorageMapNode?)breakdown.SelectedItems[0].Tag);
        };
        breakdown.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) SelectNode((StorageMapNode?)breakdown.GetItemAt(e.X, e.Y)?.Tag);
        };
        breakdown.ItemActivate += async (_, _) => await OpenSelection();
        map.MouseMove += (_, e) => HoverNode(map.NodeAt(e.Location));
        map.MouseLeave += (_, _) => HoverNode(null);
        map.MouseDown += (_, e) => SelectNode(map.NodeAt(e.Location), scrollToRow: true);
        map.MouseDoubleClick += async (_, e) =>
        {
            if (e.Button == MouseButtons.Left && map.NodeAt(e.Location) is { IsDirectory: true } folder)
                await ShowFolder(folder.Rel);
        };

        Load += (_, _) =>
        {
            Theme.Apply(this, theme);
            breadcrumb.LinkColor = breadcrumb.ActiveLinkColor = breadcrumb.ForeColor;
            Theme.AttachTooltip(drives, tips, "Choose an online or offline drive inventory.");
            Theme.AttachTooltip(up, tips, "Go to the parent folder.");
            Theme.AttachTooltip(map, tips, "Select a tile or double-click a folder to drill into it.");
            try
            {
                var volumes = catalog.Volumes();
                drives.Items.AddRange(volumes.ToArray());
                if (drives.Items.Count > 0)
                    drives.SelectedIndex = Math.Max(0, volumes.FindIndex(volume => volume.Id == initialVolumeId));
                else
                    ShowEmpty("No drive inventories are available.");
            }
            catch (Exception ex) { ShowEmpty(ex.Message); }
        };
    }

    async Task ShowFolder(string relative)
    {
        if (SelectedVolume is not { } volume) return;
        int current = ++request;
        parentRel = relative;
        UpdateBreadcrumb(volume);
        breakdown.Items.Clear();
        rows.Clear();
        map.SetNodes([]);
        HoverNode(null);
        SelectNode(null);
        total.Text = "";
        ShowEmpty("Loading folder…");
        try
        {
            var (nodes, stats) = await Task.Run(() => (
                catalog.GetStorageMapChildren(volume.Id, relative, ItemLimit + 1),
                catalog.GetFolderMetadata(volume.Id, relative)));
            if (IsDisposed || current != request) return;

            bool hasMore = nodes.Count > ItemLimit;
            if (hasMore) nodes.RemoveAt(ItemLimit);
            totalBytes = stats?.SubtreeBytes ?? nodes.Sum(node => node.Bytes);
            total.Text = $"Total: {UiHelpers.FormatBytes(totalBytes)}";
            breakdown.BeginUpdate();
            try
            {
                foreach (var node in nodes)
                {
                    var row = new ListViewItem([node.Name, UiHelpers.FormatBytes(node.Bytes), Share(node), node.FileCount.ToString("N0")]) { Tag = node };
                    rows.Add(node, row);
                    breakdown.Items.Add(row);
                }
            }
            finally { breakdown.EndUpdate(); }
            map.SetNodes(nodes);
            ShowEmpty(nodes.Count == 0 ? "No cataloged items in this folder."
                : totalBytes == 0 ? "This folder contains no file data to plot."
                : hasMore ? $"Showing the {ItemLimit} largest items." : "Select an item to see its details.");
        }
        catch (Exception ex)
        {
            if (!IsDisposed && current == request) ShowEmpty(ex.Message);
        }
    }

    void UpdateBreadcrumb(Volume volume)
    {
        up.Enabled = parentRel.Length > 0;
        breadcrumb.Links.Clear();
        breadcrumb.Text = Path.Combine(volume.Mount, parentRel);
        if (parentRel.Length > 0)
        {
            breadcrumb.Links.Add(0, volume.Mount.Length, "");
            int relativeStart = breadcrumb.Text.Length - parentRel.Length;
            int start = relativeStart;
            foreach (string part in parentRel.Split('\\'))
            {
                int end = start + part.Length;
                if (end < breadcrumb.Text.Length)
                    breadcrumb.Links.Add(start, part.Length, parentRel[..(end - relativeStart)]);
                start = end + 1;
            }
        }
        Theme.AttachTooltip(breadcrumb, tips, breadcrumb.Text);
    }

    void SelectNode(StorageMapNode? node, bool scrollToRow = false)
    {
        selection = node;
        syncingSelection = true;
        try
        {
            foreach (ListViewItem row in breakdown.SelectedItems.Cast<ListViewItem>().ToArray())
                if (!Equals(row.Tag, node)) row.Selected = false;
            if (node is not null && rows.TryGetValue(node, out var selected))
            {
                selected.Selected = true;
                selected.Focused = true;
                if (scrollToRow) selected.EnsureVisible();
            }
        }
        finally { syncingSelection = false; }
        map.Highlight(selection, hover);
        UpdateStatus();
    }

    void HoverNode(StorageMapNode? node)
    {
        if (hover == node) return;
        hover = node;
        map.Highlight(selection, hover);
        UpdateStatus();
    }

    string Share(StorageMapNode node) => (totalBytes > 0 ? (double)node.Bytes / totalBytes : 0).ToString("P1");

    void ShowEmpty(string message)
    {
        folderStatus = message;
        UpdateStatus();
    }

    void UpdateStatus()
    {
        var node = hover ?? selection;
        status.Text = node is not null && SelectedVolume is { } volume
            ? $"{node.Name}  |  {Path.Combine(volume.Mount, node.Rel)}  |  {UiHelpers.FormatBytes(node.Bytes)} ({Share(node)})  |  {node.FileCount:N0} files"
            : folderStatus;
        Theme.AttachTooltip(status, tips, status.Text);
    }

    async Task OpenSelection()
    {
        if (selection is { IsDirectory: true } folder) await ShowFolder(folder.Rel);
        else await OpenOnDrive(reveal: false);
    }

    void CopyPath()
    {
        if (SelectedVolume is not { } volume || selection is not { } node) return;
        UiHelpers.CopyText(Path.Combine(volume.Mount, node.Rel), this);
    }

    async Task OpenOnDrive(bool reveal)
    {
        if (SelectedVolume is not { Online: true } volume || selection is not { } node) return;
        try
        {
            string path = Path.Combine(volume.Mount, node.Rel);
            bool available = await Task.Run(() => WindowsVolumes.CanReveal(volume, node.Rel, path));
            if (IsDisposed || SelectedVolume != volume || selection != node) return;
            if (!available)
            {
                ShowError("The selected path is unavailable.");
                return;
            }
            if (reveal)
            {
                UiHelpers.RevealInExplorer(path, this);
                return;
            }
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { if (!IsDisposed) ShowError(ex.Message); }
    }

    void ShowError(string message) => MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tips.Dispose();
            actions.Dispose();
        }
        base.Dispose(disposing);
    }

    static Color TileColor(StorageMapNode node)
    {
        int category = node.IsDirectory ? 0 : node.Extension.TrimStart('.').ToLowerInvariant() switch
        {
            "mp4" or "mkv" or "avi" or "mov" or "webm" or "wmv" or "m4v" or "mpeg" => 1,
            "jpg" or "jpeg" or "png" or "gif" or "webp" or "svg" or "bmp" or "tiff" or "heic" or "raw" or "ico" => 2,
            "mp3" or "wav" or "flac" or "ogg" or "m4a" or "aac" or "wma" or "opus" => 3,
            "zip" or "7z" or "rar" or "tar" or "gz" or "bz2" or "xz" or "iso" or "cab" => 4,
            "txt" or "md" or "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "odt" or "ods"
                or "csv" or "json" or "xml" or "html" or "css" or "js" or "ts" or "cs" or "py" or "java"
                or "c" or "cpp" or "h" or "sql" or "log" or "yaml" or "yml" or "ini" => 5,
            "exe" or "dll" or "msi" or "sys" or "com" or "bat" or "cmd" or "ps1" or "scr" => 6,
            _ => 7
        };
        return Categories[category].Color;
    }

    sealed class Treemap : Panel
    {
        sealed record Tile(StorageMapNode Node, RectangleF Bounds);
        readonly List<Tile> tiles = new();
        StorageMapNode[] nodes = [];
        StorageMapNode? selected;
        StorageMapNode? hovered;

        public Treemap()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        public void SetNodes(List<StorageMapNode> items)
        {
            nodes = items.Where(node => node.Bytes > 0).OrderByDescending(node => node.Bytes).ToArray();
            ArrangeTiles();
        }

        public StorageMapNode? NodeAt(Point point) => tiles.Find(tile => tile.Bounds.Contains(point))?.Node;

        public void Highlight(StorageMapNode? selection, StorageMapNode? hover)
        {
            selected = selection;
            hovered = hover;
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ArrangeTiles();
        }

        void ArrangeTiles()
        {
            tiles.Clear();
            if (nodes.Length == 0 || ClientSize.Width <= 2 || ClientSize.Height <= 2)
            {
                Invalidate();
                return;
            }

            double x = 1, y = 1, width = ClientSize.Width - 2, height = ClientSize.Height - 2;
            double scale = width * height / nodes.Sum(node => (double)node.Bytes);
            var areas = nodes.Select(node => node.Bytes * scale).ToArray();
            for (int start = 0; start < nodes.Length && width > 0 && height > 0;)
            {
                // Grow a row along the short side while adding tiles improves its worst aspect ratio.
                double side = Math.Min(width, height);
                double area = areas[start];
                int end = start + 1;
                while (end < nodes.Length &&
                    WorstRatio(side, area + areas[end], areas[start], areas[end])
                        <= WorstRatio(side, area, areas[start], areas[end - 1]))
                {
                    area += areas[end++];
                }

                bool horizontal = width <= height;
                double thickness = end == nodes.Length ? Math.Max(width, height) : Math.Min(area / side, Math.Max(width, height));
                double offset = 0;
                for (int index = start; index < end; index++)
                {
                    double length = index == end - 1 ? side - offset : Math.Min(areas[index] / thickness, side - offset);
                    var bounds = horizontal
                        ? new RectangleF((float)(x + offset), (float)y, (float)length, (float)thickness)
                        : new RectangleF((float)x, (float)(y + offset), (float)thickness, (float)length);
                    tiles.Add(new(nodes[index], bounds));
                    offset += length;
                }
                if (horizontal) { y += thickness; height -= thickness; }
                else { x += thickness; width -= thickness; }
                start = end;
            }
            Invalidate();
        }

        static double WorstRatio(double side, double area, double largest, double smallest)
        {
            double ratio = side * side / (area * area);
            return Math.Max(ratio * largest, 1 / (ratio * smallest));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var border = new Pen(Color.FromArgb(165, 22, 27, 41));
            foreach (var tile in tiles)
            {
                var bounds = tile.Bounds;
                if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
                var color = TileColor(tile.Node);
                if (bounds.Width < 2 || bounds.Height < 2)
                {
                    using var solid = new SolidBrush(color);
                    e.Graphics.FillRectangle(solid, bounds);
                    continue;
                }
                using var cushion = new LinearGradientBrush(bounds, ControlPaint.Light(color, 0.25f),
                    ControlPaint.Dark(color, 0.24f), LinearGradientMode.ForwardDiagonal);
                e.Graphics.FillRectangle(cushion, bounds);
                e.Graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                var textBounds = Rectangle.Inflate(Rectangle.Truncate(bounds), -5, -4);
                if (textBounds.Width < 56 || textBounds.Height < Font.Height * 2) continue;
                const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(e.Graphics, tile.Node.Name, Font, textBounds, Color.White, flags);
                textBounds.Y += Font.Height;
                textBounds.Height -= Font.Height;
                TextRenderer.DrawText(e.Graphics, UiHelpers.FormatBytes(tile.Node.Bytes), Font, textBounds, Color.White, flags);
            }

            foreach (var tile in tiles)
            {
                if (tile.Node != selected && tile.Node != hovered) continue;
                var bounds = RectangleF.Inflate(tile.Bounds, -1, -1);
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                using var outline = new Pen(tile.Node == hovered ? Color.White : Color.Gold, 2);
                e.Graphics.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
        }
    }
}
