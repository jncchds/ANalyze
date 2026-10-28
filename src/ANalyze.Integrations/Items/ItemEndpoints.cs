using ANalyze.Infrastructure.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace ANalyze.Integrations.Items;

/// <summary>Shared endpoints of every <see cref="ItemIntegration"/>, under /api/v1/integrations/{id}/…</summary>
internal static class ItemEndpoints
{
    // Size of the scanned file or folder when matched, otherwise what the app reports.
    private const string SizeExpr = "COALESCE(CASE WHEN e.IsDir = 1 THEN e.TotalSize ELSE e.Size END, i.Size)";

    public static void Map(RouteGroupBuilder g, ItemIntegration plugin)
    {
        var src = plugin.Id;

        g.MapGet("/summary", (SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var totals = Db.Query(c, $"""
                SELECT COUNT(*), COALESCE(SUM({SizeExpr}), 0), SUM(CASE WHEN i.EntryId IS NOT NULL THEN 1 ELSE 0 END),
                       COUNT(DISTINCT COALESCE(i.GroupKey, i.ExternalId))
                FROM IntegrationItems i LEFT JOIN Entries e ON e.Id = i.EntryId WHERE i.Source = $s
                """, r => new { items = r.GetInt64(0), size = r.GetInt64(1), matched = r.IsDBNull(2) ? 0 : r.GetInt64(2), groups = r.GetInt64(3) }, ("$s", src)).First();
            var byKind = Db.Query(c, $"SELECT i.Kind, COUNT(*), COALESCE(SUM({SizeExpr}), 0) FROM IntegrationItems i LEFT JOIN Entries e ON e.Id = i.EntryId WHERE i.Source = $s GROUP BY i.Kind ORDER BY 3 DESC",
                r => new { kind = r.GetString(0), items = r.GetInt64(1), size = r.GetInt64(2) }, ("$s", src));
            var byState = Db.Query(c, $"SELECT COALESCE(i.State, ''), COUNT(*), COALESCE(SUM({SizeExpr}), 0) FROM IntegrationItems i LEFT JOIN Entries e ON e.Id = i.EntryId WHERE i.Source = $s GROUP BY 1 ORDER BY 3 DESC",
                r => new { state = r.GetString(0), items = r.GetInt64(1), size = r.GetInt64(2) }, ("$s", src));
            var roots = Db.Query(c, "SELECT a.EntryId, a.Label, a.Detail, e.RootId, e.RelPath, e.TotalSize FROM Annotations a JOIN Entries e ON e.Id = a.EntryId WHERE a.Source = $s AND a.Kind = 'root' ORDER BY e.TotalSize DESC",
                r => new { entryId = r.GetInt64(0), label = r.GetString(1), detail = r.IsDBNull(2) ? null : r.GetString(2), rootId = r.GetInt32(3), relPath = r.GetString(4), size = r.GetInt64(5) }, ("$s", src));
            return new { totals, byKind, byState, roots };
        });

        // Titles (movies, shows, artists, torrents…) ranked by size.
        g.MapGet("/groups", async (string? kind, string? state, string? filter, string? q, string? sort, int? offset, int? limit, SqliteSetup sqlite, IntegrationHost host, CancellationToken ct) =>
        {
            var settings = await host.LoadAsync(src, ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var having = filter switch
            {
                "watchedOld" => $"HAVING MAX(i.LastUsedAt) IS NOT NULL AND MAX(i.LastUsedAt) < {now - (settings.GetInt("watchedDays", 180) * 86400L)}",
                "neverWatched" => $"HAVING MAX(i.PlayCount) = 0 AND MIN(i.AddedAt) < {now - (settings.GetInt("unwatchedDays", 365) * 86400L)}",
                "missing" => "HAVING SUM(CASE WHEN i.EntryId IS NULL THEN 1 ELSE 0 END) > 0",
                "old" => $"HAVING MAX(i.LastUsedAt) IS NOT NULL AND MAX(i.LastUsedAt) < {now - (settings.GetInt("oldDays", 30) * 86400L)}",
                _ => "",
            };
            var order = sort switch
            {
                "title" => "title COLLATE NOCASE",
                "files" => "files DESC",
                "lastUsed" => "lastUsed DESC",
                "added" => "added DESC",
                _ => "size DESC",
            };
            var where = "i.Source = $s" + (string.IsNullOrEmpty(kind) ? "" : " AND i.Kind = $kind") + (string.IsNullOrEmpty(state) ? "" : " AND i.State = $state")
                + (string.IsNullOrWhiteSpace(q) ? "" : " AND (i.Title LIKE $q OR i.GroupTitle LIKE $q)");
            var sql = $"""
                SELECT COALESCE(i.GroupKey, i.ExternalId) AS k, MIN(COALESCE(i.GroupTitle, i.Title)) AS title, MIN(i.Year), COUNT(*) AS files, SUM({SizeExpr}) AS size,
                       SUM(CASE WHEN i.PlayCount > 0 THEN 1 ELSE 0 END), MAX(i.LastUsedAt) AS lastUsed, MIN(i.AddedAt) AS added,
                       SUM(CASE WHEN i.EntryId IS NULL THEN 1 ELSE 0 END), MIN(CASE WHEN e.IsDir = 1 THEN e.Id ELSE e.ParentId END), MIN(i.Url),
                       GROUP_CONCAT(DISTINCT i.Detail), GROUP_CONCAT(DISTINCT i.State), MIN(i.Kind)
                FROM IntegrationItems i LEFT JOIN Entries e ON e.Id = i.EntryId
                WHERE {where}
                GROUP BY k {having}
                """;
            using var c = sqlite.Open();
            var args = new (string, object?)[] { ("$s", src), ("$kind", kind), ("$state", state), ("$q", "%" + q + "%") };
            var totals = Db.Query(c, $"SELECT COUNT(*), COALESCE(SUM(size), 0) FROM ({sql})", r => (r.GetInt64(0), r.GetInt64(1)), args).First();
            var items = Db.Query(c, $"{sql} ORDER BY {order} LIMIT {Math.Clamp(limit ?? 100, 1, 1000)} OFFSET {Math.Max(0, offset ?? 0)}", r => new
            {
                key = r.GetString(0),
                title = r.IsDBNull(1) ? "?" : r.GetString(1),
                year = r.IsDBNull(2) ? (int?)null : r.GetInt32(2),
                files = r.GetInt64(3),
                size = r.GetInt64(4),
                playedFiles = r.GetInt64(5),
                lastUsedAt = r.IsDBNull(6) ? (long?)null : r.GetInt64(6),
                addedAt = r.IsDBNull(7) ? (long?)null : r.GetInt64(7),
                missingFiles = r.GetInt64(8),
                folderId = r.IsDBNull(9) ? (long?)null : r.GetInt64(9),
                url = r.IsDBNull(10) ? null : r.GetString(10),
                details = r.IsDBNull(11) ? null : r.GetString(11),
                states = r.IsDBNull(12) ? null : r.GetString(12),
                kind = r.GetString(13),
            }, args);
            return new { total = totals.Item1, totalSize = totals.Item2, items };
        });

        // Files of one title.
        g.MapGet("/group", (string key, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            return Db.Query(c, $"""
                SELECT i.Id, i.Title, i.SubTitle, i.Kind, i.State, i.Detail, i.PlayCount, i.LastUsedAt, i.AddedAt, i.ExternalPath, {SizeExpr},
                       i.EntryId, e.RelPath, i.Url, i.MatchedBy, i.IsDir
                FROM IntegrationItems i LEFT JOIN Entries e ON e.Id = i.EntryId
                WHERE i.Source = $s AND COALESCE(i.GroupKey, i.ExternalId) = $key ORDER BY i.SubTitle, i.Title
                """, r => new
            {
                id = r.GetInt64(0), title = r.GetString(1), subTitle = r.IsDBNull(2) ? null : r.GetString(2), kind = r.GetString(3),
                state = r.IsDBNull(4) ? null : r.GetString(4), detail = r.IsDBNull(5) ? null : r.GetString(5), playCount = r.GetInt32(6),
                lastUsedAt = r.IsDBNull(7) ? (long?)null : r.GetInt64(7), addedAt = r.IsDBNull(8) ? (long?)null : r.GetInt64(8),
                externalPath = r.GetString(9), size = r.GetInt64(10), entryId = r.IsDBNull(11) ? (long?)null : r.GetInt64(11),
                relPath = r.IsDBNull(12) ? null : r.GetString(12), url = r.IsDBNull(13) ? null : r.GetString(13),
                matchedBy = r.IsDBNull(14) ? null : r.GetString(14), isDir = r.GetBoolean(15),
            }, ("$s", src), ("$key", key));
        });

        // Items the app lists but ANalyze cannot find: usually a missing path mapping.
        g.MapGet("/unmatched", (int? limit, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            return Db.Query(c, $"SELECT Kind, Title, GroupTitle, ExternalPath, Size, State FROM IntegrationItems WHERE Source = $s AND EntryId IS NULL ORDER BY Size DESC LIMIT {Math.Clamp(limit ?? 100, 1, 1000)}",
                r => new { kind = r.GetString(0), title = r.GetString(1), group = r.IsDBNull(2) ? null : r.GetString(2), externalPath = r.GetString(3), size = r.GetInt64(4), state = r.IsDBNull(5) ? null : r.GetString(5) },
                ("$s", src));
        });

        // Inside the app's root folders, what the app does not track: files (mode=files) or top-level folders and files (mode=top).
        g.MapGet("/untracked", (string? mode, int? offset, int? limit, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var list = Untracked.Find(c, src, mode == "top", plugin.Views.UntrackedKinds);
            var page = list.Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 100, 1, 1000));
            return new { total = (long)list.Count, totalSize = list.Sum(x => x.Size), items = page };
        });

        // Files outside the app's items with the same content as one of its files (hash), not hard-linked to it.
        g.MapGet("/copies", (int? offset, int? limit, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var sql = """
                WITH f AS (
                    SELECT DISTINCT x.Id, x.FullHash, x.Dev, x.Inode, i.Title, COALESCE(i.GroupTitle, i.Title) AS GroupTitle
                    FROM IntegrationItems i JOIN Entries d ON d.Id = i.EntryId
                    JOIN Entries x ON x.RootId = d.RootId AND (x.Id = d.Id OR (d.IsDir = 1 AND x.RelPath >= d.RelPath || '/' AND x.RelPath < d.RelPath || '0'))
                    WHERE i.Source = $s AND x.IsDir = 0 AND x.FullHash IS NOT NULL)
                SELECT o.Id, o.RootId, o.RelPath, o.Size AS Size, o.MTime, MIN(f.GroupTitle), MIN(f.Id)
                FROM f JOIN Entries o ON o.FullHash = f.FullHash
                WHERE o.IsDir = 0 AND o.Id NOT IN (SELECT Id FROM f) AND NOT (o.Dev = f.Dev AND o.Inode = f.Inode)
                GROUP BY o.Id
                """;
            var totals = Db.Query(c, $"SELECT COUNT(*), COALESCE(SUM(Size), 0) FROM ({sql})", r => (r.GetInt64(0), r.GetInt64(1)), ("$s", src)).First();
            var items = Db.Query(c, $"{sql} ORDER BY o.Size DESC LIMIT {Math.Clamp(limit ?? 100, 1, 1000)} OFFSET {Math.Max(0, offset ?? 0)}", r => new
            {
                id = r.GetInt64(0), rootId = r.GetInt32(1), relPath = r.GetString(2), size = r.GetInt64(3), mtime = r.GetInt64(4),
                itemTitle = r.IsDBNull(5) ? null : r.GetString(5), itemEntryId = r.GetInt64(6),
            }, ("$s", src));
            return new { total = totals.Item1, totalSize = totals.Item2, items };
        });

        // Proposes path mappings by locating the app's files on disk (does not save anything).
        g.MapPost("/suggest-mappings", async (IntegrationSettings? body, IntegrationHost host, CancellationToken ct) =>
        {
            var settings = body is null ? await host.LoadAsync(src, ct) : await host.MergeAsync(src, body, ct);
            try
            {
                return Results.Ok(await plugin.SuggestAsync(settings, ct));
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException or UriFormatException)
            {
                return Results.Problem(e.Message, statusCode: 400);
            }
        });
    }
}

/// <summary>Finds entries inside an integration's root folders that none of its items cover.</summary>
internal static class Untracked
{
    public sealed record Row(long Id, int RootId, string RelPath, bool IsDir, long Size, long MTime);

    public static List<Row> Find(SqliteConnection c, string source, bool topLevel, IReadOnlyList<Domain.FileKind>? kinds)
    {
        var roots = Db.Query(c, "SELECT e.Id, e.RootId, e.RelPath FROM Annotations a JOIN Entries e ON e.Id = a.EntryId WHERE a.Source = $s AND a.Kind = 'root' AND e.IsDir = 1",
            r => (Id: r.GetInt64(0), RootId: r.GetInt32(1), Rel: r.GetString(2)), ("$s", source));
        if (roots.Count == 0)
        {
            return [];
        }

        var covered = Db.Query(c, "SELECT e.RootId, e.RelPath, e.IsDir FROM IntegrationItems i JOIN Entries e ON e.Id = i.EntryId WHERE i.Source = $s",
            r => (RootId: r.GetInt32(0), Rel: r.GetString(1), IsDir: r.GetBoolean(2)), ("$s", source));
        var coveredFiles = covered.Where(x => !x.IsDir).Select(x => Key(x.RootId, x.Rel)).ToHashSet(StringComparer.Ordinal);
        var coveredDirs = covered.Where(x => x.IsDir).Select(x => Key(x.RootId, x.Rel)).ToHashSet(StringComparer.Ordinal);
        bool IsCovered(int root, string rel)
        {
            if (coveredFiles.Contains(Key(root, rel)))
            {
                return true;
            }

            for (var p = rel; p.Length > 0; p = Parent(p))
            {
                if (coveredDirs.Contains(Key(root, p)))
                {
                    return true;
                }
            }

            return false;
        }

        var rows = new Dictionary<long, Row>();
        if (topLevel)
        {
            // Folders that hold items or other roots (category folders) are containers, not leftovers.
            var containers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (rootId, rel) in covered.Select(x => (x.RootId, x.Rel)).Concat(roots.Select(r => (r.RootId, r.Rel))))
            {
                containers.Add(Key(rootId, rel));
                for (var p = rel; p.Length > 0; p = Parent(p))
                {
                    containers.Add(Key(rootId, p));
                }
            }

            foreach (var root in roots)
            {
                foreach (var r in Db.Query(c, "SELECT Id, RootId, RelPath, IsDir, CASE WHEN IsDir = 1 THEN TotalSize ELSE Size END, MTime FROM Entries WHERE ParentId = $p",
                    Map, ("$p", root.Id)))
                {
                    if (!containers.Contains(Key(r.RootId, r.RelPath)) && !IsCovered(r.RootId, r.RelPath) && !IsTrash(r.RelPath))
                    {
                        rows[r.Id] = r;
                    }
                }
            }
        }
        else
        {
            var kindFilter = kinds is { Count: > 0 } ? $" AND Kind IN ({string.Join(",", kinds.Select(k => (int)k))})" : "";
            foreach (var root in roots)
            {
                var (from, to) = root.Rel.Length == 0 ? ("", "￿") : (root.Rel + "/", root.Rel + "0");
                foreach (var r in Db.Query(c, $"SELECT Id, RootId, RelPath, IsDir, Size, MTime FROM Entries WHERE RootId = $r AND RelPath >= $f AND RelPath < $t AND IsDir = 0{kindFilter}",
                    Map, ("$r", root.RootId), ("$f", from), ("$t", to)))
                {
                    if (!IsCovered(r.RootId, r.RelPath) && !IsTrash(r.RelPath))
                    {
                        rows[r.Id] = r;
                    }
                }
            }
        }

        return rows.Values.OrderByDescending(r => r.Size).ToList();

        static Row Map(SqliteDataReader r) => new(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetBoolean(3), r.GetInt64(4), r.GetInt64(5));
    }

    private static string Key(int root, string rel) => root + ":" + rel;

    private static string Parent(string rel) => rel.LastIndexOf('/') is var i and > 0 ? rel[..i] : "";

    private static bool IsTrash(string rel) =>
        rel == ANalyze.Infrastructure.Roots.RootRegistry.TrashFolder || rel.StartsWith(ANalyze.Infrastructure.Roots.RootRegistry.TrashFolder + "/", StringComparison.Ordinal);
}

internal static class Db
{
    public static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }

        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
        {
            list.Add(map(r));
        }

        return list;
    }
}
