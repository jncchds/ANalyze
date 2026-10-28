using System.Globalization;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANalyze.Actions;

public sealed record TrashBin(int PlanId, string PlanName, DateTimeOffset? AppliedAt, long Items, long Bytes, DateTimeOffset? ExpiresAt);

/// <summary>Trash lives in &lt;root&gt;/.analyze-trash/&lt;planId&gt;/ so moving there is an instant rename.</summary>
public sealed class TrashService(IDbContextFactory<AnalyzeDbContext> dbf, RootRegistry roots, SettingsStore settings, ILogger<TrashService> log)
{
    public async Task<IReadOnlyList<TrashBin>> ListAsync(CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var retention = settings.Current.TrashRetentionDays;
        var rows = await db.ActionItems.AsNoTracking()
            .Where(i => i.Op == ActionOp.Trash && i.Status == ItemStatus.Done)
            .GroupBy(i => i.PlanId)
            .Select(g => new { PlanId = g.Key, Items = g.LongCount(), Bytes = g.Sum(i => i.ExpectedSize) })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.PlanId).ToList();
        var plans = await db.ActionPlans.AsNoTracking().Where(p => ids.Contains(p.Id) && p.TrashPurgedAt == null).ToDictionaryAsync(p => p.Id, ct);
        return rows.Where(r => plans.ContainsKey(r.PlanId))
            .Select(r =>
            {
                var p = plans[r.PlanId];
                return new TrashBin(p.Id, p.Name, p.AppliedAt, r.Items, r.Bytes, retention > 0 ? p.AppliedAt?.AddDays(retention) : null);
            })
            .OrderByDescending(b => b.AppliedAt)
            .ToList();
    }

    /// <summary>Permanently removes a plan's trash folder in every root.</summary>
    public async Task<bool> PurgeAsync(int planId, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var plan = await db.ActionPlans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is null)
        {
            return false;
        }

        foreach (var root in roots.All.Where(r => !r.ReadOnly))
        {
            var dir = Path.Combine(root.Path, RootRegistry.TrashFolder, planId.ToString(CultureInfo.InvariantCulture));
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                var parent = Path.Combine(root.Path, RootRegistry.TrashFolder);
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Could not empty trash {Dir}: {Error}", dir, e.Message);
                return false;
            }
        }

        plan.TrashPurgedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var days = settings.Current.TrashRetentionDays;
        if (days <= 0)
        {
            return 0;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var bins = await ListAsync(ct);
        var n = 0;
        foreach (var b in bins.Where(b => b.AppliedAt < cutoff))
        {
            if (await PurgeAsync(b.PlanId, ct))
            {
                n++;
            }
        }

        return n;
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
                    log.LogInformation("Emptied {Count} expired trash folder(s)", n);
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
