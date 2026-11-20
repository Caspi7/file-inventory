namespace FileInventory;

sealed partial class InventoryWindow
{
    readonly Button updateButton = new() { Text = "Check for updates", AutoSize = true };
    bool checkingUpdates;
    CancellationTokenSource? updateCancellation;
    public string? UpdateInstaller { get; private set; }

    void InitializeUpdates()
    {
        updateButton.Enabled = !benchmark;
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
        if (!AppUpdates.IsInstalledHere())
        {
            MessageBox.Show(this, "Install File Inventory using Setup to enable in-app updates.", "File Inventory update");
            return;
        }
        checkingUpdates = true;
        updateButton.Text = "Cancel update check";
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

            state.Text = $"Downloading update {release.Version}…";
            updateButton.Text = "Cancel download";
            download = await AppUpdates.Download(release, timeout.Token);
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
            if (!closing)
            {
                updateButton.Enabled = true;
                updateButton.Text = "Check for updates";
            }
        }
        if (UpdateInstaller is not null) Close();
    }
}
