using System.Diagnostics;

namespace FileInventory;

sealed partial class InventoryWindow
{
    readonly GroupBox fileDetails = new() { Text = "File details", Dock = DockStyle.Fill,
        Height = 156, Visible = false, Padding = new Padding(8, 5, 8, 6) };
    readonly TextBox fileDetailsText = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, AccessibleName = "File metadata" };
    int fileActivation;

    void ClearFileDetails()
    {
        fileActivation++;
        fileDetails.Visible = false;
    }

    async Task OpenTreeFile(Branch branch, bool openExplorer = true)
    {
        int activation = ++fileActivation;
        try
        {
            var metadata = await Task.Run(() => catalog.GetFileMetadata(branch.Volume, branch.Relative));
            if (closing || activation != fileActivation) return;
            if (metadata is null)
            {
                state.Text = "This file is no longer in the catalog.";
                return;
            }

            fileDetailsText.Text = string.Join(Environment.NewLine,
                metadata.FullPath,
                $"Size: {metadata.Bytes:N0} bytes    Drive: {metadata.Volume}    Status: {metadata.Status}",
                $"Created: {metadata.Created:G}    Modified: {metadata.Modified:G}",
                $"Last indexed: {metadata.LastSeen:G}    Attributes: {metadata.Attributes}");
            fileDetails.Visible = true;

            if (!openExplorer || benchmark || !metadata.CanReveal) return;

            bool opened = await RevealFile(branch.Volume, branch.Relative, metadata.FullPath,
                () => activation == fileActivation);
            if (!opened && !closing && activation == fileActivation)
                state.Text = "This file location is currently unavailable.";
        }
        catch (Exception ex)
        {
            if (closing || activation != fileActivation) return;
            state.Text = ex.Message;
        }
    }

    async Task<bool> RevealFile(string volumeId, string relative, string fullPath, Func<bool>? stillRequested = null)
    {
        var volume = volumes.FirstOrDefault(v => v.Id == volumeId);
        if (benchmark || volume is not { Online: true }) return false;
        bool available = await Task.Run(() => WindowsVolumes.CanReveal(volume, relative, fullPath));
        if (!available || closing || stillRequested?.Invoke() == false) return false;
        using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
            { UseShellExecute = true });
        return true;
    }
}
