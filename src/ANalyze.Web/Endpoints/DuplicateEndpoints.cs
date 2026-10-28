using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Web.Api;

namespace ANalyze.Web.Endpoints;

public static class DuplicateEndpoints
{
    public sealed record Member(long Id, int RootId, string RootName, string RelPath, long MTime, int NLink, string Identity);

    public sealed record Group(string Hash, long Size, int Copies, long Wasted, List<Member> Members);

    public static void MapDuplicateEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/duplicates", (int? rootId, string? path, long? minSize, string? sort, string? q, int? offset, int? limit, SqliteSetup sqlite, RootRegistry roots) =>
        {
            using var c = sqlite.Open();
            var (scope, args) = Sql.ScopeOf(rootId, path, "s");
            var nameFilter = string.IsNullOrWhiteSpace(q) ? "" : " AND s.Name LIKE $q ESCAPE '\\'";
            var allArgs = args.Concat([("$min", (object?)(minSize ?? 0)), ("$q", (object?)("%" + (q ?? "").Replace("%", "\\%").Replace("_", "\\_") + "%"))]).ToArray();

            // Groups that have at least one copy inside the scope.
            var groupsSql = $"""
                SELECT FullHash, MAX(Size) AS size, COUNT(DISTINCT {Sql.Identity}) AS copies
                FROM Entries
                WHERE FullHash IN (SELECT DISTINCT s.FullHash FROM Entries s WHERE s.FullHash IS NOT NULL AND s.IsDir = 0 AND s.Size >= $min {scope}{nameFilter})
                  AND IsDir = 0 AND IsSymlink = 0
                GROUP BY FullHash HAVING copies > 1
                """;
            var totals = Sql.Query(c, $"SELECT COUNT(*), COALESCE(SUM((copies - 1) * size), 0) FROM ({groupsSql})", r => (r.GetInt64(0), r.GetInt64(1)), allArgs).First();
            var order = sort switch
            {
                "size" => "size DESC",
                "copies" => "copies DESC, size DESC",
                _ => "(copies - 1) * size DESC",
            };
            var page = Sql.Query(c, $"{groupsSql} ORDER BY {order} LIMIT {Math.Clamp(limit ?? 50, 1, 500)} OFFSET {Math.Max(0, offset ?? 0)}",
                r => (Hash: r.GetString(0), Size: r.GetInt64(1), Copies: r.GetInt32(2)), allArgs);

            var hashes = page.Select(p => p.Hash).ToList();
            var members = hashes.Count == 0 ? [] : Sql.Query(c, $"""
                SELECT Id, RootId, RelPath, MTime, NLink, {Sql.Identity}, FullHash FROM Entries
                WHERE FullHash IN ({string.Join(",", hashes.Select((_, i) => "$h" + i))}) AND IsDir = 0 AND IsSymlink = 0
                ORDER BY MTime
                """, r => (Hash: r.GetString(6), M: new Member(r.GetInt64(0), r.GetInt32(1), roots.Get(r.GetInt32(1))?.Name ?? "?", r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetString(5))),
                hashes.Select((h, i) => ("$h" + i, (object?)h)).ToArray());
            var byHash = members.GroupBy(m => m.Hash).ToDictionary(g => g.Key, g => g.Select(x => x.M).ToList());
            var groups = page.Select(p => new Group(p.Hash, p.Size, p.Copies, (p.Copies - 1) * p.Size, byHash.GetValueOrDefault(p.Hash) ?? [])).ToList();
            return new { groups, total = totals.Item1, wasted = totals.Item2 };
        });

        // Pairs of folders that share many identical files: usually a copied backup or an extracted archive.
        api.MapGet("/duplicates/folders", (int? limit, SqliteSetup sqlite, RootRegistry roots) =>
        {
            using var c = sqlite.Open();
            var rows = Sql.Query(c, $"""
                SELECT a.ParentId, b.ParentId, COUNT(*) AS n, SUM(a.Size) AS bytes FROM Entries a
                JOIN Entries b ON b.FullHash = a.FullHash AND b.ParentId > a.ParentId AND b.IsDir = 0
                WHERE a.FullHash IS NOT NULL AND a.IsDir = 0 AND a.Size > 0
                  AND NOT (a.Inode <> 0 AND a.Inode = b.Inode AND a.Dev = b.Dev)
                GROUP BY a.ParentId, b.ParentId ORDER BY bytes DESC LIMIT {Math.Clamp(limit ?? 30, 1, 200)}
                """, r => (A: r.GetInt64(0), B: r.GetInt64(1), Files: r.GetInt64(2), Bytes: r.GetInt64(3)));
            var ids = rows.SelectMany(r => new[] { r.A, r.B }).Distinct().ToList();
            var info = ids.Count == 0 ? [] : Sql.Query(c, $"SELECT Id, RootId, RelPath, FileCount, TotalSize FROM Entries WHERE Id IN ({string.Join(",", ids)})",
                r => (Id: r.GetInt64(0), RootId: r.GetInt32(1), RelPath: r.GetString(2), Files: r.GetInt64(3), Size: r.GetInt64(4))).ToDictionary(x => x.Id);
            object Dir(long id) => info.TryGetValue(id, out var d)
                ? new { id, d.RootId, root = roots.Get(d.RootId)?.Name, d.RelPath, d.Files, d.Size }
                : new { id, RootId = 0, root = (string?)null, RelPath = "?", Files = 0L, Size = 0L };
            return rows.Select(r => new { a = Dir(r.A), b = Dir(r.B), sharedFiles = r.Files, sharedBytes = r.Bytes });
        });
    }
}
