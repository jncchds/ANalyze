using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANalyze.Actions;

/// <summary>A plan's trash. Pending: queued or being emptied now (the "trash" job's RefId says which).</summary>
public sealed record TrashBin(int PlanId, string PlanName, DateTimeOffset? AppliedAt, long Items, long Bytes, DateTimeOffset? ExpiresAt, bool Pending);

/// <summary>
/// Trash lives in &lt;root&gt;/.analyze-trash/&lt;planId&gt;/ so moving there is an instant rename.
/// Emptying runs in the background, one plan at a time, and reports its progress as the "trash" job.
/// </summary>
public sealed class TrashService(IDbContextFactory<AnalyzeDbContext> dbf, RootRegistry roots, SettingsStore settings, JobBoard board, ILogger<TrashService> log)
{
    private static readonly EnumerationOptions Listing = new() { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, byte> _pending = new();
    private CancellationTokenSource? _current;

    internal ChannelReader<int> Reader => _channel.Reader;

    /// <summary>True while the plan's trash is queued or being emptied.</summary>
    public bool IsPending(int planId) => _pending.ContainsKey(planId);

    /// <summary>Queues emptying a plan's trash; false when it is queued already.</summary>
    public bool Enqueue(int planId) => _pending.TryAdd(planId, 0) && _channel.Writer.TryWrite(planId);

    /// <summary>Stops the trash being emptied now; what was deleted so far stays deleted.</summary>
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

    public async Task<IReadOnlyList<TrashBin>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var retention = settings.Current.TrashRetentionDays;
        var rows = await db.ActionItems.AsNoTracking()
            .Where(i => (i.Op == ActionOp.Trash || (i.Op == ActionOp.Restore && i.KeepPath == null) || (i.Op == ActionOp.Extract && i.EntryId != null && i.KeepPath == null)) && i.Status == ItemStatus.Done)
            .GroupBy(i => i.PlanId)
            .Select(g => new { PlanId = g.Key, Items = g.LongCount(), Bytes = g.Sum(i => i.ExpectedSize) })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.PlanId).ToList();
        var plans = await db.ActionPlans.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TrashPurgedAt == null).ToDictionaryAsync(p => p.Id, ct);
        return rows.Where(r => plans.ContainsKey(r.PlanId))
            .Select(r =>
            {
                var p = plans[r.PlanId];
                return new TrashBin(p.Id, p.Name, p.AppliedAt, r.Items, r.Bytes, retention > 0 ? p.AppliedAt?.AddDays(retention) : null, IsPending(p.Id));
            })
            .OrderByDescending(b => b.AppliedAt)
            .ToList();
    }

    /// <summary>Queues every bin past the retention period; returns how many were queued.</summary>
    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var days = settings.Current.TrashRetentionDays;
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        return (await ListAsync(ct)).Count(b => b.AppliedAt < cutoff && Enqueue(b.PlanId));
    }

    internal async Task RunAsync(int planId, CancellationToken stopping)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        _current = cts;
        try
        {
            await PurgeAsync(planId, cts.Token);
        }
        finally
        {
            _current = null;
            cts.Dispose();
            _pending.TryRemove(planId, out _);
        }
    }

    /// <summary>Permanently removes a plan's trash folder in every root, file by file so the progress can be shown.</summary>
    public async Task<bool> PurgeAsync(int planId, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var plan = await db.ActionPlans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null || plan.TrashPurgedAt is not null)
        {
            return false;
        }

        var bin = (await ListAsync(ct)).FirstOrDefault(b => b.PlanId == planId);
        var started = DateTimeOffset.UtcNow;
        long files = 0, bytes = 0, failed = 0;
        string? firstError = null;

        void Report(string state, string? current = null, string? message = null) => board.Report(new JobProgress
        {
            Kind = JobKinds.Trash,
            State = state,
            Phase = "empty",
            Title = "Empty trash: " + plan.Name,
            Done = files,
            Bytes = bytes,
            TotalBytes = bin?.Bytes ?? 0,
            Current = current,
            Message = message,
            RefId = planId,
            StartedAt = started,
            FinishedAt = state == JobStates.Running ? null : DateTimeOffset.UtcNow,
            Counters = new Dictionary<string, long> { ["failed"] = failed },
        }, force: state != JobStates.Running);

        if (plan.Status == PlanStatus.Running)
        {
            Report(JobStates.Failed, message: "The plan is running");
            return false;
        }

        void Fail(string path, Exception e)
        {
            failed++;
            firstError ??= e.Message;
            log.LogWarning("Could not empty trash {Path}: {Error}", path, e.Message);
        }

        // Post-order walk: files first, then the folders they leave empty. Links are removed, never followed.
        void Empty(DirectoryInfo dir, string trashDir)
        {
            List<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos("*", Listing).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Fail(dir.FullName, e);
                return;
            }

            foreach (var c in children)
            {
                ct.ThrowIfCancellationRequested();
                if (c is DirectoryInfo d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    var before = failed;
                    Empty(d, trashDir);
                    try
                    {
                        d.Delete();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // A folder still holding a file that could not be deleted is not counted twice.
                        if (failed == before)
                        {
                            Fail(d.FullName, e);
                        }
                    }

                    continue;
                }

                var size = c is FileInfo f && !f.Attributes.HasFlag(FileAttributes.ReparsePoint) ? f.Length : 0;
                try
                {
                    c.Delete();
                    files++;
                    bytes += size;
                    Report(JobStates.Running, Path.GetRelativePath(trashDir, c.FullName));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Fail(c.FullName, e);
                }
            }
        }

        Report(JobStates.Running);
        var cancelled = false;
        await Task.Run(() =>
        {
            foreach (var root in roots.All.Where(r => !r.ReadOnly))
            {
                var parent = Path.Combine(root.Path, RootRegistry.TrashFolder);
                var dir = Path.Combine(parent, planId.ToString(CultureInfo.InvariantCulture));
                try
                {
                    if (Directory.Exists(dir))
                    {
                        var before = failed;
                        Empty(new DirectoryInfo(dir), dir);
                        if (failed > before)
                        {
                            continue;
                        }

                        Directory.Delete(dir);
                    }

                    if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    {
                        Directory.Delete(parent);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Fail(dir, e);
                }
            }
        }, CancellationToken.None);

        var summary = string.Create(CultureInfo.InvariantCulture, $"{files:N0} files deleted");
        if (cancelled)
        {
            Report(JobStates.Cancelled, message: summary + " (stopped)");
            return false;
        }

        if (failed > 0)
        {
            Report(JobStates.Failed, message: string.Create(CultureInfo.InvariantCulture, $"{summary}, {failed:N0} could not be deleted: {firstError}"));
            return false;
        }

        plan.TrashPurgedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
        Report(JobStates.Completed, message: summary);
        return true;
    }
}

/// <summary>Empties queued trash bins one at a time.</summary>
internal sealed class TrashWorker(TrashService trash, ILogger<TrashWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var planId in trash.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await trash.RunAsync(planId, stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Emptying the trash of plan {Plan} failed", planId);
            }
        }
    }
}

internal sealed class TrashJanitor(IServiceProvider services, ILogger<TrashJanitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await services.GetRequiredService<TrashService>().PurgeExpiredAsync(stoppingToken);
                if (n > 0)
                {
                    log.LogInformation("Emptying {Count} expired trash folder(s)", n);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Trash cleanup failed");
            }

            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
