using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Scanner;
using ANalyze.Web.Api;
using Microsoft.EntityFrameworkCore;

namespace ANalyze.Web.Endpoints;

public static class RuleEndpoints
{
    public sealed record CategoryBody(string Name, string? Color, string? Icon, string? Description, int? SortOrder);

    public sealed record RuleBody(string Name, bool Enabled, RuleCondition Condition, int? CategoryId, List<string>? Tags, bool StopProcessing, string? Description);

    public sealed record TemplateBody(string Name, ActionOp Op, string? Destination, ConflictPolicy Conflict, string? Description);

    /// <summary>Portable rule format: categories by name so rules can move between installations.</summary>
    public sealed record RuleExport(string Name, bool Enabled, RuleCondition Condition, string? Category, List<string> Tags, bool StopProcessing, string? Description);

    public static void MapRuleEndpoints(this RouteGroupBuilder api)
    {
        // ----- Categories -----
        api.MapGet("/categories", async (AnalyzeDbContext db, CancellationToken ct) =>
            await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync(ct));

        api.MapPost("/categories", async (CategoryBody b, AnalyzeDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(b.Name))
            {
                return Results.Problem("Name is required", statusCode: 400);
            }

            if (await db.Categories.AnyAsync(c => c.Name == b.Name.Trim(), ct))
            {
                return Results.Problem("A category with this name exists", statusCode: 409);
            }

            var c = new Category
            {
                Name = b.Name.Trim(),
                Color = b.Color ?? "#64748b",
                Icon = b.Icon,
                Description = b.Description,
                SortOrder = b.SortOrder ?? (await db.Categories.MaxAsync(x => (int?)x.SortOrder, ct) ?? 0) + 1,
            };
            db.Categories.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Ok(c);
        });

        api.MapPut("/categories/{id:int}", async (int id, CategoryBody b, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var c = await db.Categories.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null)
            {
                return Results.NotFound();
            }

            c.Name = b.Name.Trim();
            c.Color = b.Color ?? c.Color;
            c.Icon = b.Icon;
            c.Description = b.Description;
            c.SortOrder = b.SortOrder ?? c.SortOrder;
            await db.SaveChangesAsync(ct);
            return Results.Ok(c);
        });

        api.MapDelete("/categories/{id:int}", async (int id, AnalyzeDbContext db, CancellationToken ct) =>
        {
            await db.Entries.Where(e => e.CategoryId == id).ExecuteUpdateAsync(u => u.SetProperty(e => e.CategoryId, (int?)null).SetProperty(e => e.CategorySource, CategorySource.None), ct);
            await db.Rules.Where(r => r.CategoryId == id).ExecuteUpdateAsync(u => u.SetProperty(r => r.CategoryId, (int?)null), ct);
            await db.Categories.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // ----- Rules -----
        api.MapGet("/rules", async (AnalyzeDbContext db, CancellationToken ct) =>
            await db.Rules.AsNoTracking().OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync(ct));

        api.MapPost("/rules", async (RuleBody b, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var rule = new Rule
            {
                Name = b.Name.Trim(),
                Enabled = b.Enabled,
                Condition = b.Condition,
                CategoryId = b.CategoryId,
                Tags = Clean(b.Tags),
                StopProcessing = b.StopProcessing,
                Description = b.Description,
                Source = RuleSource.User,
                // New rules go first so they can override the generic built-ins.
                Order = (await db.Rules.MinAsync(r => (int?)r.Order, ct) ?? 0) - 1,
            };
            db.Rules.Add(rule);
            await db.SaveChangesAsync(ct);
            return Results.Ok(rule);
        });

        api.MapPut("/rules/{id:int}", async (int id, RuleBody b, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var r = await db.Rules.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (r is null)
            {
                return Results.NotFound();
            }

            r.Name = b.Name.Trim();
            r.Enabled = b.Enabled;
            r.Condition = b.Condition;
            r.CategoryId = b.CategoryId;
            r.Tags = Clean(b.Tags);
            r.StopProcessing = b.StopProcessing;
            r.Description = b.Description;
            await db.SaveChangesAsync(ct);
            return Results.Ok(r);
        });

        api.MapDelete("/rules/{id:int}", async (int id, AnalyzeDbContext db, CancellationToken ct) =>
        {
            await db.Rules.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        api.MapPost("/rules/reorder", async (int[] ids, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var rules = await db.Rules.ToListAsync(ct);
            for (var i = 0; i < ids.Length; i++)
            {
                var r = rules.FirstOrDefault(x => x.Id == ids[i]);
                if (r is not null)
                {
                    r.Order = i;
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Live preview while editing: how many files would match, with samples.
        api.MapPost("/rules/test", (RuleCondition condition, bool? uncategorizedOnly, SqliteSetup sqlite) =>
        {
            var match = RuleEngine.CompileCondition(condition);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var c = sqlite.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, RelPath, Ext, Mime, Kind, Size, MTime, ATime, Depth, RootId, CategoryId FROM Entries WHERE IsDir = 0"
                + (uncategorizedOnly == true ? " AND CategoryId IS NULL" : "");
            using var r = cmd.ExecuteReader();
            long count = 0, bytes = 0;
            var samples = new List<object>();
            while (r.Read())
            {
                var f = new EntryFacts(r.GetString(1), r.GetString(2), r.NullableString(3), r.NullableString(4), (FileKind)r.GetInt32(5),
                    r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt32(9));
                if (!match(f, now))
                {
                    continue;
                }

                count++;
                bytes += f.Size;
                if (samples.Count < 25)
                {
                    samples.Add(new { id = r.GetInt64(0), rootId = r.GetInt32(10), relPath = f.RelPath, size = f.Size, categoryId = r.NullableInt(11) });
                }
            }

            return new { count, bytes, samples };
        });

        // Re-applies all rules to all files in the background.
        api.MapPost("/rules/apply", (Categorizer categorizer, JobBoard board, JobLock jobLock) =>
        {
            _ = Task.Run(async () =>
            {
                var started = DateTimeOffset.UtcNow;
                using var _ = await jobLock.AcquireSharedAsync("rules", CancellationToken.None);
                board.Report(new JobProgress { Kind = JobKinds.Scan, State = JobStates.Running, Phase = "categorizing", Title = "Applying rules", StartedAt = started }, force: true);
                try
                {
                    var changed = await categorizer.RunAsync(Scope.Everything, (done, total) => board.Report(new JobProgress
                    {
                        Kind = JobKinds.Scan, State = JobStates.Running, Phase = "categorizing", Title = "Applying rules", Done = done, Total = total, StartedAt = started,
                    }), CancellationToken.None);
                    board.Report(new JobProgress { Kind = JobKinds.Scan, State = JobStates.Completed, Phase = "completed", Title = "Applying rules", Message = $"{changed} files changed", StartedAt = started, FinishedAt = DateTimeOffset.UtcNow }, force: true);
                }
                catch (Exception e)
                {
                    board.Report(new JobProgress { Kind = JobKinds.Scan, State = JobStates.Failed, Phase = "failed", Title = "Applying rules", Message = e.Message, StartedAt = started, FinishedAt = DateTimeOffset.UtcNow }, force: true);
                }
            });
            return Results.Accepted();
        });

        api.MapPost("/rules/restore-builtins", async (AnalyzeDbContext db, CancellationToken ct) =>
            new { added = await DatabaseInitializer.AddMissingBuiltinsAsync(db, ct) });

        api.MapGet("/rules/export", async (AnalyzeDbContext db, CancellationToken ct) =>
        {
            var cats = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var rules = await db.Rules.AsNoTracking().OrderBy(r => r.Order).ToListAsync(ct);
            var export = rules.Select(r => new RuleExport(r.Name, r.Enabled, r.Condition, r.CategoryId is { } c ? cats.GetValueOrDefault(c) : null, r.Tags, r.StopProcessing, r.Description));
            return Results.Json(export, contentType: "application/json", statusCode: 200);
        });

        api.MapPost("/rules/import", async (List<RuleExport> rules, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var cats = await db.Categories.ToListAsync(ct);
            var order = (await db.Rules.MaxAsync(r => (int?)r.Order, ct) ?? 0) + 1;
            foreach (var r in rules)
            {
                Category? cat = null;
                if (!string.IsNullOrWhiteSpace(r.Category))
                {
                    cat = cats.FirstOrDefault(c => c.Name.Equals(r.Category, StringComparison.OrdinalIgnoreCase));
                    if (cat is null)
                    {
                        cat = new Category { Name = r.Category.Trim() };
                        db.Categories.Add(cat);
                        cats.Add(cat);
                        await db.SaveChangesAsync(ct);
                    }
                }

                db.Rules.Add(new Rule
                {
                    Name = r.Name, Enabled = r.Enabled, Condition = r.Condition, CategoryId = cat?.Id, Tags = Clean(r.Tags),
                    StopProcessing = r.StopProcessing, Description = r.Description, Source = RuleSource.User, Order = order++,
                });
            }

            await db.SaveChangesAsync(ct);
            return new { imported = rules.Count };
        });

        // ----- Action templates -----
        api.MapGet("/templates", async (AnalyzeDbContext db, CancellationToken ct) => await db.ActionTemplates.AsNoTracking().OrderBy(t => t.Id).ToListAsync(ct));

        api.MapPost("/templates", async (TemplateBody b, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var t = new ActionTemplate { Name = b.Name.Trim(), Op = b.Op, Destination = b.Destination, Conflict = b.Conflict, Description = b.Description };
            db.ActionTemplates.Add(t);
            await db.SaveChangesAsync(ct);
            return Results.Ok(t);
        });

        api.MapPut("/templates/{id:int}", async (int id, TemplateBody b, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var t = await db.ActionTemplates.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (t is null)
            {
                return Results.NotFound();
            }

            t.Name = b.Name.Trim();
            t.Op = b.Op;
            t.Destination = b.Destination;
            t.Conflict = b.Conflict;
            t.Description = b.Description;
            await db.SaveChangesAsync(ct);
            return Results.Ok(t);
        });

        api.MapDelete("/templates/{id:int}", async (int id, AnalyzeDbContext db, CancellationToken ct) =>
        {
            await db.ActionTemplates.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
    }

    private static List<string> Clean(List<string>? tags) =>
        tags?.Select(t => t.Trim().ToLowerInvariant().Replace("|", "")).Where(t => t.Length > 0).Distinct().ToList() ?? [];
}
