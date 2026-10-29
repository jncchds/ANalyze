using ANalyze.Infrastructure.Roots;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Integrations.Items;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;

namespace ANalyze.Integrations.Plex;

/// <summary>/api/v1/integrations/plex/…</summary>
internal static class PlexEndpoints
{
    public static void Map(RouteGroupBuilder g, PlexIntegration plex)
    {
        // Libraries with their size and how much of it was watched.
        g.MapGet("/summary", (SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var sections = Query(c, """
                SELECT p.SectionKey, p.SectionTitle, MIN(p.Type), COUNT(DISTINCT p.RatingKey), COUNT(*),
                       SUM(COALESCE(e.Size, p.Size)), SUM(CASE WHEN p.ViewCount > 0 THEN COALESCE(e.Size, p.Size) ELSE 0 END),
                       SUM(CASE WHEN p.EntryId IS NULL THEN 1 ELSE 0 END)
                FROM PlexItems p LEFT JOIN Entries e ON e.Id = p.EntryId
                GROUP BY p.SectionKey, p.SectionTitle ORDER BY 6 DESC
                """, r => new
            {
                key = r.GetString(0), title = r.GetString(1), type = r.GetString(2), items = r.GetInt64(3), files = r.GetInt64(4),
                size = r.GetInt64(5), watchedSize = r.GetInt64(6), missing = r.GetInt64(7),
            });
            var matchedBy = Query(c, "SELECT COALESCE(MatchedBy, 'none'), COUNT(*) FROM PlexItems GROUP BY MatchedBy", r => new { by = r.GetString(0), files = r.GetInt64(1) });
            return new { sections, matchedBy };
        });

        // Titles ranked by size: movies, shows (all episodes) or artists (all tracks).
        g.MapGet("/top", async (string? type, string? section, string? q, string? key, string? filter, string? sort, int? offset, int? limit, SqliteSetup sqlite, IntegrationHost host, CancellationToken ct) =>
        {
            var t = type is "show" or "artist" ? type : "movie";
            var itemType = t switch { "show" => "episode", "artist" => "track", _ => "movie" };
            var groupKey = t == "movie" ? "p.RatingKey" : "p.SectionKey || ':' || p.ShowTitle";
            var title = t == "movie" ? "MIN(p.Title)" : "MIN(p.ShowTitle)";
            // Folder to open in the browser: the movie folder, or the show/artist folder above season/album folders.
            var folder = t == "movie" ? "MIN(e.ParentId)" : "MIN(pa.ParentId)";
            var settings = await host.LoadAsync(plex.Id, ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var having = filter switch
            {
                "watchedOld" => $"HAVING MAX(p.LastViewedAt) IS NOT NULL AND MAX(p.LastViewedAt) < {now - (settings.GetInt("watchedDays", 180) * 86400L)}",
                "neverWatched" => $"HAVING MAX(p.ViewCount) = 0 AND MIN(p.AddedAt) < {now - (settings.GetInt("unwatchedDays", 365) * 86400L)}",
                "multiVersion" => "HAVING MAX(p.Versions) > 1",
                "missing" => "HAVING SUM(CASE WHEN p.EntryId IS NULL THEN 1 ELSE 0 END) > 0",
                _ => "",
            };
            var order = sort switch
            {
                "title" => "title COLLATE NOCASE",
                "files" => "files DESC",
                "lastViewed" => "lastViewed DESC",
                "added" => "added DESC",
                _ => "size DESC",
            };
            var where = "p.Type = $type" + (string.IsNullOrEmpty(section) ? "" : " AND p.SectionKey = $section")
                + (string.IsNullOrWhiteSpace(q) ? "" : " AND (p.Title LIKE $q OR p.ShowTitle LIKE $q)")
                + (string.IsNullOrEmpty(key) ? "" : $" AND {groupKey} = $key");
            var sql = $"""
                SELECT {groupKey} AS k, {title} AS title, MIN(p.Year), MIN(p.SectionTitle), COUNT(*) AS files, SUM(COALESCE(e.Size, p.Size)) AS size,
                       SUM(CASE WHEN p.ViewCount > 0 THEN 1 ELSE 0 END), MAX(p.LastViewedAt) AS lastViewed, MIN(p.AddedAt) AS added,
                       MAX(p.Versions), SUM(CASE WHEN p.EntryId IS NULL THEN 1 ELSE 0 END), {folder}, MIN(p.Url), GROUP_CONCAT(DISTINCT p.Resolution)
                FROM PlexItems p LEFT JOIN Entries e ON e.Id = p.EntryId LEFT JOIN Entries pa ON pa.Id = e.ParentId
                WHERE {where}
                GROUP BY k {having}
                """;
            using var c = sqlite.Open();
            var args = new (string, object?)[] { ("$type", itemType), ("$section", section), ("$q", "%" + q + "%"), ("$key", key) };
            var totals = Query(c, $"SELECT COUNT(*), COALESCE(SUM(size), 0) FROM ({sql})", r => (r.GetInt64(0), r.GetInt64(1)), args).First();
            var items = Query(c, $"{sql} ORDER BY {order} LIMIT {Math.Clamp(limit ?? 100, 1, 1000)} OFFSET {Math.Max(0, offset ?? 0)}", r => new
            {
                key = r.GetString(0),
                title = r.IsDBNull(1) ? "?" : r.GetString(1),
                year = r.IsDBNull(2) ? (int?)null : r.GetInt32(2),
                section = r.GetString(3),
                files = r.GetInt64(4),
                size = r.GetInt64(5),
                watchedFiles = r.GetInt64(6),
                lastViewedAt = r.IsDBNull(7) ? (long?)null : r.GetInt64(7),
                addedAt = r.IsDBNull(8) ? (long?)null : r.GetInt64(8),
                versions = r.GetInt32(9),
                missingFiles = r.GetInt64(10),
                folderId = r.IsDBNull(11) ? (long?)null : r.GetInt64(11),
                url = r.IsDBNull(12) ? null : r.GetString(12),
                resolutions = r.IsDBNull(13) ? null : r.GetString(13),
            }, args);
            return new { type = t, total = totals.Item1, totalSize = totals.Item2, items };
        });

        // Where a labelled file or folder belongs (from a label in the browser): the movie, show or artist it is part of, when there is exactly one.
        g.MapGet("/locate", (long entryId, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            if (Db.EntryScope(c, entryId) is not { } args)
            {
                return Results.NotFound();
            }

            var hits = Query(c, $"""
                SELECT DISTINCT CASE p.Type WHEN 'episode' THEN 'show' WHEN 'track' THEN 'artist' ELSE 'movie' END,
                       CASE p.Type WHEN 'movie' THEN p.RatingKey ELSE p.SectionKey || ':' || p.ShowTitle END
                FROM PlexItems p JOIN Entries e ON e.Id = p.EntryId WHERE {Db.InScope("e")} LIMIT 2
                """, r => (Type: r.GetString(0), Key: r.GetString(1)), args);
            return Results.Ok(new { type = hits.Count > 0 ? hits[0].Type : null, key = hits.Count == 1 ? hits[0].Key : null });
        });

        // Files of one title: versions of a movie, episodes of a show, tracks of an artist.
        g.MapGet("/title", (string type, string key, SqliteSetup sqlite, RootRegistry roots) =>
        {
            using var c = sqlite.Open();
            var where = type == "movie" ? "p.RatingKey = $key" : "p.SectionKey || ':' || p.ShowTitle = $key";
            return Query(c, $"""
                SELECT p.Id, p.Title, p.ParentTitle, p.Season, p.Episode, p.ViewCount, p.LastViewedAt, p.AddedAt, p.PlexPath, COALESCE(e.Size, p.Size),
                       p.Resolution, p.EntryId, e.RelPath, e.RootId, p.Url, p.MatchedBy, p.CheckedPath
                FROM PlexItems p LEFT JOIN Entries e ON e.Id = p.EntryId
                WHERE {where} ORDER BY p.Season, p.ParentTitle, p.Episode, p.Title
                """, r => new
            {
                id = r.GetInt64(0), title = r.GetString(1), parent = r.IsDBNull(2) ? null : r.GetString(2),
                season = r.IsDBNull(3) ? (int?)null : r.GetInt32(3), episode = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                viewCount = r.GetInt32(5), lastViewedAt = r.IsDBNull(6) ? (long?)null : r.GetInt64(6), addedAt = r.IsDBNull(7) ? (long?)null : r.GetInt64(7),
                plexPath = r.GetString(8), size = r.GetInt64(9), resolution = r.IsDBNull(10) ? null : r.GetString(10),
                entryId = r.IsDBNull(11) ? (long?)null : r.GetInt64(11), relPath = r.IsDBNull(12) ? null : r.GetString(12),
                rootId = r.IsDBNull(13) ? (int?)null : r.GetInt32(13), url = r.IsDBNull(14) ? null : r.GetString(14),
                matchedBy = r.IsDBNull(15) ? null : r.GetString(15), checkedPath = r.IsDBNull(16) ? null : r.GetString(16),
                localPath = r.IsDBNull(13) ? null : roots.Get(r.GetInt32(13)) is { } root ? RootRegistry.Combine(root.Path, r.GetString(12)) : null,
            }, ("$key", key));
        });

        // Video/audio files inside Plex library folders that Plex does not list (unscanned, misnamed or leftovers).
        g.MapGet("/not-in-plex", (int? offset, int? limit, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var libs = Query(c, "SELECT e.RootId, e.RelPath FROM Annotations a JOIN Entries e ON e.Id = a.EntryId WHERE a.Source = 'plex' AND a.Kind = 'library'",
                r => (Root: r.GetInt32(0), Rel: r.GetString(1)));
            if (libs.Count == 0)
            {
                return Results.Ok(new { total = 0L, totalSize = 0L, items = Array.Empty<object>() });
            }

            var ranges = string.Join(" OR ", libs.Select((_, i) => $"(e.RootId = $r{i} AND (e.RelPath >= $f{i} AND e.RelPath < $t{i}))"));
            var args = libs.SelectMany((l, i) => new (string, object?)[] { ($"$r{i}", l.Root), ($"$f{i}", l.Rel.Length == 0 ? "" : l.Rel + "/"), ($"$t{i}", l.Rel.Length == 0 ? "￿" : l.Rel + "0") }).ToArray();
            var where = $"""
                e.IsDir = 0 AND e.Kind IN ({(int)FileKind.Video}, {(int)FileKind.Audio}) AND ({ranges})
                AND NOT EXISTS (SELECT 1 FROM PlexItems p WHERE p.EntryId = e.Id)
                """;
            var totals = Query(c, $"SELECT COUNT(*), COALESCE(SUM(e.Size), 0) FROM Entries e WHERE {where}", r => (r.GetInt64(0), r.GetInt64(1)), args).First();
            var items = Query(c, $"SELECT e.Id, e.RootId, e.RelPath, e.Size, e.MTime FROM Entries e WHERE {where} ORDER BY e.Size DESC LIMIT {Math.Clamp(limit ?? 100, 1, 1000)} OFFSET {Math.Max(0, offset ?? 0)}",
                r => new { id = r.GetInt64(0), rootId = r.GetInt32(1), relPath = r.GetString(2), size = r.GetInt64(3), mtime = r.GetInt64(4) }, args);
            return Results.Ok(new { total = totals.Item1, totalSize = totals.Item2, items });
        });

        // Plex files ANalyze could not find: usually a missing path mapping.
        g.MapGet("/unmatched", (int? limit, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            return Query(c, $"SELECT SectionTitle, Title, ShowTitle, PlexPath, Size, CheckedPath FROM PlexItems WHERE EntryId IS NULL ORDER BY Size DESC LIMIT {Math.Clamp(limit ?? 100, 1, 1000)}",
                r => new { section = r.GetString(0), title = r.GetString(1), show = r.IsDBNull(2) ? null : r.GetString(2), plexPath = r.GetString(3), size = r.GetInt64(4), checkedPath = r.IsDBNull(5) ? null : r.GetString(5) });
        });

        // Proposes path mappings by locating sample Plex files on disk (does not save anything).
        g.MapPost("/suggest-mappings", async (IntegrationSettings? body, IntegrationHost host, SqliteSetup sqlite, ANalyze.Infrastructure.Roots.RootRegistry roots, CancellationToken ct) =>
        {
            var settings = body is null ? await host.LoadAsync(plex.Id, ct) : await host.MergeAsync(plex.Id, body, ct);
            try
            {
                var client = plex.Client(settings);
                var files = new List<(string, long)>();
                foreach (var s in await client.SectionsAsync(ct))
                {
                    var n = 0;
                    await foreach (var f in client.FilesAsync(s, ct))
                    {
                        files.Add((f.File, f.Size));
                        if (++n >= 40)
                        {
                            break;
                        }
                    }
                }

                using var c = sqlite.Open();
                var matcher = new PathMatcher(c, roots, []);
                matcher.Detect(files, sample: files.Count);
                return Results.Ok(matcher.Detected);
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                return Results.Problem(e.Message, statusCode: 400);
            }
        });
    }

    private static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
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
