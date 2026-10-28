using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Preview;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace ANalyze.Web.Endpoints;

/// <summary>Content access for previews. Paths are always resolved from the database id, never from the request.</summary>
public static class FileEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public static void MapFileEndpoints(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/files/{id:long}");

        g.MapGet("/content", async (long id, bool? download, AnalyzeDbContext db, RootRegistry roots, CancellationToken ct) =>
        {
            var (e, path) = await ResolveAsync(id, db, roots, ct);
            if (e is null || path is null)
            {
                return Results.NotFound();
            }

            var type = e.Mime ?? (ContentTypes.TryGetContentType(e.Name, out var t) ? t : "application/octet-stream");
            // Never let the browser render active content from the analyzed drive in our origin.
            if (download != true && type is "text/html" or "image/svg+xml" or "application/xhtml+xml" or "text/javascript")
            {
                type = "text/plain";
            }

            return Results.File(path, type, download == true ? e.Name : null, enableRangeProcessing: true,
                lastModified: DateTimeOffset.FromUnixTimeSeconds(e.MTime));
        });

        g.MapGet("/thumb", async (long id, int? size, AnalyzeDbContext db, RootRegistry roots, PreviewService preview, HttpContext http, CancellationToken ct) =>
        {
            var (e, path) = await ResolveAsync(id, db, roots, ct);
            if (e is null || path is null)
            {
                return Results.NotFound();
            }

            var thumb = await preview.ThumbnailAsync(e.Id, e.MTime, path, e.Kind, size ?? 320, ct);
            if (thumb is null)
            {
                return Results.NoContent();
            }

            http.Response.Headers[HeaderNames.CacheControl] = "private, max-age=86400";
            return Results.File(thumb, "image/webp");
        });

        g.MapGet("/text", async (long id, long? offset, int? length, AnalyzeDbContext db, RootRegistry roots, CancellationToken ct) =>
        {
            var (e, path) = await ResolveAsync(id, db, roots, ct);
            return e is null || path is null ? Results.NotFound() : Results.Ok(await PreviewService.ReadTextAsync(path, offset ?? 0, length ?? 64 * 1024, ct));
        });

        g.MapGet("/hex", async (long id, long? offset, int? length, AnalyzeDbContext db, RootRegistry roots, CancellationToken ct) =>
        {
            var (e, path) = await ResolveAsync(id, db, roots, ct);
            return e is null || path is null ? Results.NotFound() : Results.Ok(await PreviewService.ReadBytesAsync(path, offset ?? 0, length ?? 4096, ct));
        });

        g.MapGet("/archive", async (long id, AnalyzeDbContext db, RootRegistry roots, CancellationToken ct) =>
        {
            var (e, path) = await ResolveAsync(id, db, roots, ct);
            return e is null || path is null ? Results.NotFound() : Results.Ok(await Task.Run(() => PreviewService.ListArchive(path), ct));
        });

        g.MapGet("/meta", async (long id, AnalyzeDbContext db, RootRegistry roots, CancellationToken ct) =>
        {
            var (e, path) = await ResolveAsync(id, db, roots, ct);
            return e is null || path is null ? Results.NotFound() : Results.Ok(await Task.Run(() => PreviewService.ReadMetadata(path, e.Kind), ct));
        });
    }

    private static async Task<(Entry? Entry, string? Path)> ResolveAsync(long id, AnalyzeDbContext db, RootRegistry roots, CancellationToken ct)
    {
        var e = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsDir, ct);
        if (e is null)
        {
            return (null, null);
        }

        try
        {
            var path = roots.ResolveForRead(e.RootId, e.RelPath);
            return File.Exists(path) ? (e, path) : (null, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or KeyNotFoundException)
        {
            return (null, null);
        }
    }
}
