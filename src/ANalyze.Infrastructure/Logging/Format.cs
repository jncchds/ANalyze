using System.Globalization;

namespace ANalyze.Infrastructure.Logging;

/// <summary>Readable numbers for log lines.</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long n)
    {
        double v = n;
        var u = 0;
        while (Math.Abs(v) >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{n} B" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + Units[u];
    }

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? t.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min {t.Seconds} s"
        : $"{(int)t.TotalHours} h {t.Minutes} min";
}
