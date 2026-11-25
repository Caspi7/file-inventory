namespace FileInventory;

sealed partial class InventoryWindow
{
    readonly Button updateButton = new() { Text = "Check for updates", AutoSize = true };
    readonly Button releaseNotesButton = new() { Text = "Release notes", AutoSize = true };
    const string CurrentReleaseNotes =
        "File Inventory 1.2.9\n\n" +
        "• Improved Dark/Light mode styling across all controls (ComboBox, DateTimePicker, ListView, and buttons).\n" +
        "• Modern Win11/macOS-style UI with updated logo and release notes.";
    bool checkingUpdates;
    AppUpdates.Release? updateRelease;
    long updateBytes;
    CancellationTokenSource? updateCancellation;
    public string? UpdateInstaller { get; private set; }

    void InitializeUpdates()
    {
        updateButton.Enabled = !benchmark;
        releaseNotesButton.Click += (_, _) =>
            MessageBox.Show(this, CurrentReleaseNotes, $"File Inventory {AppUpdates.CurrentVersion.ToString(3)} Release Notes");
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
            if (!closing)
            {
                var notes = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes provided." : release.Notes;
                MessageBox.Show(this, notes, $"Update available: File Inventory {release.Version}");
            }
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
