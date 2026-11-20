using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FileInventory;

static class WindowsVolumes
{
    const FileAttributes RecallMask = (FileAttributes)(0x1000 | 0x40000 | 0x400000);
    static readonly IntPtr Invalid = new(-1);
    public static bool HasRecallRisk(FileAttributes attrs) => (attrs & RecallMask) != 0;
    public static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal)
        ? path : path.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;

    public static List<Volume> Mounted()
    {
        var mounted = new Dictionary<string, Volume>(StringComparer.OrdinalIgnoreCase);
        var guid = new StringBuilder(128);
        var search = FindFirstVolumeW(guid, (uint)guid.Capacity);
        if (search != Invalid)
        {
            try
            {
                do
                {
                    var id = guid.ToString();
                    var buffer = new char[1024];
                    if (!GetVolumePathNamesForVolumeNameW(id, buffer, (uint)buffer.Length, out var required)
                        && required > buffer.Length && required < 65536)
                    {
                        buffer = new char[required];
                        GetVolumePathNamesForVolumeNameW(id, buffer, required, out _);
                    }
                    foreach (var mount in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                        .OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
                    {
                        if (Identity(mount) != id) continue;
                        mounted[id] = MakeVolume(id, mount);
                        break;
                    }
                } while (FindNextVolumeW(search, guid, (uint)guid.Capacity));
            }
            finally { FindVolumeClose(search); }
        }
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.CDRom))
                continue;
            var id = Identity(drive.Name);
            if (id is not null) mounted.TryAdd(id, MakeVolume(id, drive.Name));
        }
        return mounted.Values.ToList();
    }

    static Volume MakeVolume(string id, string mount)
    {
        var label = new StringBuilder(261);
        GetVolumeInformationW(mount, label, (uint)label.Capacity, out _, out _, out _, null, 0);
        return new(id, mount, label.Length == 0 ? mount : label.ToString(), true, false, 24,
            null, null, "", "");
    }

    public static string? Identity(string mount)
    {
        bool restore = SetThreadErrorMode(0x0001, out uint previous);
        try
        {
            var fs = new StringBuilder(64);
            if (!GetVolumeInformationW(mount, null, 0, out var serial, out _, out _, fs, (uint)fs.Capacity))
                return null;
            var guid = new StringBuilder(128);
            if (GetVolumeNameForVolumeMountPointW(mount, guid, (uint)guid.Capacity)) return guid.ToString();
            return serial == 0 ? null : $"serial:{serial:X8}:{fs.ToString().ToUpperInvariant()}:{mount.ToUpperInvariant()}";
        }
        finally
        {
            if (restore) SetThreadErrorMode(previous, out _);
        }
    }

    public static bool IsNtfs(string mount)
    {
        var fs = new StringBuilder(64);
        return GetVolumeInformationW(mount, null, 0, out _, out _, out _, fs, (uint)fs.Capacity)
            && fs.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExcludedRelative(Volume volume, string dataDirectory)
    {
        var mount = new StringBuilder(32768);
        if (!GetVolumePathNameW(dataDirectory, mount, (uint)mount.Capacity))
            throw new IOException("Could not determine the app data mount point.");
        var root = mount.ToString();
        var id = Identity(root) ?? throw new IOException("Could not identify the app data volume.");
        return id == volume.Id ? Path.GetRelativePath(root, dataDirectory) : null;
    }

    public static bool CanReveal(Volume volume, string relative, string displayed)
    {
        if (!string.Equals(Path.Combine(volume.Mount, relative), displayed, StringComparison.OrdinalIgnoreCase)
            || Identity(volume.Mount) != volume.Id) return false;
        var path = volume.Mount;
        try
        {
            foreach (var part in relative.Split('\\'))
            {
                path = Path.Combine(path, part);
                var attrs = File.GetAttributes(Extended(path));
                if (HasRecallRisk(attrs) || attrs.HasFlag(FileAttributes.ReparsePoint)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string? ReadFileKey(string path)
    {
        // FILE_READ_ATTRIBUTES requests metadata only. Do not follow links or recall remote data.
        using var handle = CreateFileW(Extended(path), 0x80, 7, IntPtr.Zero, 3,
            0x00200000 | 0x00100000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var info, 24))
            return null;
        return $"{info.Serial:X16}:{info.Low:X16}{info.High:X16}";
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileIdInformation { public ulong Serial, Low, High; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr FindFirstVolumeW(StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool FindNextVolumeW(IntPtr search, StringBuilder name, uint length);
    [DllImport("kernel32.dll")]
    static extern bool FindVolumeClose(IntPtr search);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool GetVolumePathNamesForVolumeNameW(string id, [Out] char[] paths, uint length, out uint required);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool GetVolumeNameForVolumeMountPointW(string mount, StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool GetVolumeInformationW(string root, StringBuilder? label, uint labelLength,
        out uint serial, out uint maxComponent, out uint flags, StringBuilder? filesystem, uint filesystemLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool GetVolumePathNameW(string path, StringBuilder mount, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")]
    static extern bool SetThreadErrorMode(uint mode, out uint previous);
    [DllImport("kernel32.dll")]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileIdInformation info, uint size);
}
