using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace FileInventory;

static class AppUpdates
{
    const string Repository = "Caspi7/file-inventory";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{C79DF90C-B339-4190-8A4C-BC3E619780E2}_is1";
    const long MaximumInstallerBytes = 150_000_000;
    static readonly HttpClient Http = new();

    internal record Release(Version Version, Uri DownloadUrl, long Bytes, string Sha256, string Notes = "");
    public static Version CurrentVersion => typeof(AppUpdates).Assembly.GetName().Version ?? new(0, 0, 0);

    public static bool IsInstalledHere()
    {
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var entry = registry.OpenSubKey(UninstallKey);
        if (entry?.GetValue("InstallLocation") is not string installed) return false;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<Release?> FindUpdate(Version installed, CancellationToken token)
    {
        var url = new Uri($"https://api.github.com/repos/{Repository}/releases?per_page=30");
        using var response = await Get(url, token);
        await using var content = await response.Content.ReadAsStreamAsync(token);
        using var json = await JsonDocument.ParseAsync(content, cancellationToken: token);
        return SelectRelease(json.RootElement, installed);
    }

    internal static Release? SelectRelease(JsonElement releases, Version installed)
    {
        Release? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith('v')) continue;
            string number = tag[1..];
            if (number.EndsWith("-preview", StringComparison.Ordinal)) number = number[..^8];
            if (!Version.TryParse(number, out var version) || version.Build < 0 || version.Revision >= 0
                || number != version.ToString(3)) continue;
            if (version <= new Version(installed.Major, installed.Minor, Math.Max(0, installed.Build))
                || (best is not null && version <= best.Version)) continue;

            string filename = $"FileInventory_Setup_x64_{version}.exe";
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() != filename || asset.GetProperty("state").GetString() != "uploaded") continue;
                long bytes = asset.GetProperty("size").GetInt64();
                if (bytes is <= 0 or > MaximumInstallerBytes || !asset.TryGetProperty("digest", out var value)) continue;
                string? digest = value.GetString();
                if (digest is not { Length: 71 } || !digest.StartsWith("sha256:", StringComparison.Ordinal)
                    || !digest[7..].All(Uri.IsHexDigit)) continue;

                var url = new Uri($"https://github.com/{Repository}/releases/download/{tag}/{filename}");
                if (asset.GetProperty("browser_download_url").GetString() != url.AbsoluteUri) continue;
                string notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                best = new(version, url, bytes, digest[7..], notes);
                break;
            }
        }
        return best;
    }

    public static async Task<string> Download(Release release, CancellationToken token, IProgress<long>? progress = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"FileInventory-update-{Guid.NewGuid():N}.exe");
        try
        {
            using var response = await Get(release.DownloadUrl, token);
            await using var content = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            long sampleAt = Stopwatch.GetTimestamp();
            while (await content.ReadAsync(buffer, token) is int count && count != 0)
            {
                total += count;
                if (total > release.Bytes) throw new InvalidDataException("The installer size did not match the release.");
                sha.AppendData(buffer, 0, count);
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                if (total == release.Bytes || Stopwatch.GetElapsedTime(sampleAt).TotalMilliseconds >= 150)
                {
                    progress?.Report(total);
                    sampleAt = Stopwatch.GetTimestamp();
                }
            }
            if (total != release.Bytes || !Convert.ToHexString(sha.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installer did not match the GitHub release checksum.");
            await output.DisposeAsync();
            await File.WriteAllTextAsync(path + ":Zone.Identifier",
                $"[ZoneTransfer]\r\nZoneId=3\r\nHostUrl={release.DownloadUrl.AbsoluteUri}\r\n", token);
            return path;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    static async Task<HttpResponseMessage> Get(Uri url, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd($"FileInventory/{CurrentVersion}");
        var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.IsSuccessStatusCode) return response;
        response.Dispose();
        throw new HttpRequestException($"GitHub returned HTTP {(int)response.StatusCode} while checking or downloading the update.");
    }

    public static void RunInstaller(string installer)
    {
        using var process = Process.Start(new ProcessStartInfo(installer, "/SP- /SILENT /NORESTART /UPDATE")
            { UseShellExecute = true }) ?? throw new InvalidOperationException("The update installer could not be started.");
    }
}
