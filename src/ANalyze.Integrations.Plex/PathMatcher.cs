using ANalyze.Infrastructure.Roots;
using Microsoft.Data.Sqlite;

namespace ANalyze.Integrations.Plex;

/// <summary>
/// Finds the scanned entry for a path reported by another system, in three steps:
/// 1. configured path mappings, 2. mappings detected from sample files found by name and size,
/// 3. per-file fallback by name + size (+ parent folder name to break ties).
/// </summary>
internal sealed class PathMatcher(SqliteConnection conn, RootRegistry roots, IReadOnlyList<PathMapping> configured)
{
    private readonly List<PathMapping> _detected = [];

    public IReadOnlyList<PathMapping> Detected => _detected;

    /// <summary>Learns prefix mappings from up to <paramref name="sample"/> files that the configured mappings do not resolve.</summary>
    public void Detect(IEnumerable<(string Path, long Size)> files, int sample = 60)
    {
        var votes = new Dictionary<(string, string), int>();
        var tried = 0;
        foreach (var (path, size) in files)
        {
            if (tried >= sample)
            {
                break;
            }

            if (ByMapping(path, configured) is not null)
            {
                continue;
            }

            tried++;
            var norm = path.Replace('\\', '/');
            foreach (var local in ByName(norm, size).Select(c => c.Full))
            {
                var (from, to) = CommonSuffixSplit(norm, local);
                if (from.Length > 0 && to.Length > 0 && from != to)
                {
                    votes[(from, to)] = votes.GetValueOrDefault((from, to)) + 1;
                }
            }
        }

        // A prefix pair seen at least twice (or the only one) is trusted.
        foreach (var ((from, to), n) in votes.OrderByDescending(v => v.Value))
        {
            if (n >= 2 || votes.Count == 1)
            {
                _detected.Add(new PathMapping(from, to));
            }
        }
    }

    public (long? EntryId, string? By) Match(string path, long size)
    {
        if (ByMapping(path, configured) is { } a)
        {
            return (a, "mapping");
        }

        if (ByMapping(path, _detected) is { } b)
        {
            return (b, "detected");
        }

        var norm = path.Replace('\\', '/');
        var candidates = ByName(norm, size);
        if (candidates.Count == 1)
        {
            return (candidates[0].Id, "name");
        }

        if (candidates.Count > 1)
        {
            var parent = Segment(norm, 1);
            var hit = candidates.Where(c => Segment(c.Full, 1) == parent).ToList();
            if (hit.Count == 1)
            {
                return (hit[0].Id, "name");
            }
        }

        return (null, null);
    }

    /// <summary>Directory entry for an external folder path (library locations).</summary>
    public long? FindDir(string path)
    {
        foreach (var mappings in new[] { configured, (IReadOnlyList<PathMapping>)_detected })
        {
            var local = IntegrationSettings.MapPath(path, mappings);
            if (Lookup(local, dir: true) is { } id)
            {
                return id;
            }
        }

        return null;
    }

    public (long? Parent, long? Grand) Parents(long entryId)
    {
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT p.Id, p.ParentId FROM Entries e JOIN Entries p ON p.Id = e.ParentId WHERE e.Id = $id AND p.RelPath <> ''";
        q.Parameters.AddWithValue("$id", entryId);
        using var r = q.ExecuteReader();
        if (!r.Read())
        {
            return (null, null);
        }

        var parent = r.GetInt64(0);
        long? grand = r.IsDBNull(1) ? null : r.GetInt64(1);
        if (grand is { } g)
        {
            using var root = conn.CreateCommand();
            root.CommandText = "SELECT RelPath FROM Entries WHERE Id = $id";
            root.Parameters.AddWithValue("$id", g);
            if (root.ExecuteScalar() is "")
            {
                grand = null;
            }
        }

        return (parent, grand);
    }

    private long? ByMapping(string path, IReadOnlyList<PathMapping> mappings)
    {
        var local = IntegrationSettings.MapPath(path, mappings);
        return Lookup(local, dir: false);
    }

    private long? Lookup(string localPath, bool dir)
    {
        var root = roots.All.Where(r => RootRegistry.IsUnder(r.Path, localPath)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
        if (root is null)
        {
            return null;
        }

        var rel = localPath.Length == root.Path.Length ? "" : localPath[(root.Path.Length + 1)..];
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT Id FROM Entries WHERE RootId = $r AND RelPath = $p AND IsDir = $d";
        q.Parameters.AddWithValue("$r", root.Id);
        q.Parameters.AddWithValue("$p", rel);
        q.Parameters.AddWithValue("$d", dir);
        return q.ExecuteScalar() as long?;
    }

    private List<(long Id, string Full)> ByName(string path, long size)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT Id, RootId, RelPath FROM Entries WHERE IsDir = 0 AND Size = $s AND Name = $n LIMIT 10";
        q.Parameters.AddWithValue("$s", size);
        q.Parameters.AddWithValue("$n", name);
        using var r = q.ExecuteReader();
        var list = new List<(long, string)>();
        while (r.Read())
        {
            if (roots.Get(r.GetInt32(1)) is { } root)
            {
                list.Add((r.GetInt64(0), RootRegistry.Combine(root.Path, r.GetString(2))));
            }
        }

        return list;
    }

    private static string? Segment(string path, int fromEnd)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > fromEnd ? parts[^(fromEnd + 1)] : null;
    }

    /// <summary>"/data/movies/A/a.mkv" + "/library/Movies/A/a.mkv" → ("/data/movies", "/library/Movies").</summary>
    internal static (string From, string To) CommonSuffixSplit(string a, string b)
    {
        var sa = a.Split('/');
        var sb = b.Split('/');
        var i = 0;
        while (i < sa.Length - 1 && i < sb.Length - 1 && string.Equals(sa[^(i + 1)], sb[^(i + 1)], StringComparison.Ordinal))
        {
            i++;
        }

        return (string.Join('/', sa[..^i]), string.Join('/', sb[..^i]));
    }
}
