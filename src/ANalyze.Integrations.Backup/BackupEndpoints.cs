using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using ANalyze.Integrations.Items;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ANalyze.Integrations.Backup;

/// <summary>Child-side views of the comparison, under /api/v1/integrations/parent/…</summary>
internal static class BackupEndpoints
{
    public static void Map(RouteGroupBuilder g, ParentIntegration plugin)
    {
        g.MapGet("/summary", async (SqliteSetup sqlite, RootRegistry roots, SettingsStore settings, CancellationToken ct) =>
        {
            var snap = await plugin.LoadSnapshotAsync(ct);
            using var c = sqlite.Open();
            var byState = Db.Query(c, "SELECT State, COUNT(*), COALESCE(SUM(Size), 0) FROM BackupFiles GROUP BY State",
                r => new { state = r.GetString(0), files = r.GetInt64(1), size = r.GetInt64(2) });
            var unmapped = Db.Query(c, "SELECT COUNT(*), COALESCE(SUM(Size), 0) FROM BackupFiles WHERE LocalRootId IS NULL",
                r => new { files = r.GetInt64(0), size = r.GetInt64(1) }).First();
            var bySide = Db.Query(c, $"SELECT COALESCE(Side, ''), COUNT(*) FROM BackupFiles WHERE State = '{BackupStates.Corrupt}' GROUP BY 1",
                r => new { side = r.GetString(0), files = r.GetInt64(1) });
            var s = settings.Current;
            return new
            {
                snapshot = snap,
                targets = snap?.Targets.Select(t => new
                {
                    t.ParentPath, t.LocalPath, t.RootId, t.RelPath, t.EntryId,
                    rootName = t.RootId is { } id ? roots.Get(id)?.Name : null,
                }),
                byState,
                corruptBySide = bySide,
                unmapped,
                local = new { s.HashAllFiles, s.VerifyAfterDays },
            };
        });

        // Sub-folders of a folder here with file counts per state; files directly inside come as "here".
        g.MapGet("/folders", (int rootId, string? path, SqliteSetup sqlite) =>
        {
            var p = (path ?? "").Trim('/');
            var prefix = p.Length == 0 ? "" : p + "/";
            using var c = sqlite.Open();
            var rows = Db.Query(c, $"""
                SELECT CASE WHEN instr(rest, '/') > 0 THEN substr(rest, 1, instr(rest, '/') - 1) END AS dir, State, COUNT(*), COALESCE(SUM(Size), 0)
                FROM (SELECT State, Size, substr(LocalRelPath, length($prefix) + 1) AS rest FROM BackupFiles
                      WHERE LocalRootId = $r {(p.Length == 0 ? "" : "AND LocalRelPath >= $from AND LocalRelPath < $to")})
                GROUP BY dir, State
                """, r => (Dir: r.IsDBNull(0) ? null : r.GetString(0), State: r.GetString(1), Files: r.GetInt64(2), Size: r.GetInt64(3)),
                ("$r", rootId), ("$prefix", prefix), ("$from", p + "/"), ("$to", p + "0"));

            object Group(IEnumerable<(string? Dir, string State, long Files, long Size)> g) => new
            {
                files = g.Where(x => x.State != BackupStates.Extra).Sum(x => x.Files),
                size = g.Where(x => x.State != BackupStates.Extra).Sum(x => x.Size),
                states = g.ToDictionary(x => x.State, x => new { files = x.Files, size = x.Size }),
            };

            using var find = c.CreateCommand();
            find.CommandText = "SELECT Id FROM Entries WHERE RootId = $r AND RelPath = $p AND IsDir = 1";
            find.Parameters.AddWithValue("$r", rootId);
            find.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("$p", null));
            long? Dir(string rel)
            {
                find.Parameters["$p"].Value = rel;
                return find.ExecuteScalar() as long?;
            }

            static long Problems(IEnumerable<(string? Dir, string State, long Files, long Size)> g) =>
                g.Where(x => x.State is BackupStates.Missing or BackupStates.Corrupt or BackupStates.Outdated).Sum(x => x.Files);

            var folders = rows.Where(x => x.Dir is not null).GroupBy(x => x.Dir!)
                .OrderByDescending(g => Problems(g) > 0).ThenByDescending(g => g.Sum(x => x.Size))
                .Take(500)
                .Select(g => new { name = g.Key, path = prefix + g.Key, entryId = Dir(prefix + g.Key), counts = Group(g) })
                .ToList();
            var here = rows.Where(x => x.Dir is null).ToList();
            return new { path = p, entryId = Dir(p), folders, here = here.Count == 0 ? null : Group(here) };
        });

        // Files by state, optionally inside a folder; largest first.
        g.MapGet("/files", (string? state, string? side, int? rootId, string? path, string? q, string? sort, int? offset, int? limit, SqliteSetup sqlite) =>
        {
            var where = new List<string>();
            var p = (path ?? "").Trim('/');
            if (!string.IsNullOrEmpty(state))
            {
                where.Add("b.State = $state");
            }

            if (!string.IsNullOrEmpty(side))
            {
                where.Add(side == "unknown" ? "b.Side IS NULL" : "b.Side = $side");
            }

            if (rootId is not null)
            {
                where.Add("b.LocalRootId = $r");
                if (p.Length > 0)
                {
                    where.Add("b.LocalRelPath >= $from AND b.LocalRelPath < $to");
                }
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                where.Add("(b.LocalRelPath LIKE $q OR b.SourcePath LIKE $q)");
            }

            var sql = where.Count == 0 ? "" : "WHERE " + string.Join(" AND ", where);
            var order = sort == "path" ? "b.LocalRootId, b.LocalRelPath, b.SourcePath" : "b.Size DESC";
            var args = new (string, object?)[] { ("$state", state), ("$side", side), ("$r", rootId), ("$from", p + "/"), ("$to", p + "0"), ("$q", "%" + q?.Trim() + "%") };
            using var c = sqlite.Open();
            var totals = Db.Query(c, $"SELECT COUNT(*), COALESCE(SUM(b.Size), 0) FROM BackupFiles b {sql}", r => (r.GetInt64(0), r.GetInt64(1)), args).First();
            var items = Db.Query(c, $"""
                SELECT b.Id, b.State, b.Side, b.EntryId, b.LocalRootId, b.LocalRelPath, b.SourcePath, b.Size, b.MTime, b.LocalSize, b.LocalMTime,
                       b.Hash IS NOT NULL, b.LocalHash IS NOT NULL
                FROM BackupFiles b {sql} ORDER BY {order} LIMIT {Math.Clamp(limit ?? 200, 1, 1000)} OFFSET {Math.Max(0, offset ?? 0)}
                """, r => new
            {
                id = r.GetInt64(0),
                state = r.GetString(1),
                side = r.IsDBNull(2) ? null : r.GetString(2),
                entryId = r.IsDBNull(3) ? (long?)null : r.GetInt64(3),
                rootId = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                relPath = r.IsDBNull(5) ? null : r.GetString(5),
                sourcePath = r.IsDBNull(6) ? null : r.GetString(6),
                size = r.GetInt64(7),
                mtime = r.GetInt64(8),
                localSize = r.IsDBNull(9) ? (long?)null : r.GetInt64(9),
                localMTime = r.IsDBNull(10) ? (long?)null : r.GetInt64(10),
                parentHashed = r.GetBoolean(11),
                localHashed = r.GetBoolean(12),
            }, args);
            return new { total = totals.Item1, totalSize = totals.Item2, items };
        });

        g.MapPost("/suggest-mappings", async (IntegrationSettings? body, IntegrationHost host, CancellationToken ct) =>
        {
            var settings = body is null ? await host.LoadAsync(plugin.Id, ct) : await host.MergeAsync(plugin.Id, body, ct);
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
