using ANalyze.Infrastructure.Jobs;
using ANalyze.Web.Api;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using ANalyze.Integrations;

namespace ANalyze.Web.Endpoints;

/// <summary>Generic plugin endpoints plus each plugin's own routes under /integrations/{id}.</summary>
public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this RouteGroupBuilder api)
    {
        var host = api.ServiceProvider().GetRequiredService<IntegrationHost>();
        var g = api.MapGroup("/integrations");

        g.MapGet("", async (IntegrationHost h, IntegrationPaths paths, CancellationToken ct) =>
        {
            var list = new List<object>();
            foreach (var i in h.All)
            {
                var settings = await h.LoadAsync(i.Id, ct);
                // Mappings found automatically (Docker), shown next to the mappings field.
                var auto = settings.Enabled && i.Fields.Any(f => f.Type == "mappings") ? await paths.AutoAsync(i, settings, ct) : [];
                list.Add(new
                {
                    i.Id, i.Name, i.Description, i.Icon, i.Fields,
                    settings = await h.LoadMaskedAsync(i.Id, ct),
                    status = h.Status(i.Id),
                    auto,
                });
            }

            return list;
        });

        g.MapPut("/{id}", async (string id, IntegrationSettings body, IntegrationHost h, JobLock jobLock, IOptions<JsonOptions> json, CancellationToken ct) =>
        {
            if (h.Get(id) is null)
            {
                return Results.NotFound();
            }

            var written = await h.SaveAsync(id, body, ct);
            return SaveResult.Ok(await h.LoadMaskedAsync(id, ct), written, jobLock, json.Value);
        });

        g.MapPost("/{id}/test", async (string id, IntegrationSettings? body, IntegrationHost h, CancellationToken ct) =>
        {
            var i = h.Get(id);
            if (i is null)
            {
                return Results.NotFound();
            }

            var settings = body is null ? await h.LoadAsync(id, ct) : await h.MergeAsync(id, body, ct);
            return Results.Ok(await i.TestAsync(settings, ct));
        });

        g.MapPost("/{id}/sync", (string id, IntegrationHost h) =>
            h.Get(id) is null ? Results.NotFound() : h.StartSync(id) ? Results.Accepted() : Results.Problem("Already syncing", statusCode: 409));

        foreach (var i in host.All)
        {
            i.MapEndpoints(g.MapGroup("/" + i.Id));
        }
    }

    private static IServiceProvider ServiceProvider(this IEndpointRouteBuilder b) => b.ServiceProvider;
}
