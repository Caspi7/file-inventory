using System.Runtime.InteropServices;
using System.Text;

namespace FileInventory;

sealed partial class InventoryWindow
{
    const int TextPreviewBytes = 24 * 1024;
    static readonly HashSet<string> imagePreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico" };
    static readonly HashSet<string> textPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".json", ".xml", ".csv", ".log", ".cs", ".py", ".sql", ".js", ".ts", ".html", ".css", ".ini", ".yaml" };

    readonly GroupBox fileDetails = new() { Text = "Quick Look", Dock = DockStyle.Fill,
        Height = 248, Visible = false, Padding = new Padding(8, 5, 8, 6) };
    readonly TextBox fileDetailsText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Selection metadata" };
    readonly PictureBox previewImage = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom,
        AccessibleName = "Image preview", Visible = false };
    readonly TextBox previewText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Font = Theme.MonospaceFont, AccessibleName = "File preview" };
    readonly TextBox annotationTags = new() { Dock = DockStyle.Fill, AccessibleName = "Tags" };
    readonly TextBox annotationNote = new() { Dock = DockStyle.Fill, Multiline = true, AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical, AccessibleName = "Note" };
    readonly Button saveAnnotation = new() { Text = "Save note", AutoSize = true, Anchor = AnchorStyles.Right };
    readonly Panel previewBody = new() { Dock = DockStyle.Fill };
    int fileActivation;
    bool previewFromResults;

    void InitializeQuickLook(ToolTip tips)
    {
        // The browser adds the metadata box while constructing its layout.
        Load += (_, _) =>
        {
            var annotations = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
            annotations.ColumnStyles.Add(new(SizeType.AutoSize));
            annotations.ColumnStyles.Add(new(SizeType.Percent, 100));
            annotations.RowStyles.Add(new(SizeType.AutoSize));
            annotations.RowStyles.Add(new(SizeType.Percent, 100));
            annotations.RowStyles.Add(new(SizeType.AutoSize));
            annotations.Controls.Add(LabelFor("Tags"), 0, 0);
            annotations.Controls.Add(annotationTags, 1, 0);
            annotations.Controls.Add(LabelFor("Note"), 0, 1);
            annotations.Controls.Add(annotationNote, 1, 1);
            Theme.SetIcon(saveAnnotation, FluentIcon.Save);
            annotations.Controls.Add(saveAnnotation, 1, 2);

            var content = new Panel { Dock = DockStyle.Fill, Margin = new(0, 3, 0, 0) };
            content.Controls.AddRange([previewText, previewImage]);
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                Margin = Padding.Empty };
            body.ColumnStyles.Add(new(SizeType.Percent, 55));
            body.ColumnStyles.Add(new(SizeType.Percent, 45));
            body.Controls.Add(content, 0, 0);
            body.Controls.Add(annotations, 1, 0);
            previewBody.Controls.Add(body);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new(SizeType.Percent, 100));
            layout.RowStyles.Add(new(SizeType.AutoSize));
            previewBody.Dock = DockStyle.Bottom;
            previewBody.Height = 124;
            fileDetails.Controls.Add(layout);
            layout.Controls.Add(fileDetailsText, 0, 0);
            layout.Controls.Add(previewBody, 0, 1);

            saveAnnotation.Click += (_, _) => SaveFileAnnotation();
            Theme.AttachTooltip(previewImage, tips, "Scaled preview of the selected image.");
            Theme.AttachTooltip(previewText, tips, "Shows up to 24 KB of supported UTF-8 text.");
            Theme.AttachTooltip(annotationTags, tips, "Enter tags separated by commas.");
            Theme.AttachTooltip(annotationNote, tips, "Add a note to this catalog file.");
            Theme.AttachTooltip(saveAnnotation, tips, "Save the tags and note for this file.");

            tree.AfterSelect += async (_, e) =>
            {
                if (!locating && e.Node?.Tag is Branch branch)
                    await OpenTreeFile(branch, openExplorer: false);
            };
            results.SelectedIndexChanged += async (_, _) => await PreviewSelectedResult();
            results.Enter += async (_, _) => await PreviewSelectedResult();
        };
        fileDetails.Disposed += (_, _) => previewImage.Image?.Dispose();
    }

    async Task PreviewSelectedResult()
    {
        int index = SelectedResult;
        if (closing) return;
        if (index < 0)
        {
            if (previewFromResults) ClearQuickLook();
            return;
        }
        if (CurrentHit() is null)
        {
            ClearQuickLook();
            previewFromResults = true;
            int activation = fileActivation;
            int searchRevision = revision;
            await LoadResultPage(index / PageSize);
            if (closing || activation != fileActivation || searchRevision != revision || index != SelectedResult) return;
        }
        ShowHitPreview(CurrentHit());
    }

    internal void ShowHitPreview(Hit? hit)
    {
        if (hit is null) ClearQuickLook();
        else _ = ShowQuickLook(hit.Volume, hit.RelativePath, hit.IsDirectory, hit.FullPath, hit.Status,
            fromResults: true);
    }

    void ClearFileDetails()
    {
        // LocateCurrentHit calls this after selecting a folder programmatically.
        if (locating && tree.SelectedNode?.Tag is Branch { Directory: true } folder)
            _ = OpenTreeFile(folder, openExplorer: false);
        else
            ClearQuickLook();
    }

    void ClearQuickLook()
    {
        fileActivation++;
        previewFromResults = false;
        fileDetails.Tag = null;
        fileDetails.Visible = false;
        ReplacePreviewImage(null);
    }

    void ReplacePreviewImage(Bitmap? image)
    {
        var previous = previewImage.Image;
        previewImage.Image = image;
        previous?.Dispose();
        previewText.Visible = image is null;
        previewImage.Visible = image is not null;
    }

    Task OpenTreeFile(Branch branch, bool openExplorer = true) =>
        ShowQuickLook(branch.Volume, branch.Relative, branch.Directory,
            openExplorer: openExplorer);

    async Task ShowQuickLook(string volumeId, string relative, bool directory,
        string? fullPath = null, string? status = null, bool openExplorer = false, bool fromResults = false)
    {
        ClearQuickLook();
        previewFromResults = fromResults;
        int activation = fileActivation;
        volumesById.TryGetValue(volumeId, out var volume);
        bool IsCurrent() => !closing && !IsDisposed && activation == fileActivation;

        fileDetails.Text = directory ? "Folder details" : "Quick Look";
        fileDetails.Height = directory ? 100 : 248;
        previewBody.Visible = !directory;
        fileDetailsText.Text = fullPath ?? Path.Combine(volume?.Mount ?? volumeId, relative);
        fileDetails.Visible = true;
        annotationTags.Clear();
        annotationNote.Clear();
        annotationTags.Enabled = annotationNote.Enabled = saveAnnotation.Enabled = false;
        previewText.Text = "Loading preview…";
        try
        {
            if (directory)
            {
                var stats = await Task.Run(() => catalog.GetFolderMetadata(volumeId, relative));
                if (!IsCurrent()) return;
                fileDetailsText.AppendText(Environment.NewLine + DescribePreviewDrive(volume, volumeId)
                    + (status is null ? "" : $"    Status: {status}") + Environment.NewLine
                    + (stats is null ? "Folder totals are unavailable."
                        : $"Total: {stats.SubtreeBytes:N0} bytes    Files: {stats.SubtreeFiles:N0}    Subfolders: {stats.SubtreeDirs:N0}"));
                fileDetailsText.Select(0, 0);
                fileDetailsText.ScrollToCaret();
                return;
            }

            var (metadata, annotation) = await Task.Run(() =>
                (catalog.GetFileMetadata(volumeId, relative), catalog.GetAnnotation(volumeId, relative)));
            if (!IsCurrent()) return;
            if (metadata is null)
            {
                previewText.Text = "This file is no longer in the catalog.";
                return;
            }

            fileDetailsText.Text = string.Join(Environment.NewLine,
                metadata.FullPath,
                DescribePreviewDrive(volume, metadata.Volume),
                $"Status: {metadata.Status}    Size: {metadata.Bytes:N0} bytes",
                $"Created: {metadata.Created:G}    Modified: {metadata.Modified:G}",
                $"Last seen: {metadata.LastSeen:G}    Removed: {metadata.Removed?.ToString("G") ?? "—"}");
            annotationTags.Text = annotation?.Tags ?? "";
            annotationNote.Text = annotation?.Note ?? "";
            fileDetails.Tag = (volumeId, relative);
            annotationTags.Enabled = annotationNote.Enabled = saveAnnotation.Enabled = true;

            var preview = LoadFilePreview(volume, relative, metadata, IsCurrent);
            if (openExplorer && !benchmark && metadata.CanReveal)
            {
                bool opened = await RevealFile(volumeId, relative, metadata.FullPath, IsCurrent);
                if (!opened && IsCurrent()) state.Text = "This file location is currently unavailable.";
            }
            await preview;
        }
        catch (Exception ex)
        {
            if (IsCurrent())
            {
                if (directory) fileDetailsText.AppendText(Environment.NewLine + "Folder totals are unavailable.");
                else if (!saveAnnotation.Enabled) previewText.Text = "File details are unavailable.";
                state.Text = ex.Message;
            }
        }
    }

    static string DescribePreviewDrive(Volume? volume, string fallback)
    {
        if (volume is null) return $"Drive: {fallback}";
        return $"Drive: {volume.BaseLabel}    Host: {(string.IsNullOrWhiteSpace(volume.Host) ? "—" : volume.Host)}"
            + $"    {volume.KindLabel}"
            + (string.IsNullOrWhiteSpace(volume.Note) ? "" : $"    Location: {volume.Note}");
    }

    void SaveFileAnnotation()
    {
        if (fileDetails.Tag is not ValueTuple<string, string> target) return;
        try
        {
            catalog.SetAnnotation(target.Item1, target.Item2, annotationTags.Text, annotationNote.Text);
            state.Text = "File note saved.";
        }
        catch (Exception ex) { state.Text = $"Could not save file note: {ex.Message}"; }
    }

    async Task LoadFilePreview(Volume? volume, string relative, FileMetadata metadata, Func<bool> isCurrent)
    {
        string extension = Path.GetExtension(metadata.FullPath);
        bool image = imagePreviewExtensions.Contains(extension);
        long limit = (image ? 25L : 10L) * 1024 * 1024;
        if (volume is not { Online: true } || !metadata.CanReveal || metadata.Bytes > limit
            || (!image && !textPreviewExtensions.Contains(extension)))
        {
            previewText.Text = "No inline preview available.";
            return;
        }

        try
        {
            var (bitmap, content) = await Task.Run(() => ReadFilePreview(volume, relative, metadata.FullPath, image, limit));
            if (!isCurrent())
            {
                bitmap?.Dispose();
                return;
            }
            ReplacePreviewImage(bitmap);
            previewText.Text = content ?? "No inline preview available.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            if (isCurrent()) previewText.Text = "The file preview could not be read.";
        }
    }

    static (Bitmap? Image, string? Text) ReadFilePreview(
        Volume volume, string relative, string fullPath, bool image, long limit)
    {
        if (!WindowsVolumes.CanReveal(volume, relative, fullPath)) return (null, null);
        string path = WindowsVolumes.Extended(fullPath);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || WindowsVolumes.HasRecallRisk(attributes)) return (null, null);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long length = stream.Length;
        if (length > limit) return (null, null);
        if (image)
        {
            using var icon = Path.GetExtension(fullPath).Equals(".ico", StringComparison.OrdinalIgnoreCase)
                ? new Icon(stream) : null;
            using var original = icon is null ? Image.FromStream(stream, false, true) : icon.ToBitmap();
            double scale = Math.Min(1, Math.Min(640.0 / original.Width, 360.0 / original.Height));
            return (new Bitmap(original, Math.Max(1, (int)(original.Width * scale)),
                Math.Max(1, (int)(original.Height * scale))), null);
        }

        byte[] bytes = new byte[TextPreviewBytes];
        int count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        for (int i = 0; i < count; i++)
            if (bytes[i] is < 0x20 and not (9 or 10 or 12 or 13) or 0x7f)
                return (null, "This file contains binary data.");

        bool truncated = count == bytes.Length && stream.Length > count;
        var decoder = new UTF8Encoding(false, true).GetDecoder();
        char[] characters = new char[count];
        int decoded = decoder.GetChars(bytes, 0, count, characters, 0, !truncated);
        string text = new string(characters, 0, decoded).TrimStart('\uFEFF');
        return (null, truncated ? text + Environment.NewLine + "[Preview limited to 24 KB]" : text);
    }

    async Task<bool> RevealFile(string volumeId, string relative, string fullPath, Func<bool>? stillRequested = null)
    {
        var volume = volumes.FirstOrDefault(v => v.Id == volumeId);
        if (benchmark || volume is not { Online: true }) return false;
        bool available = await Task.Run(() => WindowsVolumes.CanReveal(volume, relative, fullPath));
        if (!available || closing || stillRequested?.Invoke() == false) return false;
        UiHelpers.RevealInExplorer(fullPath);
        return true;
    }
}
