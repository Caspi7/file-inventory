using System.Text.Json;

namespace FileInventory;

sealed record SavedSearch(string Name, string Query, Filters Filters)
{
    public ParsedQuery Parse() => QueryParser.Parse(Query, Filters);
}

sealed class SavedSearches(string dataDirectory)
{
    static readonly JsonSerializerOptions json = new() { WriteIndented = true };
    static readonly StringComparer names = StringComparer.OrdinalIgnoreCase;
    readonly string path = Path.Combine(Path.GetFullPath(dataDirectory), "saved-searches.json");

    public IReadOnlyList<SavedSearch> Load()
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }

        using (stream)
        {
            try
            {
                var searches = JsonSerializer.Deserialize<List<SavedSearch>>(stream, json);
                var seen = new HashSet<string>(names);
                if (searches is null || searches.Any(search => search is null
                    || string.IsNullOrWhiteSpace(search.Name) || search.Name != search.Name.Trim()
                    || search.Query is null || search.Filters is null || !seen.Add(search.Name)))
                    throw new InvalidDataException("The saved searches file contains invalid or duplicate entries.");
                return searches.OrderBy(search => search.Name, names).ToArray();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The saved searches file contains invalid JSON.", exception);
            }
        }
    }

    public void Save(string name, string query, Filters? filters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var search = new SavedSearch(name.Trim(), query, filters ?? new Filters());
        search.Parse();

        using var writeLock = OpenWriteLock();
        var searches = Load().Where(item => !names.Equals(item.Name, search.Name)).ToList();
        searches.Add(search);
        Write(searches);
    }

    public bool Delete(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using var writeLock = OpenWriteLock();
        var searches = Load().ToList();
        if (searches.RemoveAll(search => names.Equals(search.Name, name.Trim())) == 0) return false;
        Write(searches);
        return true;
    }

    // Hold an exclusive lock across the read and replacement so a second writer
    // cannot overwrite changes based on an earlier snapshot.
    FileStream OpenWriteLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
    }

    void Write(List<SavedSearch> searches)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, searches.OrderBy(search => search.Name, names), json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
