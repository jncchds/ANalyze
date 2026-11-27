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
    DbSetup database,
    RootRegistry roots,
    SettingsStore settings,
    ScanService scanner,
    JobBoard board,
    IEnumerable<IBackupSource> backups,
    IEnumerable<IHashListener> hashListeners,
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
            ? plan.Items.Where(i => i.Status == ItemStatus.Done && i.Op is ActionOp.Trash or ActionOp.Move or ActionOp.Restore or ActionOp.Extract).ToList()
            : plan.Items.Where(i => i.Included && i.Status == ItemStatus.Pending).ToList();
        var title = (job.Undo ? "Undo: " : "") + plan.Name;
        var started = DateTimeOffset.UtcNow;
        plan.Status = PlanStatus.Running;
        plan.Message = null;
        await db.SaveChangesAsync(ct);

        var writable = items.Select(i => i.RootId).Distinct().ToDictionary(r => r, roots.IsWritable);
        long done = 0, bytes = 0, failed = 0, skipped = 0;
        var rescan = new HashSet<(int Root, string Dir)>();
        // Files whose content an item replaced: the backup comparison re-checks them (IHashListener).
        var replaced = new List<long>();
        var cancelled = false;
        using var conn = database.Open();

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
        // Files extracted from archives: a tar or .gz is read once for all of this plan's files from it.
        using var extractor = new ArchiveExtractor();
        if (!job.Undo)
        {
            foreach (var item in items.Where(i => i.Op == ActionOp.Extract && i.ArchiveId is not null && i.TargetPath is not null))
            {
                if (ArchiveOf(conn, item.ArchiveId!.Value) is { } a && roots.Get(a.RootId) is { } ar && roots.Get(item.RootId) is { } r)
                {
                    extractor.Want(RootRegistry.Combine(ar.Path, a.RelPath), item.TargetPath!, ExtractTmp(r.Path, plan.Id));
                }
            }
        }

        // Kept copies the duplicates removed so far relied on: a later item of the plan must not trash or delete them.
        var relied = new HashSet<(int Root, string Path)>();
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
                    Undo(item, conn, rescan, replaced);
                    item.Status = ItemStatus.Undone;
                }
                else
                {
                    if (PlanBuilder.Removes(item) && item.KeepPath is null && (relied.Contains((item.RootId, item.SourcePath)) || (item.IsDir && relied.Any(k => PlanBuilder.Covers(item, k.Root, k.Path)))))
                    {
                        throw new SkipException("Kept copy of a duplicate this plan removed");
                    }

                    if (item.Op == ActionOp.Extract)
                    {
                        await ExtractAsync(plan, item, conn, rescan, replaced, extractor, ct);
                    }
                    else
                    {
                        await ApplyAsync(plan, item, conn, rescan, replaced, ct);
                    }

                    item.Status = ItemStatus.Done;
                    bytes += item.ExpectedSize;
                    if (PlanBuilder.Removes(item) && item.KeepPath is not null)
                    {
                        relied.Add((item.KeepRootId ?? item.RootId, item.KeepPath));
                    }
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
        extractor.Dispose();
        foreach (var r in items.Where(i => i.Op == ActionOp.Extract).Select(i => i.RootId).Distinct())
        {
            DropExtractTmp(r, plan.Id);
        }

        if (replaced.Count > 0)
        {
            foreach (var l in hashListeners)
            {
                try
                {
                    l.OnHashed(conn, replaced);
                }
                catch (Exception e) when (e is System.Data.Common.DbException or InvalidOperationException)
                {
                    log.LogWarning(e, "Re-check after plan {Plan} failed", plan.Id);
                }
            }
        }

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

    private async Task ApplyAsync(ActionPlan plan, ActionItem item, System.Data.Common.DbConnection conn, HashSet<(int, string)> rescan, List<long> replaced, CancellationToken ct)
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

                // With a kept path, the file being overwritten is set aside there first (undo puts it back).
                string? aside = null;
                if (item.KeepPath is { } keep && File.Exists(dest))
                {
                    aside = RootRegistry.Combine(root.Path, keep);
                    if (Path.Exists(aside))
                    {
                        throw new SkipException($"{keep} exists already: the overwritten file cannot be kept there");
                    }

                    Native.CreateDirectoryLikeParent(Path.GetDirectoryName(aside)!);
                    MovePath(dest, aside, isDir: false, overwrite: false);
                    DropIndexed(conn, item.RootId, target);
                    rescan.Add((item.RootId, IndexedAncestor(conn, item.RootId, ParentOf(keep))));
                }

                Native.CreateDirectoryLikeParent(Path.GetDirectoryName(dest)!);
                try
                {
                    MovePath(src, dest, item.IsDir, overwrite);
                }
                catch when (aside is not null)
                {
                    MovePath(aside, dest, isDir: false, overwrite: false);
                    throw;
                }

                item.UndoPath = target;
                if (aside is null)
                {
                    // Nothing was set aside (the destination was free): undo has nothing to put back.
                    item.KeepPath = null;
                }

                RemoveFromIndex(conn, item);
                rescan.Add((item.RootId, ParentOf(target)));
                break;
            }

            case ActionOp.Restore:
                await RestoreAsync(plan, item, root.Path, src, st, conn, ct);
                replaced.Add(item.EntryId!.Value);
                if (item.KeepPath is { } kept)
                {
                    // The kept local copy is a new file there.
                    rescan.Add((item.RootId, ParentOf(kept)));
                }

                break;

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

    /// <summary>
    /// Replaces a damaged file with the backup's copy: the copy is downloaded next to it and checked (size, and content hash when the
    /// backup has one), gets the owner, permissions and modification time of the file it replaces, then the damaged file goes to the
    /// trash (so the plan can be undone) and the copy takes its place.
    /// </summary>
    private async Task RestoreAsync(ActionPlan plan, ActionItem item, string rootPath, string src, FileStat st, System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        if (item.EntryId is not { } id || item.IsDir || !st.IsRegular)
        {
            throw new SkipException("Only files can be restored");
        }

        IBackupSource? source = null;
        BackupCopy? copy = null;
        foreach (var b in backups)
        {
            if ((await b.FindAsync([id], ct)).TryGetValue(id, out var c))
            {
                (source, copy) = (b, c);
                break;
            }
        }

        if (source is null || copy is null)
        {
            throw new SkipException("The backup does not have this file (any more)");
        }

        var tmp = Path.Combine(Path.GetDirectoryName(src)!, $".{Path.GetFileName(src)}.analyze-restore-{Guid.NewGuid():N}");
        try
        {
            string hash;
            long written = 0;
            await using (var input = await source.OpenAsync(id, ct))
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous))
            {
                var h = new System.IO.Hashing.XxHash128();
                var buf = new byte[1 << 20];
                int n;
                while ((n = await input.ReadAsync(buf, ct)) > 0)
                {
                    h.Append(buf.AsSpan(0, n));
                    await output.WriteAsync(buf.AsMemory(0, n), ct);
                    written += n;
                }

                await output.FlushAsync(ct);
                hash = Convert.ToHexStringLower(h.GetCurrentHash());
            }

            if (written != copy.Size)
            {
                throw new SkipException($"The backup's copy has {written:N0} bytes, not {copy.Size:N0} as at the last comparison: compare the backup again");
            }

            if (copy.Hash is not null && copy.Hash != hash)
            {
                throw new SkipException("The downloaded copy does not match the backup's content hash");
            }

            // Like the file it replaces.
            if (st.Uid >= 0)
            {
                Native.SetOwner(tmp, st.Uid, st.Gid);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, File.GetUnixFileMode(src));
            }

            File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(src));

            // The damaged file goes to the trash, or is kept where the plan says (e.g. "{relDir}/~{name}").
            var dest = item.KeepPath is { } keep ? RootRegistry.Combine(rootPath, keep) : RootRegistry.TrashPath(rootPath, plan.Id, item.SourcePath);
            if (Path.Exists(dest))
            {
                throw new SkipException(item.KeepPath is null ? "The trash of this plan already holds a file at that path" : $"{item.KeepPath} exists already");
            }

            Native.CreateDirectoryLikeParent(Path.GetDirectoryName(dest)!);
            MovePath(src, dest, isDir: false, overwrite: false);
            try
            {
                File.Move(tmp, src);
            }
            catch
            {
                MovePath(dest, src, isDir: false, overwrite: false);
                throw;
            }

            item.UndoPath = Path.GetRelativePath(rootPath, dest).Replace('\\', '/');
            var after = Native.LStat(src);
            EntryMaintenance.UpdateContent(conn, id, after, Hashing.QuickHash(src, after.Size), hash);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// Extracts an archive member to its place: over a damaged copy (which goes to the trash or to <see cref="ActionItem.KeepPath"/>, like a
    /// restore; the new file gets its owner, permissions and date) or where a missing one belongs (folders created with their parent's
    /// owner, the archive's date). The bytes are checked against the archive's listing — size and, when it has one, CRC32 — before
    /// anything on disk changes.
    /// </summary>
    private async Task ExtractAsync(ActionPlan plan, ActionItem item, System.Data.Common.DbConnection conn, HashSet<(int, string)> rescan, List<long> replaced,
        ArchiveExtractor extractor, CancellationToken ct)
    {
        var root = roots.Require(item.RootId);
        var dest = RootRegistry.Combine(root.Path, item.SourcePath);
        var memberPath = item.TargetPath ?? throw new SkipException("No file of the archive given");
        var archive = (item.ArchiveId is { } aid ? ArchiveOf(conn, aid) : null) ?? throw new SkipException("The archive is no longer indexed");
        var archiveRoot = roots.Get(archive.RootId) ?? throw new SkipException("The archive's root is gone");
        var archivePath = RootRegistry.Combine(archiveRoot.Path, archive.RelPath);
        if (!File.Exists(archivePath))
        {
            throw new SkipException("The archive is gone");
        }

        var member = MemberOf(conn, item.ArchiveId!.Value, memberPath) ?? throw new SkipException("The archive no longer lists this file");
        var st = Native.LStat(dest);
        if (item.EntryId is not null)
        {
            if (!st.Exists)
            {
                throw new SkipException("The damaged file is gone");
            }

            if (!st.IsRegular)
            {
                throw new SkipException("Not a file any more");
            }

            if (st.Size != item.ExpectedSize || st.MTime != item.ExpectedMTime)
            {
                throw new SkipException("Source changed since the plan was made");
            }
        }
        else if (st.Exists)
        {
            throw new SkipException("Something is at that path now");
        }

        var tmp = ExtractTmp(root.Path, plan.Id);
        try
        {
            ExtractedMember x;
            try
            {
                x = await extractor.ExtractAsync(archivePath, archive.Name, memberPath, tmp, ct);
            }
            catch (FileNotFoundException e)
            {
                throw new SkipException(e.Message);
            }
            catch (Exception e) when (e is not (OperationCanceledException or IOException or UnauthorizedAccessException or OutOfMemoryException))
            {
                // The archive library's own errors (damaged or unsupported archive).
                throw new IOException("The archive could not be read: " + e.Message, e);
            }

            if (x.Size != member.Size)
            {
                throw new SkipException($"The archive gives {x.Size:N0} bytes for it, not {member.Size:N0} as listed: has the archive changed? Scan it again");
            }

            if (member.Crc is { } crc && crc != x.Crc)
            {
                throw new SkipException("The extracted file does not match the archive's CRC32: the archive itself may be damaged");
            }

            if (item.EntryId is { } id)
            {
                if (st.Uid >= 0)
                {
                    Native.SetOwner(tmp, st.Uid, st.Gid);
                }

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tmp, File.GetUnixFileMode(dest));
                }

                // Its date, so undo can tell whether it was changed since.
                File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(dest));
                var aside = item.KeepPath is { } keep ? RootRegistry.Combine(root.Path, keep) : RootRegistry.TrashPath(root.Path, plan.Id, item.SourcePath);
                if (Path.Exists(aside))
                {
                    throw new SkipException(item.KeepPath is null ? "The trash of this plan already holds a file at that path" : $"{item.KeepPath} exists already");
                }

                Native.CreateDirectoryLikeParent(Path.GetDirectoryName(aside)!);
                MovePath(dest, aside, isDir: false, overwrite: false);
                try
                {
                    File.Move(tmp, dest);
                }
                catch
                {
                    MovePath(aside, dest, isDir: false, overwrite: false);
                    throw;
                }

                item.UndoPath = Path.GetRelativePath(root.Path, aside).Replace('\\', '/');
                var after = Native.LStat(dest);
                EntryMaintenance.UpdateContent(conn, id, after, Hashing.QuickHash(dest, after.Size), x.Hash);
                replaced.Add(id);
                if (item.KeepPath is { } kept)
                {
                    rescan.Add((item.RootId, IndexedAncestor(conn, item.RootId, ParentOf(kept))));
                }

                return;
            }

            var dir = Path.GetDirectoryName(dest)!;
            Native.CreateDirectoryLikeParent(dir);
            var parent = Native.LStat(dir);
            Native.SetOwner(tmp, parent.Uid, parent.Gid);
            if (x.Modified is { } m && m.Year > 1980)
            {
                File.SetLastWriteTimeUtc(tmp, m.Kind == DateTimeKind.Local ? m.ToUniversalTime() : DateTime.SpecifyKind(m, DateTimeKind.Utc));
            }

            File.Move(tmp, dest);
            var made = Native.LStat(dest);
            // What undo checks before deleting the file again.
            (item.ExpectedSize, item.ExpectedMTime, item.ExpectedHash) = (made.Size, made.MTime, x.Hash);
            item.UndoPath = item.SourcePath;
            rescan.Add((item.RootId, IndexedAncestor(conn, item.RootId, ParentOf(item.SourcePath))));
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
    }

    /// <summary>Deletes a file a plan extracted where none was, unless it was changed since; folders left empty go too.</summary>
    private static void UndoCreated(ActionItem item, string rootPath, System.Data.Common.DbConnection conn)
    {
        var path = RootRegistry.Combine(rootPath, item.SourcePath);
        var st = Native.LStat(path);
        if (!st.Exists)
        {
            throw new SkipException("The extracted file is gone already");
        }

        if (!st.IsRegular || st.Size != item.ExpectedSize || st.MTime != item.ExpectedMTime)
        {
            throw new SkipException("The extracted file was changed since; it is left as it is");
        }

        File.Delete(path);
        var pruned = PruneEmptyParents(Path.GetDirectoryName(path)!, rootPath);
        DropIndexed(conn, item.RootId, pruned is null ? item.SourcePath : Path.GetRelativePath(rootPath, pruned).Replace('\\', '/'));
    }

    private sealed record ArchiveInfo(int RootId, string RelPath, string Name);

    private static ArchiveInfo? ArchiveOf(System.Data.Common.DbConnection conn, long archiveId)
    {
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT RootId, RelPath, Name FROM Entries WHERE Id = @id AND IsDir = FALSE";
        q.Parameters.AddWithValue("@id", archiveId);
        using var r = q.ExecuteReader();
        return r.Read() ? new ArchiveInfo(r.GetInt32(0), r.GetString(1), r.GetString(2)) : null;
    }

    private static (long Size, long? Crc)? MemberOf(System.Data.Common.DbConnection conn, long archiveId, string path)
    {
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT Size, Crc FROM ArchiveMembers WHERE ArchiveId = @a AND Path = @p";
        q.Parameters.AddWithValue("@a", archiveId);
        q.Parameters.AddWithValue("@p", path);
        using var r = q.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1)) : null;
    }

    /// <summary>The path itself or the closest folder above it that is in the index (a rescan starts from there).</summary>
    private static string IndexedAncestor(System.Data.Common.DbConnection conn, int rootId, string relPath)
    {
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT 1 FROM Entries WHERE RootId = @r AND RelPath = @p AND IsDir = TRUE";
        var pr = q.Parameters.AddWithValue("@p", relPath);
        q.Parameters.AddWithValue("@r", rootId);
        while (relPath.Length > 0)
        {
            pr.Value = relPath;
            if (q.ExecuteScalar() is not null)
            {
                return relPath;
            }

            relPath = ParentOf(relPath);
        }

        return "";
    }

    /// <summary>A new temporary file name for an extraction, in the plan's trash folder of the root (which the scanner skips).</summary>
    private static string ExtractTmp(string rootPath, int planId)
    {
        var dir = RootRegistry.TrashPath(rootPath, planId, ".extracting");
        Native.CreateDirectoryLikeParent(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N"));
    }

    /// <summary>Removes the temporary folder of a plan's extractions, and the plan's trash folder when nothing else is in it.</summary>
    private void DropExtractTmp(int rootId, int planId)
    {
        if (roots.Get(rootId) is not { } root)
        {
            return;
        }

        var dir = RootRegistry.TrashPath(root.Path, planId, ".extracting");
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                PruneEmptyParents(Path.GetDirectoryName(dir)!, root.Path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Could not remove {Dir}: {Error}", dir, e.Message);
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

    private void Undo(ActionItem item, System.Data.Common.DbConnection conn, HashSet<(int, string)> rescan, List<long> replaced)
    {
        var root = roots.Require(item.RootId);
        if (item.UndoPath is null)
        {
            throw new SkipException("Nothing to undo");
        }

        if (item.Op == ActionOp.Extract && item.EntryId is null)
        {
            UndoCreated(item, root.Path, conn);
            return;
        }

        var from = RootRegistry.Combine(root.Path, item.UndoPath);
        var to = RootRegistry.Combine(root.Path, item.SourcePath);
        if (!Path.Exists(from))
        {
            throw new SkipException("The moved/trashed copy is gone (trash emptied?)");
        }

        if (item.Op is ActionOp.Restore or ActionOp.Extract)
        {
            UndoRestore(item, from, to, root.Path, conn);
            replaced.Add(item.EntryId!.Value);
            return;
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
            DropIndexed(conn, item.RootId, pruned is null ? item.UndoPath : Path.GetRelativePath(root.Path, pruned).Replace('\\', '/'));
            if (item.KeepPath is { } keep)
            {
                // The file the move overwrote goes back to its place.
                var kept = RootRegistry.Combine(root.Path, keep);
                if (!File.Exists(kept))
                {
                    // The move itself is undone; only the old file cannot come back.
                    log.LogWarning("Undo of plan {Plan}: the overwritten file kept as {Keep} is gone; {Target} stays empty", item.PlanId, keep, item.UndoPath);
                    return;
                }

                Native.CreateDirectoryLikeParent(Path.GetDirectoryName(from)!);
                MovePath(kept, from, isDir: false, overwrite: false);
                var prunedKeep = PruneEmptyParents(Path.GetDirectoryName(kept)!, root.Path);
                DropIndexed(conn, item.RootId, prunedKeep is null ? keep : Path.GetRelativePath(root.Path, prunedKeep).Replace('\\', '/'));
                rescan.Add((item.RootId, IndexedAncestor(conn, item.RootId, ParentOf(item.UndoPath))));
            }
        }
    }

    /// <summary>Removes a path that is gone from disk (and everything under it) from the index.</summary>
    private static void DropIndexed(System.Data.Common.DbConnection conn, int rootId, string relPath)
    {
        using var find = conn.CreateCommand();
        find.CommandText = "SELECT Id FROM Entries WHERE RootId = @r AND RelPath = @p";
        find.Parameters.AddWithValue("@r", rootId);
        find.Parameters.AddWithValue("@p", relPath);
        if (find.ExecuteScalar() is long goneId)
        {
            EntryMaintenance.DeleteEntry(conn, goneId);
        }
    }

    /// <summary>
    /// Puts the damaged file back in place of the backup's copy, unless the copy was changed since (it carries the damaged file's
    /// modification time until then). The file is hashed again by the background hashing.
    /// </summary>
    private static void UndoRestore(ActionItem item, string from, string to, string rootPath, System.Data.Common.DbConnection conn)
    {
        if (File.Exists(to))
        {
            if (File.GetLastWriteTimeUtc(to) != File.GetLastWriteTimeUtc(from))
            {
                throw new SkipException("The restored file was changed since; it is left as it is");
            }

            File.Delete(to);
        }

        Native.CreateDirectoryLikeParent(Path.GetDirectoryName(to)!);
        MovePath(from, to, isDir: false, overwrite: false);
        var pruned = PruneEmptyParents(Path.GetDirectoryName(from)!, rootPath);
        if (item.KeepPath is not null)
        {
            // The kept copy (indexed by the rescan after applying) is gone from there.
            DropIndexed(conn, item.RootId, pruned is null ? item.UndoPath! : Path.GetRelativePath(rootPath, pruned).Replace('\\', '/'));
        }

        if (item.EntryId is { } id)
        {
            var st = Native.LStat(to);
            EntryMaintenance.UpdateContent(conn, id, st, Hashing.QuickHash(to, st.Size), fullHash: null);
        }
    }

    private static void RemoveFromIndex(System.Data.Common.DbConnection conn, ActionItem item)
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
