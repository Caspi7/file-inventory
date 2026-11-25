namespace FileInventory;

record ScanDisplay(bool Indeterminate, int Percent, string Text)
{
    public static string FormatTimeLeft(TimeSpan remaining)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        if (rounded.TotalHours >= 1) return $"~{(long)rounded.TotalHours}h {rounded.Minutes}m left";
        if (rounded.TotalMinutes >= 1) return $"~{rounded.Minutes}m {rounded.Seconds}s left";
        return $"~{rounded.Seconds}s left";
    }

    public static ScanDisplay Create(ScanProgress scan, TimeSpan elapsed, bool cancelling = false)
    {
        string duration = elapsed.TotalHours >= 1
            ? $"{(long)elapsed.TotalHours}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
            : elapsed.ToString(@"m\:ss");
        double rate = elapsed.TotalSeconds > 0 ? scan.Entries / elapsed.TotalSeconds : 0;
        string activity = $"{scan.Entries:N0} entries · {rate:N0} entries/s · elapsed {duration} · {scan.Omitted:N0} skipped";
        if (cancelling) return new(true, 0, $"Cancelling scan…\r\n{activity}");
        if (scan.Phase == "Preparing scan") return new(true, 0, $"Preparing scan…\r\n{activity}");
        if (scan.Phase == "Saving catalog") return new(true, 0, $"Saving catalog…\r\n{activity}");
        if (scan.Phase == "Complete") return new(false, 100, $"Scan complete\r\n{activity}");
        if (scan.ExpectedEntries is not > 0)
            return new(true, 0, $"Scanning · first scan, time remaining unknown\r\n{activity}");
        if (scan.Entries > scan.ExpectedEntries.Value)
            return new(true, 99, $"Scanning · previous scan size reached, finishing discovery\r\n{activity}");
        double percent = 100.0 * scan.Entries / scan.ExpectedEntries.Value;
        string remaining = elapsed.TotalSeconds >= 1.5 && rate > 0
            ? $"estimated time remaining ({FormatTimeLeft(TimeSpan.FromSeconds((scan.ExpectedEntries.Value - scan.Entries) / rate))})"
            : "estimating time remaining";
        return new(false, Math.Min(99, (int)percent),
            $"Scanning · ~{percent:F1}% · {remaining} (based on last completed scan)\r\n{activity}");
    }
}
