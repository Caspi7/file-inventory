using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FileInventory;

static class UiHelpers
{
    static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatBytes(long bytes) => FormatBytes((decimal)bytes);

    // Reclaimable totals can exceed long.MaxValue.
    public static string FormatBytes(decimal bytes)
    {
        decimal size = bytes;
        int unit = 0;
        while (Math.Abs(size) >= 1024 && unit < SizeUnits.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{size:N1} {SizeUnits[unit]}";
    }

    public static void CopyText(string text, IWin32Window? owner = null)
    {
        try { Clipboard.SetText(text); }
        catch (ExternalException ex) { ShowWarning(owner, ex.Message); }
    }

    public static void RevealInExplorer(string fullPath, IWin32Window? owner = null)
    {
        try
        {
            if (fullPath.Contains('"'))
                throw new ArgumentException("The selected path contains a quotation mark.", nameof(fullPath));

            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fullPath}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex) when (owner is not null && (ex is Win32Exception or ArgumentException))
        {
            ShowWarning(owner, ex.Message);
        }
    }

    public static string? LoadEmbeddedText(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames();
        var name = names.FirstOrDefault(candidate =>
            candidate.Equals($"FileInventory.{resourceName}", StringComparison.Ordinal))
            ?? names.FirstOrDefault(candidate =>
                candidate.Equals(resourceName, StringComparison.Ordinal)
                || candidate.EndsWith("." + resourceName, StringComparison.Ordinal));
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static void ShowWarning(IWin32Window? owner, string message) =>
        MessageBox.Show(owner, message, (owner as Form)?.Text ?? "File Inventory",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
