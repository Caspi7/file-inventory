using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Xml;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace FileInventory;

enum ContentScanMode { Eco, Balanced, Turbo }

sealed record ContentScanOptions(IReadOnlyList<string>? VolumeIds = null,
    ContentScanMode Mode = ContentScanMode.Eco, long MaxFileBytes = 25 * 1024 * 1024,
    int MaxTextChars = 131_072, bool StoreSnippets = true, bool IncludeText = true,
    bool IncludeOffice = true, bool IncludePdf = true, bool SkipSystemFolders = true);

sealed record ContentScanEstimate(long EligibleFiles, long AlreadyIndexed, long ToScan,
    long TotalSourceBytes, long EstimatedIndexBytes);
sealed record ContentScanProgress(long Processed, long Total, long Indexed, long Skipped,
    long Failed, string CurrentFile);
public sealed record ContentSearchHit(long FileId, string Volume, string Rel, string Name, string FullPath,
    string VolumeLabel, bool Online, long Bytes, long Modified, string Snippet, double Rank);
sealed record ContentIndexStats(long IndexedFiles, long TotalTextBytes, long DatabaseBytes);

sealed partial class ContentIndex(Catalog catalog)
{
    const int ExtractorVersion = 1;
    const int Indexed = 1;
    const int NoText = 2;
    const int MaxArchiveField = 64 * 1024 * 1024;
    const string TextExtensions = "txt md json xml csv log cs py js ts jsx tsx html htm css ini cfg yaml yml sql ps1 bat sh toml c cpp h java rs go";
    const string OfficeExtensions = "docx xlsx pptx odt ods odp";
    readonly string database = Path.Combine(catalog.DataDirectory, "content.sqlite3");

    sealed record Source(long Id, string Volume, string Rel, string Mount, string Extension,
        long Bytes, long Modified, int? Status, bool HasSnippet, int TextCap)
    {
        public string Path => System.IO.Path.Combine(Mount, Rel);
    }

    sealed record Result(Source Source, string? Body, bool Failed = false);

    SqliteConnection Open()
    {
        var db = catalog.Open(database);
        try
        {
            Catalog.Execute(db, """
                PRAGMA auto_vacuum=INCREMENTAL;
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=NORMAL;
                PRAGMA temp_store=MEMORY;
                CREATE TABLE IF NOT EXISTS content_files(
                    file_id INTEGER PRIMARY KEY, volume TEXT NOT NULL, rel TEXT NOT NULL,
                    bytes INTEGER NOT NULL, modified INTEGER NOT NULL, extractor_ver INTEGER NOT NULL,
                    status INTEGER NOT NULL, indexed_at INTEGER NOT NULL, brotli_text BLOB);
                CREATE INDEX IF NOT EXISTS content_files_vol_rel ON content_files(volume, rel);
                CREATE TABLE IF NOT EXISTS content_sizes(
                    file_id INTEGER PRIMARY KEY, text_bytes INTEGER NOT NULL, text_cap INTEGER NOT NULL);
                """);
            try
            {
                Catalog.Execute(db, """
                    CREATE VIRTUAL TABLE IF NOT EXISTS content_fts
                    USING fts5(body, content='', contentless_delete=1, tokenize='porter unicode61 remove_diacritics 2');
                    """);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 &&
                ex.Message.Contains("contentless_delete", StringComparison.OrdinalIgnoreCase))
            {
                // A stored-content FTS table supports ordinary DELETE on older SQLite builds.
                Catalog.Execute(db, """
                    CREATE VIRTUAL TABLE IF NOT EXISTS content_fts
                    USING fts5(body, tokenize='porter unicode61 remove_diacritics 2');
                    """);
            }
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    static void OptimizeIndex(SqliteConnection db)
    {
        Run("INSERT INTO main.content_fts(content_fts) VALUES('optimize');");
        Run("PRAGMA main.incremental_vacuum;");
        Run("PRAGMA main.optimize;");

        void Run(string sql)
        {
            // Maintenance is best effort after the data has already been committed.
            try { Catalog.Execute(db, sql); }
            catch (SqliteException ex) { Trace.TraceWarning("Content index maintenance ({0}): {1}", sql, ex.Message); }
        }
    }

    static void AttachCatalog(SqliteConnection db, Catalog catalog) =>
        Catalog.Execute(db, "ATTACH DATABASE $path AS source_catalog;",
            ("path", Path.Combine(catalog.DataDirectory, "catalog.sqlite3")));

    static IEnumerable<Source> Candidates(SqliteConnection db, ContentScanOptions options)
    {
        if (options.MaxFileBytes < 0) throw new ArgumentOutOfRangeException(nameof(options.MaxFileBytes));
        if (options.MaxTextChars <= 0 || options.MaxTextChars > MaxArchiveField / 3)
            throw new ArgumentOutOfRangeException(nameof(options.MaxTextChars));

        var extensions = (options.IncludeText ? TextExtensions : "") + " " +
            (options.IncludeOffice ? OfficeExtensions : "") + (options.IncludePdf ? " pdf" : "");
        var allowed = extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (allowed.Length == 0 || options.VolumeIds is { Count: 0 }) yield break;
        var volumes = options.VolumeIds?.ToHashSet(StringComparer.Ordinal);
        using var cmd = Catalog.Command(db, $"""
            SELECT f.id,f.volume,f.rel,v.mount,f.extension,f.bytes,f.modified,c.status,c.brotli_text IS NOT NULL,coalesce(s.text_cap,0)
            FROM source_catalog.files f JOIN source_catalog.volumes v ON v.id=f.volume
            LEFT JOIN content_files c ON c.file_id=f.id AND c.volume=f.volume AND c.rel=f.rel
                AND c.bytes=f.bytes AND c.modified=f.modified AND c.extractor_ver=$version
            LEFT JOIN content_sizes s ON s.file_id=c.file_id
            WHERE v.online=1 AND f.removed IS NULL AND (f.attributes & $risk)=0
                AND f.bytes BETWEEN 0 AND $max AND f.extension IN ({string.Join(",", allowed.Select(e => "'" + e + "'"))});
            """, ("version", ExtractorVersion), ("risk", 0x441410), ("max", options.MaxFileBytes));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string volume = reader.GetString(1);
            if (volumes is not null && !volumes.Contains(volume)) continue;
            yield return new(reader.GetInt64(0), volume, reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7), reader.GetBoolean(8), reader.GetInt32(9));
        }
    }

    static bool NeedsScan(Source source, ContentScanOptions options) => source.Status switch
    {
        Indexed => source.HasSnippet != options.StoreSnippets || source.TextCap != options.MaxTextChars,
        NoText => source.TextCap != options.MaxTextChars,
        _ => true
    };

    static bool IsSystemOrNoisePath(string rel)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        return rel.StartsWith(@"Windows\", comparison)
            || rel.StartsWith(@"Program Files\", comparison)
            || rel.StartsWith(@"Program Files (x86)\", comparison)
            || rel.StartsWith(@"ProgramData\", comparison)
            || rel.StartsWith(@"$Recycle.Bin\", comparison)
            || rel.StartsWith(@"System Volume Information\", comparison)
            || rel.Contains(@"\node_modules\", comparison)
            || rel.Contains(@"\.git\", comparison)
            || rel.Contains(@"\AppData\Local\Temp\", comparison)
            || rel.Contains(@"\AppData\Local\Packages\", comparison)
            || rel.Contains(@"\obj\", comparison)
            || rel.Contains(@"\bin\", comparison);
    }

    public static ContentScanEstimate EstimateScan(Catalog catalog, ContentScanOptions options)
    {
        using var db = new ContentIndex(catalog).Open();
        AttachCatalog(db, catalog);
        long eligible = 0, indexed = 0, pending = 0, bytes = 0;
        double estimate = 0;
        foreach (var source in Candidates(db, options))
        {
            if (options.SkipSystemFolders && IsSystemOrNoisePath(source.Rel)) continue;
            eligible++;
            bytes += source.Bytes;
            if (source.Status == Indexed) indexed++;
            if (NeedsScan(source, options))
            {
                pending++;
                estimate += Math.Min(source.Bytes, options.MaxTextChars);
            }
        }
        return new(eligible, indexed, pending, bytes, (long)Math.Ceiling(estimate * (options.StoreSnippets ? .20 : .08)));
    }

    public static async Task ScanAsync(Catalog catalog, ContentScanOptions options,
        IProgress<ContentScanProgress>? progress, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stop.Token;
        var index = new ContentIndex(catalog);
        int workers = options.Mode switch
        {
            ContentScanMode.Eco => Math.Clamp(Environment.ProcessorCount / 4, 1, 2),
            ContentScanMode.Balanced => Math.Clamp(Environment.ProcessorCount / 2, 2, 4),
            _ => Math.Clamp(Environment.ProcessorCount, 2, 12)
        };
        var input = Channel.CreateBounded<Source>(workers * 4);
        var output = Channel.CreateBounded<Result>(workers * 2);
        long total = 0;

        Task producer = Task.Run(() => Guard(async () =>
        {
            try
            {
                using var db = index.Open();
                AttachCatalog(db, catalog);
                using var transaction = db.BeginTransaction(deferred: true);
                foreach (var source in Candidates(db, options))
                {
                    token.ThrowIfCancellationRequested();
                    if (options.SkipSystemFolders && IsSystemOrNoisePath(source.Rel)) continue;
                    total++;
                }
                foreach (var source in Candidates(db, options))
                {
                    token.ThrowIfCancellationRequested();
                    if (options.SkipSystemFolders && IsSystemOrNoisePath(source.Rel)) continue;
                    await input.Writer.WriteAsync(source, token).ConfigureAwait(false);
                }
            }
            finally { input.Writer.TryComplete(); }
        }));

        Task[] extractors = Enumerable.Range(0, workers).Select(_ => Task.Factory.StartNew(
            () => Guard(() =>
            {
                RunWorker(input.Reader, output.Writer, options, token);
                return Task.CompletedTask;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
        Task finishExtraction = CompleteOutput();
        Task writer = Task.Run(() => Guard(async () =>
        {
            using var db = index.Open();
            if (options.Mode == ContentScanMode.Turbo) Catalog.Execute(db, "PRAGMA cache_size=-131072;");
            int batchSize = options.Mode == ContentScanMode.Turbo ? 1000 : 250;
            long processed = 0, indexed = 0, skipped = 0, failed = 0;
            bool wroteContent = false;
            var batch = new List<Result>(batchSize);
            await foreach (var result in output.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                batch.Add(result);
                if (batch.Count == batchSize) Flush();
            }
            token.ThrowIfCancellationRequested();
            if (batch.Count > 0) Flush();
            if (processed == 0) progress?.Report(new(0, total, 0, 0, 0, ""));
            if (wroteContent) OptimizeIndex(db);

            void Flush()
            {
                if (batch.Any(result => result.Body is not null))
                {
                    using var transaction = db.BeginTransaction();
                    foreach (var result in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        if (result.Body is not string body) continue;
                        var source = result.Source;
                        bool hasText = !string.IsNullOrWhiteSpace(body);
                        Write(db, source.Id, source.Volume, source.Rel, source.Bytes, source.Modified,
                            ExtractorVersion, hasText ? Indexed : NoText, Catalog.Now(), body,
                            hasText && options.StoreSnippets ? Compress(body) : null,
                            hasText ? Encoding.UTF8.GetByteCount(body) : 0, options.MaxTextChars);
                    }
                    transaction.Commit();
                    wroteContent = true;
                }
                foreach (var result in batch)
                {
                    processed++;
                    if (result.Failed) failed++;
                    else if (!string.IsNullOrWhiteSpace(result.Body)) indexed++;
                    else skipped++;
                }
                progress?.Report(new(processed, total, indexed, skipped, failed, batch[^1].Source.Path));
                batch.Clear();
            }
        }));

        await Task.WhenAll(producer, finishExtraction, writer).ConfigureAwait(false);

        async Task Guard(Func<Task> work)
        {
            try { await work().ConfigureAwait(false); }
            catch { stop.Cancel(); throw; }
        }

        async Task CompleteOutput()
        {
            try { await Task.WhenAll(extractors).ConfigureAwait(false); }
            finally { output.Writer.TryComplete(); }
        }
    }

    static void RunWorker(ChannelReader<Source> input, ChannelWriter<Result> output,
        ContentScanOptions options, CancellationToken token)
    {
        var thread = Thread.CurrentThread;
        var original = thread.Priority;
        bool background = false;
        try
        {
            thread.Priority = options.Mode switch
            {
                ContentScanMode.Eco => ThreadPriority.Lowest,
                ContentScanMode.Balanced => ThreadPriority.BelowNormal,
                _ => ThreadPriority.Normal
            };
            background = options.Mode == ContentScanMode.Eco && OperatingSystem.IsWindows()
                && SetThreadPriority(GetCurrentThread(), 0x00010000);
            while (input.WaitToReadAsync(token).AsTask().GetAwaiter().GetResult())
            {
                while (input.TryRead(out var source))
                {
                    token.ThrowIfCancellationRequested();
                    var result = NeedsScan(source, options) ? Extract(source, options, token) : new(source, null);
                    output.WriteAsync(result, token).AsTask().GetAwaiter().GetResult();
                }
            }
        }
        finally
        {
            if (background) SetThreadPriority(GetCurrentThread(), 0x00020000);
            thread.Priority = original;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetThreadPriority(IntPtr thread, int priority);

    static Result Extract(Source source, ContentScanOptions options, CancellationToken token)
    {
        try
        {
            if (!SafeRelativePath(source.Rel)) return new(source, null);
            var volume = new Volume(source.Volume, source.Mount, "", true, true, 0, null, null, "", "");
            if (!WindowsVolumes.CanReveal(volume, source.Rel, source.Path)) return new(source, null);
            string path = WindowsVolumes.Extended(source.Path);
            if (!Unchanged(path, source)) return new(source, null);
            using var file = OpenSource(path);
            if (!Unchanged(path, source) || file.Length != source.Bytes) return new(source, null);
            var deadline = Stopwatch.StartNew();
            void Check()
            {
                token.ThrowIfCancellationRequested();
                if (deadline.ElapsedMilliseconds > 5000) throw new TimeoutException();
            }

            string body = source.Extension switch
            {
                "pdf" => ExtractPdf(file, options.MaxTextChars, Check),
                "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp" =>
                    ExtractOffice(file, source.Extension, options.MaxTextChars, Check),
                _ => ExtractText(file, options.MaxTextChars, Check)
            };
            return new(source, Unchanged(path, source) ? body : null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or XmlException or RegexMatchTimeoutException or TimeoutException or ArgumentException or NotSupportedException)
        {
            return new(source, null, true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    static FileStream OpenSource(string path)
    {
        const uint openNoRecall = 0x00100000;
        const uint openReparsePoint = 0x00200000;
        var handle = CreateFileW(path, 0x80000000, 1, IntPtr.Zero, 3,
            openNoRecall | openReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException($"Could not open content file (Win32 error {error}).");
        }
        try { return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }

    static bool SafeRelativePath(string path) =>
        !Path.IsPathRooted(path) && !path.Contains(':') &&
        path.Split('\\', '/').All(part => part.Length > 0 && part != "." && part != "..");

    static bool Unchanged(string path, Source source)
    {
        var info = new FileInfo(path);
        var attributes = info.Attributes;
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
            && !WindowsVolumes.HasRecallRisk(attributes) && info.Length == source.Bytes
            && new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() == source.Modified;
    }

    static string ExtractText(Stream file, int cap, Action check)
    {
        byte[] probe = ArrayPool<byte>.Shared.Rent(4096);
        char[] chars = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            int count = file.ReadAtLeast(probe.AsSpan(0, 4096), 4096, throwOnEndOfStream: false);
            var prefix = probe.AsSpan(0, count);
            bool unicodeBom = prefix.StartsWith(new byte[] { 255, 254 }) ||
                prefix.StartsWith(new byte[] { 254, 255 }) || prefix.StartsWith(new byte[] { 0, 0, 254, 255 });
            if (!unicodeBom && prefix.Contains((byte)0)) return "";
            file.Position = 0;
            using var reader = new StreamReader(file, Encoding.UTF8, true, 4096, leaveOpen: true);
            var text = new StringBuilder(Math.Min(cap, 4096));
            while (text.Length < cap)
            {
                check();
                int read = reader.Read(chars, 0, Math.Min(4096, cap - text.Length));
                if (read == 0) break;
                text.Append(chars, 0, read);
            }
            return FinishText(text);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe);
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    static string ExtractOffice(Stream file, string extension, int cap, Action check)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var text = new StringBuilder(Math.Min(cap, 4096));
        var shared = new List<string>();
        long xmlBudget = Math.Min(64L * 1024 * 1024, Math.Max(4L * 1024 * 1024, cap * 32L));
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        foreach (var entry in archive.Entries.OrderBy(e => e.FullName == "xl/sharedStrings.xml" ? 0 : 1))
        {
            check();
            if (text.Length >= cap) break;
            string name = entry.FullName;
            bool include = extension switch
            {
                "docx" => name == "word/document.xml",
                "xlsx" => name == "xl/sharedStrings.xml" || IsPart(name, "xl/worksheets/"),
                "pptx" => IsPart(name, "ppt/slides/"),
                _ => name == "content.xml"
            };
            if (!include || entry.Length > xmlBudget) continue;
            xmlBudget -= entry.Length;
            settings.MaxCharactersInDocument = Math.Max(1, entry.Length);
            using var stream = entry.Open();
            using var xml = XmlReader.Create(stream, settings);
            bool sharedStrings = name == "xl/sharedStrings.xml";
            bool sheet = extension == "xlsx" && !sharedStrings;
            int textDepth = -1, itemDepth = -1;
            string? cellType = null;
            var item = new StringBuilder();

            while (text.Length < cap && xml.Read())
            {
                check();
                if (xml.NodeType == XmlNodeType.Element)
                {
                    if (sheet && xml.LocalName == "c") cellType = xml.GetAttribute("t");
                    if (extension is "docx" or "pptx")
                    {
                        if (xml.LocalName is "p" or "br" or "tab" && text.Length > 0 && !char.IsWhiteSpace(text[^1]))
                            text.Append(' ');
                    }
                    if (sharedStrings && xml.LocalName == "si")
                    {
                        if (xml.IsEmptyElement) shared.Add("");
                        else itemDepth = xml.Depth;
                    }
                    if (!xml.IsEmptyElement && (xml.LocalName == "t" || (sheet && xml.LocalName == "v")))
                        textDepth = xml.Depth;
                }
                if (xml.NodeType == XmlNodeType.EndElement)
                {
                    if (xml.Depth == textDepth) textDepth = -1;
                    if (xml.Depth == itemDepth)
                    {
                        shared.Add(item.ToString());
                        AppendText(text, item.ToString(), cap);
                        item.Clear();
                        itemDepth = -1;
                    }
                }
                if (xml.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace))
                    continue;
                if (extension is "odt" or "ods" or "odp")
                    AppendText(text, xml.Value, cap);
                else if (textDepth >= 0 && sharedStrings && itemDepth >= 0)
                    item.Append(xml.Value.AsSpan(0, Math.Min(xml.Value.Length, cap - item.Length)));
                else if (textDepth >= 0 && sheet && cellType == "s")
                {
                    if (int.TryParse(xml.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                        && id < shared.Count) AppendText(text, shared[id], cap);
                }
                else if (textDepth >= 0 && extension is "docx" or "pptx")
                    text.Append(xml.Value.AsSpan(0, Math.Min(xml.Value.Length, cap - text.Length)));
                else if (textDepth >= 0)
                    AppendText(text, xml.Value, cap);
            }
        }
        return FinishText(text);

        static bool IsPart(string name, string folder) => name.StartsWith(folder, StringComparison.Ordinal)
            && name.EndsWith(".xml", StringComparison.Ordinal) && !name.AsSpan(folder.Length).Contains('/');
    }

    static void AppendText(StringBuilder text, string value, int cap)
    {
        if (value.Length == 0 || text.Length >= cap) return;
        if (text.Length > 0 && !char.IsWhiteSpace(text[^1]) && !char.IsWhiteSpace(value[0])) text.Append(' ');
        text.Append(value.AsSpan(0, Math.Min(value.Length, cap - text.Length)));
    }

    static string FinishText(StringBuilder text)
    {
        if (text.Length > 0 && char.IsHighSurrogate(text[^1])) text.Length--;
        return text.ToString();
    }

    static readonly Regex StreamStart = new(@"\bstream(?:\r\n|\n|\r)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex StreamFilter = new(@"/Filter\s*(?<filter>/\w+|\[[^\]]*\])",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    static readonly Regex StreamLength = new(@"/Length\s+(\d+)\b(?!\s+\d+\s+R\b)",
        RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    static string ExtractPdf(Stream file, int cap, Action check)
    {
        const int maxInput = 64 * 1024 * 1024;
        int length = (int)Math.Min(file.Length, maxInput);
        byte[] bytes = new byte[length];
        int filled = 0;
        while (filled < length)
        {
            check();
            int read = file.Read(bytes, filled, Math.Min(65536, length - filled));
            if (read == 0) break;
            filled += read;
        }

        string pdf = Encoding.Latin1.GetString(bytes, 0, filled);
        var text = new StringBuilder(Math.Min(cap, 4096));
        long remaining = Math.Min(maxInput, Math.Max(1024 * 1024, cap * 32L));
        int position = 0;
        while (position < filled && text.Length < cap && remaining > 0)
        {
            check();
            var start = StreamStart.Match(pdf, position);
            if (!start.Success) break;
            int body = start.Index + start.Length;
            position = body;
            int dictionaryStart = pdf.LastIndexOf("<<", start.Index, Math.Min(start.Index + 1, 8192),
                StringComparison.Ordinal);
            if (dictionaryStart < 0) continue;
            string dictionary = pdf[dictionaryStart..start.Index];
            if (!dictionary.TrimEnd().EndsWith(">>", StringComparison.Ordinal)) continue;

            var declared = StreamLength.Match(dictionary);
            int end;
            if (declared.Success && int.TryParse(declared.Groups[1].Value, out int size)
                && size <= filled - body && AtEndStream(pdf, body + size))
                end = body + size;
            else
            {
                end = pdf.IndexOf("endstream", body, StringComparison.Ordinal);
                if (end < 0) break;
                while (end > body && pdf[end - 1] is '\r' or '\n') end--;
            }
            position = pdf.IndexOf("endstream", end, StringComparison.Ordinal) + "endstream".Length;
            var filter = StreamFilter.Match(dictionary);
            try
            {
                using var raw = new MemoryStream(bytes, body, end - body, writable: false);
                string operators;
                if (!filter.Success)
                {
                    int count = (int)Math.Min(end - body, remaining);
                    remaining -= count;
                    operators = pdf.Substring(body, count);
                }
                else
                {
                    string filterName = filter.Groups["filter"].Value.Trim('[', ']').Trim();
                    if (filterName is not ("/FlateDecode" or "/Fl")) continue;
                    using var zlib = new ZLibStream(raw, CompressionMode.Decompress);
                    using var decoded = new MemoryStream();
                    byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
                    try
                    {
                        while (remaining > 0)
                        {
                            check();
                            int read = zlib.Read(buffer, 0, (int)Math.Min(8192, remaining));
                            if (read == 0) break;
                            decoded.Write(buffer, 0, read);
                            remaining -= read;
                        }
                        operators = Encoding.Latin1.GetString(decoded.GetBuffer(), 0, (int)decoded.Length);
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                ReadPdfOperators(operators, text, cap, check);
            }
            catch (InvalidDataException) { }
        }
        return FinishText(text);

        static bool AtEndStream(string pdf, int offset)
        {
            while (offset < pdf.Length && pdf[offset] is '\r' or '\n') offset++;
            return pdf.AsSpan(offset).StartsWith("endstream", StringComparison.Ordinal);
        }
    }

    static void ReadPdfOperators(string stream, StringBuilder output, int cap, Action check)
    {
        var operand = new StringBuilder();
        bool inText = false, inArray = false, hasString = false;
        int i = 0;
        while (i < stream.Length && output.Length < cap)
        {
            check();
            char c = stream[i++];
            if (char.IsWhiteSpace(c) || c == '\0') continue;
            if (c == '%')
            {
                while (i < stream.Length && stream[i] is not ('\n' or '\r')) i++;
                continue;
            }
            if (c is '(' or '<')
            {
                if (c == '<' && i < stream.Length && stream[i] == '<')
                {
                    i++;
                    continue;
                }
                if (!inArray) operand.Clear();
                string value = ReadPdfString(stream, ref i, c, cap - operand.Length, check);
                operand.Append(value);
                hasString = true;
                continue;
            }
            if (c == '[')
            {
                operand.Clear();
                inArray = true;
                hasString = false;
                continue;
            }
            if (c == ']') { inArray = false; continue; }
            if (c is '/' or '>' or ')' or '{' or '}')
            {
                if (c == '/')
                    while (i < stream.Length && !Delimiter(stream[i])) i++;
                continue;
            }

            int start = i - 1;
            while (i < stream.Length && !Delimiter(stream[i])) i++;
            var word = stream.AsSpan(start, i - start);
            if (inArray)
            {
                if (operand.Length < cap && int.TryParse(word, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int kerning) && kerning < -120)
                    operand.Append(' ');
                continue;
            }
            if (word.SequenceEqual("BT")) inText = true;
            if (word.SequenceEqual("ET")) inText = false;
            if (inText && hasString && (word.SequenceEqual("Tj") || word.SequenceEqual("TJ")
                || word.SequenceEqual("'") || word.SequenceEqual("\"")))
                AppendText(output, operand.ToString(), cap);
            operand.Clear();
            hasString = false;
        }

        static bool Delimiter(char c) => char.IsWhiteSpace(c) ||
            c is '\0' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';
    }

    static string ReadPdfString(string stream, ref int i, char opener, int cap, Action check)
    {
        // Retain enough bytes for UTF-16 text strings and consume the complete operand.
        var bytes = new StringBuilder();
        int byteCap = cap * 2 + 2;
        int depth = 1, high = -1;
        while (i < stream.Length)
        {
            if ((i & 1023) == 0) check();
            char c = stream[i++];
            if (opener == '<')
            {
                if (c == '>') break;
                int digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => -1
                };
                if (digit < 0) continue;
                if (high < 0) { high = digit; continue; }
                c = (char)(high * 16 + digit);
                high = -1;
            }
            else if (c == '\\' && i < stream.Length)
            {
                c = stream[i++];
                if (c is '\r' or '\n')
                {
                    if (c == '\r' && i < stream.Length && stream[i] == '\n') i++;
                    continue;
                }
                if (c is >= '0' and <= '7')
                {
                    int octal = c - '0';
                    for (int j = 0; j < 2 && i < stream.Length && stream[i] is >= '0' and <= '7'; j++)
                        octal = octal * 8 + stream[i++] - '0';
                    c = (char)(octal & 255);
                }
                else c = c switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => c };
            }
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) break;

            if (bytes.Length < byteCap) bytes.Append(c);
        }
        if (high >= 0 && bytes.Length < byteCap) bytes.Append((char)(high * 16));
        string value = bytes.ToString();
        if (value.StartsWith("\u00fe\u00ff", StringComparison.Ordinal))
            value = Encoding.BigEndianUnicode.GetString(Encoding.Latin1.GetBytes(value[2..]));
        if (value.Length > cap) value = value[..cap];
        return value;
    }

    static void Write(SqliteConnection db, long id, string volume, string rel, long bytes,
        long modified, int version, int status, long indexedAt, string body, byte[]? snippet, int textBytes, int textCap)
    {
        Catalog.Execute(db, """
            DELETE FROM content_fts WHERE rowid=$id
                AND EXISTS (SELECT 1 FROM content_files WHERE file_id=$id AND status=1);
            INSERT INTO content_files(file_id,volume,rel,bytes,modified,extractor_ver,status,indexed_at,brotli_text)
            VALUES($id,$volume,$rel,$bytes,$modified,$version,$status,$at,$snippet)
            ON CONFLICT(file_id) DO UPDATE SET volume=excluded.volume,rel=excluded.rel,bytes=excluded.bytes,
                modified=excluded.modified,extractor_ver=excluded.extractor_ver,status=excluded.status,
                indexed_at=excluded.indexed_at,brotli_text=excluded.brotli_text;
            INSERT INTO content_sizes(file_id,text_bytes,text_cap) VALUES($id,$textBytes,$textCap)
            ON CONFLICT(file_id) DO UPDATE SET text_bytes=excluded.text_bytes,text_cap=excluded.text_cap;
            """, ("id", id), ("volume", volume), ("rel", rel), ("bytes", bytes), ("modified", modified),
            ("version", version), ("status", status), ("at", indexedAt), ("snippet", snippet), ("textBytes", textBytes), ("textCap", textCap));
        if (status == Indexed)
            Catalog.Execute(db, "INSERT INTO content_fts(rowid,body) VALUES($id,$body);", ("id", id), ("body", body));
    }

    public static List<ContentSearchHit> Search(Catalog catalog, string query, string? volumeId = null,
        string? extension = null, int limit = 100) =>
        SearchDetailed(catalog, query, volumeId, extension, limit).Hits;

    public static ContentSearchResult SearchDetailed(Catalog catalog, string query, string? volumeId = null,
        string? extension = null, int limit = 100, ContentSearchOptions? options = null)
    {
        options ??= new();
        if (limit <= 0 || string.IsNullOrWhiteSpace(query)) return new([], "", [], false);
        var (root, explicitSyntax) = ContentQuery.Parse(query, options.Mode);
        if (root is null) return new([], "Enter a valid search with at least one positive term", [], false);
        var positive = ContentQuery.PositiveTerms(root).Distinct().ToArray();
        if (positive.Length == 0) return new([], "Enter at least one positive search term", [], false);
        var concepts = options.Mode == ContentMatchMode.Smart && !explicitSyntax &&
            positive.Sum(term => ContentQuery.Words.Matches(term.Text).Count) >= 2
            ? positive.Where(term => term.Phrase || term.Prefix || !StopWords.Contains(term.Text))
                .DistinctBy(term => (NormalizeSearchWord(term.Text), term.Prefix, term.Phrase)).ToArray()
            : [];
        int strictLimit = concepts.Length >= 3 ? Math.Max(limit, 3) : limit;

        using var db = new ContentIndex(catalog).Open();
        AttachCatalog(db, catalog);
        var corrections = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var hits = Read(root, strictLimit, relaxed: false);
        if (options.FuzzyMatching)
        {
            bool vocabCreated = false;
            var words = positive.Where(term => !term.Prefix && (!term.Phrase || hits.Count == 0))
                .SelectMany(term => ContentQuery.Words.Matches(term.Text).Select(word => word.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(32);
            foreach (string word in words)
            {
                string normalized = NormalizeSearchWord(word);
                if (normalized.Length is < 3 or > 64 || normalized.All(char.IsDigit)) continue;
                using var probe = Catalog.Command(db, "SELECT 1 FROM content_fts WHERE content_fts MATCH $word LIMIT 1;",
                    ("word", ContentQuery.Quote(word)));
                if (probe.ExecuteScalar() is not null) continue;
                if (!vocabCreated)
                {
                    Catalog.Execute(db, """
                        CREATE VIRTUAL TABLE IF NOT EXISTS temp.content_vocab USING fts5vocab(main, content_fts, row);
                        """);
                    vocabCreated = true;
                }
                var candidates = FindSpellings(db, normalized);
                if (candidates.Length > 0) corrections.Add(word, candidates);
            }
            if (corrections.Count > 0) hits = Read(root, strictLimit, relaxed: false);
        }

        bool usedRelaxed = false;
        if (concepts.Length > 0 && (hits.Count == 0 || (hits.Count < 3 && concepts.Length >= 3)))
        {
            ContentQuery.Node relaxed = concepts.Cast<ContentQuery.Node>()
                .Aggregate((left, right) => new ContentQuery.Join(left, right, Any: true));
            foreach (var exclusion in ContentQuery.Conjuncts(root).OfType<ContentQuery.Exclude>())
                relaxed = new ContentQuery.Join(relaxed, exclusion);
            var closeHits = Read(relaxed, (int)Math.Clamp((long)limit * 3, 150, int.MaxValue), relaxed: true);
            if (closeHits.Count > 0)
            {
                hits = closeHits;
                usedRelaxed = true;
            }
        }
        if (hits.Count > limit) hits.RemoveRange(limit, hits.Count - limit);
        return new(hits, usedRelaxed ? "Close match (matched best keywords)" : "",
            corrections.Select(pair => $"{pair.Key} → {pair.Value[0]}*").ToArray(), usedRelaxed);

        string Render(ContentQuery.Term term, bool excluded)
        {
            string exact = ContentQuery.Quote(term.Text, term.Prefix);
            if (excluded || term.Prefix || corrections.Count == 0) return exact;
            if (!term.Phrase)
                return corrections.TryGetValue(term.Text, out var alternatives)
                    ? "(" + exact + " OR " + string.Join(" OR ", alternatives.Select(word => ContentQuery.Quote(word, true))) + ")"
                    : exact;

            string[] words = term.Text.Split(' ');
            if (!words.Any(corrections.ContainsKey)) return exact;
            // FTS5's + operator keeps each spelling alternative in the same phrase position.
            IEnumerable<string> phrases = [""];
            foreach (string word in words)
            {
                string token = ContentQuery.Quote(word);
                var choices = corrections.TryGetValue(word, out var variants)
                    ? variants.Select(value => ContentQuery.Quote(value, true)).Append(token)
                    : [token];
                phrases = phrases.SelectMany(phrase => choices.Select(choice =>
                    phrase.Length == 0 ? choice : phrase + " + " + choice)).Take(32).ToArray();
            }
            return "(" + exact + " OR " + string.Join(" OR ", phrases) + ")";
        }

        List<ContentSearchHit> Read(ContentQuery.Node node, int fetch, bool relaxed)
        {
            var (match, complement) = ContentQuery.Compile(node, Render);
            string predicate = complement
                ? "content_fts.rowid NOT IN (SELECT rowid FROM content_fts WHERE content_fts MATCH $match)"
                : "content_fts MATCH $match";
            var terms = ContentQuery.PositiveTerms(node).Distinct().ToArray();
            var variants = terms.Select(term => term.Text.Split(' ').SelectMany(word =>
                corrections.TryGetValue(word, out var spellings) && !term.Prefix
                    ? spellings.Select(value => (Text: value, Prefix: true)).Prepend((Text: word, Prefix: false))
                    : [(Text: word, Prefix: term.Prefix)]).ToArray()).ToArray();
            string[] snippetTerms = variants.SelectMany(values => values).Select(value => value.Text)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (relaxed)
                variants = variants.Select(values => values.Select(value =>
                    (Text: NormalizeSearchWord(value.Text), value.Prefix)).ToArray()).ToArray();
            using var cmd = Catalog.Command(db, $"""
            SELECT f.id,f.volume,f.rel,f.name,v.mount,v.label,v.online,f.bytes,f.modified,c.brotli_text,
                bm25(content_fts) AS rank
            FROM content_fts JOIN content_files c ON c.file_id=content_fts.rowid
            JOIN source_catalog.files f ON f.id=c.file_id AND f.volume=c.volume AND f.rel=c.rel
                AND f.bytes=c.bytes AND f.modified=c.modified
            JOIN source_catalog.volumes v ON v.id=f.volume
            WHERE {predicate} AND f.removed IS NULL AND (f.attributes & 16)=0
                AND ($volume IS NULL OR f.volume=$volume) AND ($ext IS NULL OR f.extension=$ext)
            ORDER BY rank,f.id LIMIT $limit;
            """, ("match", match), ("volume", volumeId),
                ("ext", extension is null ? null : Catalog.Key(extension.TrimStart('.'))), ("limit", fetch));
            using var reader = cmd.ExecuteReader();
            var ranked = new List<(ContentSearchHit Hit, int Coverage, int Filename)>();
            while (reader.Read())
            {
                string rel = reader.GetString(2), name = reader.GetString(3);
                string body = reader.IsDBNull(9) ? "" : Decompress((byte[])reader.GetValue(9));
                var hit = new ContentSearchHit(reader.GetInt64(0), reader.GetString(1), rel, name,
                    Path.Combine(reader.GetString(4), rel), reader.GetString(5), reader.GetBoolean(6),
                    reader.GetInt64(7), reader.GetInt64(8), MakeSnippet(body, snippetTerms), reader.GetDouble(10));
                if (!relaxed)
                {
                    ranked.Add((hit, 0, 0));
                    continue;
                }
                var bodyWords = SearchWords(body);
                var nameWords = SearchWords(name);
                int coverage = 0, filename = 0;
                foreach (var concept in variants)
                {
                    bool inName = MatchesConcept(nameWords, concept);
                    if (inName) filename++;
                    if (inName || MatchesConcept(bodyWords, concept)) coverage++;
                }
                ranked.Add((hit, coverage, filename));
            }
            return relaxed
                ? ranked.OrderByDescending(row => row.Coverage).ThenByDescending(row => row.Filename)
                    .ThenBy(row => row.Hit.Rank).Take(limit).Select(row => row.Hit).ToList()
                : ranked.Select(row => row.Hit).ToList();
        }
    }

    static readonly HashSet<string> StopWords = ("""
        a an the and or of to in on for with about from by at as is are was were be been that this these those
        it its my your our their file files document documents looking find something where what when how into
        onto over under between through during before after above below up down out off all any both each few
        more most other some such only own same so than too very can will just should now
        """).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static string NormalizeSearchWord(string value)
    {
        var normalized = new StringBuilder(value.Length);
        foreach (var rune in value.Normalize(NormalizationForm.FormD).EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark)
                normalized.Append(Rune.ToLowerInvariant(rune));
        return normalized.ToString();
    }

    static HashSet<string> SearchWords(string text) => ContentQuery.Words.Matches(text)
        .Select(word => NormalizeSearchWord(word.Value)).ToHashSet(StringComparer.Ordinal);

    static bool MatchesConcept(HashSet<string> words, (string Text, bool Prefix)[] variants) =>
        variants.Any(variant => variant.Prefix
            ? words.Any(word => word.StartsWith(variant.Text, StringComparison.Ordinal))
            : words.Contains(variant.Text));

    static string[] FindSpellings(SqliteConnection db, string word)
    {
        int maxDistance = word.Length <= 5 ? 1 : word.Length <= 10 ? 2 : 3;
        string prefix = Prefix(word, 2);
        bool canTranspose = prefix.Length == 2 && !char.IsSurrogate(prefix[0]) && !char.IsSurrogate(prefix[1]);
        string swapped = canTranspose ? string.Concat(word[1], word[0]) : prefix;
        string repaired = swapped + word[prefix.Length..];
        string stem = word;
        foreach (string suffix in new[] { "ing", "ed" })
        {
            if (word.Length < suffix.Length + 3 || !word.EndsWith(suffix, StringComparison.Ordinal)) continue;
            stem = word[..^suffix.Length];
            if (stem.Length > 3 && stem[^1] == stem[^2]) stem = stem[..^1];
            break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var close = new List<(string Term, int Distance, int PrefixLength, long Documents)>();
        AddRange(prefix);
        if (swapped != prefix) AddRange(swapped);
        if (word.Length <= 5 || close.Count < 3) AddRange(Prefix(word, 1));
        if (close.Count == 0 && canTranspose)
        {
            string tail = word[1..];
            AddRange(Prefix(tail, 1));
            // A wrong first letter leaves the next characters intact. Probe bounded ranges
            // for those suffixes with each possible Latin initial.
            if (close.Count == 0)
                for (char initial = 'a'; initial <= 'z'; initial++)
                    AddRange(initial + Prefix(tail, 2));
        }

        return close.OrderBy(hit => hit.Distance).ThenByDescending(hit => hit.PrefixLength)
            .ThenByDescending(hit => hit.Documents).ThenBy(hit => hit.Term, StringComparer.Ordinal)
            .Take(4).Select(hit => hit.Term).ToArray();

        void AddRange(string start)
        {
            using var cmd = Catalog.Command(db, """
                SELECT term, doc FROM temp.content_vocab WHERE term >= $a AND term < $b LIMIT 3000;
                """, ("a", start), ("b", start + char.ConvertFromUtf32(0x10FFFF)));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string candidate = reader.GetString(0);
                if (Math.Abs(candidate.Length - word.Length) > maxDistance + 1 &&
                    Math.Abs(candidate.Length - stem.Length) > maxDistance) continue;
                if (!seen.Add(candidate)) continue;

                int distance = Score(word, candidate);
                if (stem != word) distance = Math.Min(distance, Score(stem, candidate));
                // An initial transposition can mask a second misspelling in the rest of the word.
                if (distance > maxDistance && swapped != prefix && word.Length >= 6 &&
                    candidate.StartsWith(swapped, StringComparison.Ordinal))
                    distance = Score(repaired, candidate);
                if (distance > maxDistance) continue;

                int common = 0;
                while (common < Math.Min(word.Length, candidate.Length) && word[common] == candidate[common]) common++;
                close.Add((candidate, distance, common, reader.GetInt64(1)));
            }
        }

        int Score(string typed, string candidate)
        {
            int distance = Distance(typed, candidate, maxDistance);
            // Porter can remove a final e, as in invoice -> invoic.
            if (distance > 1 && typed.EndsWith('e') && !candidate.EndsWith('e'))
                distance = Math.Min(distance, Distance(typed, candidate + "e", maxDistance));
            return distance;
        }
    }

    static string Prefix(string word, int length) =>
        word[..(char.IsHighSurrogate(word[length - 1]) ? length + 1 : length)];

    static int Distance(string left, string right, int maximum)
    {
        if (Math.Abs(left.Length - right.Length) > maximum) return maximum + 1;
        int columns = right.Length + 2, infinity = left.Length + right.Length;
        Span<int> matrix = stackalloc int[(left.Length + 2) * columns];
        Span<int> previousRows = stackalloc int[right.Length];
        previousRows.Clear();
        matrix[0] = infinity;
        for (int i = 0; i <= left.Length; i++)
        {
            matrix[(i + 1) * columns] = infinity;
            matrix[(i + 1) * columns + 1] = i;
        }
        for (int j = 0; j <= right.Length; j++)
        {
            matrix[j + 1] = infinity;
            matrix[columns + j + 1] = j;
        }
        for (int i = 1; i <= left.Length; i++)
        {
            int previousColumn = 0;
            for (int j = 1; j <= right.Length; j++)
            {
                int previousRow = previousRows[j - 1], transposedColumn = previousColumn;
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                if (cost == 0)
                {
                    previousColumn = j;
                    previousRows[j - 1] = i;
                }
                int index = (i + 1) * columns + j + 1;
                matrix[index] = Math.Min(
                    Math.Min(matrix[index - columns - 1] + cost, matrix[index - 1] + 1),
                    Math.Min(matrix[index - columns] + 1,
                        matrix[previousRow * columns + transposedColumn] + i - previousRow + j - transposedColumn - 1));
            }
        }
        return matrix[^1];
    }

    static string MakeSnippet(string body, string[] terms)
    {
        if (body.Length == 0) return "";
        const int width = 140;
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions flags = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        int first = body.Length;
        foreach (string term in terms)
        {
            int found = compare.IndexOf(body, term, flags);
            if (found >= 0) first = Math.Min(first, found);
        }
        // Porter matches can have different endings from the query.
        if (first == body.Length)
            foreach (string term in terms)
                for (int length = term.Length - 1; length >= Math.Max(3, term.Length - 4); length--)
                {
                    if (char.IsHighSurrogate(term[length - 1])) continue;
                    int found = compare.IndexOf(body, term[..length], flags);
                    if (found < 0) continue;
                    first = Math.Min(first, found);
                    break;
                }
        int start = first == body.Length ? 0 : Math.Clamp(first - 45, 0, Math.Max(0, body.Length - width));
        int end = Math.Min(body.Length, start + width);
        if (start > 0 && char.IsLowSurrogate(body[start])) start--;
        if (end > start && char.IsHighSurrogate(body[end - 1])) end--;
        string excerpt = Regex.Replace(body[start..end], @"\s+", " ").Trim();
        return (start > 0 ? "…" : "") + excerpt + (end < body.Length ? "…" : "");
    }

    static byte[] Compress(string body)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            brotli.Write(bytes);
        }
        return output.ToArray();
    }

    static string Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int count;
            while ((count = brotli.Read(buffer, 0, 8192)) > 0)
            {
                if (output.Length + count > MaxArchiveField) throw new InvalidDataException("Content text is too large.");
                output.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public void ExportContent(string destinationPath, string? volumeId = null)
    {
        using var db = Open();
        Catalog.Execute(db, "CREATE VIRTUAL TABLE temp.content_export_terms USING fts5vocab(main, content_fts, instance);");
        using var snapshot = db.BeginTransaction(deferred: true);
        using var command = Catalog.Command(db, """
            SELECT c.file_id,volume,rel,bytes,modified,extractor_ver,status,indexed_at,brotli_text,text_bytes,text_cap
            FROM content_files c JOIN content_sizes s ON s.file_id=c.file_id
            WHERE $volume IS NULL OR volume=$volume ORDER BY c.file_id;
            """, ("volume", volumeId));
        using var reader = command.ExecuteReader();

        string path = Path.GetFullPath(destinationPath);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var brotli = new BrotliStream(file, CompressionLevel.Optimal))
            using (var archive = new BinaryWriter(brotli, Encoding.UTF8))
            {
                archive.Write("FICONTENT1"u8);
                while (reader.Read())
                {
                    archive.Write(true);
                    long id = reader.GetInt64(0);
                    WriteField(archive, Encoding.UTF8.GetBytes(reader.GetString(1)));
                    WriteField(archive, Encoding.UTF8.GetBytes(reader.GetString(2)));
                    archive.Write(reader.GetInt64(3));
                    archive.Write(reader.GetInt64(4));
                    archive.Write(reader.GetInt32(5));
                    int status = reader.GetInt32(6);
                    archive.Write(status);
                    archive.Write(reader.GetInt64(7));
                    archive.Write(reader.GetInt32(9));
                    archive.Write(reader.GetInt32(10));
                    byte[]? snippet = reader.IsDBNull(8) ? null : (byte[])reader.GetValue(8);
                    archive.Write(snippet is not null);
                    if (snippet is not null) WriteField(archive, snippet);
                    else if (status == Indexed) WriteField(archive, Encoding.UTF8.GetBytes(ExportTerms(db, id)));
                }
                archive.Write(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    static string ExportTerms(SqliteConnection db, long id)
    {
        // Contentless rows expose the indexed terms and their positions through fts5vocab.
        using var command = Catalog.Command(db,
            "SELECT term FROM content_export_terms WHERE doc=$id ORDER BY offset;", ("id", id));
        using var reader = command.ExecuteReader();
        var terms = new StringBuilder();
        while (reader.Read()) AppendText(terms, reader.GetString(0), MaxArchiveField / 3);
        return FinishText(terms);
    }

    public int ImportContent(string sourcePath)
    {
        using var file = File.OpenRead(sourcePath);
        using var brotli = new BrotliStream(file, CompressionMode.Decompress);
        using var archive = new BinaryReader(brotli, Encoding.UTF8);
        if (!archive.ReadBytes(10).AsSpan().SequenceEqual("FICONTENT1"u8))
            throw new InvalidDataException("Unrecognized content archive.");

        using var db = Open();
        AttachCatalog(db, catalog);
        using var transaction = db.BeginTransaction();
        int imported = 0;
        while (archive.ReadBoolean())
        {
            string volume = Encoding.UTF8.GetString(ReadField(archive));
            string rel = Encoding.UTF8.GetString(ReadField(archive));
            long bytes = archive.ReadInt64();
            long modified = archive.ReadInt64();
            int version = archive.ReadInt32();
            int status = archive.ReadInt32();
            long indexedAt = archive.ReadInt64();
            int textBytes = archive.ReadInt32();
            int textCap = archive.ReadInt32();
            byte[]? snippet = archive.ReadBoolean() ? ReadField(archive) : null;
            if (!SafeRelativePath(rel) || bytes < 0 || textBytes < 0 || textBytes > MaxArchiveField || textCap <= 0 || status is not (Indexed or NoText)
                || (status == NoText && snippet is not null))
                throw new InvalidDataException("Invalid content archive record.");

            string body = snippet is not null ? Decompress(snippet) :
                status == Indexed ? Encoding.UTF8.GetString(ReadField(archive)) : "";
            using var lookup = Catalog.Command(db, """
                SELECT f.id FROM source_catalog.files f
                WHERE f.volume=$volume AND f.rel=$rel AND f.bytes=$bytes AND f.modified=$modified
                    AND f.removed IS NULL AND (f.attributes & 16)=0
                    AND NOT EXISTS (
                        SELECT 1 FROM content_files c WHERE c.file_id=f.id AND c.volume=f.volume AND c.rel=f.rel
                            AND c.bytes=f.bytes AND c.modified=f.modified
                            AND (c.extractor_ver>$version OR (c.extractor_ver=$version AND c.indexed_at>=$at))
                    );
                """, ("volume", volume), ("rel", rel), ("bytes", bytes), ("modified", modified),
                ("version", version), ("at", indexedAt));
            if (lookup.ExecuteScalar() is not long id) continue;
            Write(db, id, volume, rel, bytes, modified, version, status, indexedAt, body, snippet, textBytes, textCap);
            imported++;
        }
        if (brotli.ReadByte() != -1) throw new InvalidDataException("Unexpected content archive data.");
        transaction.Commit();
        if (imported > 0) OptimizeIndex(db);
        return imported;
    }

    static void WriteField(BinaryWriter writer, byte[] field)
    {
        if (field.Length > MaxArchiveField) throw new InvalidDataException("Content archive field is too large.");
        writer.Write(field.Length);
        writer.Write(field);
    }

    static byte[] ReadField(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaxArchiveField) throw new InvalidDataException("Invalid content archive field length.");
        byte[] value = reader.ReadBytes(length);
        if (value.Length != length) throw new EndOfStreamException();
        return value;
    }

    public ContentIndexStats GetStats()
    {
        using var db = Open();
        using var command = Catalog.Command(db,
            "SELECT count(*),coalesce(sum(text_bytes),0) FROM content_files JOIN content_sizes USING(file_id) WHERE status=1;");
        using var reader = command.ExecuteReader();
        reader.Read();
        long databaseBytes = new FileInfo(database).Length;
        var wal = new FileInfo(database + "-wal");
        if (wal.Exists) databaseBytes += wal.Length;
        return new(reader.GetInt64(0), reader.GetInt64(1), databaseBytes);
    }
}
