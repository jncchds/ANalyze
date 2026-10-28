using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Scanner;
using ANalyze.Web.Api;
using ANalyze.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ANalyze.Web.Endpoints;

public static class BrowseEndpoints
{
    public sealed record TreeNode(long Id, string Name, long Size, bool IsDir, int? CategoryId, long Files, List<TreeNode>? Children, bool Other = false);

    public sealed record CategoryBody(int? CategoryId);

    public static void MapBrowseEndpoints(this RouteGroupBuilder api)
    {
        // Totals and breakdowns for everything, one root, or one folder.
        api.MapGet("/overview", async (int? rootId, string? path, SqliteSetup sqlite, IMemoryCache cache) =>
            await Caches.GetAsync(cache, $"overview:{rootId}:{path}", () => Task.FromResult(Overview(sqlite, rootId, path))));

        api.MapGet("/entries/{id:long}", async (long id, AnalyzeDbContext db, RootRegistry roots, CancellationToken ct) =>
        {
            var e = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null)
            {
                return Results.NotFound();
            }

            var crumbs = new List<object>();
            var root = roots.Get(e.RootId);
            var parts = e.RelPath.Length == 0 ? [] : e.RelPath.Split('/');
            var paths = Enumerable.Range(0, parts.Length + 1).Select(i => string.Join('/', parts.Take(i))).ToList();
            var ancestors = await db.Entries.AsNoTracking().Where(x => x.RootId == e.RootId && paths.Contains(x.RelPath))
                .Select(x => new { x.Id, x.Name, x.RelPath }).ToListAsync(ct);
            foreach (var a in ancestors.OrderBy(a => a.RelPath.Length))
            {
                crumbs.Add(new { a.Id, Name = a.RelPath.Length == 0 ? root?.Name ?? a.Name : a.Name });
            }

            var copies = e.FullHash is null ? 0 : await db.Entries.CountAsync(x => x.FullHash == e.FullHash && x.Id != e.Id, ct);
            var ai = await db.AiResults.AsNoTracking().Where(r => r.EntryId == id).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
            // How this file compares with the parent ANalyze, when this instance is its backup.
            var backup = await db.BackupFiles.AsNoTracking().Where(b => b.EntryId == id).FirstOrDefaultAsync(ct);
            return Results.Ok(new
            {
                entry = EntryDto.From(e),
                root = root is null ? null : new { root.Id, root.Name, root.Path, root.ReadOnly },
                fullPath = root is null ? null : RootRegistry.Combine(root.Path, e.RelPath),
                breadcrumbs = crumbs,
                copies,
                linkTarget = e.LinkTarget,
                ai = ai is null ? null : new { ai.Id, ai.Category, ai.Tags, ai.Confidence, ai.Reason, ai.Applied, ai.Model },
                backup = backup is null ? null : new { backup.State, backup.Side, backup.SourcePath, backup.Size, backup.MTime, parentHashed = backup.Hash != null },
                annotations = (await AnnotationsFor(db, [id], ct)).GetValueOrDefault(id) ?? [],
            });
        });

        api.MapGet("/entries/{id:long}/children", async (long id, string? sort, bool? asc, int? offset, int? limit, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var q = db.Entries.AsNoTracking().Where(e => e.ParentId == id);
            var total = await q.CountAsync(ct);
            var desc = asc != true;
            q = (sort ?? "size") switch
            {
                "name" => desc ? q.OrderByDescending(e => e.IsDir).ThenByDescending(e => e.Name) : q.OrderByDescending(e => e.IsDir).ThenBy(e => e.Name),
                "mtime" => desc ? q.OrderByDescending(e => e.MTime) : q.OrderBy(e => e.MTime),
                "files" => desc ? q.OrderByDescending(e => e.FileCount) : q.OrderBy(e => e.FileCount),
                "kind" => desc ? q.OrderByDescending(e => e.Kind).ThenByDescending(e => e.TotalSize) : q.OrderBy(e => e.Kind).ThenByDescending(e => e.TotalSize),
                _ => desc ? q.OrderByDescending(e => e.TotalSize) : q.OrderBy(e => e.TotalSize),
            };
            var items = await q.Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 500, 1, 5000)).ToListAsync(ct);
            var ids = items.Select(i => i.Id).ToList();
            var notes = await AnnotationsFor(db, ids, ct);
            return new { items = items.Select(EntryDto.From).ToList(), total, annotations = notes };
        });

        // Nested sizes for the treemap/sunburst; small children are folded into "other".
        api.MapGet("/entries/{id:long}/tree", (long id, int? depth, int? maxChildren, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var top = Sql.Query(c, "SELECT Id, Name, TotalSize, IsDir, CategoryId, FileCount FROM Entries WHERE Id = $id",
                r => new TreeNode(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetBoolean(3), r.NullableInt(4), r.GetInt64(5), null), ("$id", id)).FirstOrDefault();
            if (top is null)
            {
                return Results.NotFound();
            }

            return Results.Ok(Expand(c, top, Math.Clamp(depth ?? 2, 1, 4), Math.Clamp(maxChildren ?? 30, 5, 200)));
        });

        api.MapGet("/entries/{id:long}/history", async (long id, AnalyzeDbContext db, CancellationToken ct) =>
            await db.DirSizeHistory.AsNoTracking().Where(h => h.EntryId == id).OrderBy(h => h.ScanId)
                .Select(h => new { h.ScanId, h.At, h.TotalSize, h.FileCount }).ToListAsync(ct));

        api.MapPut("/entries/{id:long}/category", async (long id, CategoryBody body, AnalyzeDbContext db, Categorizer categorizer, CancellationToken ct) =>
        {
            var e = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null)
            {
                return Results.NotFound();
            }

            // On a folder this applies to every file inside it.
            var q = e.IsDir
                ? db.Entries.Where(x => x.RootId == e.RootId && !x.IsDir && string.Compare(x.RelPath, e.RelPath + "/") >= 0 && string.Compare(x.RelPath, e.RelPath + "0") < 0)
                : db.Entries.Where(x => x.Id == id);
            if (e.IsDir && e.RelPath.Length == 0)
            {
                q = db.Entries.Where(x => x.RootId == e.RootId && !x.IsDir);
            }

            if (body.CategoryId is { } cat)
            {
                await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.CategoryId, cat).SetProperty(x => x.CategorySource, CategorySource.Manual), ct);
            }
            else
            {
                // Back to automatic: forget the manual choice and let the rules decide again.
                await q.ExecuteUpdateAsync(u => u.SetProperty(x => x.CategoryId, (int?)null).SetProperty(x => x.CategorySource, CategorySource.None), ct);
                await categorizer.RunAsync(new Scope(e.RootId, e.RelPath), null, ct);
            }

            return Results.NoContent();
        });

        api.MapPost("/entries/{id:long}/rescan", async (long id, AnalyzeDbContext db, ScanQueue queue, CancellationToken ct) =>
        {
            var e = await db.Entries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null)
            {
                return Results.NotFound();
            }

            var dir = e.IsDir ? e.RelPath : (e.RelPath.LastIndexOf('/') is var i and > 0 ? e.RelPath[..i] : "");
            queue.Enqueue(new ScanRequest(e.RootId, dir, Reason: "Folder rescan"));
            return Results.Accepted();
        });

        api.MapGet("/search", async ([AsParameters] SearchQuery query, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var q = query.Filter.Apply(db.Entries.AsNoTracking());
            var (total, totalSize) = await CountAsync(q, query.Filter, ct);
            var items = await query.Filter.Order(q).Skip(Math.Max(0, query.Offset ?? 0)).Take(Math.Clamp(query.Limit ?? 100, 1, 1000)).ToListAsync(ct);
            return new { items = items.Select(EntryDto.From).ToList(), total, totalSize, annotations = await AnnotationsFor(db, items.Select(i => i.Id).ToList(), ct) };
        });

        api.MapPost("/search", async (SearchFilter filter, int? offset, int? limit, AnalyzeDbContext db, CancellationToken ct) =>
        {
            var q = filter.Apply(db.Entries.AsNoTracking());
            var (total, totalSize) = await CountAsync(q, filter, ct);
            var items = await filter.Order(q).Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 100, 1, 1000)).ToListAsync(ct);
            return new Page<EntryDto>(items.Select(EntryDto.From).ToList(), total, totalSize);
        });
    }

    public sealed class SearchQuery
    {
        public string? Q { get; init; }
        public int? RootId { get; init; }
        public string? Path { get; init; }
        public int? CategoryId { get; init; }
        public bool? Uncategorized { get; init; }
        public string? Kind { get; init; }
        public string? Ext { get; init; }
        public string? Tag { get; init; }
        public long? MinSize { get; init; }
        public long? MaxSize { get; init; }
        public int? OlderThanDays { get; init; }
        public int? NewerThanDays { get; init; }
        public int? NotAccessedDays { get; init; }
        public bool? Duplicates { get; init; }
        public bool? Dirs { get; init; }
        public bool? EmptyDirs { get; init; }
        public bool? BrokenLinks { get; init; }
        public bool? Sparse { get; init; }
        public bool? Hardlinked { get; init; }
        public bool? Corrupt { get; init; }
        public string? Segment { get; init; }
        public string? Sort { get; init; }
        public bool? Asc { get; init; }
        public int? Offset { get; init; }
        public int? Limit { get; init; }

        public SearchFilter Filter => new()
        {
            Q = Q, RootId = RootId, Path = Path, CategoryId = CategoryId, Uncategorized = Uncategorized, Kind = Enum.TryParse<FileKind>(Kind, ignoreCase: true, out var k) ? k : null, Ext = Ext, Tag = Tag,
            MinSize = MinSize, MaxSize = MaxSize, OlderThanDays = OlderThanDays, NewerThanDays = NewerThanDays, NotAccessedDays = NotAccessedDays,
            Duplicates = Duplicates, Dirs = Dirs, EmptyDirs = EmptyDirs, BrokenLinks = BrokenLinks, Sparse = Sparse, Hardlinked = Hardlinked, Corrupt = Corrupt,
            Segment = Segment, Sort = Sort, Asc = Asc,
        };
    }

    /// <summary>Integration labels (Plex title, library…) for a set of entries, grouped by entry id.</summary>
    public static async Task<Dictionary<long, List<AnnotationDto>>> AnnotationsFor(AnalyzeDbContext db, List<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Annotations.AsNoTracking().Where(a => ids.Contains(a.EntryId))
            .Select(a => new AnnotationDto(a.EntryId, a.Source, a.Kind, a.Label, a.Detail, a.State, a.Url)).ToListAsync(ct);
        return rows.GroupBy(r => r.EntryId).ToDictionary(g => g.Key, g => g.ToList());
    }

    public sealed record AnnotationDto(long EntryId, string Source, string Kind, string Label, string? Detail, string? State, string? Url);

    private static async Task<(long Count, long Size)> CountAsync(IQueryable<Entry> q, SearchFilter f, CancellationToken ct)
    {
        var dirs = f.Dirs == true || f.EmptyDirs == true;
        var agg = await q.GroupBy(_ => 1)
            .Select(g => new { Count = g.LongCount(), Size = dirs ? g.Sum(e => e.TotalSize) : g.Sum(e => e.Size) })
            .FirstOrDefaultAsync(ct);
        return (agg?.Count ?? 0, agg?.Size ?? 0);
    }

    private static TreeNode Expand(Microsoft.Data.Sqlite.SqliteConnection c, TreeNode node, int depth, int maxChildren)
    {
        if (!node.IsDir || depth == 0)
        {
            return node;
        }

        var children = Sql.Query(c, $"SELECT Id, Name, TotalSize, IsDir, CategoryId, FileCount FROM Entries WHERE ParentId = $id ORDER BY TotalSize DESC LIMIT {maxChildren + 1}",
            r => new TreeNode(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetBoolean(3), r.NullableInt(4), r.GetInt64(5), null), ("$id", node.Id));
        var list = new List<TreeNode>();
        long shown = 0;
        foreach (var ch in children.Take(maxChildren))
        {
            // Tiny slivers are unreadable in a treemap.
            if (node.Size > 0 && ch.Size < node.Size / 1000 && list.Count >= 5)
            {
                break;
            }

            list.Add(Expand(c, ch, depth - 1, maxChildren));
            shown += ch.Size;
        }

        var rest = node.Size - shown;
        if (rest > 0 && rest > node.Size / 500)
        {
            list.Add(new TreeNode(-node.Id, "(other)", rest, false, null, 0, null, Other: true));
        }

        return node with { Children = list };
    }

    private static object Overview(SqliteSetup sqlite, int? rootId, string? path)
    {
        using var c = sqlite.Open();
        var (scope, args) = Sql.ScopeOf(rootId, path);
        var totals = Sql.Query(c, $"SELECT COUNT(*), COALESCE(SUM(Size),0), COALESCE(SUM(AllocSize),0) FROM Entries e WHERE IsDir = 0 {scope}",
            r => new { files = r.GetInt64(0), size = r.GetInt64(1), alloc = r.GetInt64(2) }, args).First();
        var dirs = Sql.Scalar<long>(c, $"SELECT COUNT(*) FROM Entries e WHERE IsDir = 1 {scope}", args);

        var byCategory = Sql.Query(c, $"""
            SELECT e.CategoryId, COUNT(*), SUM(e.Size) FROM Entries e WHERE e.IsDir = 0 {scope}
            GROUP BY e.CategoryId ORDER BY SUM(e.Size) DESC
            """, r => new { categoryId = r.NullableInt(0), files = r.GetInt64(1), size = r.GetInt64(2) }, args);

        var byKind = Sql.Query(c, $"""
            SELECT e.Kind, COUNT(*), SUM(e.Size) FROM Entries e WHERE e.IsDir = 0 {scope}
            GROUP BY e.Kind ORDER BY SUM(e.Size) DESC
            """, r => new { kind = (FileKind)r.GetInt32(0), files = r.GetInt64(1), size = r.GetInt64(2) }, args);

        var byExt = Sql.Query(c, $"""
            SELECT COALESCE(e.Ext, ''), COUNT(*), SUM(e.Size) FROM Entries e WHERE e.IsDir = 0 {scope}
            GROUP BY e.Ext ORDER BY SUM(e.Size) DESC LIMIT 25
            """, r => new { ext = r.GetString(0), files = r.GetInt64(1), size = r.GetInt64(2) }, args);

        var largestFiles = Sql.Query(c, $"""
            SELECT e.Id, e.RootId, e.Name, e.RelPath, e.Size, e.CategoryId, e.Kind, e.MTime FROM Entries e
            WHERE e.IsDir = 0 {scope} ORDER BY e.Size DESC LIMIT 25
            """, r => new { id = r.GetInt64(0), rootId = r.GetInt32(1), name = r.GetString(2), relPath = r.GetString(3), size = r.GetInt64(4), categoryId = r.NullableInt(5), kind = (FileKind)r.GetInt32(6), mtime = r.GetInt64(7) }, args);

        // "Heaviest folders": by bytes of the files directly inside them, so parents do not drown out the real culprits.
        var heaviest = Sql.Query(c, $"""
            SELECT p.Id, p.RootId, p.Name, p.RelPath, SUM(e.Size) AS direct, COUNT(*), p.TotalSize FROM Entries e JOIN Entries p ON p.Id = e.ParentId
            WHERE e.IsDir = 0 {scope} GROUP BY p.Id ORDER BY direct DESC LIMIT 25
            """, r => new { id = r.GetInt64(0), rootId = r.GetInt32(1), name = r.GetString(2), relPath = r.GetString(3), direct = r.GetInt64(4), files = r.GetInt64(5), total = r.GetInt64(6) }, args);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var age = Sql.Query(c, $"""
            SELECT CASE
                WHEN $now - e.MTime < 2592000 THEN 0
                WHEN $now - e.MTime < 31536000 THEN 1
                WHEN $now - e.MTime < 94608000 THEN 2
                WHEN $now - e.MTime < 157680000 THEN 3
                WHEN $now - e.MTime < 315360000 THEN 4
                ELSE 5 END AS bucket, COUNT(*), SUM(e.Size)
            FROM Entries e WHERE e.IsDir = 0 {scope} GROUP BY bucket ORDER BY bucket
            """, r => new { bucket = r.GetInt32(0), files = r.GetInt64(1), size = r.GetInt64(2) }, [.. args, ("$now", now)]);
        string[] ageLabels = ["< 1 month", "1-12 months", "1-3 years", "3-5 years", "5-10 years", "10+ years"];

        var bySize = Sql.Query(c, $"""
            SELECT CASE
                WHEN e.Size < 1048576 THEN 0
                WHEN e.Size < 104857600 THEN 1
                WHEN e.Size < 1073741824 THEN 2
                WHEN e.Size < 10737418240 THEN 3
                ELSE 4 END AS bucket, COUNT(*), SUM(e.Size)
            FROM Entries e WHERE e.IsDir = 0 {scope} GROUP BY bucket ORDER BY bucket
            """, r => new { bucket = r.GetInt32(0), files = r.GetInt64(1), size = r.GetInt64(2) }, args);
        string[] sizeLabels = ["< 1 MB", "1-100 MB", "100 MB-1 GB", "1-10 GB", "10+ GB"];

        return new
        {
            totals = new { totals.files, dirs, totals.size, totals.alloc },
            byCategory,
            byKind,
            byExtension = byExt,
            largestFiles,
            heaviestFolders = heaviest,
            byAge = age.Select(a => new { label = ageLabels[a.bucket], a.files, a.size }),
            bySize = bySize.Select(a => new { label = sizeLabels[a.bucket], a.files, a.size }),
        };
    }
}
