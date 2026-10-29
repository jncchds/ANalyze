using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>
/// Drops indexed files and folders that the ignore list now covers, so they leave search, duplicates and insights at once instead
/// of at the next scan of their folder. Reads only the <c>(RootId, RelPath)</c> index.
/// </summary>
internal static class IgnorePruner
{
    /// <summary>Returns how many entries (outermost ignored ones; their contents go with them) were removed.</summary>
    public static int Run(SqliteConnection conn, ScanContext ctx, int rootId, IgnoreMatcher matcher)
    {
        var ignored = new List<long>();
        var ignoredPaths = new HashSet<string>(StringComparer.Ordinal);
        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT Id, RelPath FROM Entries WHERE RootId = $r AND RelPath <> '' ORDER BY RelPath";
            q.Parameters.AddWithValue("$r", rootId);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                ctx.Ct.ThrowIfCancellationRequested();
                var rel = r.GetString(1);
                if (InsideIgnored(rel, ignoredPaths))
                {
                    continue;
                }

                var slash = rel.LastIndexOf('/');
                if (matcher.IsIgnored(rel, rel[(slash + 1)..], rel.Count(ch => ch == '/') + 1))
                {
                    ignored.Add(r.GetInt64(0));
                    ignoredPaths.Add(rel);
                }
            }
        }

        if (ignored.Count == 0)
        {
            return 0;
        }

        foreach (var id in ignored)
        {
            EntryMaintenance.DeleteEntry(conn, id);
        }

        ctx.Log.LogInformation("Removed {Count} ignored files and folders from the index, e.g. {Path}", ignored.Count, ignoredPaths.First());
        return ignored.Count;
    }

    private static bool InsideIgnored(string rel, HashSet<string> ignored)
    {
        for (var i = rel.LastIndexOf('/'); i > 0; i = rel.LastIndexOf('/', i - 1))
        {
            if (ignored.Contains(rel[..i]))
            {
                return true;
            }
        }

        return false;
    }
}
