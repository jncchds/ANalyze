using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Web.Api;
using ANalyze.Web.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ANalyze.Web.Endpoints;

/// <summary>
/// Ready-made findings. Each insight carries the <see cref="SearchFilter"/> that lists its items,
/// so the UI can open it in Search and turn it into a plan with one click.
/// </summary>
public static class InsightEndpoints
{
    public sealed record Insight(string Key, string Title, string Description, string Severity, long Count, long Bytes, SearchFilter Filter, ActionOp? SuggestedOp);

    public static void MapInsightEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/insights", async (int? rootId, AnalyzeDbContext db, IMemoryCache cache, CancellationToken ct) =>
            await Caches.GetAsync(cache, $"insights:{rootId}", () => BuildAsync(db, rootId, ct)));

        // Folders that grew the most between the last two full scans.
        api.MapGet("/insights/growth", (int? rootId, int? limit, SqliteSetup sqlite) =>
        {
            using var c = sqlite.Open();
            var scans = Sql.Query(c, "SELECT DISTINCT ScanId FROM DirSizeHistory" + (rootId is null ? "" : " WHERE RootId = $r") + " ORDER BY ScanId DESC LIMIT 2",
                r => r.GetInt64(0), ("$r", rootId));
            if (scans.Count < 2)
            {
                return Results.Ok(new { from = (DateTimeOffset?)null, to = (DateTimeOffset?)null, items = Array.Empty<object>() });
            }

            var items = Sql.Query(c, $"""
                SELECT n.EntryId, n.RootId, n.RelPath, n.TotalSize, COALESCE(o.TotalSize, 0), n.FileCount - COALESCE(o.FileCount, 0)
                FROM DirSizeHistory n LEFT JOIN DirSizeHistory o ON o.EntryId = n.EntryId AND o.ScanId = $old
                WHERE n.ScanId = $new AND n.RelPath <> ''
                ORDER BY ABS(n.TotalSize - COALESCE(o.TotalSize, 0)) DESC LIMIT {Math.Clamp(limit ?? 30, 1, 200)}
                """, r => new { id = r.GetInt64(0), rootId = r.GetInt32(1), relPath = r.GetString(2), size = r.GetInt64(3), previous = r.GetInt64(4), delta = r.GetInt64(3) - r.GetInt64(4), filesDelta = r.GetInt64(5) },
                ("$new", scans[0]), ("$old", scans[1]));
            var at = Sql.Query(c, "SELECT ScanId, MAX(At) FROM DirSizeHistory WHERE ScanId IN ($a, $b) GROUP BY ScanId", r => (r.GetInt64(0), r.GetInt64(1)), ("$a", scans[0]), ("$b", scans[1]))
                .ToDictionary(x => x.Item1, x => x.Item2);
            var conv = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter();
            return Results.Ok(new
            {
                from = at.TryGetValue(scans[1], out var f) ? (DateTimeOffset?)conv.ConvertFromProvider(f) : null,
                to = at.TryGetValue(scans[0], out var t) ? (DateTimeOffset?)conv.ConvertFromProvider(t) : null,
                items,
            });
        });
    }

    private static async Task<List<Insight>> BuildAsync(AnalyzeDbContext db, int? rootId, CancellationToken ct)
    {
        var cats = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Name, c => c.Id, ct);
        var list = new List<Insight>();

        async Task Add(string key, string title, string description, string severity, SearchFilter filter, ActionOp? op)
        {
            filter = filter with { RootId = rootId };
            var q = filter.Apply(db.Entries.AsNoTracking());
            var dirs = filter.Dirs == true || filter.EmptyDirs == true;
            var agg = await q.GroupBy(_ => 1).Select(g => new { N = g.LongCount(), B = dirs ? g.Sum(e => e.TotalSize) : g.Sum(e => e.Size) }).FirstOrDefaultAsync(ct);
            if (agg is { N: > 0 })
            {
                list.Add(new Insight(key, title, description, severity, agg.N, agg.B, filter, op));
            }
        }

        async Task AddCategory(string key, string category, string title, string description, string severity, ActionOp op)
        {
            if (cats.TryGetValue(category, out var id))
            {
                await Add(key, title, description, severity, new SearchFilter { CategoryId = id }, op);
            }
        }

        await Add("node_modules", "Dependency folders", "node_modules, virtualenvs, package caches — restorable with an install command.", "high",
            new SearchFilter { Dirs = true, Segment = "node_modules,.venv,venv,__pycache__,.gradle,.m2,.nuget,.pnpm-store,bower_components" }, ActionOp.Trash);
        await AddCategory("devjunk", "Dev junk", "Dev junk", "All files in dependency and build-output folders (obj, .next, node_modules, *.pyc…).", "medium", ActionOp.Trash);
        await AddCategory("caches", "Caches & temp", "Caches & temporary files", "Application caches, thumbnails, swap and temp files.", "high", ActionOp.Trash);
        await AddCategory("incomplete", "Incomplete downloads", "Incomplete downloads", "Partial downloads and torrent leftovers.", "high", ActionOp.Trash);
        await AddCategory("system", "System files", "OS & NAS metadata", "Thumbs.db, .DS_Store, @eaDir and recycle bins.", "low", ActionOp.Trash);
        await Add("corrupt", "Possibly corrupted files", "Content changed although size and modification time did not (found when hashes were verified) — bit rot, or a program that restores timestamps. Compare with a backup.", "high",
            new SearchFilter { Corrupt = true }, null);
        await Add("copynames", "Files named like copies", "'file (1).jpg', 'report - Copy.docx' — often accidental duplicates.", "medium", new SearchFilter { Tag = "copy-name" }, ActionOp.Trash);
        await Add("huge", "Huge files", "Single files over 4 GB.", "info", new SearchFilter { MinSize = 4L << 30 }, null);
        await Add("stale", "Untouched for 5+ years", "Files not modified in five years — archive candidates.", "info", new SearchFilter { OlderThanDays = 1825, MinSize = 1 << 20 }, ActionOp.Move);
        await Add("diskimages", "Disk images", "ISO and VM disks — often downloaded once and forgotten.", "info", new SearchFilter { Kind = FileKind.DiskImage }, null);
        await Add("installers", "Installers", "Setup files and packages.", "low", new SearchFilter { Kind = FileKind.Executable, Ext = "exe,msi,dmg,pkg,deb,rpm,apk,appimage" }, ActionOp.Trash);
        await Add("emptyfiles", "Empty files", "Zero-byte files.", "low", new SearchFilter { MaxSize = 0 }, ActionOp.Delete);
        await Add("emptydirs", "Empty folders", "Folders with nothing inside.", "low", new SearchFilter { EmptyDirs = true }, ActionOp.Delete);
        await Add("brokenlinks", "Broken symlinks", "Links whose target does not exist.", "low", new SearchFilter { BrokenLinks = true }, ActionOp.Delete);
        await Add("sparse", "Sparse files", "Files that use much less disk than their size (VM disks, databases).", "info", new SearchFilter { Sparse = true }, null);
        await Add("hardlinked", "Hard-linked files", "Files with several names sharing one copy on disk (already deduplicated).", "info", new SearchFilter { Hardlinked = true }, null);
        await Add("uncategorized", "Uncategorized", "Files no rule recognizes — try AI classification or add a rule.", "info", new SearchFilter { Uncategorized = true }, null);
        return list;
    }
}
