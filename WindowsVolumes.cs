using System.Buffers.Binary;
using System.ComponentModel;
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
        bool removable = false;
        try { removable = new DriveInfo(mount).DriveType == DriveType.Removable; }
        catch (Exception) { }
        return new(id, mount, label.Length == 0 ? mount : label.ToString(), true, false, 24,
            null, null, "", "", Environment.MachineName, removable);
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

    public static ulong? ReadVolumeSerial(string rootPath)
    {
        // Open root directory with FILE_READ_ATTRIBUTES + FILE_FLAG_BACKUP_SEMANTICS.
        using var handle = CreateFileW(Extended(rootPath), 0x80, 7, IntPtr.Zero, 3,
            0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var info, 24))
            return null;
        return info.Serial;
    }

    public static bool TryEnumerateNtfsDirectory(
        string fullDirectory,
        string relativeDirectory,
        ulong volumeSerial,
        byte[] buffer,
        Action<Entry> onEntry,
        CancellationToken token)
    {
        const uint FileListDirectory = 0x00000001;
        const uint Synchronize = 0x00100000;
        const uint FileFlagBackupSemantics = 0x02000000;
        const int FileIdExtdDirectoryInformation = 60;

        const int StatusSuccess = 0x00000000;
        const int StatusNoMoreFiles = unchecked((int)0x80000006);
        const int StatusNoSuchFile = unchecked((int)0xC000000F);
        const int StatusInvalidInfoClass = unchecked((int)0xC0000003);
        const int StatusNotImplemented = unchecked((int)0xC0000002);
        const int StatusInvalidParameter = unchecked((int)0xC000000D);
        const int StatusNotSupported = unchecked((int)0xC00000BB);
        const int StatusAccessDenied = unchecked((int)0xC0000022);

        using var handle = CreateFileW(Extended(fullDirectory), FileListDirectory | Synchronize, 7,
            IntPtr.Zero, 3, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            ThrowWin32(err);
        }

        bool restart = true;
        bool emittedAny = false;
        var span = buffer.AsSpan();

        while (true)
        {
            token.ThrowIfCancellationRequested();
            int status = NtQueryDirectoryFile(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                out var ioStatus, buffer, (uint)buffer.Length, FileIdExtdDirectoryInformation,
                false, IntPtr.Zero, restart);
            restart = false;

            if (status == StatusNoMoreFiles || status == StatusNoSuchFile)
                return true;

            if (status != StatusSuccess)
            {
                if (!emittedAny && (status == StatusInvalidInfoClass || status == StatusNotSupported
                    || status == StatusNotImplemented || status == StatusInvalidParameter))
                    return false;

                if (status == StatusAccessDenied)
                    throw new UnauthorizedAccessException(new Win32Exception(5).Message);

                int dosError = (int)RtlNtStatusToDosError(status);
                ThrowWin32(dosError);
            }

            int bytesReturned = checked((int)ioStatus.Information);
            if (bytesReturned == 0)
                return true;

            int offset = 0;
            while (offset + 88 <= bytesReturned)
            {
                int nextOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
                long creationTime = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 8, 8));
                long lastWriteTime = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 24, 8));
                long endOfFile = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 40, 8));
                uint fileAttributes = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 56, 4));
                int fileNameLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset + 60, 4));
                ulong idLow = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 72, 8));
                ulong idHigh = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(offset + 80, 8));

                if (fileNameLength > 0 && offset + 88 + fileNameLength <= bytesReturned)
                {
                    bool isDot = (fileNameLength == 2 && buffer[offset + 88] == '.' && buffer[offset + 89] == 0)
                        || (fileNameLength == 4 && buffer[offset + 88] == '.' && buffer[offset + 89] == 0
                            && buffer[offset + 90] == '.' && buffer[offset + 91] == 0);
                    if (!isDot)
                    {
                        string name = Encoding.Unicode.GetString(buffer, offset + 88, fileNameLength);
                        string relative = relativeDirectory.Length == 0
                            ? name
                            : string.Concat(relativeDirectory, "\\", name);
                        var attrs = (FileAttributes)fileAttributes;
                        bool isDirectory = (attrs & FileAttributes.Directory) != 0;
                        bool risk = (attrs & FileAttributes.ReparsePoint) != 0 || HasRecallRisk(attrs);
                        string? fileKey = (!isDirectory && !risk && (idLow != 0 || idHigh != 0))
                            ? $"{volumeSerial:X16}:{idLow:X16}{idHigh:X16}"
                            : null;
                        emittedAny = true;
                        onEntry(new Entry(relative, name, relativeDirectory, isDirectory ? 0L : endOfFile,
                            FileTimeToUnixMs(lastWriteTime), FileTimeToUnixMs(creationTime),
                            (int)fileAttributes, fileKey));
                    }
                }

                if (nextOffset == 0) break;
                offset += nextOffset;
            }
        }
    }

    static long FileTimeToUnixMs(long fileTime)
    {
        if (fileTime <= 0 || fileTime > 2_650_467_743_999_999_999L) return 0;
        return DateTimeOffset.FromFileTime(fileTime).ToUnixTimeMilliseconds();
    }

    static void ThrowWin32(int err)
    {
        var message = new Win32Exception(err).Message;
        if (err == 5) throw new UnauthorizedAccessException(message);
        if (err is 2 or 3) throw new DirectoryNotFoundException(message);
        throw new IOException(message, unchecked((int)(0x80070000u | (uint)err)));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FileIdInformation { public ulong Serial, Low, High; }

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatusBlock { public IntPtr Status, Information; }

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
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")]
    static extern bool SetThreadErrorMode(uint mode, out uint previous);
    [DllImport("kernel32.dll")]
    static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out FileIdInformation info, uint size);
    [DllImport("ntdll.dll")]
    static extern int NtQueryDirectoryFile(SafeFileHandle fileHandle, IntPtr eventHandle,
        IntPtr apcRoutine, IntPtr apcContext, out IoStatusBlock ioStatusBlock,
        [Out] byte[] fileInformation, uint length, int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry, IntPtr fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);
    [DllImport("ntdll.dll")]
    static extern uint RtlNtStatusToDosError(int status);
}
