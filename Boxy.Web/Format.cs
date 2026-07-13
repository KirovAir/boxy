using System.Globalization;

namespace Boxy.Web;

/// <summary>Small display formatters shared by views.</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Human-readable byte size, e.g. "0 B", "512 KB", "3.4 GB" (invariant, so no locale comma).</summary>
    public static string Bytes(long bytes)
    {
        double n = bytes;
        var u = 0;
        while (n >= 1024 && u < Units.Length - 1)
        {
            n /= 1024;
            u++;
        }

        return u == 0 ? $"{bytes} B" : $"{n.ToString("0.0", CultureInfo.InvariantCulture)} {Units[u]}";
    }

    /// <summary>Pixel dimensions like "3840×2160", or "" when either side is unknown (still processing,
    /// or not visual media).</summary>
    public static string Dimensions(int? width, int? height)
    {
        return width is > 0 && height is > 0 ? $"{width}×{height}" : "";
    }

    /// <summary>Media length as "2:14" or "1:02:14"; "" when unknown or zero.</summary>
    public static string Duration(double? seconds)
    {
        if (seconds is not (> 0 and var s))
        {
            return "";
        }

        var t = TimeSpan.FromSeconds(s);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }
}
