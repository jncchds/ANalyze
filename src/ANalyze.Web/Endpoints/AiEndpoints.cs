using ANalyze.Ai;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Settings;
using ANalyze.Web.Api;
using Microsoft.EntityFrameworkCore;

namespace ANalyze.Web.Endpoints;

public static class AiEndpoints
{
    public sealed record ClassifyBody(int? Limit, int? RootId);

    public sealed record AcceptBody(string? Category);

    public static void MapAiEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/ai");

        g.MapGet("/status", async (AiService ai, SettingsStore settings, AnalyzeDbContext db, CancellationToken ct) => new
        {
            enabled = settings.Current.Ai.Enabled,
            configured = !string.IsNullOrWhiteSpace(settings.Current.Ai.BaseUrl) && !string.IsNullOrWhiteSpace(settings.Current.Ai.Model),
            model = settings.Current.Ai.Model,
            running = ai.IsRunning,
            history = ai.History,
            uncategorized = await db.Entries.CountAsync(e => !e.IsDir && e.CategorySource == CategorySource.None && e.Size > 0, ct),
            review = await db.AiResults.CountAsync(r => !r.Applied && !r.Dismissed, ct),
            suggestions = await db.AiRuleSuggestions.CountAsync(s => s.Status == SuggestionStatus.Pending, ct),
        });

        // Optional body: unsaved settings from the form, so the connection can be tested before saving.
        g.MapPost("/test", async (AiSettings? body, AiService ai, SettingsStore settings, SecretProtector secrets, CancellationToken ct) =>
        {
            var s = settings.Current.Ai;
            if (body is not null)
            {
                var key = body.ApiKey == SystemEndpoints.MaskedKey ? s.ApiKey : secrets.Protect(body.ApiKey);
                s = body with { ApiKey = key };
            }

            return await ai.TestAsync(s, ct);
        });

        g.MapPost("/classify", (ClassifyBody? b, AiService ai, SettingsStore settings) =>
        {
            if (!settings.Current.Ai.Enabled)
            {
                return Results.Problem("AI is disabled in settings", statusCode: 409);
            }

            return ai.StartClassify(b?.Limit, b?.RootId) ? Results.Accepted() : Results.Problem("An AI job is already running", statusCode: 409);
        });

        g.MapPost("/suggest", (AiService ai, SettingsStore settings) =>
        {
            if (!settings.Current.Ai.Enabled)
            {
                return Results.Problem("AI is disabled in settings", statusCode: 409);
            }

            return ai.StartSuggest() ? Results.Accepted() : Results.Problem("An AI job is already running", statusCode: 409);
        });

        g.MapPost("/cancel", (AiService ai) => ai.Cancel() ? Results.NoContent() : Results.NotFound());

        g.MapGet("/review", async (int? offset, int? limit, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var q = db.AiResults.AsNoTracking().Where(r => !r.Applied && !r.Dismissed && r.EntryId != null);
            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(r => r.Confidence)
                .Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 100, 1, 500))
                .Join(db.Entries, r => r.EntryId, e => e.Id, (r, e) => new
                {
                    r.Id, r.Category, r.Tags, r.Confidence, r.Reason, r.Model, r.CreatedAt,
                    entry = new { e.Id, e.RootId, e.Name, e.RelPath, e.Size, e.Kind, e.CategoryId },
                })
                .ToListAsync(ct);
            return new Page<object>(rows.Cast<object>().ToList(), total);
        });

        g.MapPost("/review/{id:long}/accept", async (long id, AcceptBody? b, AiService ai, CancellationToken ct) =>
            await ai.AcceptResultAsync(id, b?.Category, ct) ? Results.NoContent() : Results.NotFound());

        g.MapPost("/review/{id:long}/dismiss", async (long id, AiService ai, CancellationToken ct) =>
            await ai.DismissResultAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        g.MapGet("/suggestions", async (AnalyzeDbContext db, CancellationToken ct) =>
            await db.AiRuleSuggestions.AsNoTracking().OrderBy(s => s.Status).ThenByDescending(s => s.MatchBytes).Take(200).ToListAsync(ct));

        g.MapPost("/suggestions/{id:int}/accept", async (int id, AiService ai, CancellationToken ct) =>
            await ai.AcceptSuggestionAsync(id, ct) is { } rule ? Results.Ok(rule) : Results.NotFound());

        g.MapPost("/suggestions/{id:int}/reject", async (int id, AnalyzeDbContext db, CancellationToken ct) =>
        {
            await db.AiRuleSuggestions.Where(s => s.Id == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SuggestionStatus.Rejected), ct);
            return Results.NoContent();
        });

        g.MapDelete("/suggestions", async (AnalyzeDbContext db, CancellationToken ct) =>
        {
            await db.AiRuleSuggestions.Where(s => s.Status != SuggestionStatus.Pending).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
    }
}
