using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace FileInventory;

enum InventoryExportFormat { Csv, Json, Html }

static class InventoryExport
{
    const string titleMarker = "{{TITLE}}";
    const string rowsMarker = "{{ROWS}}";
    static readonly string[] columns =
        ["Name", "Full path", "Relative path", "Volume ID", "Drive label", "Status",
         "Bytes", "Modified", "Last seen", "Kind"];

    public static void Write(string destination, IEnumerable<Hit?> rows, InventoryExportFormat format,
        string title = "File Inventory report", string? templatePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(title);
        cancellationToken.ThrowIfCancellationRequested();
        string? template = format == InventoryExportFormat.Html
            ? File.ReadAllText(templatePath ?? throw new ArgumentNullException(nameof(templatePath)))
            : null;
        destination = Path.GetFullPath(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            ".inventory-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (format == InventoryExportFormat.Json)
                    WriteJson(stream, PresentRows());
                else
                {
                    using var writer = new StreamWriter(stream,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == InventoryExportFormat.Csv));
                    switch (format)
                    {
                        case InventoryExportFormat.Csv:
                            WriteCsv(writer, PresentRows());
                            break;
                        case InventoryExportFormat.Html:
                            WriteHtml(writer, PresentRows(), title, template!);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(format));
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        IEnumerable<Hit> PresentRows()
        {
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A catalog search page can contain a placeholder for a vanished entry.
                if (row is not null) yield return row;
            }
        }
    }

    static void WriteCsv(TextWriter writer, IEnumerable<Hit> rows)
    {
        writer.NewLine = "\r\n";
        writer.WriteLine(string.Join(',', columns.Select(CsvCell)));
        foreach (var row in rows)
            writer.WriteLine(string.Join(',', Values(row).Select(CsvCell)));
    }

    static string CsvCell(string value)
    {
        // Quoting alone does not stop spreadsheets from interpreting formulas.
        var trimmed = value.AsSpan().TrimStart();
        if ((trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')
            || (value.Length > 0 && value[0] is '\t' or '\r' or '\n'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    static void WriteJson(Stream stream, IEnumerable<Hit> rows)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartArray();
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("name", row.Name);
            writer.WriteString("fullPath", row.FullPath);
            writer.WriteString("relativePath", row.RelativePath);
            writer.WriteString("volumeId", row.Volume);
            writer.WriteString("driveLabel", row.Label);
            writer.WriteString("status", row.Status);
            writer.WriteNumber("bytes", row.Bytes);
            writer.WriteString("modified", row.Modified);
            writer.WriteString("lastSeen", row.LastSeen);
            writer.WriteBoolean("isDirectory", row.IsDirectory);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    static void WriteHtml(TextWriter writer, IEnumerable<Hit> rows, string title, string template)
    {
        string[] parts = template.Split(rowsMarker, StringSplitOptions.None);
        if (parts.Length != 2 || !template.Contains(titleMarker, StringComparison.Ordinal))
            throw new InvalidDataException("The HTML template must contain {{TITLE}} and one {{ROWS}} marker.");

        string encodedTitle = WebUtility.HtmlEncode(title);
        writer.Write(parts[0].Replace(titleMarker, encodedTitle, StringComparison.Ordinal));
        foreach (var row in rows)
        {
            writer.Write("<tr>");
            foreach (string value in Values(row))
            {
                writer.Write("<td>");
                writer.Write(WebUtility.HtmlEncode(value));
                writer.Write("</td>");
            }
            writer.WriteLine("</tr>");
        }
        writer.Write(parts[1].Replace(titleMarker, encodedTitle, StringComparison.Ordinal));
    }

    static string[] Values(Hit row) =>
        [row.Name, row.FullPath, row.RelativePath, row.Volume, row.Label, row.Status,
         row.Bytes.ToString(CultureInfo.InvariantCulture),
         row.Modified.ToString("O", CultureInfo.InvariantCulture),
         row.LastSeen.ToString("O", CultureInfo.InvariantCulture),
         row.IsDirectory ? "Folder" : "File"];
}
