using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileInventory;

sealed partial class Catalog
{
    const string ImportedPrefix = "import:";
    const string ArchiveFormat = "FileInventory";
    static readonly UTF8Encoding ArchiveEncoding = new(false, true);
    sealed record ArchiveHeader(string Format, int Version, string Id, string Label, string Mount,
        string? Host = null, bool Removable = false, string? Note = null);

    public void ExportInventory(string drive, string destination)
    {
        using var db = Open();
        Execute(db, "INSERT OR IGNORE INTO catalog_meta VALUES('archive-origin',lower(hex(randomblob(16))));");
        Execute(db, "BEGIN;");
        ArchiveHeader header;
        using (var command = Command(db, """
            SELECT label,mount,host,removable,(SELECT value FROM catalog_meta WHERE key='archive-origin'),note
            FROM volumes WHERE id=$drive AND (succeeded IS NOT NULL OR id LIKE 'import:%');
            """, ("drive", drive)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Scan this drive before exporting it.");
            string host = reader.GetString(2);
            if (string.IsNullOrWhiteSpace(host)) host = Environment.MachineName;
            bool removable = reader.GetBoolean(3);
            string id = drive.StartsWith(ImportedPrefix, StringComparison.Ordinal) ? drive[ImportedPrefix.Length..]
                : Convert.ToHexString(SHA256.HashData(ArchiveEncoding.GetBytes(reader.GetString(4) + drive)));
            header = new(ArchiveFormat, 1, id, reader.GetString(0), reader.GetString(1), host, removable, reader.GetString(5));
        }

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
            using (var writer = new StreamWriter(gzip, ArchiveEncoding))
            using (var command = Command(db, """
                SELECT rel,bytes,created,modified,attributes,seen,stale,removed
                FROM files WHERE volume=$drive ORDER BY rel;
                """, ("drive", drive)))
            using (var reader = command.ExecuteReader())
            {
                writer.WriteLine(JsonSerializer.Serialize(header));
                long count = 0;
                var values = new object[8];
                while (reader.Read())
                {
                    reader.GetValues(values);
                    for (int i = 0; i < values.Length; i++)
                        if (values[i] is DBNull) values[i] = null!;
                    writer.WriteLine(JsonSerializer.Serialize(values));
                    count++;
                }
                writer.WriteLine(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public string ImportInventory(string source)
    {
        using var file = File.OpenRead(source);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, ArchiveEncoding, detectEncodingFromByteOrderMarks: false);
        var header = JsonSerializer.Deserialize<ArchiveHeader>(ReadArchiveLine(reader) ?? "null");
        if (header is null || header.Format != ArchiveFormat || header.Version != 1
            || header.Id is not { Length: 64 } || !header.Id.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(header.Label) || header.Label.Length > 512
            || header.Label.Any(char.IsControl) || header.Mount is not { Length: >= 3 and <= 32767 }
            || !char.IsAsciiLetter(header.Mount[0]) || header.Mount[1..3] != ":\\"
            || (header.Mount.Length > 3 && !ValidRelativePath(header.Mount[3..].TrimEnd('\\'))))
            throw new InvalidDataException("Unsupported or invalid inventory header.");

        string host = string.IsNullOrWhiteSpace(header.Host) ? "" : header.Host.Trim();
        if (host.Length > 256 || host.Any(char.IsControl))
            throw new InvalidDataException("Unsupported or invalid inventory header.");

        string id = ImportedPrefix + header.Id.ToUpperInvariant();
        using var db = Open();
        Execute(db, "BEGIN IMMEDIATE;");
        Execute(db, """
            INSERT INTO volumes(id,mount,label,online,outcome,host,removable,note)
            VALUES($id,$mount,$label,0,'Imported',$host,$removable,coalesce($note,''))
            ON CONFLICT(id) DO UPDATE SET mount=excluded.mount,label=excluded.label,online=0,
                host=excluded.host,removable=excluded.removable,note=coalesce($note,volumes.note);
            DELETE FROM files WHERE volume=$id;
            """, ("id", id), ("mount", header.Mount), ("label", header.Label),
            ("host", host), ("removable", header.Removable ? 1 : 0), ("note", header.Note));
        using var insert = Command(db, """
            INSERT INTO files(volume,rel,name,name_key,parent_key,extension,bytes,created,modified,attributes,seen,stale,removed)
            VALUES($v,$r,$n,$k,$p,$x,$b,$c,$m,$a,$s,$t,$d);
            """, ("v", id), ("r", ""), ("n", ""), ("k", ""), ("p", ""), ("x", ""),
            ("b", 0), ("c", 0), ("m", 0), ("a", 0), ("s", 0), ("t", 0), ("d", null));
        using var prefix = Command(db, """
            INSERT INTO prefixes(token,file_id) SELECT value,last_insert_rowid() FROM json_each($tokens);
            """, ("tokens", "[]"));
        insert.Prepare();
        prefix.Prepare();
        long count = 0;
        while (ReadArchiveLine(reader) is { } line)
        {
            using var json = JsonDocument.Parse(line);
            var row = json.RootElement;
            if (row.ValueKind == JsonValueKind.Number)
            {
                if (row.GetInt64() != count || ReadArchiveLine(reader) is not null)
                    throw new InvalidDataException("The inventory entry count does not match.");
                RebuildFolderStats(db, id);
                Execute(db, "COMMIT;");
                return id;
            }
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 8
                || row[0].GetString() is not { } relative || !ValidRelativePath(relative))
                throw new InvalidDataException("An inventory path or record is invalid.");

            string name = Path.GetFileName(relative), parent = Key(Path.GetDirectoryName(relative) ?? "");
            long bytes = row[1].GetInt64();
            int attributes = row[4].GetInt32(), stale = row[6].GetInt32();
            if (bytes < 0 || attributes < 0 || stale is not (0 or 1))
                throw new InvalidDataException("Invalid inventory metadata.");
            object?[] values = [id, relative, name, Key(name), parent,
                (attributes & 16) != 0 ? "" : Key(Path.GetExtension(name).TrimStart('.')), bytes,
                ReadTimestamp(row[2]), ReadTimestamp(row[3]), attributes, ReadTimestamp(row[5]), stale,
                row[7].ValueKind == JsonValueKind.Null ? null : ReadTimestamp(row[7])];
            for (int i = 0; i < values.Length; i++) insert.Parameters[i].Value = values[i] ?? DBNull.Value;
            insert.ExecuteNonQuery();
            prefix.Parameters[0].Value = JsonSerializer.Serialize(Prefixes(Key(name), parent));
            prefix.ExecuteNonQuery();
            count++;
        }
        throw new InvalidDataException("The inventory is incomplete.");
    }

    static long ReadTimestamp(JsonElement value)
    {
        long timestamp = value.GetInt64();
        _ = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
        return timestamp;
    }

    static bool ValidRelativePath(string path) => path.Length is > 0 and <= 32767
        && path.Split('\\').All(segment => segment.Length != 0 && segment is not ("." or "..")
            && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);

    static string? ReadArchiveLine(StreamReader reader)
    {
        var line = new StringBuilder();
        while (reader.Read() is int character && character >= 0)
        {
            if (character == '\n') return line.ToString();
            if (line.Length >= 250_000) throw new InvalidDataException("An inventory record is too long.");
            line.Append((char)character);
        }
        return line.Length == 0 ? null : line.ToString();
    }
}
