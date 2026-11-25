using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace FileInventory;

record ScanProgress(long Entries, long Omitted, string Directory,
    double ElapsedSeconds = 0, long? ExpectedEntries = null, string Phase = "Scanning");

sealed class Scanner(Catalog catalog)
{
    readonly ConcurrentDictionary<string, byte> active = new(StringComparer.OrdinalIgnoreCase);
    readonly string publish = Catalog.Resource("Publish.sql");

    public async Task ScanAsync(Volume volume, IProgress<ScanProgress> progress, CancellationToken token)
    {
        if (!active.TryAdd(volume.Id, 0)) return;
        try { await Task.Run(() =>
        {
            var thread = Thread.CurrentThread;
            var priority = thread.Priority;
            try { thread.Priority = ThreadPriority.BelowNormal; Scan(volume, progress, token); }
            finally { thread.Priority = priority; }
        }); }
        finally { active.TryRemove(volume.Id, out _); }
    }

    void Scan(Volume volume, IProgress<ScanProgress> progress, CancellationToken token)
    {
        var stagePath = Path.Combine(catalog.DataDirectory, $"scan-{Guid.NewGuid():N}.sqlite3");
        var started = Catalog.Now();
        using var db = catalog.Open();
        using var interrupt = token.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        long? expected = null;
        using (var previous = Catalog.Command(db, """
            SELECT items FROM scans WHERE volume=$volume AND outcome IN ('Complete','Complete with omissions')
                AND finished IS NOT NULL ORDER BY id DESC LIMIT 1;
            """, ("volume", volume.Id)))
            if (previous.ExecuteScalar() is long items && items > 0) expected = items;
        Catalog.Execute(db, "BEGIN IMMEDIATE;");
        Catalog.Execute(db, "INSERT INTO scans(volume,started,outcome) VALUES($id,$when,'Scanning');",
            ("id", volume.Id), ("when", started));
        long scan;
        using (var command = Catalog.Command(db, "SELECT last_insert_rowid();"))
            scan = (long)command.ExecuteScalar()!;
        Catalog.Execute(db, "UPDATE volumes SET attempted=$when,outcome='Scanning',issues='' WHERE id=$id; COMMIT;",
            ("id", volume.Id), ("when", started));

        var samples = new List<string>();
        long count = 0, omitted = 0, identityTicks = 0, identityMisses = 0;
        var elapsed = Stopwatch.StartNew();
        string outcome = "Failed";
        bool published = false;
        void Report(string path, string phase = "Scanning") =>
            progress.Report(new(count, omitted, path, elapsed.Elapsed.TotalSeconds, expected, phase));
        Report(volume.Mount, "Preparing scan");

        void Issue(string path, string message)
        {
            omitted++;
            if (samples.Count < 40) samples.Add($"{path}: {message}");
        }

        string Summary() => $"Scanned {count:N0} entries in {elapsed.Elapsed.TotalSeconds:N1}s. "
            + $"Omitted areas: {omitted:N0}. NTFS identity time: {IdentityTime()} ms. "
            + $"Identity unavailable: {identityMisses:N0}."
            + (samples.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, samples));
        long IdentityTime() => (long)(identityTicks * 1000.0 / Stopwatch.Frequency);

        try
        {
            token.ThrowIfCancellationRequested();
            CheckMount(volume);
            var stableRoot = volume.Id.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                ? volume.Id : WindowsVolumes.Extended(volume.Mount);
            var excluded = WindowsVolumes.ExcludedRelative(volume, catalog.DataDirectory);
            var ntfs = WindowsVolumes.IsNtfs(volume.Mount);
            using (var stage = catalog.Open(stagePath))
            {
                // This scratch database is discarded on interruption. The live catalog
                // still uses its normal durable transaction when publishing the snapshot.
                Catalog.Execute(stage, "PRAGMA journal_mode=MEMORY; PRAGMA synchronous=OFF; PRAGMA locking_mode=EXCLUSIVE; PRAGMA cache_size=-131072;");
                Catalog.Execute(stage, """
                    CREATE TABLE entries(rel TEXT PRIMARY KEY,name TEXT,name_key TEXT,parent_key TEXT,
                     extension TEXT,bytes INTEGER,modified INTEGER,created INTEGER,attributes INTEGER,
                     file_key TEXT,tokens TEXT NOT NULL) WITHOUT ROWID;
                    CREATE TABLE coverage(directory TEXT PRIMARY KEY,complete INTEGER,detail TEXT) WITHOUT ROWID;
                    CREATE INDEX coverage_status ON coverage(complete,directory);
                    BEGIN IMMEDIATE;
                    """);
                using var item = Catalog.Command(stage, """
                    INSERT OR IGNORE INTO entries VALUES
                    ($rel,$name,$key,$parent,$ext,$bytes,$modified,$created,$attributes,$identity,$tokens);
                    """, ("rel", null), ("name", null), ("key", null), ("parent", null), ("ext", null),
                     ("bytes", 0), ("modified", 0), ("created", 0), ("attributes", 0), ("identity", null), ("tokens", "[]"));
                var pending = new Stack<string>();
                pending.Push("");
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0,
                    ReturnSpecialDirectories = false, BufferSize = 64 * 1024
                };
                item.Prepare();
                long previousReport = 0;
                while (pending.TryPop(out var directory))
                {
                    token.ThrowIfCancellationRequested();
                    string? error = null;
                    try
                    {
                        var fullDirectory = Path.Combine(stableRoot, directory);
                        var parentKey = Catalog.Key(directory);
                        var parentTokens = Catalog.Prefixes("", parentKey).ToArray();
                        var parentTokenSet = parentTokens.ToHashSet(StringComparer.Ordinal);
                        if (directory.Length != 0 && (File.GetAttributes(fullDirectory) &
                            (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000)) != 0)
                        {
                            Cover(directory, "Directory changed to a reparse point or recall risk.", false);
                            continue;
                        }
                        var entries = new FileSystemEnumerable<Entry>(fullDirectory, (ref FileSystemEntry info) =>
                        {
                            string name = info.FileName.ToString();
                            return new(Path.Combine(directory, name), name, directory, info.Length,
                                info.LastWriteTimeUtc.ToUnixTimeMilliseconds(),
                                info.CreationTimeUtc.ToUnixTimeMilliseconds(), (int)info.Attributes, null);
                        }, options);
                        foreach (var entry in entries)
                        {
                            token.ThrowIfCancellationRequested();
                            if (excluded is not null &&
                                string.Equals(entry.Relative, excluded, StringComparison.OrdinalIgnoreCase))
                                continue;
                            var attrs = (FileAttributes)entry.Attributes;
                            bool isDirectory = attrs.HasFlag(FileAttributes.Directory);
                            bool risk = attrs.HasFlag(FileAttributes.ReparsePoint) || WindowsVolumes.HasRecallRisk(attrs);
                            if (isDirectory && !risk)
                            {
                                pending.Push(entry.Relative);
                            }
                            if (isDirectory && risk) Cover(entry.Relative, "Reparse point or recall risk. Traversal skipped.", false);
                            string? identity = null;
                            if (ntfs && !risk && !isDirectory)
                            {
                                var start = Stopwatch.GetTimestamp();
                                identity = WindowsVolumes.ReadFileKey(Path.Combine(stableRoot, entry.Relative));
                                identityTicks += Stopwatch.GetTimestamp() - start;
                                if (identity is null) identityMisses++;
                            }

                            var name = Catalog.Key(entry.Name);
                            var tokens = JsonSerializer.Serialize(parentTokens.Concat(
                                Catalog.Prefixes(name, "").Where(t => !parentTokenSet.Contains(t))));
                            object?[] values = [entry.Relative, entry.Name, name, parentKey,
                                isDirectory ? "" : Catalog.Key(Path.GetExtension(entry.Name).TrimStart('.')), isDirectory ? 0L : entry.Bytes,
                                entry.Modified, entry.Created, entry.Attributes, identity, tokens];
                            for (int i = 0; i < values.Length; i++)
                                item.Parameters[i].Value = values[i] ?? DBNull.Value;
                            item.ExecuteNonQuery();
                            count++;
                            if (count % 20000 == 0) Catalog.Execute(stage, "COMMIT; BEGIN IMMEDIATE;");
                            if (elapsed.ElapsedMilliseconds - previousReport >= 300)
                            {
                                Report(entry.Relative);
                                previousReport = elapsed.ElapsedMilliseconds;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        if (directory.Length == 0) throw;
                        CheckMount(volume);
                        error = ex.Message;
                    }
                    Cover(directory, error, error is null);
                    if (elapsed.ElapsedMilliseconds - previousReport >= 300)
                    {
                        Report(directory);
                        previousReport = elapsed.ElapsedMilliseconds;
                    }
                }
                Catalog.Execute(stage, "COMMIT;");

                void Cover(string directory, string? detail, bool complete)
                {
                    if (!complete) Issue(directory, detail!);
                    Catalog.Execute(stage, "INSERT OR REPLACE INTO coverage VALUES($path,$complete,$detail);",
                        ("path", directory), ("complete", complete), ("detail", detail));
                }
            }

            CheckMount(volume);
            token.ThrowIfCancellationRequested();
            Report(volume.Mount, "Saving catalog");
            Catalog.Execute(db, "PRAGMA temp_store=FILE; PRAGMA main.cache_size=-65536;");
            var finished = Catalog.Now();
            outcome = omitted == 0 ? "Complete" : "Complete with omissions";
            Catalog.Execute(db, "ATTACH DATABASE $path AS pending; BEGIN IMMEDIATE;",
                ("path", stagePath));
            token.ThrowIfCancellationRequested();
            Catalog.Execute(db, publish, ("volume", volume.Id), ("scan", scan), ("seen", finished));
            CheckMount(volume);
            token.ThrowIfCancellationRequested();
            Record(finished, true);
            Catalog.Execute(db, "COMMIT;");
            published = true;
            Report(volume.Mount, "Complete");
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled";
        }
        catch (SqliteException) when (token.IsCancellationRequested)
        {
            outcome = "Cancelled";
        }
        catch (Exception ex)
        {
            outcome = "Failed";
            Issue(volume.Mount, ex.Message);
        }
        finally
        {
            interrupt.Dispose();
            if (!published)
            {
                try { Catalog.Execute(db, "ROLLBACK;"); }
                catch (SqliteException) { }
                Record(Catalog.Now(), false);
            }
            db.Close();
            File.Delete(stagePath);
        }

        void Record(long finished, bool success)
        {
            Catalog.Execute(db, """
                UPDATE scans SET finished=$when,outcome=$outcome,items=$items,issues=$issues,
                 identity_ms=$identity WHERE id=$scan;
                UPDATE volumes SET succeeded=CASE WHEN $success THEN $when ELSE succeeded END,
                  outcome=$outcome,issues=$issues WHERE id=$volume;
                """, ("when", finished), ("outcome", outcome), ("items", count), ("issues", Summary()),
                ("identity", IdentityTime()), ("scan", scan), ("success", success), ("volume", volume.Id));
        }
    }

    static void CheckMount(Volume volume)
    {
        if (WindowsVolumes.Identity(volume.Mount) != volume.Id)
            throw new IOException("The selected volume was disconnected or its mount point changed.");
    }
}
