using ANalyze.Actions;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Web.Api;
using Microsoft.EntityFrameworkCore;

namespace ANalyze.Web.Endpoints;

public static class PlanEndpoints
{
    // PlanId: add to that draft plan instead of creating one. Destination of a restore: where the local file is kept (null = trash).
    public sealed record FilterPlanBody(SearchFilter Filter, ActionOp Op, string? Destination, ConflictPolicy? Conflict, string? Name, int? TemplateId, int? PlanId);

    public sealed record ManualPlanBody(long[] EntryIds, ActionOp Op, string? Destination, ConflictPolicy? Conflict, string? Name, int? TemplateId, int? PlanId);

    /// <summary>Every damaged file; any action (restore by default).</summary>
    public sealed record DamagedPlanBody(string? Name, ActionOp? Op, string? Destination, ConflictPolicy? Conflict, int? TemplateId, int? PlanId);

    public sealed record ItemsBody(long[]? Ids, bool Included, string? Filter);

    /// <summary>Gives items (by id, or the ones a filter shows) another action.</summary>
    public sealed record ChangeItemsBody(long[]? Ids, string? Filter, string? Q, ActionOp Op, string? Destination, int? TemplateId);

    public static void MapPlanEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/plans", async (AnalyzeDbContext db, CancellationToken ct) =>
        {
            var plans = await db.ActionPlans.AsNoTracking().OrderByDescending(p => p.Id).Take(200).ToListAsync(ct);
            var ids = plans.Select(p => p.Id).ToList();
            var stats = await db.ActionItems.AsNoTracking().Where(i => ids.Contains(i.PlanId))
                .GroupBy(i => i.PlanId)
                .Select(g => new
                {
                    PlanId = g.Key,
                    Items = g.Count(),
                    Included = g.Count(i => i.Included),
                    Bytes = g.Where(i => i.Included).Sum(i => i.ExpectedSize),
                    Done = g.Count(i => i.Status == ItemStatus.Done),
                    Failed = g.Count(i => i.Status == ItemStatus.Failed),
                    Op = g.Min(i => i.Op),
                })
                .ToDictionaryAsync(x => x.PlanId, ct);
            return plans.Select(p => new
            {
                p.Id, p.Name, p.Source, p.Status, p.Conflict, p.CreatedAt, p.AppliedAt, p.TrashPurgedAt, p.Message,
                items = stats.GetValueOrDefault(p.Id)?.Items ?? 0,
                included = stats.GetValueOrDefault(p.Id)?.Included ?? 0,
                bytes = stats.GetValueOrDefault(p.Id)?.Bytes ?? 0,
                done = stats.GetValueOrDefault(p.Id)?.Done ?? 0,
                failed = stats.GetValueOrDefault(p.Id)?.Failed ?? 0,
                op = stats.GetValueOrDefault(p.Id)?.Op,
            });
        });

        api.MapGet("/plans/{id:int}", async (int id, string? filter, string? q, int? offset, int? limit, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var plan = await db.ActionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null)
            {
                return Results.NotFound();
            }

            var items = Filter(db.ActionItems.AsNoTracking().Where(i => i.PlanId == id), filter, q);
            var total = await items.CountAsync(ct);
            var page = await items.OrderBy(i => i.SourcePath).Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 200, 1, 2000)).ToListAsync(ct);
            var all = db.ActionItems.AsNoTracking().Where(i => i.PlanId == id);
            var summary = new
            {
                items = await all.CountAsync(ct),
                included = await all.CountAsync(i => i.Included, ct),
                bytes = await all.Where(i => i.Included).SumAsync(i => (long?)i.ExpectedSize, ct) ?? 0,
                warnings = await all.CountAsync(i => i.Warning != null, ct),
                done = await all.CountAsync(i => i.Status == ItemStatus.Done, ct),
                skipped = await all.CountAsync(i => i.Status == ItemStatus.Skipped, ct),
                failed = await all.CountAsync(i => i.Status == ItemStatus.Failed, ct),
                undone = await all.CountAsync(i => i.Status == ItemStatus.Undone, ct),
                undoable = await all.CountAsync(i => i.Status == ItemStatus.Done && (i.Op == ActionOp.Trash || i.Op == ActionOp.Move || i.Op == ActionOp.Restore), ct),
            };
            // The folder each item's entry is in, for links to the browser (entries removed by the plan are gone).
            var entryIds = page.Where(i => i.EntryId is not null).Select(i => i.EntryId!.Value).ToList();
            var parents = await db.Entries.AsNoTracking().Where(e => entryIds.Contains(e.Id) && e.ParentId != null)
                .ToDictionaryAsync(e => e.Id, e => e.ParentId!.Value, ct);
            return Results.Ok(new { plan, summary, page = new Page<ActionItem>(page, total), parents });
        });

        api.MapPost("/plans", async (ManualPlanBody b, PlanBuilder builder, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var (op, dest, conflict) = await FromTemplateAsync(db, b.TemplateId, b.Op, b.Destination, b.Conflict, ct);
            return await Run(builder, b.PlanId, new ManualPlanRequest(b.Name, op, b.EntryIds, dest, conflict, b.TemplateId is null ? PlanSource.Manual : PlanSource.Template), ct);
        });

        // Another action for some items of a draft plan: they are planned again from their entries.
        api.MapPost("/plans/{id:int}/items/change", async (int id, ChangeItemsBody b, PlanBuilder builder, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var q = db.ActionItems.AsNoTracking().Where(i => i.PlanId == id && i.EntryId != null);
            q = b.Ids is { Length: > 0 } ids ? q.Where(i => ids.Contains(i.Id)) : Filter(q, b.Filter, b.Q);
            var entries = await q.Select(i => i.EntryId!.Value).Take(PlanBuilder.MaxItems).ToListAsync(ct);
            if (entries.Count == 0)
            {
                return Results.Problem("No items to change (items whose file is no longer indexed cannot be planned again).", statusCode: 400);
            }

            var (op, dest, _) = await FromTemplateAsync(db, b.TemplateId, b.Op, b.Destination, null, ct);
            return await Run(builder, id, new ManualPlanRequest(null, op, entries, dest), ct);
        });

        api.MapPost("/plans/from-filter", async (FilterPlanBody b, PlanBuilder builder, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var (op, dest, conflict) = await FromTemplateAsync(db, b.TemplateId, b.Op, b.Destination, b.Conflict, ct);
            var ids = await b.Filter.Apply(db.Entries.AsNoTracking(), db.ArchiveExtractions.AsNoTracking()).OrderBy(e => e.Id).Select(e => e.Id).Take(PlanBuilder.MaxItems + 1).ToListAsync(ct);
            if (ids.Count > PlanBuilder.MaxItems)
            {
                return Results.Problem($"The filter matches more than {PlanBuilder.MaxItems} items; narrow it down.", statusCode: 400);
            }

            return await Run(builder, b.PlanId, new ManualPlanRequest(b.Name, op, ids, dest, conflict, PlanSource.Insight), ct);
        });

        api.MapPost("/plans/dedupe", async (DedupePlanRequest req, PlanBuilder builder, CancellationToken ct) =>
            await Run(() => builder.CreateDedupeAsync(req, ct)));

        // Every file the backup comparison found damaged here, replaced with the backup's copy by default (draft plan for review).
        api.MapPost("/plans/restore-damaged", async (DamagedPlanBody? b, PlanBuilder builder, AnalyzeDbContext db, IEnumerable<ANalyze.Scanner.IBackupSource> backups, CancellationToken ct) =>
        {
            var ids = new HashSet<long>();
            foreach (var source in backups)
            {
                ids.UnionWith(await source.DamagedAsync(ct));
            }

            if (ids.Count == 0)
            {
                return Results.Problem("No damaged files with a copy on the backup.", statusCode: 400);
            }

            var (op, dest, conflict) = await FromTemplateAsync(db, b?.TemplateId, b?.Op ?? ActionOp.Restore, b?.Destination, b?.Conflict, ct);
            var name = b?.Name ?? (op == ActionOp.Restore ? $"Restore {ids.Count} damaged file(s) from the backup" : null);
            return await Run(builder, b?.PlanId, new ManualPlanRequest(name, op, [.. ids.Take(PlanBuilder.MaxItems)], dest, conflict), ct);
        });

        api.MapPatch("/plans/{id:int}/items", async (int id, ItemsBody b, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var plan = await db.ActionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null)
            {
                return Results.NotFound();
            }

            if (plan.Status != PlanStatus.Draft)
            {
                return Results.Problem("Only draft plans can be edited", statusCode: 409);
            }

            var q = db.ActionItems.Where(i => i.PlanId == id);
            q = b.Ids is { Length: > 0 } ids ? q.Where(i => ids.Contains(i.Id)) : Filter(q, b.Filter, null);
            if (b.Included)
            {
                // Read-only roots can never be included.
                q = q.Where(i => i.Warning != "Root is read-only");
            }

            var n = await q.ExecuteUpdateAsync(u => u.SetProperty(i => i.Included, b.Included), ct);
            return Results.Ok(new { updated = n });
        });

        api.MapPost("/plans/{id:int}/apply", async (int id, AnalyzeDbContext db, PlanQueue queue, CancellationToken ct) =>
        {
            var plan = await db.ActionPlans.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null)
            {
                return Results.NotFound();
            }

            if (plan.Status is not (PlanStatus.Draft or PlanStatus.Cancelled or PlanStatus.Failed))
            {
                return Results.Problem("This plan was already applied", statusCode: 409);
            }

            queue.Enqueue(new PlanJob(id, Undo: false));
            return Results.Accepted();
        });

        api.MapPost("/plans/{id:int}/undo", async (int id, AnalyzeDbContext db, PlanQueue queue, CancellationToken ct) =>
        {
            var plan = await db.ActionPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null)
            {
                return Results.NotFound();
            }

            if (plan.Status is not (PlanStatus.Done or PlanStatus.Cancelled or PlanStatus.Failed))
            {
                return Results.Problem("Only applied plans can be undone", statusCode: 409);
            }

            // Items whose trash copy is gone are reported as skipped by the executor.
            queue.Enqueue(new PlanJob(id, Undo: true));
            return Results.Accepted();
        });

        api.MapPost("/plans/cancel", (PlanQueue queue) => queue.Cancel() ? Results.NoContent() : Results.NotFound());

        api.MapDelete("/plans/{id:int}", async (int id, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var plan = await db.ActionPlans.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null)
            {
                return Results.NotFound();
            }

            if (plan.Status == PlanStatus.Running)
            {
                return Results.Problem("The plan is running", statusCode: 409);
            }

            var hasTrash = plan.TrashPurgedAt is null && await db.ActionItems.AnyAsync(i => i.PlanId == id && (i.Op == ActionOp.Trash || (i.Op == ActionOp.Restore && i.KeepPath == null)) && i.Status == ItemStatus.Done, ct);
            if (hasTrash)
            {
                return Results.Problem("Empty this plan's trash first (or undo it)", statusCode: 409);
            }

            db.ActionPlans.Remove(plan);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/trash", async (TrashService trash, CancellationToken ct) => await trash.ListAsync(ct));

        api.MapDelete("/trash/{planId:int}", async (int planId, TrashService trash, CancellationToken ct) =>
            await trash.PurgeAsync(planId, ct) ? Results.NoContent() : Results.Problem("Could not empty the trash", statusCode: 409));

        api.MapDelete("/trash", async (TrashService trash, CancellationToken ct) =>
        {
            var n = 0;
            foreach (var b in await trash.ListAsync(ct))
            {
                if (await trash.PurgeAsync(b.PlanId, ct))
                {
                    n++;
                }
            }

            return Results.Ok(new { emptied = n });
        });
    }

    private static IQueryable<ActionItem> Filter(IQueryable<ActionItem> q, string? filter, string? search)
    {
        q = filter switch
        {
            "included" => q.Where(i => i.Included),
            "excluded" => q.Where(i => !i.Included),
            "warnings" => q.Where(i => i.Warning != null),
            "failed" => q.Where(i => i.Status == ItemStatus.Failed || i.Status == ItemStatus.Skipped),
            "done" => q.Where(i => i.Status == ItemStatus.Done),
            _ => q,
        };
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = "%" + search.Trim() + "%";
            q = q.Where(i => EF.Functions.Like(i.SourcePath, like) || (i.TargetPath != null && EF.Functions.Like(i.TargetPath, like)));
        }

        return q;
    }

    private static async Task<(ActionOp, string?, ConflictPolicy)> FromTemplateAsync(AnalyzeDbContext db, int? templateId, ActionOp op, string? dest, ConflictPolicy? conflict, CancellationToken ct)
    {
        if (templateId is { } tid && await db.ActionTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid, ct) is { } t)
        {
            return (t.Op, t.Destination, conflict ?? t.Conflict);
        }

        return (op, dest, conflict ?? ConflictPolicy.Rename);
    }

    /// <summary>Creates a plan, or adds to the draft plan <paramref name="planId"/>.</summary>
    private static async Task<IResult> Run(PlanBuilder builder, int? planId, ManualPlanRequest req, CancellationToken ct)
    {
        try
        {
            if (planId is { } pid)
            {
                var r = await builder.AddAsync(pid, req, ct);
                return Results.Ok(new { r.Id, r.Name, r.Items, r.Added, r.Replaced });
            }

            var plan = await builder.CreateAsync(req, ct);
            return Results.Ok(new { plan.Id, plan.Name, items = plan.Items.Count, added = plan.Items.Count, replaced = 0 });
        }
        catch (ArgumentException e)
        {
            return Results.Problem(e.Message, statusCode: 400);
        }
        catch (KeyNotFoundException e)
        {
            return Results.Problem(e.Message, statusCode: 404);
        }
        catch (InvalidOperationException e)
        {
            return Results.Problem(e.Message, statusCode: 409);
        }
    }

    private static async Task<IResult> Run(Func<Task<ActionPlan>> create)
    {
        try
        {
            var plan = await create();
            return Results.Ok(new { plan.Id, plan.Name, items = plan.Items.Count });
        }
        catch (ArgumentException e)
        {
            return Results.Problem(e.Message, statusCode: 400);
        }
    }
}
