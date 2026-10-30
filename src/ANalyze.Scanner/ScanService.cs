using ANalyze.Domain;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Logging;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>
/// What to scan: everything, one root, or a folder inside a root. Two lighter jobs share the scan queue (one at a time,
/// same progress and cancel button): <see cref="PruneIgnored"/> only removes newly ignored entries from the index, and
/// <see cref="VerifyExtractions"/> only checks those extracted archives against their archives' CRC32s.
/// </summary>
public sealed record ScanRequest(int? RootId = null, string? SubPath = null, bool SkipHashing = false, string? Reason = null)
{
    public bool PruneIgnored { get; init; }

    public IReadOnlyList<long>? VerifyExtractions { get; init; }
}

/// <summary>
/// Runs a scan: the walk (and the folder size history) holds the <see cref="JobLock"/> alone; everything after it (detect,
/// categorize, archives, hashing, duplicates, verification) is handed to <see cref="ScanProcessor"/>, which runs in the
/// background next to other shared work and steps aside for the next walk.
/// </summary>
public sealed class ScanService(
    SqliteSetup sqlite,
    IDbContextFactory<AnalyzeDbContext> dbf,
    RootRegistry roots,
    SettingsStore settings,
    JobBoard board,
    JobLock jobLock,
    ScanProcessor processor,
    ILogger<ScanService> log)
{
    /// <summary>Raised when a scan's walk completed (the tree is current; types, hashes etc. follow in the background).</summary>
    public event Action<long>? Completed;

    /// <param name="lockHeld">The caller already holds the job lock alone (a plan rescanning the folders it changed).</param>
    public async Task RunAsync(ScanRequest req, CancellationToken ct, bool lockHeld = false)
    {
        if (lockHeld && !req.PruneIgnored && req.VerifyExtractions is null)
        {
            var (id, after) = await WalkAsync(req, ct);
            if (after is not null)
            {
                processor.Request(after);
                Completed?.Invoke(id);
            }

            return;
        }

        if (req.PruneIgnored)
        {
            using (await jobLock.AcquireAsync("scan", ct))
            {
                await RunLightAsync(req, ct);
            }

            return;
        }

        if (req.VerifyExtractions is not null)
        {
            using (await jobLock.AcquireSharedAsync("checking extractions", ct))
            {
                await RunLightAsync(req, ct);
            }

            return;
        }

        if (jobLock.Holder is { } busy)
        {
            log.LogInformation("Scan waits for {Holder} to finish", busy);
        }

        ProcessRequest? next;
        long scanId;
        using (await jobLock.AcquireAsync("scan", ct))
        {
            (scanId, next) = await WalkAsync(req, ct);
        }

        if (next is not null)
        {
            processor.Request(next);
            Completed?.Invoke(scanId);
        }
    }

    /// <summary>The exclusive part of a scan; returns what the processor should do next (null when the walk did not complete).</summary>
    private async Task<(long ScanId, ProcessRequest? Next)> WalkAsync(ScanRequest req, CancellationToken ct)
    {

        var s = settings.Current;
        var targets = roots.All.Where(r => !r.Missing && (req.RootId is null || r.Id == req.RootId)).ToList();
        var full = string.IsNullOrEmpty(req.SubPath);
        var title = req.RootId is null ? "All roots" : targets.FirstOrDefault()?.Name + (full ? "" : "/" + req.SubPath);

        ScanRun run;
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            run = new ScanRun { RootId = req.RootId, SubPath = req.SubPath, Status = ScanStatus.Running, StartedAt = DateTimeOffset.UtcNow, Message = req.Reason };
            db.Scans.Add(run);
            await db.SaveChangesAsync(ct);
        }

        var ctx = new ScanContext(board, run.Id, title ?? "Scan", run.StartedAt, ct, log);
        ctx.Report(force: true);
        log.LogInformation("Scan {Id} started: {Title}{Reason}. Roots: {Roots}", run.Id, title ?? "Scan",
            req.Reason is null ? "" : $" ({req.Reason})", string.Join(", ", targets.Select(t => t.Path)));
        foreach (var missing in roots.All.Where(r => r.Missing && (req.RootId is null || r.Id == req.RootId)))
        {
            log.LogWarning("Root {Path} is not mounted (or not readable); it is skipped", missing.Path);
        }

        var phaseClock = System.Diagnostics.Stopwatch.StartNew();
        var status = ScanStatus.Completed;
        string? message = null;
        try
        {
            using var conn = sqlite.Open();
            foreach (var root in targets)
            {
                WalkRoot(conn, ctx, root, req.SubPath, s);
            }

            log.LogInformation("Walk finished in {Time}: {Files} files in {Dirs} folders ({Size}); {Added} new, {Changed} changed, {Removed} removed, {Errors} errors",
                Format.Duration(phaseClock.Elapsed), ctx.FilesSeen, ctx.DirsSeen, Format.Bytes(ctx.BytesSeen), ctx.Added, ctx.Changed, ctx.Removed, ctx.Errors);

            if (full)
            {
                ctx.StartPhase("finishing");
                log.LogInformation("Finishing: saving the folder size history");
                foreach (var root in targets)
                {
                    Snapshot(conn, run.Id, root.Id, s.HistoryDepth);
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = ScanStatus.Cancelled;
            message = "Cancelled";
        }
        catch (Exception e)
        {
            log.LogError(e, "Scan {Id} failed", run.Id);
            status = ScanStatus.Failed;
            message = e.Message;
        }

        if (status != ScanStatus.Completed)
        {
            // A walk that stopped half-way leaves the folders above the last finished one with old or running totals.
            try
            {
                log.LogInformation("Recomputing folder sizes after the {Status} scan", status.ToString().ToLowerInvariant());
                using var conn = sqlite.Open();
                foreach (var root in targets)
                {
                    RollupDirectories(conn, root.Id);
                }
            }
            catch (Exception e) when (e is SqliteException or InvalidOperationException)
            {
                log.LogWarning(e, "Could not recompute folder sizes after scan {Id}", run.Id);
            }
        }

        await using (var db = await dbf.CreateDbContextAsync(CancellationToken.None))
        {
            var row = await db.Scans.FirstAsync(x => x.Id == run.Id, CancellationToken.None);
            row.Status = status;
            row.FinishedAt = DateTimeOffset.UtcNow;
            row.FilesSeen = ctx.FilesSeen;
            row.DirsSeen = ctx.DirsSeen;
            row.Added = ctx.Added;
            row.Changed = ctx.Changed;
            row.Removed = ctx.Removed;
            row.Errors = ctx.Errors;
            row.TotalSize = ctx.BytesSeen;
            row.Message = message ?? row.Message;
            if (status == ScanStatus.Completed)
            {
                var ids = targets.Select(t => t.Id).ToList();
                foreach (var r in await db.Roots.Where(r => ids.Contains(r.Id)).ToListAsync(CancellationToken.None))
                {
                    r.LastScanAt = row.FinishedAt;
                    roots.UpdateCached(r);
                }
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }

        ctx.Phase = status.ToString().ToLowerInvariant();
        ctx.Report(status switch
        {
            ScanStatus.Completed => JobStates.Completed,
            ScanStatus.Cancelled => JobStates.Cancelled,
            _ => JobStates.Failed,
        }, message, force: true);
        log.LogInformation("Scan {Id} {Status}: {Files} files, +{Added} ~{Changed} -{Removed}, {Errors} errors{Next}",
            run.Id, status, ctx.FilesSeen, ctx.Added, ctx.Changed, ctx.Removed, ctx.Errors,
            status == ScanStatus.Completed ? "; types, categories and hashes follow in the background" : "");
        if (status != ScanStatus.Completed)
        {
            return (run.Id, null);
        }

        var scope = targets.Count == 1 ? new Scope(targets[0].Id, req.SubPath) : Scope.Everything;
        return (run.Id, new ProcessRequest(scope, full, req.SkipHashing, ctx.Added + ctx.Changed + ctx.Removed > 0, run.Id));
    }

    /// <summary>Prunes ignored entries or checks extractions: no walk, no scan history row, no "scan completed" event.</summary>
    private async Task RunLightAsync(ScanRequest req, CancellationToken ct)
    {
        var title = req.PruneIgnored ? "Removing ignored folders" : "Checking extracted archives";
        var ctx = new ScanContext(board, 0, title, DateTimeOffset.UtcNow, ct, log);
        string state = JobStates.Completed, message;
        try
        {
            using var conn = sqlite.Open();
            if (req.PruneIgnored)
            {
                ctx.StartPhase("pruning");
                var s = settings.Current;
                var removed = 0;
                foreach (var root in roots.All.Where(r => !r.Missing))
                {
                    removed += IgnorePruner.Run(conn, ctx, root.Id, new IgnoreMatcher(s.IgnorePatterns, root.Path));
                }

                message = $"{removed} ignored files and folders removed from the index";
            }
            else
            {
                using var write = sqlite.Open();
                var rootPaths = roots.All.ToDictionary(r => r.Id, r => r.Path);
                var (ok, bad) = await new ExtractionVerifier(conn, write, ctx, rootPaths, settings.Current.WorkerThreads()).RunAsync(req.VerifyExtractions!);
                message = bad > 0 ? $"{bad} extracted files differ from their archive, {ok} match" : $"{ok} extracted files match their archive";
            }

            log.LogInformation("{Title}: {Message}", title, message);
        }
        catch (OperationCanceledException)
        {
            (state, message) = (JobStates.Cancelled, "Cancelled");
        }
        catch (Exception e) when (e is SqliteException or IOException or InvalidOperationException)
        {
            log.LogError(e, "{Title} failed", title);
            (state, message) = (JobStates.Failed, e.Message);
        }

        ctx.Phase = state;
        ctx.Report(state, message, force: true);
    }

    private void WalkRoot(SqliteConnection conn, ScanContext ctx, Root root, string? subPath, AppSettings s)
    {
        var rootStat = Native.LStat(root.Path);
        if (!rootStat.Exists)
        {
            ctx.Log.LogWarning("Root {Path} does not exist; skipped", root.Path);
            return;
        }

        ctx.Log.LogInformation("Walking {Path}{Sub}: listing every folder and comparing it with the index", root.Path, string.IsNullOrEmpty(subPath) ? "" : "/" + subPath.Trim('/'));
        using var walker = new Walker(conn, ctx, root.Id, root.Path, s.CrossFilesystems, new IgnoreMatcher(s.IgnorePatterns, root.Path), s.EnterFilesystems);
        var rootEntryId = walker.EnsureRootEntry(rootStat);
        walker.Commit();
        if (root.RootEntryId != rootEntryId)
        {
            root.RootEntryId = rootEntryId;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE Roots SET RootEntryId = $e WHERE Id = $r";
            cmd.Parameters.AddWithValue("$e", rootEntryId);
            cmd.Parameters.AddWithValue("$r", root.Id);
            cmd.ExecuteNonQuery();
        }

        // Find the deepest existing directory entry for a subtree scan.
        var (startId, startRel, depth) = (rootEntryId, "", 0);
        if (!string.IsNullOrEmpty(subPath))
        {
            var rel = subPath.Trim('/');
            while (rel.Length > 0)
            {
                using var find = conn.CreateCommand();
                find.CommandText = "SELECT Id, Depth FROM Entries WHERE RootId = $r AND RelPath = $p AND IsDir = 1";
                find.Parameters.AddWithValue("$r", root.Id);
                find.Parameters.AddWithValue("$p", rel);
                using var r = find.ExecuteReader();
                if (r.Read() && Directory.Exists(RootRegistry.Combine(root.Path, rel)))
                {
                    (startId, startRel, depth) = (r.GetInt64(0), rel, r.GetInt32(1));
                    break;
                }

                var slash = rel.LastIndexOf('/');
                rel = slash < 0 ? "" : rel[..slash];
            }
        }

        // Folders created since the last scan (e.g. move targets) get entries so only they are walked.
        if (!string.IsNullOrEmpty(subPath))
        {
            (startId, startRel, depth) = walker.EnsureDirChain(startId, startRel, depth, subPath.Trim('/'));
        }

        walker.ScanDirectory(startId, RootRegistry.Combine(root.Path, startRel), startRel, depth, rootStat.Dev);
        if (startRel.Length > 0)
        {
            RecomputeAncestors(conn, startId);
        }

        if (walker.SkippedMounts.Count > 0)
        {
            ctx.Log.LogWarning("{Root}: {Count} folders are other filesystems and were not entered, so they look empty: {Paths}", root.Path,
                walker.SkippedMounts.Count, string.Join(", ", walker.SkippedMounts.Take(20)) + (walker.SkippedMounts.Count > 20 ? ", …" : ""));
        }

        if (walker.Unreadable.Count > 0)
        {
            ctx.Log.LogWarning("{Root}: {Count} folders could not be read: {Paths}", root.Path,
                walker.Unreadable.Count, string.Join(", ", walker.Unreadable.Take(20)) + (walker.Unreadable.Count > 20 ? ", …" : ""));
        }
    }

    /// <summary>Recomputes every folder's totals of a root from its children, deepest folders first.</summary>
    public static void RollupDirectories(SqliteConnection conn, int rootId)
    {
        using var tx = conn.BeginTransaction();
        using var max = conn.CreateCommand();
        max.Transaction = tx;
        max.CommandText = "SELECT COALESCE(MAX(Depth), 0) FROM Entries WHERE RootId = $r AND IsDir = 1";
        max.Parameters.AddWithValue("$r", rootId);
        var deepest = Convert.ToInt32(max.ExecuteScalar());
        using var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = """
            UPDATE Entries SET
                TotalSize = COALESCE((SELECT SUM(TotalSize) FROM Entries c WHERE c.ParentId = Entries.Id), 0),
                TotalAlloc = COALESCE((SELECT SUM(TotalAlloc) FROM Entries c WHERE c.ParentId = Entries.Id), 0),
                FileCount = COALESCE((SELECT SUM(FileCount) FROM Entries c WHERE c.ParentId = Entries.Id), 0),
                DirCount = COALESCE((SELECT SUM(DirCount + IsDir) FROM Entries c WHERE c.ParentId = Entries.Id), 0)
            WHERE RootId = $r AND IsDir = 1 AND Depth = $d
            """;
        update.Parameters.AddWithValue("$r", rootId);
        update.Parameters.Add(new SqliteParameter("$d", null));
        for (var d = deepest; d >= 0; d--)
        {
            update.Parameters["$d"].Value = d;
            update.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>After a subtree scan, refresh the aggregates of every directory above it.</summary>
    public static void RecomputeAncestors(SqliteConnection conn, long entryId)
    {
        using var parent = conn.CreateCommand();
        parent.CommandText = "SELECT ParentId FROM Entries WHERE Id = $id";
        parent.Parameters.Add(new SqliteParameter("$id", null));
        using var update = conn.CreateCommand();
        update.CommandText = """
            UPDATE Entries SET
                TotalSize = COALESCE((SELECT SUM(TotalSize) FROM Entries c WHERE c.ParentId = $id), 0),
                TotalAlloc = COALESCE((SELECT SUM(TotalAlloc) FROM Entries c WHERE c.ParentId = $id), 0),
                FileCount = COALESCE((SELECT SUM(FileCount) FROM Entries c WHERE c.ParentId = $id), 0),
                DirCount = COALESCE((SELECT SUM(DirCount + IsDir) FROM Entries c WHERE c.ParentId = $id), 0)
            WHERE Id = $id
            """;
        update.Parameters.Add(new SqliteParameter("$id", null));

        long? current = entryId;
        while (true)
        {
            parent.Parameters["$id"].Value = current;
            var p = parent.ExecuteScalar();
            if (p is not long pid)
            {
                break;
            }

            update.Parameters["$id"].Value = pid;
            update.ExecuteNonQuery();
            current = pid;
        }
    }

    private static void Snapshot(SqliteConnection conn, long scanId, int rootId, int depth)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO DirSizeHistory (ScanId, EntryId, RootId, RelPath, TotalSize, FileCount, At)
            SELECT $scan, Id, RootId, RelPath, TotalSize, FileCount, $at FROM Entries
            WHERE RootId = $root AND IsDir = 1 AND Depth <= $depth
            """;
        cmd.Parameters.AddWithValue("$scan", scanId);
        cmd.Parameters.AddWithValue("$root", rootId);
        cmd.Parameters.AddWithValue("$depth", Math.Clamp(depth, 0, 4));
        // Same encoding EF uses for DateTimeOffset (DateTimeOffsetToBinaryConverter).
        cmd.Parameters.AddWithValue("$at", new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter()
            .ConvertToProvider(DateTimeOffset.UtcNow));
        cmd.ExecuteNonQuery();
    }

    internal static void Optimize(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA optimize;";
        cmd.ExecuteNonQuery();
    }
}
