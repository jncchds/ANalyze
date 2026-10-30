using System.Diagnostics;
using System.Globalization;

namespace ANalyze.Infrastructure;

/// <summary>
/// How busy the disks under the roots are (Linux <c>/proc/diskstats</c>: share of time a device had I/O in flight). Background work
/// measures it while it does no I/O of its own (between batches), so what it sees is the load of everything else on the server.
/// Devices are matched by the roots' device numbers; when none matches (ZFS, btrfs subvolumes, overlay mounts) all real disks count.
/// </summary>
public sealed class DiskLoad(IEnumerable<string> paths)
{
    private static readonly string[] Virtual = ["loop", "ram", "zram", "sr", "fd"];
    private readonly HashSet<(long Major, long Minor)> _devices = Devices(paths);

    public static bool Supported => OperatingSystem.IsLinux() && File.Exists("/proc/diskstats");

    /// <summary>Busiest matching device over <paramref name="window"/>, 0–1; null when it cannot be measured.</summary>
    public async Task<double?> MeasureAsync(TimeSpan window, CancellationToken ct)
    {
        if (!Supported)
        {
            return null;
        }

        var before = Read();
        var clock = Stopwatch.StartNew();
        await Task.Delay(window, ct);
        var after = Read();
        var ms = clock.Elapsed.TotalMilliseconds;
        if (before is null || after is null || ms <= 0)
        {
            return null;
        }

        var useAll = !_devices.Any(after.ContainsKey);
        double busiest = 0;
        foreach (var (dev, (name, ticks)) in after)
        {
            if (useAll ? Virtual.Any(name.StartsWith) : !_devices.Contains(dev))
            {
                continue;
            }

            if (before.TryGetValue(dev, out var b))
            {
                busiest = Math.Max(busiest, Math.Clamp((ticks - b.Ticks) / ms, 0, 1));
            }
        }

        return busiest;
    }

    private static Dictionary<(long, long), (string Name, long Ticks)>? Read()
    {
        try
        {
            var result = new Dictionary<(long, long), (string, long)>();
            foreach (var line in File.ReadLines("/proc/diskstats"))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                // major minor name reads … ; field 13 (index 12) is the time spent doing I/O, in ms.
                if (f.Length > 12 && long.TryParse(f[0], CultureInfo.InvariantCulture, out var major) && long.TryParse(f[1], CultureInfo.InvariantCulture, out var minor)
                    && long.TryParse(f[12], CultureInfo.InvariantCulture, out var ticks))
                {
                    result[(major, minor)] = (f[2], ticks);
                }
            }

            return result;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HashSet<(long, long)> Devices(IEnumerable<string> paths)
    {
        var set = new HashSet<(long, long)>();
        foreach (var p in paths)
        {
            var st = Native.LStat(p);
            if (st.Exists && st.Dev >> 32 != 0)
            {
                set.Add((st.Dev >> 32, st.Dev & 0xFFFFFFFF));
            }
        }

        return set;
    }
}
