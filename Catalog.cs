using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace FileInventory;

record Volume(string Id, string Mount, string Label, bool Online, bool Enabled, double Hours,
    long? Attempted, long? Succeeded, string Outcome, string Issues,
    string Host = "", bool Removable = false, string Note = "", int FreshnessDays = 0)
{
    public bool IsOverdue(long nowMs) => FreshnessDays > 0
        && (Succeeded is null || nowMs >= Succeeded.Value + FreshnessDays * 86_400_000L);

    public string BaseLabel => Label == Mount ? Mount : $"{Label} ({Mount})";
    public string KindLabel => Removable ? "Removable" : "Fixed";

    public string DisplayName
    {
        get
        {
            var parts = new List<string>(3) { BaseLabel };
            if (Removable) parts.Add("Removable");
            if (!string.IsNullOrWhiteSpace(Host)) parts.Add(Host);
            return string.Join(" · ", parts);
        }
    }

    public string DisplayStatusLabel
    {
        get
        {
            string status = Id.StartsWith("import:", StringComparison.Ordinal) ? "Imported" : Online ? "" : "Offline";
            return status.Length == 0 ? DisplayName : $"{DisplayName} · {status}";
        }
    }

    public string DisplayLibraryLabel
    {
        get
        {
            var parts = new List<string>(4) { BaseLabel, KindLabel };
            if (!string.IsNullOrWhiteSpace(Host)) parts.Add(Host);
            parts.Add(Id.StartsWith("import:", StringComparison.Ordinal) ? "Imported" : Online ? "Online" : "Offline");
            return string.Join(" · ", parts);
        }
    }
}
record Entry(string Relative, string Name, string Parent, long Bytes, long Modified, long Created,
    int Attributes, string? FileKey);
record FolderStats(long SubtreeBytes, long SubtreeFiles, long SubtreeDirs);
record FileAnnotation(string Tags, string Note, long Updated);
enum ScanChangeKind { Added = 1, Modified, Removed, Moved }
record ScanChange(long ScanId, string Volume, string Rel, ScanChangeKind Kind, long? OldBytes, long? NewBytes);
record ScanSummary(long Id, string Volume, long Started, long? Finished, string Outcome, long Items,
    string Issues, long IdentityMs, long AddedCount, long ModifiedCount, long RemovedCount, long MovedCount,
    long BytesDelta);
record Hit(string Name, string FullPath, string RelativePath, string Volume, string Label, string Status,
    long Bytes, DateTime Modified, DateTime LastSeen, string Matched, bool CanReveal, bool IsDirectory = false);
record CatalogPage(int Total, List<Hit> Rows);
record FileMetadata(string Name, string FullPath, string Volume, string Status, long Bytes,
    DateTime Created, DateTime Modified, DateTime LastSeen, FileAttributes Attributes, bool CanReveal, DateTime? Removed);
record Filters(string? Volume = null, string? Extension = null, long? Min = null, long? Max = null,
    long? From = null, long? Through = null, bool? Online = null, IReadOnlyList<string>? Drives = null, bool IncludeRemoved = true);

sealed partial class Catalog
{
    public string DataDirectory { get; }
    readonly string database;
    static readonly string search = Resource("Search.sql");
    static readonly Regex words = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);

    public Catalog(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        database = Path.Combine(dataDirectory, "catalog.sqlite3");
        using var db = Open();
        Execute(db, Resource("Catalog.sql"));
        Execute(db, "BEGIN IMMEDIATE;");
        AddColumn(db, "files", "removed", "INTEGER");
        AddColumn(db, "volumes", "host", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "volumes", "removable", "INTEGER NOT NULL DEFAULT 0");
        AddColumn(db, "volumes", "note", "TEXT NOT NULL DEFAULT ''");
        AddColumn(db, "volumes", "freshness_days", "INTEGER NOT NULL DEFAULT 0");
        foreach (string column in new[] { "added_count", "modified_count", "removed_count", "moved_count", "bytes_delta" })
            AddColumn(db, "scans", column, "INTEGER NOT NULL DEFAULT 0");
        Execute(db, "COMMIT;");
        BackfillFolders(db);
        BackfillFolderStats(db);
    }

    static void AddColumn(SqliteConnection db, string table, string column, string definition)
    {
        using var check = Command(db, "SELECT 1 FROM pragma_table_info($table) WHERE name=$column;",
            ("table", table), ("column", column));
        if (check.ExecuteScalar() is null)
            Execute(db, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    public static string Resource(string name)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("FileInventory." + name)!;
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }

    public SqliteConnection Open(string? path = null)
    {
        var databasePath = path ?? database;
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Pooling = false, DefaultTimeout = 30
        }.ToString());
        db.Open();
        Execute(db, "PRAGMA temp_store=MEMORY; PRAGMA synchronous=NORMAL; PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456; PRAGMA busy_timeout=5000;");
        return db;
    }

    public static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue("$" + name, value ?? DBNull.Value);
        return command;
    }

    public static void Execute(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var command = Command(db, sql, args);
        command.ExecuteNonQuery();
    }

    public static string Key(string value)
    {
        try { return value.Normalize().ToLowerInvariant(); }
        catch (ArgumentException) { return value.ToLowerInvariant(); }
    }

    public static IEnumerable<string> Prefixes(string name, string parent)
    {
        return parent.Split('\\', StringSplitOptions.RemoveEmptyEntries).Append(name)
            .SelectMany(segment => words.Matches(segment).Select(match => match.Value).Prepend(segment))
            .SelectMany(word =>
            {
                var runes = word.EnumerateRunes().Take(2).Select(r => r.ToString()).ToArray();
                return runes.Length switch { 0 => [], 1 => runes, _ => new[] { runes[0], string.Concat(runes) } };
            }).Distinct(StringComparer.Ordinal);
    }

    public void Discover(IReadOnlyList<Volume> mounted)
    {
        using var db = Open();
        Execute(db, "BEGIN IMMEDIATE;");
        Execute(db, "UPDATE volumes SET online=0;");
        foreach (var volume in mounted)
        {
            string host = string.IsNullOrWhiteSpace(volume.Host) ? Environment.MachineName : volume.Host;
            Execute(db, """
                INSERT INTO volumes(id,mount,label,online,host,removable) VALUES($id,$mount,$label,1,$host,$removable)
                ON CONFLICT(id) DO UPDATE SET mount=excluded.mount,label=excluded.label,online=1,host=excluded.host,removable=excluded.removable;
                """, ("id", volume.Id), ("mount", volume.Mount), ("label", volume.Label),
                ("host", host), ("removable", volume.Removable ? 1 : 0));
        }
        Execute(db, "COMMIT;");
    }

    public List<Volume> Volumes()
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT id,mount,label,online,enabled,hours,attempted,succeeded,outcome,issues,host,removable,note,freshness_days
            FROM volumes ORDER BY online DESC,mount,label;
            """);
        using var reader = command.ExecuteReader();
        var volumes = new List<Volume>();
        while (reader.Read())
            volumes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetBoolean(4), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetBoolean(11), reader.GetString(12), reader.GetInt32(13)));
        return volumes;
    }

    public void UpdateVolumeMetadata(string id, string note, int freshnessDays)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(freshnessDays);
        using var db = Open();
        Execute(db, "UPDATE volumes SET note=$note,freshness_days=$days WHERE id=$id;",
            ("id", id), ("note", note), ("days", freshnessDays));
    }

    public FolderStats? GetFolderMetadata(string volume, string rel)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT subtree_bytes,subtree_files,subtree_dirs FROM folder_stats WHERE volume=$volume AND rel=$rel;
            """, ("volume", volume), ("rel", rel));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)) : null;
    }

    public List<ScanSummary> ScanHistory(string volume)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT id,volume,started,finished,outcome,items,issues,identity_ms,
                   added_count,modified_count,removed_count,moved_count,bytes_delta
            FROM scans WHERE volume=$volume ORDER BY id DESC LIMIT 20;
            """, ("volume", volume));
        using var reader = command.ExecuteReader();
        var scans = new List<ScanSummary>();
        while (reader.Read())
            scans.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), NullableLong(reader, 3),
                reader.GetString(4), reader.GetInt64(5), reader.GetString(6), reader.GetInt64(7),
                reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetInt64(12)));
        return scans;
    }

    public List<ScanChange> ScanChanges(long scanId)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT scan_id,volume,rel,kind,old_bytes,new_bytes FROM scan_changes WHERE scan_id=$scan ORDER BY kind,rel;
            """, ("scan", scanId));
        using var reader = command.ExecuteReader();
        var changes = new List<ScanChange>();
        while (reader.Read())
            changes.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                (ScanChangeKind)reader.GetInt32(3), NullableLong(reader, 4), NullableLong(reader, 5)));
        return changes;
    }

    public static void PruneScanHistory(SqliteConnection db, string volume)
    {
        Execute(db, """
            DELETE FROM scan_changes WHERE volume=$volume AND scan_id IN (
                SELECT id FROM scans WHERE volume=$volume AND finished IS NOT NULL ORDER BY id DESC LIMIT -1 OFFSET 20);
            DELETE FROM scan_coverage WHERE scan_id IN (
                SELECT id FROM scans WHERE volume=$volume AND finished IS NOT NULL ORDER BY id DESC LIMIT -1 OFFSET 20);
            DELETE FROM moves WHERE scan_id IN (
                SELECT id FROM scans WHERE volume=$volume AND finished IS NOT NULL ORDER BY id DESC LIMIT -1 OFFSET 20);
            DELETE FROM scans WHERE id IN (
                SELECT id FROM scans WHERE volume=$volume AND finished IS NOT NULL ORDER BY id DESC LIMIT -1 OFFSET 20);
            """, ("volume", volume));
    }

    static long? NullableLong(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    public FileAnnotation? GetAnnotation(string volume, string rel)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT tags,note,updated FROM file_annotations WHERE volume=$volume AND rel=$rel;
            """, ("volume", volume), ("rel", rel));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)) : null;
    }

    public void SetAnnotation(string volume, string rel, string tags, string note)
    {
        var indexedTags = tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Key).Distinct(StringComparer.Ordinal);
        using var db = Open();
        Execute(db, "BEGIN IMMEDIATE;");
        Execute(db, """
            INSERT INTO file_annotations(volume,rel,tags,note,updated) VALUES($volume,$rel,$tags,$note,$updated)
            ON CONFLICT(volume,rel) DO UPDATE SET tags=excluded.tags,note=excluded.note,updated=excluded.updated;
            DELETE FROM file_tags WHERE volume=$volume AND rel=$rel;
            INSERT INTO file_tags(tag,volume,rel) SELECT value,$volume,$rel FROM json_each($indexedTags);
            COMMIT;
            """, ("volume", volume), ("rel", rel), ("tags", tags), ("note", note), ("updated", Now()),
            ("indexedTags", JsonSerializer.Serialize(indexedTags)));
    }

    public void Configure(string id, bool enabled, double hours)
    {
        using var db = Open();
        Execute(db, "UPDATE volumes SET enabled=$on,hours=$hours WHERE id=$id;",
            ("on", enabled), ("hours", hours), ("id", id));
    }

    static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    public List<Hit> Find(string input, Filters filters) => QueryPage(input, filters, 0, 50).Rows;

    public FileMetadata? GetFileMetadata(string volume, string relative)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT f.name,v.mount,v.label,v.online,f.stale,f.bytes,f.created,f.modified,f.seen,f.attributes,f.removed
            FROM files f JOIN volumes v ON v.id=f.volume
            WHERE f.volume=$volume AND f.rel=$relative AND (f.attributes & 16)=0;
            """, ("volume", volume), ("relative", relative));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        bool online = reader.GetBoolean(3), stale = reader.GetBoolean(4);
        var attributes = (FileAttributes)reader.GetInt32(9);
        DateTime? removed = reader.IsDBNull(10) ? null : Local(reader.GetInt64(10));
        return new(reader.GetString(0), Path.Combine(reader.GetString(1), relative), reader.GetString(2),
            EntryStatus(online, stale, removed is not null),
            reader.GetInt64(5), Local(reader.GetInt64(6)), Local(reader.GetInt64(7)), Local(reader.GetInt64(8)),
            attributes, online && !stale && removed is null && !WindowsVolumes.HasRecallRisk(attributes)
                && !attributes.HasFlag(FileAttributes.ReparsePoint), removed);
    }

    public List<long> IndexSearch(string input, Filters filters, CancellationToken token)
    {
        var ids = new List<long>();
        QueryPage(input, filters, 0, 0, includeCount: false, cancellationToken: token, index: ids);
        return ids;
    }

    public List<Hit> ReadSearchPage(string input, IReadOnlyList<long> ids, int offset, int limit, CancellationToken token)
    {
        var pageIds = new long[Math.Min(limit, ids.Count - offset)];
        for (int i = 0; i < pageIds.Length; i++) pageIds[i] = ids[offset + i];
        return QueryPage(input, new Filters(), 0, limit, includeCount: false,
            cancellationToken: token, pageIds: pageIds).Rows;
    }

    public CatalogPage QueryPage(string input, Filters filters, int offset, int limit,
        string? parent = null, bool includeCount = true, CancellationToken cancellationToken = default,
        List<long>? index = null, long[]? pageIds = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string query = Key(input.Trim().Replace('/', '\\').Replace("\0", ""));
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        var shortTerms = terms.Where(t => t.EnumerateRunes().Count() < 3).ToArray();
        var longTerms = terms.Except(shortTerms).ToArray();
        string? match = longTerms.Length == 0 ? null
            : "name_key : (" + string.Join(" AND ", longTerms.Select(Quote)) + ")";
        using var db = Open();
        // Existing catalogs also index ancestor prefixes. Verify short candidates against the name.
        db.CreateFunction<string, bool>("matches_name_prefixes", name =>
        {
            var prefixes = Prefixes(name, "").ToHashSet(StringComparer.Ordinal);
            return shortTerms.All(prefixes.Contains);
        }, isDeterministic: true);
        using var interrupt = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        Execute(db, "BEGIN;");
        var args = new (string, object?)[] { ("query", query), ("match", match),
            ("firstShort", shortTerms.FirstOrDefault()), ("volume", filters.Volume),
            ("extension", filters.Extension is null ? null : Key(filters.Extension.TrimStart('.'))),
            ("min", filters.Min), ("max", filters.Max), ("modifiedFrom", filters.From),
            ("modifiedThrough", filters.Through), ("online", filters.Online) };
        if (pageIds is not null)
        {
            // Preserve positions if a scan removes or replaces a row after the search.
            var rowsByPosition = Read("""
                SELECT f.name,f.rel,v.mount,f.volume,v.label,v.online,f.stale,f.seen,f.bytes,f.modified,
                       f.attributes,0,0,f.removed
                FROM json_each($ids) i LEFT JOIN files f ON f.id=i.value LEFT JOIN volumes v ON v.id=f.volume
                ORDER BY i.key;
                """, ("ids", JsonSerializer.Serialize(pageIds)));
            return new(-1, rowsByPosition);
        }

        var sql = query.Length > 0 ? search : """
            SELECT name,rel,mount,volume,label,online,stale,seen,bytes,modified,attributes,3,0,removed
            FROM files f JOIN volumes v ON v.id=f.volume
            WHERE ($volume IS NULL OR f.volume=$volume)
              AND ($extension IS NULL OR extension=$extension)
              AND ($min IS NULL OR bytes >= $min) AND ($max IS NULL OR bytes <= $max)
              AND ($modifiedFrom IS NULL OR modified >= $modifiedFrom)
              AND ($modifiedThrough IS NULL OR modified < $modifiedThrough)
              AND ($online IS NULL OR v.online=$online)
              AND ($parent IS NULL OR parent_key=$parent)
            ORDER BY (attributes & 16) DESC,name_key,volume,rel LIMIT 50;
            """;
        sql = sql.Replace("WHERE ($volume IS NULL OR f.volume=$volume)",
            "WHERE ($volume IS NULL OR f.volume=$volume)" +
            (filters.Drives is null ? "" : " AND f.volume IN (SELECT value FROM json_each($drives))") +
            (filters.IncludeRemoved ? "" : " AND f.removed IS NULL"), StringComparison.Ordinal);
        // Concrete predicates allow SQLite to seek the volume/parent indexes.
        foreach (var (optional, predicate, enabled) in new[] {
            ("($volume IS NULL OR f.volume=$volume)", "f.volume=$volume", filters.Volume is not null),
            ("($extension IS NULL OR extension=$extension)", "extension=$extension", filters.Extension is not null),
            ("($min IS NULL OR bytes >= $min)", "bytes >= $min", filters.Min is not null),
            ("($max IS NULL OR bytes <= $max)", "bytes <= $max", filters.Max is not null),
            ("($modifiedFrom IS NULL OR modified >= $modifiedFrom)", "modified >= $modifiedFrom", filters.From is not null),
            ("($modifiedThrough IS NULL OR modified < $modifiedThrough)", "modified < $modifiedThrough", filters.Through is not null),
            ("($online IS NULL OR v.online=$online)", "v.online=$online", filters.Online is not null),
            ("($parent IS NULL OR parent_key=$parent)", "parent_key=$parent", parent is not null) })
            sql = sql.Replace(optional, enabled ? predicate : "1", StringComparison.Ordinal);
        args = [.. args, ("parent", parent is null ? null : Key(parent)),
            ("limit", limit), ("offset", offset), ("drives", JsonSerializer.Serialize(filters.Drives))];
        if (index is not null)
        {
            int projection = sql.LastIndexOf("SELECT ", StringComparison.Ordinal);
            int fromClause = sql.IndexOf("FROM ", projection, StringComparison.Ordinal);
            string orderedIds = sql[..projection] + (query.Length > 0 ? "SELECT id " : "SELECT f.id ") + sql[fromClause..sql.LastIndexOf("LIMIT", StringComparison.Ordinal)];
            using var command = Command(db, orderedIds, args);
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                index.Add(reader.GetInt64(0));
            }
            return new(index.Count, []);
        }

        int total = -1;
        if (includeCount)
        {
            var unpaged = sql[..sql.LastIndexOf("ORDER BY", StringComparison.Ordinal)];
            using var count = Command(db, "SELECT count(*) FROM (" + unpaged + ");", args);
            cancellationToken.ThrowIfCancellationRequested();
            total = Convert.ToInt32(count.ExecuteScalar());
        }
        var rows = Read(sql.Replace("LIMIT 50;", "LIMIT $limit OFFSET $offset;", StringComparison.Ordinal));
        return new(total, rows);

        List<Hit> Read(string sql, params (string, object?)[] extra)
        {
            using var command = Command(db, sql, [.. args, .. extra]);
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            var rows = new List<Hit>();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.IsDBNull(0)) { rows.Add(null!); continue; }
                var name = reader.GetString(0);
                var rel = reader.GetString(1);
                var online = reader.GetBoolean(5);
                var stale = reader.GetBoolean(6);
                var attributes = (FileAttributes)reader.GetInt32(10);
                bool removed = !reader.IsDBNull(13);
                var evidence = terms.Select(term => $"{term} → {name}");
                rows.Add(new(name, Path.Combine(reader.GetString(2), rel), rel, reader.GetString(3), reader.GetString(4),
                    EntryStatus(online, stale, removed),
                    reader.GetInt64(8), Local(reader.GetInt64(9)), Local(reader.GetInt64(7)),
                    string.Join("; ", evidence), online && !stale && !removed &&
                        !WindowsVolumes.HasRecallRisk(attributes) && !attributes.HasFlag(FileAttributes.ReparsePoint),
                    attributes.HasFlag(FileAttributes.Directory)));
            }
            return rows;
        }
    }

    static void BackfillFolders(SqliteConnection db)
    {
        using (var check = Command(db, "SELECT value FROM catalog_meta WHERE key='folders-v1';"))
            if (check.ExecuteScalar() is not null) return;
        var folders = new Dictionary<(string Volume, string Relative), (long Seen, long Modified)>();
        using (var command = Command(db, "SELECT volume,rel,seen,modified FROM files;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var relative = Path.GetDirectoryName(reader.GetString(1));
                while (!string.IsNullOrEmpty(relative))
                {
                    folders.TryAdd((reader.GetString(0), relative), (reader.GetInt64(2), reader.GetInt64(3)));
                    relative = Path.GetDirectoryName(relative);
                }
            }
        Execute(db, "BEGIN IMMEDIATE;");
        try
        {
            foreach (var (key, stamps) in folders)
            {
                Execute(db, """
                    INSERT OR IGNORE INTO files(volume,rel,name,name_key,parent_key,extension,
                        bytes,modified,created,attributes,seen)
                    VALUES($v,$r,$n,$k,$p,'',0,$m,$m,16,$s);
                    """, ("v", key.Volume), ("r", key.Relative), ("n", Path.GetFileName(key.Relative)),
                    ("k", Key(Path.GetFileName(key.Relative))), ("p", Key(Path.GetDirectoryName(key.Relative) ?? "")),
                    ("m", stamps.Modified), ("s", stamps.Seen));
                foreach (var token in Prefixes(Key(Path.GetFileName(key.Relative)),
                    Key(Path.GetDirectoryName(key.Relative) ?? "")))
                    Execute(db, """
                        INSERT OR IGNORE INTO prefixes(token,file_id)
                        SELECT $token,id FROM files WHERE volume=$v AND rel=$r;
                        """, ("token", token), ("v", key.Volume), ("r", key.Relative));
            }
            Execute(db, "INSERT OR IGNORE INTO catalog_meta VALUES('folders-v1','done'); COMMIT;");
        }
        catch { Execute(db, "ROLLBACK;"); throw; }
    }

    static void BackfillFolderStats(SqliteConnection db)
    {
        using var check = Command(db, "SELECT 1 FROM catalog_meta WHERE key='folder-stats-v1';");
        if (check.ExecuteScalar() is not null) return;
        Execute(db, "BEGIN IMMEDIATE;");
        if (check.ExecuteScalar() is null)
        {
            var volumes = new List<string>();
            using (var command = Command(db, "SELECT id FROM volumes;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) volumes.Add(reader.GetString(0));
            foreach (string volume in volumes) RebuildFolderStats(db, volume);
            Execute(db, "INSERT INTO catalog_meta VALUES('folder-stats-v1','done');");
        }
        Execute(db, "COMMIT;");
    }

    // The caller holds the write transaction for the snapshot being summarized.
    public static void RebuildFolderStats(SqliteConnection db, string volume)
    {
        db.CreateFunction<string, string>("folder_parent", rel => rel[..Math.Max(0, rel.LastIndexOf('\\'))],
            isDeterministic: true);
        Execute(db, """
            DELETE FROM folder_stats WHERE volume=$volume;
            WITH RECURSIVE direct(rel,bytes,files,dirs) AS (
                SELECT folder_parent(rel),sum(CASE WHEN (attributes & 16)=0 THEN bytes ELSE 0 END),
                       sum((attributes & 16)=0),sum((attributes & 16)<>0)
                FROM files WHERE volume=$volume AND removed IS NULL GROUP BY folder_parent(rel)
            ), ancestors(rel,bytes,files,dirs) AS (
                SELECT rel,bytes,files,dirs FROM direct
                UNION ALL
                SELECT folder_parent(rel),bytes,files,dirs FROM ancestors WHERE rel<>''
            ), totals(rel,bytes,files,dirs) AS (
                SELECT * FROM ancestors
                UNION ALL SELECT rel,0,0,0 FROM files
                    WHERE volume=$volume AND removed IS NULL AND (attributes & 16)<>0
                UNION ALL SELECT '',0,0,0
            )
            INSERT INTO folder_stats(volume,rel,subtree_bytes,subtree_files,subtree_dirs)
            SELECT $volume,rel,sum(bytes),sum(files),sum(dirs) FROM totals GROUP BY rel;
            """, ("volume", volume));
    }

    public List<string> CopyCandidates(Hit hit)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT v.mount,c.rel,v.online,c.stale
            FROM files selected
            JOIN files c ON c.bytes=selected.bytes AND c.modified=selected.modified AND c.created=selected.created
            JOIN volumes v ON v.id=c.volume
            WHERE selected.volume=$volume AND selected.rel=$rel AND selected.bytes>0
              AND c.volume<>selected.volume AND c.removed IS NULL AND selected.removed IS NULL
            ORDER BY c.stale,v.online DESC,c.id LIMIT 20;
            """, ("volume", hit.Volume), ("rel", hit.RelativePath));
        using var reader = command.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read())
        {
            string status = reader.GetBoolean(2) ? "online" : "offline";
            if (reader.GetBoolean(3)) status += ", stale";
            paths.Add($"{Path.Combine(reader.GetString(0), reader.GetString(1))} ({status})");
        }
        return paths;
    }

    static string EntryStatus(bool online, bool stale, bool removed) => removed ? "Removed" :
        online ? stale ? "Stale" : "Online" : stale ? "Offline · stale" : "Offline";

    public static DateTime Local(long stamp) => DateTimeOffset.FromUnixTimeMilliseconds(stamp).LocalDateTime;
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
