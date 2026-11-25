using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileInventory;

sealed record ParsedQuery(string Text, Filters Filters);

// Fields are case-insensitive. Dates are local calendar dates. Size units
// KB/MB/GB/TB are decimal, and KiB/MiB/GiB/TiB are binary.
static class QueryParser
{
    static readonly Regex sizePattern = new(
        @"\A(?<number>[0-9]+(?:\.[0-9]+)?)(?<unit>[a-z]*)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ParsedQuery Parse(string input, Filters? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var filters = defaults ?? new Filters();
        var terms = new List<string>();
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (token, separator) in Tokenize(input))
        {
            string field = separator < 0 ? "" : token[..separator].ToLowerInvariant();
            if (field is not ("ext" or "drive" or "min" or "max" or "after" or "before" or "online" or "removed"))
            {
                terms.Add(token);
                continue;
            }

            string value = token[(separator + 1)..];
            if (string.IsNullOrWhiteSpace(value) || !fields.Add(field))
                throw new FormatException($"Specify one nonempty value for '{field}'.");
            filters = field switch
            {
                "ext" => filters with { Extension = ParseExtension(value) },
                "drive" => filters with { Volume = value },
                "min" => filters with { Min = ParseSize(value) },
                "max" => filters with { Max = ParseSize(value) },
                "after" => filters with { From = ParseDate(value) },
                "before" => filters with { Through = ParseDate(value) },
                "online" => filters with { Online = ParseBoolean(field, value) },
                "removed" => filters with { IncludeRemoved = ParseBoolean(field, value) },
                _ => filters
            };
        }

        if (filters.Min > filters.Max)
            throw new FormatException("The minimum size exceeds the maximum size.");
        if (filters.From >= filters.Through)
            throw new FormatException("The start date must precede the end date.");
        return new(string.Join(' ', terms), filters);
    }

    // Quotes group a field value or protect a literal colon. The catalog still
    // applies its usual filename term matching to the resulting free text.
    static IEnumerable<(string Token, int Separator)> Tokenize(string input)
    {
        var token = new StringBuilder();
        bool quoted = false;
        int separator = -1;
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '"')
            {
                if (quoted && i + 1 < input.Length && input[i + 1] == '"')
                {
                    token.Append('"');
                    i++;
                }
                else
                    quoted = !quoted;
                continue;
            }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (token.Length > 0) yield return (token.ToString(), separator);
                token.Clear();
                separator = -1;
                continue;
            }
            if (!quoted && c == ':' && separator < 0) separator = token.Length;
            token.Append(c);
        }
        if (quoted) throw new FormatException("Close the quoted search text.");
        if (token.Length > 0) yield return (token.ToString(), separator);
    }

    static string ParseExtension(string value)
    {
        string extension = value.TrimStart('.');
        if (extension.Length == 0 || extension.Any(c => char.IsWhiteSpace(c) || ".\\/:*?\"<>|".Contains(c)))
            throw new FormatException("Enter a single file extension, such as pdf.");
        return extension;
    }

    static long ParseSize(string value)
    {
        var match = sizePattern.Match(value);
        long multiplier = match.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "" or "B" => 1,
            "KB" => 1_000,
            "MB" => 1_000_000,
            "GB" => 1_000_000_000,
            "TB" => 1_000_000_000_000,
            "KIB" => 1L << 10,
            "MIB" => 1L << 20,
            "GIB" => 1L << 30,
            "TIB" => 1L << 40,
            _ => 0
        };
        if (!match.Success || multiplier == 0
            || !decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal number)
            || number > long.MaxValue / (decimal)multiplier)
            throw new FormatException($"Invalid size '{value}'. Use bytes or a unit such as MiB.");
        decimal bytes = number * multiplier;
        if (bytes > long.MaxValue || bytes != decimal.Truncate(bytes))
            throw new FormatException($"Size '{value}' must resolve to a whole number of bytes.");
        return (long)bytes;
    }

    // Start is inclusive and end is exclusive, matching Catalog.QueryPage.
    static long ParseDate(string value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date))
            throw new FormatException($"Invalid date '{value}'. Use yyyy-MM-dd.");
        try
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local))
                .ToUnixTimeMilliseconds();
        }
        catch (ArgumentException exception)
        {
            throw new FormatException($"Date '{value}' is outside the supported range.", exception);
        }
    }

    static bool ParseBoolean(string field, string value) =>
        bool.TryParse(value, out bool parsed) ? parsed
            : throw new FormatException($"Use true or false for '{field}'.");
}
