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
            var files = Db.Query(c, "SELECT State, COUNT(*), COALESCE(SUM(Size), 0) FROM BackupFiles WHERE IsDir = 0 GROUP BY State",
                r => new { state = r.GetString(0), files = r.GetInt64(1), size = r.GetInt64(2) });
            var verified = Db.Query(c, "SELECT COUNT(*) FROM BackupFiles WHERE IsDir = 0 AND Verified = 1", r => r.GetInt64(0)).First();
            var folders = Db.Query(c, $"SELECT COUNT(*), COALESCE(SUM(State = '{BackupStates.Synced}'), 0) FROM BackupFiles WHERE IsDir = 1",
                r => new { total = r.GetInt64(0), synced = r.GetInt64(1) }).First();
            var s = settings.Current;
            return new
            {
                snapshot = snap,
                targets = snap?.Targets.Select(t => new
                {
                    t.ParentPath, t.LocalPath, t.RootId, t.RelPath, t.EntryId,
                    rootName = t.RootId is { } id ? roots.Get(id)?.Name : null,
                }),
                files,
                verified,
                folders,
                threshold = BackupStates.FolderThreshold,
                local = new { s.HashAllFiles, s.VerifyAfterDays },
            };
        });

        // One folder: its own state, its sub-folders with their state, and its direct files counted by state.
        g.MapGet("/folders", (int rootId, string? path, SqliteSetup sqlite) =>
        {
            var p = (path ?? "").Trim('/');
            var prefix = p.Length == 0 ? "" : p + "/";
            using var c = sqlite.Open();
            var self = Db.Query(c, "SELECT EntryId, State, Children, SyncedChildren, Size FROM BackupFiles WHERE LocalRootId = $r AND LocalRelPath = $p AND IsDir = 1",
                r => new { entryId = r.GetInt64(0), state = r.GetString(1), children = r.GetInt32(2), synced = r.GetInt32(3), size = r.GetInt64(4) }, ("$r", rootId), ("$p", p)).FirstOrDefault();
            var range = p.Length == 0 ? "LocalRelPath <> ''" : "LocalRelPath >= $from AND LocalRelPath < $to";
            var direct = $"LocalRootId = $r AND {range} AND instr(substr(LocalRelPath, length($prefix) + 1), '/') = 0";
            var args = new (string, object?)[] { ("$r", rootId), ("$from", p + "/"), ("$to", p + "0"), ("$prefix", prefix) };
            var folders = Db.Query(c, $"""
                SELECT EntryId, substr(LocalRelPath, length($prefix) + 1), LocalRelPath, State, Children, SyncedChildren, Size
                FROM BackupFiles WHERE IsDir = 1 AND {direct}
                ORDER BY State = '{BackupStates.Synced}', Size DESC LIMIT 1000
                """, r => new { entryId = r.GetInt64(0), name = r.GetString(1), path = r.GetString(2), state = r.GetString(3), children = r.GetInt32(4), synced = r.GetInt32(5), size = r.GetInt64(6) }, args);
            var files = Db.Query(c, $"SELECT State, COUNT(*), COALESCE(SUM(Size), 0) FROM BackupFiles WHERE IsDir = 0 AND {direct} GROUP BY State",
                r => new { state = r.GetString(0), files = r.GetInt64(1), size = r.GetInt64(2) }, args);
            return new { path = p, self, folders, files };
        });

        // Where a labelled file or folder is in the folder view (from a label in the browser): the folder itself, or a file's folder.
        g.MapGet("/locate", (long entryId, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var row = Db.Query(c, "SELECT LocalRootId, LocalRelPath, IsDir FROM BackupFiles WHERE EntryId = $e", r => (Root: r.GetInt32(0), Rel: r.GetString(1), Dir: r.GetBoolean(2)), ("$e", entryId));
            return row.Count == 0
                ? Results.NotFound()
                : Results.Ok(new { rootId = row[0].Root, path = row[0].Dir ? row[0].Rel : BackupSync.Parent(row[0].Rel), file = row[0].Dir ? null : row[0].Rel });
        });

        // Files by state, inside a folder (direct=true: only files directly in it); largest first.
        g.MapGet("/files", async (string? state, int? rootId, string? path, bool? direct, string? q, string? sort, int? offset, int? limit, SqliteSetup sqlite, RootRegistry roots, CancellationToken ct) =>
        {
            var snap = await plugin.LoadSnapshotAsync(ct);
            var where = new List<string> { "b.IsDir = 0" };
            var p = (path ?? "").Trim('/');
            var prefix = p.Length == 0 ? "" : p + "/";
            if (!string.IsNullOrEmpty(state))
            {
                where.Add("b.State = $state");
            }

            if (rootId is not null)
            {
                where.Add("b.LocalRootId = $r");
                if (p.Length > 0)
                {
                    where.Add("b.LocalRelPath >= $from AND b.LocalRelPath < $to");
                }

                if (direct == true)
                {
                    where.Add("instr(substr(b.LocalRelPath, length($prefix) + 1), '/') = 0");
                }
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                where.Add("b.LocalRelPath LIKE $q");
            }

            var sql = "WHERE " + string.Join(" AND ", where);
            var order = sort == "path" ? "b.LocalRootId, b.LocalRelPath" : "b.Size DESC";
            var args = new (string, object?)[] { ("$state", state), ("$r", rootId), ("$from", p + "/"), ("$to", p + "0"), ("$prefix", prefix), ("$q", "%" + q?.Trim() + "%") };
            using var c = sqlite.Open();
            var totals = Db.Query(c, $"SELECT COUNT(*), COALESCE(SUM(b.Size), 0) FROM BackupFiles b {sql}", r => (r.GetInt64(0), r.GetInt64(1)), args).First();
            var items = Db.Query(c, $"""
                SELECT b.Id, b.State, b.EntryId, b.LocalRootId, b.LocalRelPath, b.Size, b.ParentSize, b.Verified
                FROM BackupFiles b {sql} ORDER BY {order} LIMIT {Math.Clamp(limit ?? 200, 1, 1000)} OFFSET {Math.Max(0, offset ?? 0)}
                """, r => new
            {
                id = r.GetInt64(0),
                state = r.GetString(1),
                entryId = r.GetInt64(2),
                rootId = r.GetInt32(3),
                relPath = r.GetString(4),
                parentPath = ParentPath(roots, snap, r.GetInt32(3), r.GetString(4)),
                size = r.GetInt64(5),
                parentSize = r.IsDBNull(6) ? (long?)null : r.GetInt64(6),
                verified = r.GetBoolean(7),
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

    /// <summary>Where a local file is (or would be) on the parent.</summary>
    internal static string? ParentPath(RootRegistry roots, BackupSnapshot? snap, int rootId, string relPath) =>
        snap is null || roots.Get(rootId) is not { } root ? null : BackupSync.ToParentPath(RootRegistry.Combine(root.Path, relPath), MappingsOf(snap));

    /// <summary>The mappings of the last sync, plus each target as a mapping (same paths need none).</summary>
    private static IReadOnlyList<PathMapping> MappingsOf(BackupSnapshot snap) =>
        [.. snap.Mappings, .. snap.Targets.Select(t => new PathMapping(t.ParentPath, t.LocalPath))];
}
