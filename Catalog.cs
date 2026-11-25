using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace FileInventory;

record Volume(string Id, string Mount, string Label, bool Online, bool Enabled, double Hours,
    long? Attempted, long? Succeeded, string Outcome, string Issues);
record Entry(string Relative, string Name, string Parent, long Bytes, long Modified, long Created,
    int Attributes, string? FileKey);
record Hit(string Name, string FullPath, string RelativePath, string Volume, string Label, string Status,
    long Bytes, DateTime Modified, DateTime LastSeen, string Matched, bool CanReveal, bool IsDirectory = false);
record CatalogPage(int Total, List<Hit> Rows);
record FileMetadata(string Name, string FullPath, string Volume, string Status, long Bytes,
    DateTime Created, DateTime Modified, DateTime LastSeen, FileAttributes Attributes, bool CanReveal);
record Filters(string? Volume = null, string? Extension = null, long? Min = null, long? Max = null,
    long? From = null, long? Through = null, bool? Online = null);

sealed class Catalog
{
    public string DataDirectory { get; }
    readonly string database;
    readonly Dictionary<string, string[]> aliases;
    static readonly string search = Resource("Search.sql");
    static readonly string prefixSearch = Resource("PrefixSearch.sql");
    static readonly string longSearch = Resource("LongTerms.sql");
    static readonly Regex words = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);

    public Catalog(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
        database = Path.Combine(dataDirectory, "catalog.sqlite3");
        using var db = Open();
        Execute(db, Resource("Catalog.sql"));
        BackfillFolders(db);
        var config = Path.Combine(dataDirectory, "aliases.json");
        if (!File.Exists(config))
            File.WriteAllText(config, "{\n  \"hangar\": [\"hangaar\"],\n  \"hangaar\": [\"hangar\"]\n}\n");
        var configured = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(config))
            ?? new();
        aliases = configured.ToDictionary(pair => Key(pair.Key), pair => pair.Value
            .Select(Key).Where(word => word.EnumerateRunes().Count() >= 3).Distinct().ToArray());
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
            DataSource = databasePath, Pooling = true, DefaultTimeout = 30
        }.ToString());
        db.Open();
        Execute(db, "PRAGMA temp_store=MEMORY;");
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
            Execute(db, """
                INSERT INTO volumes(id,mount,label,online) VALUES($id,$mount,$label,1)
                ON CONFLICT(id) DO UPDATE SET mount=excluded.mount,label=excluded.label,online=1;
                """, ("id", volume.Id), ("mount", volume.Mount), ("label", volume.Label));
        Execute(db, "COMMIT;");
    }

    public List<Volume> Volumes()
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT id,mount,label,online,enabled,hours,attempted,succeeded,outcome,issues
            FROM volumes ORDER BY online DESC,mount,label;
            """);
        using var reader = command.ExecuteReader();
        var volumes = new List<Volume>();
        while (reader.Read())
            volumes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                reader.GetBoolean(4), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.GetString(8), reader.GetString(9)));
        return volumes;
    }

    public void Configure(string id, bool enabled, double hours)
    {
        using var db = Open();
        Execute(db, "UPDATE volumes SET enabled=$on,hours=$hours WHERE id=$id;",
            ("on", enabled), ("hours", hours), ("id", id));
    }

    string[] Alternatives(string term) => aliases.TryGetValue(term, out var values) ? values : [];
    static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    public List<Hit> Find(string input, Filters filters) => QueryPage(input, filters, 0, 50).Rows;

    public FileMetadata? GetFileMetadata(string volume, string relative)
    {
        using var db = Open();
        using var command = Command(db, """
            SELECT f.name,v.mount,v.label,v.online,f.stale,f.bytes,f.created,f.modified,f.seen,f.attributes
            FROM files f JOIN volumes v ON v.id=f.volume
            WHERE f.volume=$volume AND f.rel=$relative AND (f.attributes & 16)=0;
            """, ("volume", volume), ("relative", relative));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        bool online = reader.GetBoolean(3), stale = reader.GetBoolean(4);
        var attributes = (FileAttributes)reader.GetInt32(9);
        return new(reader.GetString(0), Path.Combine(reader.GetString(1), relative), reader.GetString(2),
            online ? stale ? "Stale" : "Online" : stale ? "Offline · stale" : "Offline",
            reader.GetInt64(5), Local(reader.GetInt64(6)), Local(reader.GetInt64(7)), Local(reader.GetInt64(8)),
            attributes, online && !stale && !WindowsVolumes.HasRecallRisk(attributes)
                && !attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    public CatalogPage QueryPage(string input, Filters filters, int offset, int limit,
        string? parent = null, bool includeCount = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string query = Key(input.Trim().Replace('/', '\\').Replace("\0", ""));
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Distinct().ToArray();
        var shortTerms = terms.Where(t => t.EnumerateRunes().Count() < 3).ToArray();
        var longTerms = terms.Except(shortTerms).ToArray();
        string? match = longTerms.Length == 0 ? null : string.Join(" AND ", longTerms.Select(term =>
        {
            var variants = Alternatives(term);
            return variants.Length == 0 ? Quote(term) :
                "(" + Quote(term) + " OR parent_key : (" + string.Join(" OR ", variants.Select(Quote)) + "))";
        }));
        using var db = Open();
        using var interrupt = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(db.Handle));
        Execute(db, "BEGIN;");
        var args = new (string, object?)[] { ("query", query), ("end", query + char.ConvertFromUtf32(0x10ffff)),
            ("long", query.EnumerateRunes().Count() >= 3), ("match", match),
            ("firstShort", shortTerms.FirstOrDefault()), ("short", JsonSerializer.Serialize(shortTerms)),
            ("terms", JsonSerializer.Serialize(terms)), ("volume", filters.Volume),
            ("extension", filters.Extension is null ? null : Key(filters.Extension.TrimStart('.'))),
            ("min", filters.Min), ("max", filters.Max), ("modifiedFrom", filters.From),
            ("modifiedThrough", filters.Through), ("online", filters.Online) };
        var sql = query.Length > 0 ? search : """
            SELECT name,rel,mount,volume,label,online,stale,seen,bytes,modified,attributes,3,0
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
        if (query.Length > 0)
        {
            // FTS already includes name and ancestor matches; avoid UNIONing duplicate
            // exact/prefix candidates and sorting a second million-row candidate set.
            int ranked = sql.IndexOf(", ranked AS", StringComparison.Ordinal);
            string candidates = longTerms.Length > 0
                ? "WITH candidates AS (SELECT rowid AS id FROM fragments WHERE fragments MATCH $match)"
                : "WITH candidates AS (SELECT file_id AS id FROM prefixes WHERE token=$firstShort)";
            sql = candidates + sql[ranked..];
        }
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
            ("limit", limit), ("offset", offset)];
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
                var name = reader.GetString(0);
                var rel = reader.GetString(1);
                var online = reader.GetBoolean(5);
                var stale = reader.GetBoolean(6);
                var attributes = (FileAttributes)reader.GetInt32(10);
                var directory = Path.GetDirectoryName(rel) ?? "";
                var evidence = new List<string>();
                foreach (var term in terms)
                {
                    bool Matches(string value) => term.EnumerateRunes().Count() < 3
                        ? Prefixes(Key(value), "").Contains(term)
                        : Key(value).Contains(term, StringComparison.Ordinal);
                    if (Matches(name)) evidence.Add($"{term} → {name}");
                    foreach (var segment in directory.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (Matches(segment)) evidence.Add($"{term} → {segment}");
                        else foreach (var alias in Alternatives(term))
                            if (Key(segment).Contains(alias, StringComparison.Ordinal))
                                evidence.Add($"{term} → {segment} (alias {alias})");
                    }
                }
                rows.Add(new(name, Path.Combine(reader.GetString(2), rel), rel, reader.GetString(3), reader.GetString(4),
                    online ? stale ? "Stale" : "Online" : stale ? "Offline · stale" : "Offline",
                    reader.GetInt64(8), Local(reader.GetInt64(9)), Local(reader.GetInt64(7)),
                    string.Join("; ", evidence.Distinct()), online && !stale &&
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
            Execute(db, "INSERT INTO catalog_meta VALUES('folders-v1','done'); COMMIT;");
        }
        catch { Execute(db, "ROLLBACK;"); throw; }
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
              AND c.volume<>selected.volume
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

    public static DateTime Local(long stamp) => DateTimeOffset.FromUnixTimeMilliseconds(stamp).LocalDateTime;
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
