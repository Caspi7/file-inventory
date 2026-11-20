namespace FileInventory;

record ScanDisplay(bool Indeterminate, int Percent, string Text)
{
    public static ScanDisplay Create(ScanProgress scan, TimeSpan elapsed, bool cancelling = false)
    {
        string duration = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
        double rate = elapsed.TotalSeconds > 0 ? scan.Entries / elapsed.TotalSeconds : 0;
        string activity = $"{scan.Entries:N0} items · {rate:N0}/sec · elapsed {duration} · {scan.Omitted:N0} skipped";
        if (cancelling) return new(true, 0, $"Cancelling scan…\r\n{activity}");
        if (scan.Phase == "Preparing scan") return new(true, 0, $"Preparing scan…\r\n{activity}");
        if (scan.Phase == "Saving catalog") return new(true, 0, $"Saving catalog…\r\n{activity}");
        if (scan.Phase == "Complete") return new(false, 100, $"Scan complete\r\n{activity}");
        if (scan.ExpectedEntries is not > 0)
            return new(true, 0, $"Scanning · first scan, time remaining unknown\r\n{activity}");
        if (scan.Entries >= scan.ExpectedEntries.Value)
            return new(true, 99, $"Scanning · previous scan size reached, finishing discovery\r\n{activity}");
        int percent = (int)Math.Min(99, 100.0 * scan.Entries / scan.ExpectedEntries.Value);
        string remaining = elapsed.TotalSeconds < 3 || rate < 1 ? "estimating time remaining"
            : "about " + TimeSpan.FromSeconds((scan.ExpectedEntries.Value - scan.Entries) / rate).ToString(@"h\:mm\:ss") + " remaining";
        return new(false, percent, $"Scanning · ~{percent}% · {remaining} (based on last completed scan)\r\n{activity}");
    }
}
