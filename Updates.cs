namespace FileInventory;

sealed partial class InventoryWindow
{
    readonly Button updateButton = new() { Text = "Check for updates", AutoSize = true };
    readonly Button releaseNotesButton = new() { Text = "Release notes", AutoSize = true };
    const string FullReleaseNotes = """
        File Inventory 1.2.18
        • System/host ownership & removable drive tracking: each scanned drive records its owning PC (host) and Fixed vs. Removable type, synced across machines via Cloud Sync & exports.
        • Hardware volume IDs automatically update ownership if a drive is moved to another PC; Browse drive, Drive Library, search results, and file details now clearly show the owning system.

        File Inventory 1.2.17
        • Fixed background lingering process on close: bounded 3s scan shutdown timeout, clean Environment.Exit(0) on exit, and normal taskbar minimize instead of hiding invisibly to the system tray.

        File Inventory 1.2.16
        • Universal 1-Click Cloud Sync: auto-detects Google Drive, OneDrive, Dropbox, iCloud Drive, or custom/Syncthing folders.
        • Optional Auto-sync after each drive scan and optional AES-256-GCM passphrase encryption for private cloud snapshots.

        File Inventory 1.2.15
        • Saved theme preference across restarts (no longer resets to Light mode).
        • Added a 3rd VS Code-inspired Dark Blue theme (cycle Theme: Light → Dark → Dark Blue).

        File Inventory 1.2.14
        • Full scrollable Release Notes history (1.1.0-preview through 1.2.14) in a themed dialog.

        File Inventory 1.2.13
        • Single-pass NTFS directory enumeration (NtQueryDirectoryFile + FileIdExtdDirectoryInformation) eliminating per-file handle opens.
        • Multi-threaded producer/consumer scanner pipeline (2–8 parallel workers + dedicated SQLite writer via Channels).

        File Inventory 1.2.12
        • Full Fluent / Windows 11 UI overhaul: Segoe UI Variable typography, homogeneous rounded buttons, custom-painted Dark Mode for ListView headers (SysHeader32), ComboBox, DateTimePicker, ProgressBar, NumericUpDown, and dialogs.
        • Faster catalog indexing & queries: ordered prefix B-tree publishing, 64–128 MiB SQLite cache, 256 MiB memory-mapped reads, and WAL synchronous=NORMAL.
        • Hybrid installer: checks GitHub for newer versions on startup (with offline fallback) and lets you choose the install folder.

        File Inventory 1.2.10
        • Fixed Dark Mode rendering for ListView column headers, ComboBox dropdowns, ProgressBar, and disabled buttons.
        • Added Accept & Install Now update dialog showing release notes before downloading and running the installer.

        File Inventory 1.2.9
        • Improved Dark/Light mode styling across ComboBox, DateTimePicker, ListView, and buttons.

        File Inventory 1.2.8
        • Modern Win11/macOS-style UI with Light/Dark mode toggle, updated logo, and in-app/updater release notes.

        File Inventory 1.2.7
        • Speed, SQLite catalog size (removed redundant files_parent index & duplicate filename_fragments FTS5 table), and single-file publish size optimizations + Elastic License 2.0 (LICENSE).

        File Inventory 1.2.6 Preview
        • Editable Extension filter dropdown with common extensions; type a custom extension or clear to search all.

        File Inventory 1.2.5 Preview
        • Added Extension column next to Filename in search results.

        File Inventory 1.2.4 Preview
        • Keyword search matches file and folder names only; unhandled exceptions save diagnostics to %LOCALAPPDATA%\FileInventory\CrashDumps.

        File Inventory 1.2.3 Preview
        • Background keyword search with loading indicator and virtualized native-scrollbar results list.

        File Inventory 1.2.2 Preview
        • Update download progress in scan bar and smaller self-contained installer.

        File Inventory 1.2.1 Preview
        • Added File Inventory archive-and-magnifier logo to window, tray, executable, and installer.

        File Inventory 1.2.0 Preview
        • Added Check for updates button checking GitHub releases with SHA-256 verification and in-place updates.

        File Inventory 1.1.1 Preview
        • Choose install location in Setup and fixed clipped scan progress text.

        File Inventory 1.1.0 Preview
        • Drive Library preview: offline drive filters, inventory import/export, and removed file tracking.
        """;
    bool checkingUpdates;
    AppUpdates.Release? updateRelease;
    long updateBytes;
    CancellationTokenSource? updateCancellation;
    public string? UpdateInstaller { get; private set; }

    void InitializeUpdates()
    {
        updateButton.Enabled = !benchmark;
        releaseNotesButton.Click += (_, _) => ShowReleaseNotesHistory();
        updateButton.Click += async (_, _) =>
        {
            if (updateCancellation is not null) updateCancellation.Cancel();
            else await CheckForUpdates();
        };
    }

    async Task CheckForUpdates()
    {
        if (checkingUpdates || closing) return;
        if (running.Count != 0 || transferringInventory)
        {
            MessageBox.Show(this, "Wait for the current scan or inventory transfer to finish, then try again.", "File Inventory update");
            return;
        }
        checkingUpdates = true;
        updateButton.Text = "Cancel update check";
        UpdateScanDisplay();
        string? download = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            updateCancellation = timeout;
            var release = await AppUpdates.FindUpdate(AppUpdates.CurrentVersion, timeout.Token);
            if (release is null)
            {
                if (!closing) MessageBox.Show(this, "You have the latest version.", "File Inventory update");
                return;
            }

            updateRelease = release;
            if (!closing && !ConfirmUpdate(release))
                return;
            updateButton.Text = "Cancel download";
            UpdateScanDisplay();
            var downloadProgress = new Progress<long>(bytes =>
            {
                if (updateCancellation == timeout) updateBytes = bytes;
            });
            download = await AppUpdates.Download(release, timeout.Token, downloadProgress);
            if (closing) return;
            UpdateInstaller = download;
        }
        catch (OperationCanceledException)
        {
            if (!closing) state.Text = "Update check cancelled or timed out.";
        }
        catch (Exception ex)
        {
            if (!closing) MessageBox.Show(this, ex.Message, "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            updateCancellation = null;
            if (download is not null && UpdateInstaller is null) File.Delete(download);
            checkingUpdates = false;
            updateRelease = null;
            updateBytes = 0;
            if (!closing)
            {
                updateButton.Enabled = true;
                updateButton.Text = "Check for updates";
                UpdateScanDisplay();
            }
        }
        if (UpdateInstaller is not null) Close();
    }

    void ShowReleaseNotesHistory()
    {
        using var dialog = new Form
        {
            Text = $"File Inventory {AppUpdates.CurrentVersion.ToString(3)} — Release Notes",
            Width = 580,
            Height = 460,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent
        };
        var header = new Label
        {
            Text = $"Release History (Current: v{AppUpdates.CurrentVersion.ToString(3)})",
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(12, 10, 12, 0)
        };
        Theme.MarkSectionTitle(header);
        var notesBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            Text = FullReleaseNotes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            SelectionStart = 0,
            SelectionLength = 0
        };
        var notesPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6) };
        notesPanel.Controls.Add(notesBox);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(12, 6, 12, 8)
        };
        var close = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(close);

        dialog.Controls.AddRange([notesPanel, header, buttons]);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.Load += (_, _) => Theme.Apply(dialog, currentTheme);
        dialog.ShowDialog(this);
    }

    bool ConfirmUpdate(AppUpdates.Release release)
    {
        using var dialog = new Form
        {
            Text = $"Update available — File Inventory {release.Version}",
            Width = 500,
            Height = 320,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent
        };
        var header = new Label
        {
            Text = $"File Inventory {release.Version} is ready to download and install.",
            Font = Theme.SectionFont,
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(12, 10, 12, 0)
        };
        Theme.MarkSectionTitle(header);
        var notesBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            Text = string.IsNullOrWhiteSpace(release.Notes)
                ? "No release notes provided for this version."
                : release.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
        };
        var notesPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 12, 6) };
        notesPanel.Controls.Add(notesBox);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(12, 6, 12, 8)
        };
        var accept = new Button { Text = "Accept & Install Now", DialogResult = DialogResult.OK, AutoSize = true };
        var later = new Button { Text = "Later", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.AddRange([later, accept]);

        dialog.Controls.AddRange([notesPanel, header, buttons]);
        dialog.AcceptButton = accept;
        dialog.CancelButton = later;
        dialog.Load += (_, _) => Theme.Apply(dialog, currentTheme);
        return dialog.ShowDialog(this) == DialogResult.OK;
    }

    void ShowUpdateProgress()
    {
        scanBar.AccessibleName = "Update download progress";
        scanBar.Visible = true;
        if (updateRelease is not { } release)
        {
            if (scanBar.Style != ProgressBarStyle.Marquee) scanBar.Style = ProgressBarStyle.Marquee;
            scanState.Text = "Checking for updates…";
            return;
        }

        if (scanBar.Style != ProgressBarStyle.Continuous) scanBar.Style = ProgressBarStyle.Continuous;
        int percent = (int)(100 * updateBytes / release.Bytes);
        scanBar.Value = percent;
        scanState.Text = updateBytes == release.Bytes
            ? $"Verifying update {release.Version}…"
            : $"Downloading update {release.Version} · {percent}% · {updateBytes / 1_000_000.0:N1} of {release.Bytes / 1_000_000.0:N1} MB";
    }
}
