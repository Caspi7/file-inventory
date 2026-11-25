using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileInventory;

sealed record SyncConfig(string Folder = "", bool AutoSyncAfterScan = false, string Passphrase = "");

sealed record CloudProvider(string Name, string RootPath, string SuggestedFolder);

sealed record SyncSummary(int Exported, int Imported, int Skipped, List<string> Errors);

static class CloudSync
{
    const string ConfigFileName = "sync.json";
    const string StateFileName = "sync_state.json";
    const string PlainExtension = ".fiinventory";
    const string LegacyPlainExtension = ".finv.gz";
    const string EncryptedExtension = ".finv.enc";
    const string SyncSubfolderName = "FileInventorySync";
    const int SaltSize = 16;
    const int NonceSize = 12;
    const int TagSize = 16;
    const int KeySize = 32;
    const int Pbkdf2Iterations = 100_000;

    static ReadOnlySpan<byte> MagicHeader => "FINVENC1"u8;

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static SyncConfig LoadConfig(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, ConfigFileName);
        if (!File.Exists(path)) return new SyncConfig();
        try
        {
            return JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(path)) ?? new SyncConfig();
        }
        catch (Exception)
        {
            return new SyncConfig();
        }
    }

    public static void SaveConfig(string dataDirectory, SyncConfig config)
    {
        Directory.CreateDirectory(dataDirectory);
        string path = Path.Combine(dataDirectory, ConfigFileName);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(config, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                try { File.Delete(temp); } catch (Exception) { }
        }
    }

    public static IReadOnlyList<CloudProvider> DetectProviders()
    {
        var providers = new List<CloudProvider>();
        var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAdd(string name, string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            try
            {
                string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(full) || !seenRoots.Add(full)) return;
                providers.Add(new CloudProvider(name, full, Path.Combine(full, SyncSubfolderName)));
            }
            catch (Exception) { }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Google Drive: mounted drives containing "My Drive", plus %USERPROFILE%\Google Drive
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    string myDrive = Path.Combine(drive.RootDirectory.FullName, "My Drive");
                    if (Directory.Exists(myDrive))
                        TryAdd("Google Drive", myDrive);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }

        if (!string.IsNullOrWhiteSpace(userProfile))
            TryAdd("Google Drive", Path.Combine(userProfile, "Google Drive"));

        // OneDrive
        TryAdd("OneDrive", Environment.GetEnvironmentVariable("OneDrive"));
        TryAdd("OneDrive (Work)", Environment.GetEnvironmentVariable("OneDriveCommercial"));
        TryAdd("OneDrive (Personal)", Environment.GetEnvironmentVariable("OneDriveConsumer"));
        if (!string.IsNullOrWhiteSpace(userProfile))
            TryAdd("OneDrive", Path.Combine(userProfile, "OneDrive"));

        // Dropbox
        if (!string.IsNullOrWhiteSpace(userProfile))
            TryAdd("Dropbox", Path.Combine(userProfile, "Dropbox"));

        // iCloud Drive
        if (!string.IsNullOrWhiteSpace(userProfile))
            TryAdd("iCloud Drive", Path.Combine(userProfile, "iCloudDrive"));

        return providers;
    }

    public static Task<SyncSummary> SyncAsync(Catalog catalog, SyncConfig config, CancellationToken token = default)
        => Task.Run(() => Sync(catalog, config, token), token);

    static SyncSummary Sync(Catalog catalog, SyncConfig config, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(config.Folder))
            throw new InvalidOperationException("Choose a cloud sync folder first.");

        Directory.CreateDirectory(config.Folder);

        int exported = 0, imported = 0, skipped = 0;
        var errors = new List<string>();
        string machineName = Environment.MachineName;
        string machinePrefix = SanitizeFilePart(machineName);
        bool encrypt = !string.IsNullOrEmpty(config.Passphrase);
        string activeExt = encrypt ? EncryptedExtension : PlainExtension;
        string alternateExt = encrypt ? PlainExtension : EncryptedExtension;

        // 1. Export local scanned volumes that are missing or newer than existing cloud snapshots
        var localVolumes = catalog.Volumes()
            .Where(v => !v.Id.StartsWith("import:", StringComparison.Ordinal) && v.Succeeded is not null)
            .ToList();

        foreach (var volume in localVolumes)
        {
            token.ThrowIfCancellationRequested();
            string baseName = SnapshotBaseName(machineName, volume);
            string legacyBaseName = $"{machinePrefix}__{SanitizeFilePart(volume.Id)}";
            string targetPath = Path.Combine(config.Folder, baseName + activeExt);
            string alternatePath = Path.Combine(config.Folder, baseName + alternateExt);
            DateTime succeededUtc = DateTimeOffset.FromUnixTimeMilliseconds(volume.Succeeded!.Value).UtcDateTime;

            try
            {
                if (File.Exists(targetPath) && File.GetLastWriteTimeUtc(targetPath) >= succeededUtc)
                {
                    skipped++;
                    continue;
                }

                if (encrypt)
                {
                    string tempPlain = Path.Combine(Path.GetTempPath(), $"finv-export-{Guid.NewGuid():N}{PlainExtension}");
                    try
                    {
                        catalog.ExportInventory(volume.Id, tempPlain);
                        EncryptFileAtomic(tempPlain, targetPath, config.Passphrase);
                    }
                    finally
                    {
                        if (File.Exists(tempPlain))
                            try { File.Delete(tempPlain); } catch (Exception) { }
                    }
                }
                else
                {
                    catalog.ExportInventory(volume.Id, targetPath);
                }

                if (File.Exists(alternatePath))
                    try { File.Delete(alternatePath); } catch (Exception) { }
                foreach (string extension in new[] { LegacyPlainExtension, PlainExtension, EncryptedExtension })
                {
                    string legacyPath = Path.Combine(config.Folder, legacyBaseName + extension);
                    try { File.Delete(legacyPath); } catch (Exception) { }
                }

                exported++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"Export {volume.DisplayName}: {ex.Message}");
            }
        }

        // 2. Import snapshots present in the sync folder (.finv.gz, .finv.enc, and .fiinventory)
        var state = LoadState(catalog.DataDirectory);
        bool stateDirty = false;
        string ownPrefix = machinePrefix + "__";
        string readableOwnPrefix = ReadableMachineName(machineName) + " - ";
        var localVolumeSanitizedIds = localVolumes
            .Select(v => SanitizeFilePart(v.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var localShortIds = localVolumes
            .Select(v => SnapshotShortId(v.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingVolumeIds = catalog.Volumes()
            .Select(v => v.Id)
            .ToHashSet(StringComparer.Ordinal);

        var snapshotFiles = Directory.EnumerateFiles(config.Folder, "*", SearchOption.TopDirectoryOnly)
            .Where(p =>
                p.EndsWith(PlainExtension, StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(LegacyPlainExtension, StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(EncryptedExtension, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (string filePath in snapshotFiles)
        {
            token.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);
            string extension = fileName.EndsWith(EncryptedExtension, StringComparison.OrdinalIgnoreCase)
                ? EncryptedExtension
                : fileName.EndsWith(LegacyPlainExtension, StringComparison.OrdinalIgnoreCase)
                    ? LegacyPlainExtension : PlainExtension;
            string stem = fileName[..^extension.Length];
            bool ownLegacy = stem.StartsWith(ownPrefix, StringComparison.OrdinalIgnoreCase)
                && localVolumeSanitizedIds.Contains(stem[ownPrefix.Length..]);
            bool ownReadable = stem.StartsWith(readableOwnPrefix, StringComparison.OrdinalIgnoreCase)
                && stem.Length >= readableOwnPrefix.Length + 8 && stem[^8] == '[' && stem[^1] == ']'
                && localShortIds.Contains(stem[^7..^1]);
            if (ownLegacy || ownReadable)
            {
                skipped++;
                continue;
            }

            try
            {
                long writeTicks = File.GetLastWriteTimeUtc(filePath).Ticks;
                string stateVolKey = "vol:" + fileName;
                bool hasImportedVol = state.TryGetValue(stateVolKey, out long volHash) &&
                    existingVolumeIds.Any(id => StringComparer.Ordinal.GetHashCode(id) == volHash);
                if (hasImportedVol && state.TryGetValue(fileName, out long previousTicks) && previousTicks >= writeTicks)
                {
                    skipped++;
                    continue;
                }

                string importedId = ImportSnapshot(catalog, filePath, config.Passphrase);
                existingVolumeIds.Add(importedId);
                state[fileName] = writeTicks;
                state[stateVolKey] = StringComparer.Ordinal.GetHashCode(importedId);
                stateDirty = true;
                imported++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"Import {fileName}: {ex.Message}");
            }
        }

        if (stateDirty)
            SaveState(catalog.DataDirectory, state);

        return new SyncSummary(exported, imported, skipped, errors);
    }

    static string ImportSnapshot(Catalog catalog, string sourcePath, string passphrase)
    {
        if (!IsEncryptedSnapshot(sourcePath))
            return catalog.ImportInventory(sourcePath);

        if (string.IsNullOrEmpty(passphrase))
            throw new InvalidDataException("Snapshot is encrypted. Enter the sync passphrase in Cloud Sync settings.");

        string tempPlain = Path.Combine(Path.GetTempPath(), $"finv-import-{Guid.NewGuid():N}{PlainExtension}");
        try
        {
            DecryptFile(sourcePath, tempPlain, passphrase);
            return catalog.ImportInventory(tempPlain);
        }
        finally
        {
            if (File.Exists(tempPlain))
                try { File.Delete(tempPlain); } catch (Exception) { }
        }
    }

    static bool IsEncryptedSnapshot(string path)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        int read = stream.Read(header);
        return read == 8 && header.SequenceEqual(MagicHeader);
    }

    static void EncryptFileAtomic(string plainPath, string destinationPath, string passphrase)
    {
        byte[] plaintext = File.ReadAllBytes(plainPath);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] tag = new byte[TagSize];
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] key = DeriveKey(passphrase, salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, MagicHeader);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        string temp = destinationPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                output.Write(MagicHeader);
                output.Write(salt);
                output.Write(nonce);
                output.Write(tag);
                output.Write(ciphertext);
            }
            File.Move(temp, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                try { File.Delete(temp); } catch (Exception) { }
        }
    }

    static void DecryptFile(string encryptedPath, string destinationPath, string passphrase)
    {
        byte[] data = File.ReadAllBytes(encryptedPath);
        int headerLength = MagicHeader.Length + SaltSize + NonceSize + TagSize;
        if (data.Length < headerLength || !data.AsSpan(0, MagicHeader.Length).SequenceEqual(MagicHeader))
            throw new InvalidDataException("Invalid encrypted snapshot header.");

        int offset = MagicHeader.Length;
        ReadOnlySpan<byte> salt = data.AsSpan(offset, SaltSize); offset += SaltSize;
        ReadOnlySpan<byte> nonce = data.AsSpan(offset, NonceSize); offset += NonceSize;
        ReadOnlySpan<byte> tag = data.AsSpan(offset, TagSize); offset += TagSize;
        ReadOnlySpan<byte> ciphertext = data.AsSpan(offset);

        byte[] plaintext = new byte[ciphertext.Length];
        byte[] key = DeriveKey(passphrase, salt);

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, MagicHeader);
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("Could not decrypt snapshot. Check that your sync passphrase matches.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        File.WriteAllBytes(destinationPath, plaintext);
    }

    static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

    public static string SnapshotBaseName(string machineName, Volume volume)
        => $"{SnapshotDisplayName(machineName, volume)} [{SnapshotShortId(volume.Id)}]";

    public static string SnapshotDisplayName(string machineName, Volume volume)
    {
        string driveName = string.IsNullOrWhiteSpace(volume.Label)
            || string.Equals(volume.Label, volume.Mount, StringComparison.OrdinalIgnoreCase)
            ? "Drive" : SanitizeReadable(volume.Label, 150, "Drive");
        string mount = volume.Mount;
        string driveLetter = mount.Length >= 2 && mount[1] == ':' && char.IsAsciiLetter(mount[0])
            ? $" ({char.ToUpperInvariant(mount[0])})" : "";
        return $"{ReadableMachineName(machineName)} - {driveName}{driveLetter}";
    }

    static string ReadableMachineName(string machineName) => SanitizeReadable(machineName, 64, "PC");

    static string SanitizeReadable(string value, int maxLength, string fallback)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string readable = new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        if (readable.Length > maxLength)
        {
            int length = char.IsHighSurrogate(readable[maxLength - 1]) ? maxLength - 1 : maxLength;
            readable = readable[..length];
        }
        readable = readable.TrimEnd('.', ' ');
        return readable.Length == 0 ? fallback : readable;
    }

    static string SnapshotShortId(string volumeId)
    {
        int guidStart = volumeId.IndexOf('{');
        string identifier = guidStart >= 0 ? volumeId[(guidStart + 1)..]
            : volumeId.StartsWith("serial:", StringComparison.OrdinalIgnoreCase) ? volumeId[7..]
            : volumeId;
        string shortId = new string(identifier.Where(char.IsAsciiLetterOrDigit).Take(6).ToArray());
        return shortId.Length == 6 ? shortId
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(volumeId)))[..6];
    }

    static string SanitizeFilePart(string value)
    {
        var sb = new StringBuilder(value.Length);
        bool lastUnderscore = false;
        foreach (char c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '-')
            {
                sb.Append(c);
                lastUnderscore = false;
            }
            else if (!lastUnderscore && sb.Length > 0)
            {
                sb.Append('_');
                lastUnderscore = true;
            }
        }
        string cleaned = sb.ToString().Trim('_');
        if (cleaned.Length == 0 || cleaned.Length > 48)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
            cleaned = cleaned.Length == 0 ? hash : cleaned[..35].TrimEnd('_') + "_" + hash;
        }
        return cleaned;
    }

    static Dictionary<string, long> LoadState(string dataDirectory)
    {
        string path = Path.Combine(dataDirectory, StateFileName);
        if (!File.Exists(path)) return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path));
            return loaded is null
                ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, long>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }
    }

    static void SaveState(string dataDirectory, Dictionary<string, long> state)
    {
        Directory.CreateDirectory(dataDirectory);
        string path = Path.Combine(dataDirectory, StateFileName);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                try { File.Delete(temp); } catch (Exception) { }
        }
    }
}

sealed partial class InventoryWindow
{
    readonly Button syncButton = new() { Text = "☁ Sync", AutoSize = true };
    readonly Button syncSettingsButton = new() { Text = "Sync settings…", AutoSize = true };
    bool syncingCloud;
    CancellationTokenSource? syncCancellation;

    void InitializeSync()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Sync now", null, async (_, _) => await HandleSyncButtonClickAsync());
        menu.Items.Add("Sync settings…", null, async (_, _) => await OpenSyncDialogAsync());
        syncButton.ContextMenuStrip = menu;
        syncButton.Click += async (_, _) => await HandleSyncButtonClickAsync();
        syncSettingsButton.Click += async (_, _) => await OpenSyncDialogAsync();
        FormClosed += (_, _) =>
        {
            syncCancellation?.Cancel();
            syncCancellation?.Dispose();
            menu.Dispose();
        };
    }

    async Task HandleSyncButtonClickAsync()
    {
        if (syncingCloud || transferringInventory || checkingUpdates || benchmark) return;
        var config = CloudSync.LoadConfig(catalog.DataDirectory);
        if (string.IsNullOrWhiteSpace(config.Folder))
        {
            await OpenSyncDialogAsync();
            return;
        }
        await RunCloudSyncAsync(config, interactive: true);
    }

    async Task OpenSyncDialogAsync()
    {
        if (syncingCloud || transferringInventory || checkingUpdates || benchmark) return;
        var config = CloudSync.LoadConfig(catalog.DataDirectory);
        var providers = CloudSync.DetectProviders();

        using var dialog = new Form
        {
            Text = "Cloud Sync",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(580, 320),
            MinimumSize = new Size(520, 300),
            Font = Font,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false
        };

        var folderBox = new TextBox
        {
            Text = config.Folder,
            Dock = DockStyle.Fill,
            PlaceholderText = @"e.g. G:\My Drive\FileInventorySync"
        };
        var browseFolder = new Button { Text = "Browse…", AutoSize = true };
        browseFolder.Click += (_, _) =>
        {
            using var picker = new FolderBrowserDialog
            {
                Description = "Choose a cloud-synced folder for File Inventory snapshots",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(folderBox.Text) ? folderBox.Text : ""
            };
            if (picker.ShowDialog(dialog) == DialogResult.OK)
                folderBox.Text = picker.SelectedPath;
        };

        var autoSyncCheck = new CheckBox
        {
            Text = "Auto-sync automatically after each drive scan",
            Checked = config.AutoSyncAfterScan,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 4)
        };

        var passphraseBox = new TextBox
        {
            Text = config.Passphrase,
            UseSystemPasswordChar = true,
            Dock = DockStyle.Fill,
            PlaceholderText = "(Optional) Encrypt cloud snapshots with AES-256"
        };

        var providerFlow = Flow();
        providerFlow.Margin = new Padding(0, 2, 0, 6);
        if (providers.Count > 0)
        {
            foreach (var provider in providers)
            {
                var btn = new Button
                {
                    Text = provider.Name,
                    AutoSize = true,
                    Margin = new Padding(0, 2, 6, 2)
                };
                var suggested = provider.SuggestedFolder;
                btn.Click += (_, _) => folderBox.Text = suggested;
                providerFlow.Controls.Add(btn);
            }
        }
        else
        {
            providerFlow.Controls.Add(LabelFor("No cloud drive folders were auto-detected. Use Browse… to pick any synced folder."));
        }

        var folderRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 2, 0, 4)
        };
        folderRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new(SizeType.AutoSize));
        folderRow.RowStyles.Add(new(SizeType.AutoSize));
        folderBox.Margin = new Padding(0, 3, 6, 3);
        browseFolder.Margin = new Padding(0, 1, 0, 1);
        folderRow.Controls.Add(folderBox, 0, 0);
        folderRow.Controls.Add(browseFolder, 1, 0);

        bool syncNowRequested = false;
        SyncConfig? savedConfig = null;

        SyncConfig ReadFormConfig() => new(folderBox.Text.Trim(), autoSyncCheck.Checked, passphraseBox.Text);

        var syncNowButton = new Button { Text = "Sync Now", AutoSize = true };
        var saveButton = new Button { Text = "Save", AutoSize = true };
        var closeButton = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };

        syncNowButton.Click += (_, _) =>
        {
            var next = ReadFormConfig();
            if (string.IsNullOrWhiteSpace(next.Folder))
            {
                MessageBox.Show(dialog, "Choose a cloud sync folder before syncing.", "Cloud Sync",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CloudSync.SaveConfig(catalog.DataDirectory, next);
            savedConfig = next;
            syncNowRequested = true;
            dialog.DialogResult = DialogResult.OK;
        };
        saveButton.Click += (_, _) =>
        {
            var next = ReadFormConfig();
            CloudSync.SaveConfig(catalog.DataDirectory, next);
            savedConfig = next;
            dialog.DialogResult = DialogResult.OK;
        };

        var buttons = Flow();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Margin = new Padding(0, 10, 0, 0);
        buttons.Controls.AddRange([closeButton, saveButton, syncNowButton]);
        foreach (Control c in buttons.Controls)
            c.Margin = new Padding(6, 2, 0, 2);

        var layout = Stack(7);
        layout.Padding = new Padding(14);
        var providerHeader = LabelFor("Quick-select detected cloud drive:");
        providerHeader.Margin = new Padding(0, 0, 0, 2);
        var folderHeader = LabelFor("Sync folder:");
        folderHeader.Margin = new Padding(0, 4, 0, 2);
        var passphraseHeader = LabelFor("Encryption passphrase (optional):");
        passphraseHeader.Margin = new Padding(0, 6, 0, 2);
        passphraseBox.Margin = new Padding(0, 2, 0, 4);

        layout.Controls.Add(providerHeader, 0, 0);
        layout.Controls.Add(providerFlow, 0, 1);
        layout.Controls.Add(folderHeader, 0, 2);
        layout.Controls.Add(folderRow, 0, 3);
        layout.Controls.Add(autoSyncCheck, 0, 4);
        layout.Controls.Add(passphraseHeader, 0, 5);
        layout.Controls.Add(passphraseBox, 0, 6);
        for (int i = 0; i < 7; i++)
            layout.RowStyles.Add(new(SizeType.AutoSize));

        var outer = Stack(2);
        outer.Padding = new Padding(0);
        outer.Controls.Add(layout, 0, 0);
        buttons.Padding = new Padding(14, 0, 14, 12);
        outer.Controls.Add(buttons, 0, 1);
        outer.RowStyles.Add(new(SizeType.Percent, 100));
        outer.RowStyles.Add(new(SizeType.AutoSize));

        dialog.Controls.Add(outer);
        dialog.AcceptButton = syncNowButton;
        dialog.CancelButton = closeButton;
        dialog.Load += (_, _) => Theme.Apply(dialog, currentTheme);

        if (dialog.ShowDialog(this) == DialogResult.OK && syncNowRequested && savedConfig is not null)
            await RunCloudSyncAsync(savedConfig, interactive: true);
    }

    async Task TryAutoSyncAfterScanAsync()
    {
        if (closing || syncingCloud || transferringInventory || checkingUpdates || benchmark) return;
        var config = CloudSync.LoadConfig(catalog.DataDirectory);
        if (!config.AutoSyncAfterScan || string.IsNullOrWhiteSpace(config.Folder)) return;
        await RunCloudSyncAsync(config, interactive: false);
    }

    async Task RunCloudSyncAsync(SyncConfig config, bool interactive)
    {
        if (closing || syncingCloud) return;
        syncingCloud = true;
        syncButton.Enabled = false;
        syncSettingsButton.Enabled = false;
        syncButton.Text = "Syncing…";
        state.Text = "Syncing with cloud folder…";

        syncCancellation?.Dispose();
        var cts = new CancellationTokenSource();
        syncCancellation = cts;

        try
        {
            var summary = await CloudSync.SyncAsync(catalog, config, cts.Token);
            if (closing) return;

            if (summary.Imported > 0)
            {
                await ReloadVolumes();
                ResetTree();
                QueueSearch();
            }

            if (summary.Errors.Count > 0)
            {
                state.Text = $"Cloud sync finished with {summary.Errors.Count} error(s) ({summary.Exported} exported, {summary.Imported} imported).";
                if (interactive && !closing)
                {
                    MessageBox.Show(this,
                        $"Exported: {summary.Exported}\r\nImported: {summary.Imported}\r\nSkipped: {summary.Skipped}\r\n\r\nErrors:\r\n"
                        + string.Join("\r\n", summary.Errors),
                        "Cloud Sync completed with errors", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (summary.Exported == 0 && summary.Imported == 0)
            {
                state.Text = "Cloud sync is up to date.";
            }
            else
            {
                state.Text = $"Cloud sync complete: {summary.Exported} exported, {summary.Imported} imported.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!closing)
            {
                state.Text = $"Cloud sync failed: {ex.Message}";
                if (interactive)
                    MessageBox.Show(this, ex.Message, "Cloud Sync failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            syncingCloud = false;
            if (!closing)
            {
                syncButton.Text = "☁ Sync";
                syncButton.Enabled = true;
                syncSettingsButton.Enabled = true;
            }
        }
    }
}
