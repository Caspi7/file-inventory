using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace FileInventory;

record ScanProgress(long Entries, long Omitted, string Directory,
    double ElapsedSeconds = 0, long? ExpectedEntries = null, string Phase = "Scanning");

sealed class Scanner(Catalog catalog)
{
    readonly record struct StagedEntry(
        string Relative, string Name, string NameKey, string ParentKey, string Extension,
        long Bytes, long Modified, long Created, int Attributes, string? FileKey, string Tokens);

    readonly record struct CoverageUpdate(string Directory, string? Detail, bool Complete);

    sealed class WorkBatch(List<StagedEntry>? entries, List<CoverageUpdate>? coverages, string directory)
    {
        public List<StagedEntry>? Entries { get; } = entries;
        public List<CoverageUpdate>? Coverages { get; } = coverages;
        public string Directory { get; } = directory;
    }

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
        var issueLock = new object();
        long count = 0, omitted = 0, identityTicks = 0, identityMisses = 0;
        var elapsed = Stopwatch.StartNew();
        string outcome = "Failed";
        bool published = false;
        void Report(string path, string phase = "Scanning") =>
            progress.Report(new(Volatile.Read(ref count), Volatile.Read(ref omitted), path,
                elapsed.Elapsed.TotalSeconds, expected, phase));
        Report(volume.Mount, "Preparing scan");

        void Issue(string path, string message)
        {
            lock (issueLock)
            {
                omitted++;
                if (samples.Count < 40) samples.Add($"{path}: {message}");
            }
        }

        string Summary()
        {
            lock (issueLock)
            {
                return $"Scanned {Volatile.Read(ref count):N0} entries in {elapsed.Elapsed.TotalSeconds:N1}s. "
                    + $"Omitted areas: {omitted:N0}. NTFS identity time: {IdentityTime()} ms. "
                    + $"Identity unavailable: {Volatile.Read(ref identityMisses):N0}."
                    + (samples.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, samples));
            }
        }
        long IdentityTime() => (long)(Volatile.Read(ref identityTicks) * 1000.0 / Stopwatch.Frequency);

        try
        {
            token.ThrowIfCancellationRequested();
            CheckMount(volume);
            var stableRoot = volume.Id.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                ? volume.Id : WindowsVolumes.Extended(volume.Mount);
            var excluded = WindowsVolumes.ExcludedRelative(volume, catalog.DataDirectory);
            var ntfs = WindowsVolumes.IsNtfs(volume.Mount);
            ulong? volumeSerial = ntfs ? WindowsVolumes.ReadVolumeSerial(stableRoot) : null;
            int useNtfsFastPath = (ntfs && volumeSerial.HasValue) ? 1 : 0;

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
                using var cover = Catalog.Command(stage,
                    "INSERT OR REPLACE INTO coverage VALUES($path,$complete,$detail);",
                    ("path", null), ("complete", 0), ("detail", null));
                item.Prepare();
                cover.Prepare();

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0,
                    ReturnSpecialDirectories = false, BufferSize = 64 * 1024
                };

                var dirQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
                {
                    SingleReader = false, SingleWriter = false
                });
                var results = Channel.CreateBounded<WorkBatch>(new BoundedChannelOptions(4096)
                {
                    SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
                });

                int pendingDirs = 1;
                dirQueue.Writer.TryWrite("");

                using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                var scanToken = scanCts.Token;
                Exception? fatalError = null;
                void Fail(Exception ex)
                {
                    Interlocked.CompareExchange(ref fatalError, ex, null);
                    try { scanCts.Cancel(); } catch (ObjectDisposedException) { }
                }

                int workerCount = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
                var workers = new Task[workerCount];
                for (int w = 0; w < workerCount; w++)
                {
                    workers[w] = Task.Run(async () =>
                    {
                        var buffer = new byte[64 * 1024];
                        const int BatchSize = 512;
                        try
                        {
                            await foreach (var directory in dirQueue.Reader.ReadAllAsync(scanToken))
                            {
                                try
                                {
                                    scanToken.ThrowIfCancellationRequested();
                                    string? error = null;
                                    var batch = new List<StagedEntry>(64);
                                    List<CoverageUpdate>? coverages = null;
                                    bool skippedDir = false;

                                    void AddCoverage(string dir, string? detail, bool complete)
                                    {
                                        coverages ??= new List<CoverageUpdate>(4);
                                        coverages.Add(new(dir, detail, complete));
                                    }

                                    try
                                    {
                                        var fullDirectory = Path.Combine(stableRoot, directory);
                                        var parentKey = Catalog.Key(directory);
                                        var parentTokens = Catalog.Prefixes("", parentKey).ToArray();
                                        var parentTokenSet = parentTokens.ToHashSet(StringComparer.Ordinal);
                                        if (directory.Length != 0 && (File.GetAttributes(fullDirectory) &
                                            (FileAttributes.ReparsePoint | FileAttributes.Offline | (FileAttributes)0x440000)) != 0)
                                        {
                                            AddCoverage(directory, "Directory changed to a reparse point or recall risk.", false);
                                            skippedDir = true;
                                        }
                                        else
                                        {
                                            void ProcessEntry(Entry entry, bool usedFastPath)
                                            {
                                                scanToken.ThrowIfCancellationRequested();
                                                if (excluded is not null &&
                                                    string.Equals(entry.Relative, excluded, StringComparison.OrdinalIgnoreCase))
                                                    return;
                                                var attrs = (FileAttributes)entry.Attributes;
                                                bool isDirectory = attrs.HasFlag(FileAttributes.Directory);
                                                bool risk = attrs.HasFlag(FileAttributes.ReparsePoint) || WindowsVolumes.HasRecallRisk(attrs);
                                                if (isDirectory && !risk)
                                                {
                                                    Interlocked.Increment(ref pendingDirs);
                                                    dirQueue.Writer.TryWrite(entry.Relative);
                                                }
                                                if (isDirectory && risk)
                                                    AddCoverage(entry.Relative, "Reparse point or recall risk. Traversal skipped.", false);

                                                string? identity = entry.FileKey;
                                                if (ntfs && !risk && !isDirectory)
                                                {
                                                    if (!usedFastPath)
                                                    {
                                                        var start = Stopwatch.GetTimestamp();
                                                        identity = WindowsVolumes.ReadFileKey(Path.Combine(stableRoot, entry.Relative));
                                                        Interlocked.Add(ref identityTicks, Stopwatch.GetTimestamp() - start);
                                                    }
                                                    if (identity is null) Interlocked.Increment(ref identityMisses);
                                                }

                                                var name = Catalog.Key(entry.Name);
                                                var tokens = JsonSerializer.Serialize(parentTokens.Concat(
                                                    Catalog.Prefixes(name, "").Where(t => !parentTokenSet.Contains(t))));
                                                batch.Add(new StagedEntry(
                                                    entry.Relative,
                                                    entry.Name,
                                                    name,
                                                    parentKey,
                                                    isDirectory ? "" : Catalog.Key(Path.GetExtension(entry.Name).TrimStart('.')),
                                                    isDirectory ? 0L : entry.Bytes,
                                                    entry.Modified,
                                                    entry.Created,
                                                    entry.Attributes,
                                                    identity,
                                                    tokens));
                                            }

                                            bool enumeratedFast = false;
                                            if (Volatile.Read(ref useNtfsFastPath) != 0)
                                            {
                                                enumeratedFast = WindowsVolumes.TryEnumerateNtfsDirectory(
                                                    fullDirectory, directory, volumeSerial!.Value, buffer,
                                                    e =>
                                                    {
                                                        ProcessEntry(e, usedFastPath: true);
                                                        if (batch.Count >= BatchSize)
                                                        {
                                                            var toSend = batch;
                                                            batch = new List<StagedEntry>(BatchSize);
                                                            if (!results.Writer.TryWrite(new WorkBatch(toSend, null, directory)))
                                                                results.Writer.WriteAsync(new WorkBatch(toSend, null, directory), scanToken)
                                                                    .AsTask().GetAwaiter().GetResult();
                                                        }
                                                    }, scanToken);
                                                if (!enumeratedFast)
                                                    Volatile.Write(ref useNtfsFastPath, 0);
                                            }

                                            if (!enumeratedFast)
                                            {
                                                var entries = new FileSystemEnumerable<Entry>(fullDirectory, (ref FileSystemEntry info) =>
                                                {
                                                    string name = info.FileName.ToString();
                                                    return new(Path.Combine(directory, name), name, directory, info.Length,
                                                        info.LastWriteTimeUtc.ToUnixTimeMilliseconds(),
                                                        info.CreationTimeUtc.ToUnixTimeMilliseconds(), (int)info.Attributes, null);
                                                }, options);
                                                foreach (var entry in entries)
                                                {
                                                    ProcessEntry(entry, usedFastPath: false);
                                                    if (batch.Count >= BatchSize)
                                                    {
                                                        var toSend = batch;
                                                        batch = new List<StagedEntry>(BatchSize);
                                                        await results.Writer.WriteAsync(new WorkBatch(toSend, null, directory), scanToken);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                    {
                                        if (directory.Length == 0) throw;
                                        CheckMount(volume);
                                        error = ex.Message;
                                    }

                                    if (!skippedDir)
                                        AddCoverage(directory, error, error is null);

                                    if (batch.Count > 0 || coverages is not null)
                                        await results.Writer.WriteAsync(
                                            new WorkBatch(batch.Count > 0 ? batch : null, coverages, directory), scanToken);
                                }
                                finally
                                {
                                    if (Interlocked.Decrement(ref pendingDirs) == 0)
                                        dirQueue.Writer.TryComplete();
                                }
                            }
                        }
                        catch (OperationCanceledException) when (scanToken.IsCancellationRequested)
                        {
                        }
                        catch (Exception ex)
                        {
                            Fail(ex);
                        }
                    });
                }

                var workersDone = Task.WhenAll(workers).ContinueWith(_ => results.Writer.TryComplete(),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                long previousReport = 0;
                try
                {
                    while (results.Reader.WaitToReadAsync(scanToken).AsTask().GetAwaiter().GetResult())
                    {
                        while (results.Reader.TryRead(out var work))
                        {
                            scanToken.ThrowIfCancellationRequested();
                            if (work.Entries is not null)
                            {
                                foreach (var e in work.Entries)
                                {
                                    item.Parameters[0].Value = e.Relative;
                                    item.Parameters[1].Value = e.Name;
                                    item.Parameters[2].Value = e.NameKey;
                                    item.Parameters[3].Value = e.ParentKey;
                                    item.Parameters[4].Value = e.Extension;
                                    item.Parameters[5].Value = e.Bytes;
                                    item.Parameters[6].Value = e.Modified;
                                    item.Parameters[7].Value = e.Created;
                                    item.Parameters[8].Value = e.Attributes;
                                    item.Parameters[9].Value = (object?)e.FileKey ?? DBNull.Value;
                                    item.Parameters[10].Value = e.Tokens;
                                    item.ExecuteNonQuery();
                                    count++;
                                    if (count % 20000 == 0) Catalog.Execute(stage, "COMMIT; BEGIN IMMEDIATE;");
                                    if (elapsed.ElapsedMilliseconds - previousReport >= 300)
                                    {
                                        Report(e.Relative);
                                        previousReport = elapsed.ElapsedMilliseconds;
                                    }
                                }
                            }
                            if (work.Coverages is not null)
                            {
                                foreach (var c in work.Coverages)
                                {
                                    if (!c.Complete) Issue(c.Directory, c.Detail!);
                                    cover.Parameters[0].Value = c.Directory;
                                    cover.Parameters[1].Value = c.Complete ? 1 : 0;
                                    cover.Parameters[2].Value = (object?)c.Detail ?? DBNull.Value;
                                    cover.ExecuteNonQuery();
                                }
                            }
                            if (elapsed.ElapsedMilliseconds - previousReport >= 300)
                            {
                                Report(work.Directory);
                                previousReport = elapsed.ElapsedMilliseconds;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (scanToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }

                workersDone.GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
                if (fatalError is not null)
                    ExceptionDispatchInfo.Capture(fatalError).Throw();

                Catalog.Execute(stage, "COMMIT;");
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
            using var prior = Catalog.Command(db, "SELECT succeeded IS NOT NULL FROM volumes WHERE id=$volume;",
                ("volume", volume.Id));
            bool hasPriorScan = prior.ExecuteScalar() is 1L;
            Catalog.Execute(db, publish, ("volume", volume.Id), ("scan", scan), ("seen", finished),
                ("hasPriorScan", hasPriorScan));
            Catalog.RebuildFolderStats(db, volume.Id);
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
                Catalog.Execute(db, "BEGIN IMMEDIATE;");
                Record(Catalog.Now(), false);
                Catalog.Execute(db, "COMMIT;");
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
            Catalog.PruneScanHistory(db, volume.Id);
        }
    }

    static void CheckMount(Volume volume)
    {
        if (WindowsVolumes.Identity(volume.Mount) != volume.Id)
            throw new IOException("The selected volume was disconnected or its mount point changed.");
    }
}
