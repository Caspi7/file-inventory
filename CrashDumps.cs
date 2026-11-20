using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileInventory;

static class CrashDumps
{
    const uint MiniDumpNormal = 0;
    static int capturing;

    public static void Register(string dataDirectory) =>
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Capture(dataDirectory, args.ExceptionObject);

    public static void Capture(string dataDirectory, object error)
    {
        if (Interlocked.Exchange(ref capturing, 1) != 0) return;
        try
        {
            var writer = new Thread(() => Write(dataDirectory, error)) { IsBackground = true };
            writer.Start();
            writer.Join(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // A diagnostics failure must not hide the original crash.
        }
    }

    static void Write(string dataDirectory, object error)
    {
        try
        {
            string folder = Path.Combine(dataDirectory, "CrashDumps");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, $"FileInventory-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}");
            string version = typeof(CrashDumps).Assembly.GetName().Version?.ToString() ?? "unknown";
            File.WriteAllText(path + ".txt", $"File Inventory {version} · {DateTime.UtcNow:O}{Environment.NewLine}{error}");
            string pending = path + ".tmp";
            try
            {
                using var process = Process.GetCurrentProcess();
                bool written;
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    written = MiniDumpWriteDump(process.Handle, (uint)process.Id, stream.SafeFileHandle,
                        MiniDumpNormal, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (written) File.Move(pending, path + ".dmp");
            }
            finally { File.Delete(pending); }
        }
        catch (Exception)
        {
            // Keep the exception report if the minidump could not be written.
        }
    }

    [DllImport("dbghelp.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool MiniDumpWriteDump(IntPtr process, uint processId, SafeFileHandle file,
        uint dumpType, IntPtr exception, IntPtr userStreams, IntPtr callback);
}
