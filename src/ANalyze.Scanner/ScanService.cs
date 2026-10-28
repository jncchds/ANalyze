using ANalyze.Domain;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>What to scan: everything, one root, or a folder inside a root.</summary>
public sealed record ScanRequest(int? RootId = null, string? SubPath = null, bool SkipHashing = false, string? Reason = null);

/// <summary>Runs a scan: walk → detect → categorize → hash duplicates → history snapshot.</summary>
public sealed class ScanService(
    SqliteSetup sqlite,
    IDbContextFactory<AnalyzeDbContext> dbf,
    RootRegistry roots,
    SettingsStore settings,
    Categorizer categorizer,
    JobBoard board,
    ILogger<ScanService> log)
{
    public event Action<long>? Completed;

    public async Task RunAsync(ScanRequest req, CancellationToken ct)
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

        var ctx = new ScanContext(board, run.Id, title ?? "Scan", run.StartedAt, ct);
        ctx.Report(force: true);
        var status = ScanStatus.Completed;
        string? message = null;
        try
        {
            var rootPaths = roots.All.ToDictionary(r => r.Id, r => r.Path);
            using var conn = sqlite.Open();
            foreach (var root in targets)
            {
                WalkRoot(conn, ctx, root, req.SubPath, s);
            }

            var scope = targets.Count == 1 ? new Scope(targets[0].Id, req.SubPath) : Scope.Everything;
            Detector.Run(conn, ctx, rootPaths, scope, s.SniffUnknownFiles);

            ctx.Phase = "categorizing";
            ctx.Done = 0;
            ctx.Report(force: true);
            var engine = await categorizer.LoadEngineAsync(ct);
            ctx.Categorized = Categorizer.Run(conn, engine, scope, (done, total) =>
            {
                ctx.Done = done;
                ctx.Total = total;
                ctx.Report();
            }, ct);

            if (s.FindDuplicates && !req.SkipHashing)
            {
                using var write = sqlite.Open();
                await new DuplicateFinder(conn, write, ctx, rootPaths, Math.Max(1, s.MinDuplicateSize), s.HashThreads).RunAsync();
            }

            ctx.Phase = "finishing";
            ctx.Report(force: true);
            if (full)
            {
                foreach (var root in targets)
                {
                    Snapshot(conn, run.Id, root.Id, s.HistoryDepth);
                }
            }

            Optimize(conn);
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
            row.Hashed = ctx.Hashed;
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
        log.LogInformation("Scan {Id} {Status}: {Files} files, +{Added} ~{Changed} -{Removed}, {Hashed} hashed, {Errors} errors",
            run.Id, status, ctx.FilesSeen, ctx.Added, ctx.Changed, ctx.Removed, ctx.Hashed, ctx.Errors);
        if (status == ScanStatus.Completed)
        {
            Completed?.Invoke(run.Id);
        }
    }

    private void WalkRoot(SqliteConnection conn, ScanContext ctx, Root root, string? subPath, AppSettings s)
    {
        var rootStat = Native.LStat(root.Path);
        if (!rootStat.Exists)
        {
            return;
        }

        using var walker = new Walker(conn, ctx, root.Id, root.Path, s.CrossFilesystems, new IgnoreMatcher(s.IgnorePatterns));
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

    private static void Optimize(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA optimize;";
        cmd.ExecuteNonQuery();
    }
}
