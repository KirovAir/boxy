namespace Boxy.Web;

/// <summary>
/// Relative time formatting: "just now", "5 minutes ago", "3 hours ago", "2 days ago", then an
/// absolute date once something is older than a month. Rendered by the _TimeAgo partial.
/// </summary>
public static class TimeHelper
{
    public static string FormatTimeAgo(DateTime dateTime)
    {
        var timespan = DateTime.UtcNow - dateTime;

        if (timespan.TotalMinutes < 1)
        {
            return "just now";
        }

        if (timespan.TotalMinutes < 60)
        {
            return Pluralize((int)timespan.TotalMinutes, "minute") + " ago";
        }

        if (timespan.TotalHours < 24)
        {
            return Pluralize((int)timespan.TotalHours, "hour") + " ago";
        }

        if (timespan.TotalDays < 30)
        {
            return Pluralize((int)timespan.TotalDays, "day") + " ago";
        }

        return dateTime.ToString("MMMM d, yyyy");
    }

    private static string Pluralize(int value, string unit)
    {
        return value == 1 ? $"1 {unit}" : $"{value} {unit}s";
    }
}
