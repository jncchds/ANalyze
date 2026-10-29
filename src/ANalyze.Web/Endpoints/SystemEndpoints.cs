using System.Reflection;
using ANalyze.Actions;
using ANalyze.Domain;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Logging;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using ANalyze.Preview;
using ANalyze.Scanner;
using ANalyze.Web.Api;
using ANalyze.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ANalyze.Web.Endpoints;

public static class SystemEndpoints
{
    public const string MaskedKey = "********";

    public sealed record ScanBody(int? RootId, string? SubPath, bool? SkipHashing);

    public sealed record RootDto(int Id, string Name, string Path, bool ReadOnly, bool Missing, DateTimeOffset? LastScanAt, long? RootEntryId,
        long Size, long Files, long? DiskTotal, long? DiskFree);

    /// <summary>Cleanup-oriented built-in categories shown as "reclaimable" in the sidebar.</summary>
    private static readonly string[] Reclaimable = ["Dev junk", "Caches & temp", "Incomplete downloads", "System files"];

    public static void MapSystemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/config", (RootRegistry roots, SettingsStore settings, IOptions<AnalyzeOptions> o) => new
        {
            version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
            readOnly = roots.All.All(r => r.ReadOnly),
            forcedReadOnly = o.Value.ReadOnly,
            authRequired = !string.IsNullOrEmpty(o.Value.Password),
            ffmpeg = PreviewService.FfmpegPath is not null,
            ai = settings.Current.Ai.Enabled,
            pathTokens = PathTemplate.Tokens,
        });

        api.MapGet("/release-notes", () => ReleaseNotes.All);

        api.MapGet("/roots", (RootRegistry roots, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var drives = SafeDrives();
            return roots.All.Select(r =>
            {
                var (size, files) = r.RootEntryId is { } id
                    ? Sql.Query(c, "SELECT TotalSize, FileCount FROM Entries WHERE Id = $id", x => (x.GetInt64(0), x.GetInt64(1)), ("$id", id)).FirstOrDefault()
                    : (0, 0);
                var drive = drives.Where(d => RootRegistry.IsUnder(d.Mount, r.Path) || d.Mount == "/").OrderByDescending(d => d.Mount.Length).FirstOrDefault();
                return new RootDto(r.Id, r.Name, r.Path, r.ReadOnly, r.Missing, r.LastScanAt, r.RootEntryId, size, files, drive.Total, drive.Free);
            }).ToList();
        });

        api.MapPost("/roots/refresh", async (RootRegistry roots, CancellationToken ct) =>
        {
            await roots.SyncAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/jobs", (JobBoard board) => board.All);

        // Activity log (in memory, newest first): ?before=<id>&level=info|warning|error|debug&source=scan&q=text&limit=
        api.MapGet("/logs", (long? before, string? level, string? source, string? q, int? limit, ActivityLog activity) =>
        {
            var min = Enum.TryParse<LogSeverity>(level, ignoreCase: true, out var l) ? l
                : string.Equals(level, "info", StringComparison.OrdinalIgnoreCase) ? LogSeverity.Information
                : LogSeverity.Debug;
            return activity.Query(before, min, source, q, Math.Clamp(limit ?? 300, 1, 2000));
        });

        api.MapGet("/logs/sources", (ActivityLog activity) => activity.Sources());

        // Where folders ANalyze sees are on the host: configured ones, then Docker mounts (none in the desktop app).
        api.MapGet("/host-paths", async (SettingsStore settings, IEnumerable<IHostPathSource> sources, ILoggerFactory lf, CancellationToken ct) =>
        {
            var configured = settings.Current.HostPaths.Where(h => !string.IsNullOrWhiteSpace(h.Path) && !string.IsNullOrWhiteSpace(h.Host)).ToList();
            var auto = new List<HostPath>();
            foreach (var s in sources)
            {
                try
                {
                    auto.AddRange((await s.GetAsync(ct)).Where(a => a.Path != a.Host && configured.All(c => c.Path.TrimEnd('/') != a.Path.TrimEnd('/'))));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    lf.CreateLogger("ANalyze.Web.HostPaths").LogDebug(e, "Host paths from {Source} failed", s.GetType().Name);
                }
            }

            return new { configured, auto };
        });

        api.MapPost("/scan", (ScanBody? body, ScanQueue queue) =>
        {
            queue.Enqueue(new ScanRequest(body?.RootId, body?.SubPath, body?.SkipHashing ?? false, Reason: "Started from the UI"));
            return Results.Accepted();
        });

        api.MapPost("/scan/cancel", (ScanQueue queue) => queue.Cancel() ? Results.NoContent() : Results.NotFound());

        api.MapGet("/scans", async (int? limit, AnalyzeDbContext db, CancellationToken ct) =>
            await db.Scans.AsNoTracking().OrderByDescending(s => s.Id).Take(Math.Clamp(limit ?? 20, 1, 200)).ToListAsync(ct));

        api.MapGet("/settings", (SettingsStore settings) => Masked(settings.Current));

        api.MapPut("/settings", async (AppSettings body, SettingsStore settings, SecretProtector secrets, JobLock jobLock, ScanQueue queue, IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(body.ScanCron))
            {
                try
                {
                    Cronos.CronExpression.Parse(body.ScanCron.Trim());
                }
                catch (Cronos.CronFormatException e)
                {
                    return Results.Problem($"Invalid schedule: {e.Message}", statusCode: 400);
                }
            }

            var current = settings.Current;
            var key = body.Ai.ApiKey switch
            {
                MaskedKey => current.Ai.ApiKey,
                null or "" => null,
                var plain => secrets.Protect(plain),
            };
            var next = body with
            {
                HashThreads = Math.Clamp(body.HashThreads, 1, 16),
                HistoryDepth = Math.Clamp(body.HistoryDepth, 0, 4),
                Ai = body.Ai with
                {
                    ApiKey = key,
                    BatchSize = Math.Clamp(body.Ai.BatchSize, 1, 100),
                    MaxConcurrency = Math.Clamp(body.Ai.MaxConcurrency, 1, 8),
                    MinConfidence = Math.Clamp(body.Ai.MinConfidence, 0, 1),
                },
            };
            var written = await settings.SaveAsync(next, ct);
            // New ignore patterns take effect at once: what they cover leaves the index (and so every result list).
            if (next.IgnorePatterns.Select(p => p.Trim()).Except(current.IgnorePatterns.Select(p => p.Trim())).Any())
            {
                queue.Enqueue(new ScanRequest(Reason: "Ignore list changed") { PruneIgnored = true });
            }

            return SaveResult.Ok(Masked(next), written, jobLock, json.Value);
        });

        // Everything the sidebar needs in one call.
        api.MapGet("/summary", async (SqliteSetup sqlite, AnalyzeDbContext db, SettingsStore settings, TrashService trash, IMemoryCache cache, CancellationToken ct) =>
        {
            var reclaim = await Caches.GetAsync(cache, "summary.reclaimable", () => Task.FromResult(ComputeReclaimable(sqlite)));
            var drafts = await db.ActionPlans.CountAsync(p => p.Status == PlanStatus.Draft, ct);
            var bins = await trash.ListAsync(ct);
            var pinnedIds = settings.Current.PinnedFolders;
            var pinned = await db.Entries.AsNoTracking().Where(e => pinnedIds.Contains(e.Id))
                .Select(e => new { e.Id, e.RootId, e.Name, e.RelPath, Size = e.TotalSize }).ToListAsync(ct);
            var lastScans = await db.DirSizeHistory.AsNoTracking().Where(h => pinnedIds.Contains(h.EntryId))
                .GroupBy(h => h.EntryId)
                .Select(g => new { EntryId = g.Key, Points = g.OrderByDescending(h => h.ScanId).Select(h => h.TotalSize).Take(2).ToList() })
                .ToListAsync(ct);
            var reviewCount = await db.AiResults.CountAsync(r => !r.Applied && !r.Dismissed, ct);
            var suggestions = await db.AiRuleSuggestions.CountAsync(s => s.Status == SuggestionStatus.Pending, ct);
            return new
            {
                reclaimable = reclaim,
                trashBytes = bins.Sum(b => b.Bytes),
                trashItems = bins.Sum(b => b.Items),
                draftPlans = drafts,
                aiReview = reviewCount,
                aiSuggestions = suggestions,
                pinned = pinned.Select(p =>
                {
                    var pts = lastScans.FirstOrDefault(l => l.EntryId == p.Id)?.Points;
                    long? delta = pts is { Count: 2 } ? pts[0] - pts[1] : null;
                    return new { p.Id, p.RootId, p.Name, p.RelPath, p.Size, delta };
                }),
            };
        });

        api.MapPost("/pins/{id:long}", async (long id, SettingsStore settings, CancellationToken ct) =>
        {
            var s = settings.Current;
            if (!s.PinnedFolders.Contains(id))
            {
                await settings.SaveAsync(s with { PinnedFolders = [.. s.PinnedFolders, id] }, ct);
            }

            return Results.NoContent();
        });

        api.MapDelete("/pins/{id:long}", async (long id, SettingsStore settings, CancellationToken ct) =>
        {
            var s = settings.Current;
            await settings.SaveAsync(s with { PinnedFolders = s.PinnedFolders.Where(x => x != id).ToList() }, ct);
            return Results.NoContent();
        });
    }

    private static object Masked(AppSettings s) => s with { Ai = s.Ai with { ApiKey = string.IsNullOrEmpty(s.Ai.ApiKey) ? null : MaskedKey } };

    private static object ComputeReclaimable(SqliteSetup sqlite)
    {
        using var c = sqlite.Open();
        var dup = Sql.Query(c, $"""
            SELECT COALESCE(SUM(w), 0), COUNT(*) FROM (
                SELECT (COUNT(DISTINCT {Sql.Identity}) - 1) * MAX(Size) AS w FROM Entries
                WHERE FullHash IS NOT NULL AND IsDir = 0 AND IsSymlink = 0
                GROUP BY FullHash HAVING COUNT(DISTINCT {Sql.Identity}) > 1)
            """, r => new { bytes = r.GetInt64(0), groups = r.GetInt64(1) }).First();
        var cats = Sql.Query(c, $"""
            SELECT c.Id, c.Name, c.Color, COUNT(e.Id), COALESCE(SUM(e.Size), 0) FROM Categories c
            LEFT JOIN Entries e ON e.CategoryId = c.Id AND e.IsDir = 0
            WHERE c.Name IN ({string.Join(",", Reclaimable.Select((_, i) => "$c" + i))})
            GROUP BY c.Id, c.Name, c.Color
            """, r => new { id = r.GetInt32(0), name = r.GetString(1), color = r.GetString(2), files = r.GetInt64(3), bytes = r.GetInt64(4) },
            Reclaimable.Select((n, i) => ("$c" + i, (object?)n)).ToArray());
        return new { duplicates = dup, categories = cats.OrderByDescending(x => x.bytes).ToList() };
    }

    private static List<(string Mount, long? Total, long? Free)> SafeDrives()
    {
        var list = new List<(string, long?, long?)>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.IsReady && d.TotalSize > 0)
                    {
                        list.Add((d.RootDirectory.FullName.TrimEnd('/') is "" ? "/" : d.RootDirectory.FullName.TrimEnd('/'), d.TotalSize, d.AvailableFreeSpace));
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // unreadable mount
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return list;
    }
}
