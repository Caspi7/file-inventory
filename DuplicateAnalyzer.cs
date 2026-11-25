using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace FileInventory;

record DuplicateOptions(long MinimumBytes = 1, bool IncludeOffline = true, bool IncludeStale = true);
record DuplicateFile(long Id, string Volume, string Rel, string Mount, bool Online, bool Stale,
    FileAttributes Attributes)
{
    public string FullPath => Path.Combine(Mount, Rel);
}
record DuplicateGroup(long Bytes, long Modified, long Created, IReadOnlyList<DuplicateFile> Files,
    string? FullHash = null);

static class DuplicateAnalyzer
{
    const int QuickBytes = 64 * 1024;

    public static List<DuplicateGroup> FindDuplicateGroups(Catalog catalog, DuplicateOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinimumBytes);
        using var db = catalog.Open();
        using var command = Catalog.Command(db, """
            WITH eligible AS NOT MATERIALIZED (
                SELECT f.id,f.volume,f.rel,f.bytes,f.modified,f.created,f.stale,f.attributes,v.mount,v.online
                FROM files f INDEXED BY files_copy_candidates JOIN volumes v ON v.id=f.volume
                WHERE f.bytes >= $minimum AND (f.attributes & 16)=0 AND f.removed IS NULL
                AND ($offline OR v.online=1) AND ($stale OR f.stale=0)
            ), candidates AS (
                SELECT bytes,modified,created FROM eligible GROUP BY bytes,modified,created HAVING count(*)>1
            )
            SELECT e.bytes,e.modified,e.created,e.id,e.volume,e.rel,e.mount,e.online,e.stale,e.attributes
            FROM candidates c JOIN eligible e USING(bytes,modified,created)
            ORDER BY e.bytes DESC,e.modified,e.created,e.volume,e.rel;
            """, ("minimum", options.MinimumBytes), ("offline", options.IncludeOffline), ("stale", options.IncludeStale));
        using var reader = command.ExecuteReader();
        var groups = new List<DuplicateGroup>();
        List<DuplicateFile>? files = null;
        (long Bytes, long Modified, long Created) previous = default;
        while (reader.Read())
        {
            var key = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
            if (files is null || key != previous)
            {
                files = [];
                groups.Add(new(key.Item1, key.Item2, key.Item3, files));
                previous = key;
            }
            files.Add(new(reader.GetInt64(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetBoolean(7), reader.GetBoolean(8), (FileAttributes)reader.GetInt32(9)));
        }
        return groups;
    }

    public static Task<List<DuplicateGroup>> VerifyGroupsAsync(Catalog catalog,
        IEnumerable<DuplicateGroup> groups, CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            using var db = catalog.Open();
            var verified = new List<DuplicateGroup>();
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var quickMatches = new List<(DuplicateFile File, string Hash)>();
                foreach (var file in group.Files.DistinctBy(file => file.Id))
                {
                    var hash = await HashAsync(db, group, file, full: false, cancellationToken).ConfigureAwait(false);
                    if (hash is not null) quickMatches.Add((file, hash));
                }

                foreach (var bucket in quickMatches.GroupBy(match => match.Hash).Where(bucket => bucket.Count() > 1))
                {
                    var fullMatches = new List<(DuplicateFile File, string Hash)>();
                    foreach (var (file, _) in bucket)
                    {
                        var hash = await HashAsync(db, group, file, full: true, cancellationToken).ConfigureAwait(false);
                        if (hash is not null) fullMatches.Add((file, hash));
                    }
                    foreach (var matches in fullMatches.GroupBy(match => match.Hash).Where(matches => matches.Count() > 1))
                        verified.Add(group with { Files = matches.Select(match => match.File).ToArray(), FullHash = matches.Key });
                }
            }
            return verified;
        }, cancellationToken);
    }

    static async Task<string?> HashAsync(SqliteConnection db, DuplicateGroup group, DuplicateFile file,
        bool full, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var args = new (string, object?)[] { ("id", file.Id), ("volume", file.Volume), ("rel", file.Rel),
            ("bytes", group.Bytes), ("modified", group.Modified), ("created", group.Created), ("mount", file.Mount) };
        const string current = """
            f.id=$id AND f.volume=$volume AND f.rel=$rel AND f.bytes=$bytes AND f.modified=$modified
            AND f.created=$created AND f.stale=0 AND f.removed IS NULL AND v.online=1 AND v.mount=$mount
            """;
        const string unchanged = " AND f.seen=$seen AND f.file_key IS $fileKey AND f.attributes=$attributes";
        long seen;
        int attributes;
        string? fileKey;
        byte[]? cached;
        using (var command = Catalog.Command(db, $"""
            SELECT f.attributes,f.seen,f.file_key,h.quick_hash,h.full_hash
            FROM files f JOIN volumes v ON v.id=f.volume LEFT JOIN file_hashes h
                ON h.file_id=f.id AND h.bytes=f.bytes AND h.modified=f.modified
            WHERE {current}
            """, args))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            attributes = reader.GetInt32(0);
            if (UnsafeAttributes((FileAttributes)attributes)) return null;
            seen = reader.GetInt64(1);
            fileKey = reader.IsDBNull(2) ? null : reader.GetString(2);
            int ordinal = full ? 4 : 3;
            cached = reader.IsDBNull(ordinal) ? null : (byte[])reader.GetValue(ordinal);
        }

        try
        {
            var volume = new Volume(file.Volume, file.Mount, "", true, false, 0, null, null, "", "");
            string path = WindowsVolumes.Extended(file.FullPath);
            if (!MatchesDisk()) return null;
            (string, object?)[] snapshot = [.. args, ("seen", seen), ("fileKey", fileKey), ("attributes", attributes)];
            if (cached is not null)
            {
                string column = full ? "full_hash" : "quick_hash";
                using var check = Catalog.Command(db, $"""
                    SELECT 1 FROM files f JOIN volumes v ON v.id=f.volume JOIN file_hashes h ON h.file_id=f.id
                    WHERE h.bytes=f.bytes AND h.modified=f.modified AND h.{column}=$cached
                    AND {current}{unchanged}
                    """, [.. snapshot, ("cached", cached)]);
                return check.ExecuteScalar() is null ? null : Convert.ToHexString(cached);
            }

            byte[] hash;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                QuickBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != group.Bytes) return null;
            if (full)
                hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            else
            {
                var buffer = new byte[(int)Math.Min(QuickBytes, group.Bytes)];
                await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
                hash = SHA256.HashData(buffer);
            }
            if (!MatchesDisk()) return null;

            token.ThrowIfCancellationRequested();
            using var save = Catalog.Command(db, $"""
                INSERT INTO file_hashes(file_id,bytes,modified,quick_hash,full_hash,hashed_at)
                SELECT f.id,f.bytes,f.modified,$quick,$full,$now FROM files f JOIN volumes v ON v.id=f.volume
                WHERE {current}{unchanged}
                ON CONFLICT(file_id) DO UPDATE SET bytes=excluded.bytes,modified=excluded.modified,
                  quick_hash=coalesce(excluded.quick_hash,CASE WHEN file_hashes.bytes=excluded.bytes
                      AND file_hashes.modified=excluded.modified THEN file_hashes.quick_hash END),
                  full_hash=coalesce(excluded.full_hash,CASE WHEN file_hashes.bytes=excluded.bytes
                      AND file_hashes.modified=excluded.modified THEN file_hashes.full_hash END),
                  hashed_at=excluded.hashed_at;
                """, [.. snapshot,
                    ("quick", full ? null : hash), ("full", full ? hash : null), ("now", Catalog.Now())]);
            return save.ExecuteNonQuery() == 1 ? Convert.ToHexString(hash) : null;

            bool MatchesDisk()
            {
                if (!WindowsVolumes.CanReveal(volume, file.Rel, file.FullPath)) return false;
                var info = new FileInfo(path);
                return !UnsafeAttributes(info.Attributes) && info.Length == group.Bytes
                    && new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() == group.Modified
                    && new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeMilliseconds() == group.Created
                    && (fileKey is null || WindowsVolumes.ReadFileKey(path) == fileKey);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    static bool UnsafeAttributes(FileAttributes attributes) =>
        (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
        || WindowsVolumes.HasRecallRisk(attributes);
}
