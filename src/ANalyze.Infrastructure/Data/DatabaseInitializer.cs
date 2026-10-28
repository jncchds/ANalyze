using ANalyze.Domain;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ANalyze.Infrastructure.Data;

/// <summary>Applies migrations, seeds built-in categories/rules/templates and syncs roots.</summary>
public sealed class DatabaseInitializer(
    AnalyzeDbContext db,
    SqliteSetup sqlite,
    SettingsStore settings,
    RootRegistry roots,
    ILogger<DatabaseInitializer> log)
{
    public async Task InitializeAsync(bool migrate, CancellationToken ct = default)
    {
        if (migrate)
        {
            await db.Database.MigrateAsync(ct);
        }

        sqlite.EnableWal();
        await SeedAsync(db, ct);
        await settings.LoadAsync(ct);
        await roots.SyncAsync(ct);
        await RecoverAsync(ct);
    }

    /// <summary>Seeds built-ins into an empty database.</summary>
    public static async Task SeedAsync(AnalyzeDbContext db, CancellationToken ct = default)
    {
        if (!await db.Categories.AnyAsync(ct))
        {
            await AddMissingBuiltinsAsync(db, ct);
        }

        if (!await db.ActionTemplates.AnyAsync(ct))
        {
            foreach (var t in Builtins.Templates)
            {
                db.ActionTemplates.Add(new ActionTemplate { Name = t.Name, Op = t.Op, Destination = t.Destination, Conflict = t.Conflict, Description = t.Description });
            }

            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Adds built-in categories and rules that are missing (by name). Returns the number of rules added.</summary>
    public static async Task<int> AddMissingBuiltinsAsync(AnalyzeDbContext db, CancellationToken ct = default)
    {
        var categories = await db.Categories.ToListAsync(ct);
        var order = categories.Count == 0 ? 0 : categories.Max(c => c.SortOrder) + 1;
        foreach (var c in Builtins.Categories)
        {
            if (categories.All(x => !x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
            {
                var row = new Category { Name = c.Name, Color = c.Color, Icon = c.Icon, Description = c.Description, BuiltIn = true, SortOrder = order++ };
                db.Categories.Add(row);
                categories.Add(row);
            }
        }

        await db.SaveChangesAsync(ct);

        var rules = await db.Rules.ToListAsync(ct);
        var ruleOrder = rules.Count == 0 ? 0 : rules.Max(r => r.Order) + 1;
        var added = 0;
        foreach (var r in Builtins.Rules)
        {
            if (rules.Any(x => x.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var cat = r.Category is null ? null : categories.FirstOrDefault(c => c.Name.Equals(r.Category, StringComparison.OrdinalIgnoreCase));
            db.Rules.Add(new Rule
            {
                Name = r.Name,
                Order = ruleOrder++,
                Condition = r.Condition,
                CategoryId = cat?.Id,
                Tags = [.. r.Tags],
                StopProcessing = r.Stop,
                Source = RuleSource.Builtin,
                Description = r.Description,
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>Jobs interrupted by a restart cannot resume; mark them so the UI does not show them as running.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        var scans = await db.Scans.Where(s => s.Status == ScanStatus.Running || s.Status == ScanStatus.Queued).ToListAsync(ct);
        foreach (var s in scans)
        {
            s.Status = ScanStatus.Cancelled;
            s.Message = "Interrupted by restart";
            s.FinishedAt ??= DateTimeOffset.UtcNow;
        }

        var plans = await db.ActionPlans.Where(p => p.Status == PlanStatus.Running).ToListAsync(ct);
        foreach (var p in plans)
        {
            p.Status = PlanStatus.Failed;
            p.Message = "Interrupted by restart; pending items were not applied";
        }

        if (scans.Count + plans.Count > 0)
        {
            log.LogWarning("Marked {Scans} scans and {Plans} plans as interrupted", scans.Count, plans.Count);
        }

        await db.SaveChangesAsync(ct);
    }
}
