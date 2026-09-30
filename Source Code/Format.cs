using System.Globalization;

namespace WorldTelemetry;

internal static class Format
{
    internal static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("F0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };

    internal static string Ms(double ms) => ms switch
    {
        <= 0 => "0 ms",
        < 0.001 => "<0.001 ms",
        < 10 => ms.ToString("F3", CultureInfo.InvariantCulture) + " ms",
        _ => ms.ToString("F1", CultureInfo.InvariantCulture) + " ms"
    };

    internal static string Count(long value) => value switch
    {
        >= 10_000_000 => (value / 1_000_000.0).ToString("F0", CultureInfo.InvariantCulture) + "M",
        >= 1_000_000 => (value / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1000.0).ToString("F0", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("N0", CultureInfo.InvariantCulture)
    };

    internal static string Score(double score) => Math.Round(score).ToString("N0", CultureInfo.InvariantCulture);

    internal static string Seconds(double seconds)
    {
        if (Math.Abs(seconds - Math.Round(seconds)) < 0.05)
        {
            long whole = (long)Math.Round(seconds);
            return whole == 1 ? "1 second" : whole.ToString(CultureInfo.InvariantCulture) + " seconds";
        }
        return seconds.ToString("F1", CultureInfo.InvariantCulture) + " seconds";
    }

    internal static string Age(long fromTick)
    {
        long seconds = Math.Max(0, (Environment.TickCount64 - fromTick) / 1000);
        if (seconds < 5)
            return "just now";

        if (seconds < 120)
            return Seconds(seconds) + " ago";

        long minutes = seconds / 60;
        return minutes < 120 ? $"{minutes} minutes ago" : $"{minutes / 60} hours ago";
    }
}
