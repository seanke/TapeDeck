namespace TapeDeck;

/// <summary>
/// Formats byte counts for compact console status output.
/// </summary>
public static class ByteFormat
{
    /// <summary>
    /// Converts a byte count into a one-decimal binary unit string.
    /// </summary>
    public static string Format(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var suffixIndex = 0;

        while (value >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            value /= 1024;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{value:0} {suffixes[suffixIndex]}"
            : $"{value:0.0} {suffixes[suffixIndex]}";
    }
}
