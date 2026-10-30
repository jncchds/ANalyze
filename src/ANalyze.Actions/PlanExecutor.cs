using System.Threading.Channels;
using ANalyze.Domain;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using ANalyze.Scanner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANalyze.Actions;

public sealed record PlanJob(int PlanId, bool Undo);

/// <summary>Queues plan apply/undo jobs; one runs at a time.</summary>
public sealed class PlanQueue
{
    private readonly Channel<PlanJob> _channel = Channel.CreateUnbounded<PlanJob>();
    private CancellationTokenSource? _current;

    internal ChannelReader<PlanJob> Reader => _channel.Reader;

    public void Enqueue(PlanJob job) => _channel.Writer.TryWrite(job);

    public bool Cancel()
    {
        var c = _current;
        if (c is null)
        {
            return false;
        }

        c.Cancel();
        return true;
    }

    internal CancellationToken Begin(CancellationToken stopping)
    {
        _current = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        return _current.Token;
    }

    internal void End()
    {
        _current?.Dispose();
        _current = null;
    }
}

/// <summary>
/// Applies plans item by item. Each item is re-verified against the disk right before it runs
/// (still there, same size and mtime, same content for duplicates), so a stale plan cannot destroy changed files.
/// </summary>
public sealed class PlanExecutor(
    IDbContextFactory<AnalyzeDbContext> dbf,
    SqliteSetup sqlite,
    RootRegistry roots,
    SettingsStore settings,
    ScanService scanner,
    JobBoard board,
    ILogger<PlanExecutor> log)
{
    public async Task RunAsync(PlanJob job, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var plan = await db.ActionPlans.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == job.PlanId, ct);
        if (plan is null)
        {
            return;
        }

        var items = job.Undo
            ? plan.Items.Where(i => i.Status == ItemStatus.Done && i.Op is ActionOp.Trash or ActionOp.Move).ToList()
            : plan.Items.Where(i => i.Included && i.Status == ItemStatus.Pending).ToList();
        var title = (job.Undo ? "Undo: " : "") + plan.Name;
        var started = DateTimeOffset.UtcNow;
        plan.Status = PlanStatus.Running;
        plan.Message = null;
        await db.SaveChangesAsync(ct);

        var writable = items.Select(i => i.RootId).Distinct().ToDictionary(r => r, roots.IsWritable);
        long done = 0, bytes = 0, failed = 0, skipped = 0;
        var rescan = new HashSet<(int Root, string Dir)>();
        var cancelled = false;
        using var conn = sqlite.Open();

        void Report(string state, string? current = null, string? message = null) => board.Report(new JobProgress
        {
            Kind = JobKinds.Apply,
            State = state,
            Phase = job.Undo ? "undo" : "apply",
            Title = title,
            Done = done,
            Total = items.Count,
            Bytes = bytes,
            Current = current,
            Message = message,
            RefId = plan.Id,
            StartedAt = started,
            FinishedAt = state == JobStates.Running ? null : DateTimeOffset.UtcNow,
            Counters = new Dictionary<string, long> { ["failed"] = failed, ["skipped"] = skipped },
        }, force: state != JobStates.Running);

        Report(JobStates.Running);
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            Report(JobStates.Running, item.SourcePath);
            try
            {
                if (!writable[item.RootId])
                {
                    throw new SkipException("Root is read-only");
                }

                if (job.Undo)
                {
                    Undo(item, conn, rescan);
                    item.Status = ItemStatus.Undone;
                }
                else
                {
                    await ApplyAsync(plan, item, conn, rescan, ct);
                    item.Status = ItemStatus.Done;
                    bytes += item.ExpectedSize;
                }

                item.Error = null;
            }
            catch (SkipException e)
            {
                item.Status = job.Undo ? ItemStatus.Done : ItemStatus.Skipped;
                item.Error = e.Message;
                skipped++;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                item.Status = job.Undo ? ItemStatus.Done : ItemStatus.Failed;
                item.Error = e.Message;
                failed++;
                log.LogWarning("Plan {Plan} item {Path} failed: {Error}", plan.Id, item.SourcePath, e.Message);
            }

            done++;
            if (done % 200 == 0)
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        if (job.Undo)
        {
            plan.Status = cancelled ? PlanStatus.Done : PlanStatus.Undone;
        }
        else
        {
            plan.Status = cancelled ? PlanStatus.Cancelled : failed > 0 && failed == done ? PlanStatus.Failed : PlanStatus.Done;
            plan.AppliedAt = DateTimeOffset.UtcNow;
        }

        plan.Message = $"{done - failed - skipped} done, {skipped} skipped, {failed} failed" + (cancelled ? " (cancelled)" : "");
        await db.SaveChangesAsync(CancellationToken.None);

        // Bring the index up to date for folders that received files.
        foreach (var (root, dir) in Minimal(rescan))
        {
            try
            {
                // PlanWorker holds the job lock alone already.
                await scanner.RunAsync(new ScanRequest(root, dir, Reason: $"After plan #{plan.Id}"), CancellationToken.None, lockHeld: true);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log.LogWarning(e, "Rescan after plan {Plan} failed", plan.Id);
            }
        }

        Report(cancelled ? JobStates.Cancelled : JobStates.Completed, message: plan.Message);
    }

    private async Task ApplyAsync(ActionPlan plan, ActionItem item, Microsoft.Data.Sqlite.SqliteConnection conn, HashSet<(int, string)> rescan, CancellationToken ct)
    {
        var root = roots.Require(item.RootId);
        var src = RootRegistry.Combine(root.Path, item.SourcePath);
        var st = Native.LStat(src);
        if (!st.Exists)
        {
            throw new SkipException("Source no longer exists");
        }

        if (st.IsDir != item.IsDir)
        {
            throw new SkipException("Source changed type");
        }

        if (!item.IsDir && (st.Size != item.ExpectedSize || st.MTime != item.ExpectedMTime))
        {
            throw new SkipException("Source changed since the plan was made");
        }

        if (item.ExpectedHash is not null)
        {
            await VerifyDuplicateAsync(plan, item, src, ct);
        }

        switch (item.Op)
        {
            case ActionOp.Trash:
            {
                var dest = RootRegistry.TrashPath(root.Path, plan.Id, item.SourcePath);
                Native.CreateDirectoryLikeParent(Path.GetDirectoryName(dest)!);
                MovePath(src, dest, item.IsDir, overwrite: false);
                item.UndoPath = Path.GetRelativePath(root.Path, dest).Replace('\\', '/');
                RemoveFromIndex(conn, item);
                break;
            }

            case ActionOp.Delete:
                if (item.IsDir)
                {
                    Directory.Delete(src, recursive: true);
                }
                else
                {
                    File.Delete(src);
                }

                RemoveFromIndex(conn, item);
                break;

            case ActionOp.Move:
            {
                var target = item.TargetPath ?? throw new SkipException("No destination");
                var dest = RootRegistry.Combine(root.Path, target);
                var overwrite = plan.Conflict == ConflictPolicy.Overwrite;
                if (Path.Exists(dest))
                {
                    if (!overwrite)
                    {
                        throw new SkipException("Destination exists");
                    }

                    if (Directory.Exists(dest) || item.IsDir)
                    {
                        throw new SkipException("Will not overwrite a folder");
                    }
                }

                Native.CreateDirectoryLikeParent(Path.GetDirectoryName(dest)!);
                MovePath(src, dest, item.IsDir, overwrite);
                item.UndoPath = target;
                RemoveFromIndex(conn, item);
                rescan.Add((item.RootId, ParentOf(target)));
                break;
            }

            case ActionOp.Hardlink:
            {
                var keepRoot = roots.Require(item.KeepRootId ?? item.RootId);
                var keep = RootRegistry.Combine(keepRoot.Path, item.KeepPath ?? throw new SkipException("No kept copy"));
                var keepSt = Native.LStat(keep);
                if (keepSt.Dev != st.Dev)
                {
                    throw new SkipException("Kept copy is on another filesystem");
                }

                var tmp = Path.Combine(Path.GetDirectoryName(src)!, $".{Path.GetFileName(src)}.analyze-link-{Guid.NewGuid():N}");
                Native.CreateHardLink(keep, tmp);
                try
                {
                    File.Move(tmp, src, overwrite: true);
                }
                catch
                {
                    File.Delete(tmp);
                    throw;
                }

                var after = Native.LStat(src);
                if (item.EntryId is { } id)
                {
                    EntryMaintenance.UpdateIdentity(conn, id, after.Inode, after.Dev, after.NLink, after.MTime);
                }

                break;
            }
        }
    }

    private async Task VerifyDuplicateAsync(ActionPlan plan, ActionItem item, string src, CancellationToken ct)
    {
        if (item.KeepPath is null)
        {
            return;
        }

        var keepRoot = roots.Get(item.KeepRootId ?? item.RootId) ?? throw new SkipException("Kept copy's root is gone");
        var keep = RootRegistry.Combine(keepRoot.Path, item.KeepPath);
        var keepSt = Native.LStat(keep);
        if (!keepSt.Exists || keepSt.Size != item.ExpectedSize)
        {
            throw new SkipException("Kept copy is missing or changed");
        }

        if (!plan.VerifyHashes || !settings.Current.VerifyHashBeforeDelete)
        {
            return;
        }

        var a = await Hashing.FullHashAsync(src, null, ct);
        if (a != item.ExpectedHash)
        {
            throw new SkipException("Content changed since it was hashed");
        }

        var b = await Hashing.FullHashAsync(keep, null, ct);
        if (b != item.ExpectedHash)
        {
            throw new SkipException("Kept copy's content changed");
        }
    }

    private void Undo(ActionItem item, Microsoft.Data.Sqlite.SqliteConnection conn, HashSet<(int, string)> rescan)
    {
        var root = roots.Require(item.RootId);
        if (item.UndoPath is null)
        {
            throw new SkipException("Nothing to undo");
        }

        var from = RootRegistry.Combine(root.Path, item.UndoPath);
        var to = RootRegistry.Combine(root.Path, item.SourcePath);
        if (!Path.Exists(from))
        {
            throw new SkipException("The moved/trashed copy is gone (trash emptied?)");
        }

        if (Path.Exists(to))
        {
            throw new SkipException("Original location is occupied");
        }

        Native.CreateDirectoryLikeParent(Path.GetDirectoryName(to)!);
        MovePath(from, to, Directory.Exists(from), overwrite: false);
        var pruned = PruneEmptyParents(Path.GetDirectoryName(from)!, root.Path);
        rescan.Add((item.RootId, ParentOf(item.SourcePath)));
        if (item.Op == ActionOp.Move)
        {
            // The moved copy is gone from the target: drop it (or the folders the move created) from the index directly.
            var gone = pruned is null ? item.UndoPath : Path.GetRelativePath(root.Path, pruned).Replace('\\', '/');
            using var find = conn.CreateCommand();
            find.CommandText = "SELECT Id FROM Entries WHERE RootId = $r AND RelPath = $p";
            find.Parameters.AddWithValue("$r", item.RootId);
            find.Parameters.AddWithValue("$p", gone);
            if (find.ExecuteScalar() is long goneId)
            {
                EntryMaintenance.DeleteEntry(conn, goneId);
            }
        }
    }

    private static void RemoveFromIndex(Microsoft.Data.Sqlite.SqliteConnection conn, ActionItem item)
    {
        if (item.EntryId is { } id)
        {
            EntryMaintenance.DeleteEntry(conn, id);
        }
    }

    private static void MovePath(string src, string dest, bool isDir, bool overwrite)
    {
        var owner = Native.LStat(src);
        if (!isDir)
        {
            // File.Move falls back to copy+delete across filesystems; a copy made as root must get the owner back.
            File.Move(src, dest, overwrite);
            if (Native.LStat(dest) is { } after && (after.Uid != owner.Uid || after.Gid != owner.Gid))
            {
                Native.SetOwner(dest, owner.Uid, owner.Gid);
            }

            return;
        }

        try
        {
            Directory.Move(src, dest);
        }
        catch (IOException) when (!Path.Exists(dest) && Native.LStat(src).Dev != Native.LStat(Path.GetDirectoryName(dest)!).Dev)
        {
            CopyDirectory(src, dest);
            Directory.Delete(src, recursive: true);
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        var dirOwner = Native.LStat(src);
        Native.SetOwner(dest, dirOwner.Uid, dirOwner.Gid);
        foreach (var f in Directory.EnumerateFiles(src))
        {
            var target = Path.Combine(dest, Path.GetFileName(f));
            File.Copy(f, target);
            var o = Native.LStat(f);
            Native.SetOwner(target, o.Uid, o.Gid);
        }

        foreach (var d in Directory.EnumerateDirectories(src))
        {
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
        }
    }

    /// <summary>Removes folders left empty by an undo (move targets, trash folders), up to the root.</summary>
    /// <returns>The topmost folder removed, or null.</returns>
    private static string? PruneEmptyParents(string dir, string rootPath)
    {
        string? top = null;
        try
        {
            while (RootRegistry.IsUnder(rootPath, dir) && dir != rootPath && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
                top = dir;
                dir = Path.GetDirectoryName(dir)!;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }

        return top;
    }

    private static string ParentOf(string rel)
    {
        var slash = rel.LastIndexOf('/');
        return slash < 0 ? "" : rel[..slash];
    }

    /// <summary>Drops folders whose ancestor is already in the set.</summary>
    private static IEnumerable<(int Root, string Dir)> Minimal(HashSet<(int Root, string Dir)> set) =>
        set.Where(x => !set.Any(o => o.Root == x.Root && o.Dir != x.Dir && (o.Dir.Length == 0 || x.Dir.StartsWith(o.Dir + "/", StringComparison.Ordinal))))
            .OrderBy(x => x.Root).ThenBy(x => x.Dir);

    private sealed class SkipException(string message) : Exception(message);
}

internal sealed class PlanWorker(PlanQueue queue, PlanExecutor executor, JobLock jobLock, ILogger<PlanWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var ct = queue.Begin(stoppingToken);
            try
            {
                using var _ = await jobLock.AcquireAsync("apply", stoppingToken);
                await executor.RunAsync(job, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Plan {Plan} failed", job.PlanId);
            }
            finally
            {
                queue.End();
            }
        }
    }
}
