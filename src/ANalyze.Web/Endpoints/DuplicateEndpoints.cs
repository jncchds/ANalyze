using ANalyze.Actions;
using ANalyze.Domain;
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
        api.MapGet("/duplicates", (int? rootId, string? path, long? minSize, string? sort, string? q, int? offset, int? limit, DbSetup database, RootRegistry roots) =>
        {
            using var c = database.Open();
            var (scope, args) = Sql.ScopeOf(rootId, path, "s");
            var nameFilter = string.IsNullOrWhiteSpace(q) ? "" : $" AND s.Name {c.Like()} @q ESCAPE '\\'";
            var allArgs = args.Concat([("@min", (object?)(minSize ?? 0)), ("@q", (object?)("%" + (q ?? "").Replace("%", "\\%").Replace("_", "\\_") + "%"))]).ToArray();

            // Groups that have at least one copy inside the scope.
            var groupsSql = $"""
                SELECT FullHash, MAX(Size) AS size, COUNT(DISTINCT {Sql.Identity}) AS copies
                FROM Entries
                WHERE FullHash IN (SELECT DISTINCT s.FullHash FROM Entries s WHERE s.FullHash IS NOT NULL AND s.IsDir = FALSE AND s.Size >= @min {scope}{nameFilter})
                  AND IsDir = FALSE AND IsSymlink = FALSE
                GROUP BY FullHash HAVING COUNT(DISTINCT {Sql.Identity}) > 1
                """;
            var totals = Sql.Query(c, $"SELECT COUNT(*), COALESCE(SUM((copies - 1) * size), 0) FROM ({groupsSql}) g", r => (r.GetInt64(0), r.GetInt64(1)), allArgs).First();
            var order = sort switch
            {
                "size" => "size DESC",
                "copies" => "copies DESC, size DESC",
                _ => "(copies - 1) * size DESC",
            };
            var page = Sql.Query(c, $"SELECT * FROM ({groupsSql}) g ORDER BY {order} LIMIT {Math.Clamp(limit ?? 50, 1, 500)} OFFSET {Math.Max(0, offset ?? 0)}",
                r => (Hash: r.GetString(0), Size: r.GetInt64(1), Copies: r.GetInt32(2)), allArgs);

            var hashes = page.Select(p => p.Hash).ToList();
            var members = hashes.Count == 0 ? [] : Sql.Query(c, $"""
                SELECT Id, RootId, RelPath, MTime, NLink, {Sql.Identity}, FullHash FROM Entries
                WHERE FullHash IN ({string.Join(",", hashes.Select((_, i) => "@h" + i))}) AND IsDir = FALSE AND IsSymlink = FALSE
                ORDER BY MTime
                """, r => (Hash: r.GetString(6), M: new Member(r.GetInt64(0), r.GetInt32(1), roots.Get(r.GetInt32(1))?.Name ?? "?", r.GetString(2), r.GetInt64(3), r.GetInt32(4), r.GetString(5))),
                hashes.Select((h, i) => ("@h" + i, (object?)h)).ToArray());
            var byHash = members.GroupBy(m => m.Hash).ToDictionary(g => g.Key, g => g.Select(x => x.M).ToList());
            var groups = page.Select(p => new Group(p.Hash, p.Size, p.Copies, (p.Copies - 1) * p.Size, byHash.GetValueOrDefault(p.Hash) ?? [])).ToList();
            return new { groups, total = totals.Item1, wasted = totals.Item2 };
        });

        // Pairs of folders that share many identical files: usually a copied backup or an extracted archive. Each side carries its
        // own files (directly inside, not below) as `ownFiles`/`ownBytes`; `similarity` is the shared bytes over the larger side's, so
        // 1 means the same files and "0.9" a near copy. sort=similar ranks by it (among the pairs sharing the most bytes), min drops the
        // pairs below a similarity.
        api.MapGet("/duplicates/folders", (int? limit, string? sort, double? min, DbSetup database, RootRegistry roots) =>
        {
            using var c = database.Open();
            var take = Math.Clamp(limit ?? 30, 1, 200);
            var bySimilarity = sort == "similar" || min > 0;
            var rows = Sql.Query(c, $"""
                SELECT pa, pb, COUNT(*) AS n, SUM(sz) AS bytes FROM (
                  SELECT DISTINCT a.ParentId AS pa, b.ParentId AS pb, a.Id AS id, a.Size AS sz FROM Entries a
                  JOIN Entries b ON b.FullHash = a.FullHash AND b.ParentId > a.ParentId AND b.IsDir = FALSE
                  WHERE a.FullHash IS NOT NULL AND a.IsDir = FALSE AND a.Size > 0
                    AND NOT (a.Inode <> 0 AND a.Inode = b.Inode AND a.Dev = b.Dev)
                ) x
                GROUP BY pa, pb ORDER BY bytes DESC LIMIT {(bySimilarity ? 1000 : take)}
                """, r => (A: r.GetInt64(0), B: r.GetInt64(1), Files: r.GetInt64(2), Bytes: r.GetInt64(3)));
            var ids = rows.SelectMany(r => new[] { r.A, r.B }).Distinct().ToList();
            var info = new Dictionary<long, (long Id, int RootId, string RelPath, long Files, long Size)>();
            var own = new Dictionary<long, (long Files, long Bytes)>();
            foreach (var chunk in ids.Chunk(500))
            {
                var list = string.Join(",", chunk);
                foreach (var d in Sql.Query(c, $"SELECT Id, RootId, RelPath, FileCount, TotalSize FROM Entries WHERE Id IN ({list})",
                    r => (Id: r.GetInt64(0), RootId: r.GetInt32(1), RelPath: r.GetString(2), Files: r.GetInt64(3), Size: r.GetInt64(4))))
                {
                    info[d.Id] = d;
                }

                foreach (var o in Sql.Query(c, $"SELECT ParentId, COUNT(*), COALESCE(SUM(Size), 0) FROM Entries WHERE ParentId IN ({list}) AND IsDir = FALSE AND IsSymlink = FALSE GROUP BY ParentId",
                    r => (Id: r.GetInt64(0), Files: r.GetInt64(1), Bytes: r.GetInt64(2))))
                {
                    own[o.Id] = (o.Files, o.Bytes);
                }
            }

            object Dir(long id)
            {
                var (ownFiles, ownBytes) = own.GetValueOrDefault(id);
                return info.TryGetValue(id, out var d)
                    ? new { id, d.RootId, root = roots.Get(d.RootId)?.Name, d.RelPath, d.Files, d.Size, ownFiles, ownBytes }
                    : new { id, RootId = 0, root = (string?)null, RelPath = "?", Files = 0L, Size = 0L, ownFiles, ownBytes };
            }

            // A file matched by several copies on the other side counts once; the bytes are a's.
            double Similarity((long A, long B, long Files, long Bytes) r) =>
                Math.Min(1, (double)Math.Min(r.Bytes, own.GetValueOrDefault(r.A).Bytes) / Math.Max(1, Math.Max(own.GetValueOrDefault(r.A).Bytes, own.GetValueOrDefault(r.B).Bytes)));
            var pairs = rows.Select(r => (Row: r, Similarity: Similarity(r))).Where(p => p.Similarity >= (min ?? 0));
            pairs = sort == "similar" ? pairs.OrderByDescending(p => p.Similarity).ThenByDescending(p => p.Row.Bytes) : pairs.OrderByDescending(p => p.Row.Bytes);
            return pairs.Take(take).Select(p => new
            {
                a = Dir(p.Row.A), b = Dir(p.Row.B), sharedFiles = p.Row.Files, sharedBytes = p.Row.Bytes, similarity = Math.Round(p.Similarity, 4),
            });
        });

        // Two folders compared file by file, by path below each (the comparison view of extracted archives): usually the parent folders of
        // two copies of a duplicate. a and b are entry ids, a file standing for its folder; with hash, `copies` lists that group's files.
        api.MapGet("/duplicates/compare", (long a, long b, string? hash, DbSetup database, RootRegistry roots) =>
        {
            using var c = database.Open();
            var (fa, fb) = (FolderOf(c, a, roots), FolderOf(c, b, roots));
            if (fa is null || fb is null)
            {
                return Results.NotFound();
            }

            if (fa.Id == fb.Id)
            {
                return Results.Problem("Both copies are in the same folder.", statusCode: 400);
            }

            // When one folder holds the other, the outer one is compared without the inner one's files.
            var (la, capA) = FilesBelow(c, fa, Inner(fa, fb));
            var (lb, capB) = FilesBelow(c, fb, Inner(fb, fa));
            var files = CompareFolders(la, lb);
            var copies = string.IsNullOrEmpty(hash) ? null : Sql.Query(c, "SELECT Id, RootId, RelPath, ParentId FROM Entries WHERE FullHash = @h AND IsDir = FALSE AND IsSymlink = FALSE ORDER BY RootId, RelPath LIMIT 200",
                r => new { id = r.GetInt64(0), rootId = r.GetInt32(1), root = roots.Get(r.GetInt32(1))?.Name, relPath = r.GetString(2), parentId = r.NullableLong(3) }, ("@h", hash));
            return Results.Ok(new { a = fa, b = fb, files, capped = capA || capB, copies });
        });

        // A plan from a comparison, cleaning up folder A: mode "copies" removes A's files whose content B has (at the same path or
        // elsewhere in B, by content hash); "merge" also moves the files only A has into B at the same path below it (same root
        // only), so A ends up holding nothing B lacks. Files at the same path with another size or content are left alone.
        api.MapPost("/duplicates/compare/plan", async (ComparePlanBody body, DbSetup database, RootRegistry roots, PlanBuilder builder, CancellationToken ct) =>
        {
            if (body.Mode is not ("copies" or "merge"))
            {
                return Results.Problem("mode is \"copies\" or \"merge\".", statusCode: 400);
            }

            List<FolderPick> picks;
            using (var c = database.Open())
            {
                var (fa, fb) = (FolderOf(c, body.A, roots), FolderOf(c, body.B, roots));
                if (fa is null || fb is null)
                {
                    return Results.NotFound();
                }

                if (fa.Id == fb.Id)
                {
                    return Results.Problem("Both sides are the same folder.", statusCode: 400);
                }

                var (la, capA) = FilesBelow(c, fa, Inner(fa, fb));
                var (lb, capB) = FilesBelow(c, fb, Inner(fb, fa));
                if (capA || capB)
                {
                    return Results.Problem($"A folder has more than {MaxCompareFiles:N0} files: plan from smaller folders.", statusCode: 400);
                }

                picks = PicksFor(CompareFolders(la, lb), lb, body.Mode == "merge" && fa.RootId == fb.RootId ? fb.RelPath : null);
            }

            try
            {
                var r = await builder.PlanFolderAsync(body.PlanId, body.Name, picks, body.Op, ct);
                return Results.Ok(new { r.Id, r.Name, r.Items, r.Added, r.Replaced });
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
        });
    }

    public sealed record ComparePlanBody(long A, long B, string Mode, ActionOp Op = ActionOp.Trash, int? PlanId = null, string? Name = null);

    /// <summary>
    /// What a comparison plans for A: each file whose content B has is removed in favour of B's copy (the one at the same path, else the
    /// one <c>elsewhere</c> names); with <paramref name="mergeInto"/> (B's path, same root) each file only A has moves there.
    /// </summary>
    private static List<FolderPick> PicksFor(List<CompareFile> files, List<FileAt> b, string? mergeInto)
    {
        var bByPath = b.ToDictionary(x => x.Path, x => x.Id, StringComparer.Ordinal);
        var picks = new List<FolderPick>();
        foreach (var f in files)
        {
            if (f.SourceId is not { } aId)
            {
                continue;
            }

            if (f is { State: "matched", Crc: "match", EntryId: { } keep })
            {
                picks.Add(new FolderPick(aId, keep, null));
            }
            else if (f.State == "missing" && f.Elsewhere is { } other && bByPath.TryGetValue(other, out var elsewhere))
            {
                picks.Add(new FolderPick(aId, elsewhere, null));
            }
            else if (f.State == "missing" && mergeInto is not null)
            {
                picks.Add(new FolderPick(aId, null, mergeInto.Length == 0 ? f.Path : mergeInto + "/" + f.Path));
            }
        }

        return picks;
    }

    /// <summary>A folder of a comparison: <see cref="ParentId"/> for going one level up, <see cref="Files"/> and <see cref="Size"/> of its whole subtree.</summary>
    public sealed record CompareSide(long Id, int RootId, string? Root, string RelPath, long? ParentId, long Files, long Size);

    /// <summary>
    /// One path below the two folders. State: <c>matched</c> (same size: <c>crc</c> = match when both have the same content hash,
    /// unchecked when one has none), <c>corrupt</c> (same size, other content hash), <c>differs</c> (other size), <c>missing</c> (only in a),
    /// <c>extra</c> (only in b; <c>size</c> is b's). <c>entryId</c>/<c>diskSize</c>/<c>diskMTime</c>/<c>target</c> are b's file, <c>sourceId</c>/<c>sourceMTime</c>
    /// a's; <c>elsewhere</c> is a path in the other folder with the same content, for a file found only on one side.
    /// </summary>
    public sealed record CompareFile(string Path, long Size, string State, string? Target, long? EntryId, long? DiskSize, bool Checkable, string? Crc, long? DiskMTime,
        long? SourceId, long? SourceMTime, string? Elsewhere);

    /// <summary>Files read per folder at most.</summary>
    private const int MaxCompareFiles = 50_000;

    private sealed record FileAt(long Id, string Path, string RelPath, long Size, long MTime, string? Hash);

    private static CompareSide? FolderOf(System.Data.Common.DbConnection c, long id, RootRegistry roots)
    {
        var e = Sql.Query(c, "SELECT IsDir, ParentId FROM Entries WHERE Id = @id", r => (IsDir: r.GetBoolean(0), ParentId: r.NullableLong(1)), ("@id", id)).FirstOrDefault();
        if (e == default)
        {
            return null;
        }

        var dirId = e.IsDir ? id : e.ParentId;
        return dirId is null ? null : Sql.Query(c, "SELECT Id, RootId, RelPath, ParentId, FileCount, TotalSize FROM Entries WHERE Id = @id AND IsDir = TRUE",
            r => new CompareSide(r.GetInt64(0), r.GetInt32(1), roots.Get(r.GetInt32(1))?.Name, r.GetString(2), r.NullableLong(3), r.GetInt64(4), r.GetInt64(5)), ("@id", dirId)).FirstOrDefault();
    }

    /// <summary>The relative path of <paramref name="inner"/> below <paramref name="outer"/>, when it is inside it.</summary>
    private static string? Inner(CompareSide outer, CompareSide inner) =>
        inner.RootId == outer.RootId && (outer.RelPath.Length == 0 ? inner.RelPath.Length > 0 : inner.RelPath.StartsWith(outer.RelPath + "/", StringComparison.Ordinal))
            ? inner.RelPath[(outer.RelPath.Length == 0 ? 0 : outer.RelPath.Length + 1)..]
            : null;

    /// <summary>Every file below a folder (not symlinks), with its path relative to it, leaving out the subtree <paramref name="skip"/>.</summary>
    private static (List<FileAt> Files, bool Capped) FilesBelow(System.Data.Common.DbConnection c, CompareSide f, string? skip)
    {
        var (range, args) = Sql.ScopeOf(f.RootId, f.RelPath, "e");
        var cut = f.RelPath.Length == 0 ? 0 : f.RelPath.Length + 1;
        var list = Sql.Query(c, $"SELECT Id, RelPath, Size, MTime, FullHash FROM Entries e WHERE e.IsDir = FALSE AND e.IsSymlink = FALSE{range} LIMIT {MaxCompareFiles + 1}",
            r => new FileAt(r.GetInt64(0), r.GetString(1)[cut..], r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.NullableString(4)), args);
        if (skip is not null)
        {
            list.RemoveAll(x => x.Path.StartsWith(skip + "/", StringComparison.Ordinal));
        }

        var capped = list.Count > MaxCompareFiles;
        return (capped ? list.GetRange(0, MaxCompareFiles) : list, capped);
    }

    /// <summary>Pairs the files of two folders by relative path.</summary>
    private static List<CompareFile> CompareFolders(List<FileAt> a, List<FileAt> b)
    {
        var byPath = b.ToDictionary(x => x.Path, StringComparer.Ordinal);
        var hashA = HashIndex(a);
        var hashB = HashIndex(b);
        var list = new List<CompareFile>(a.Count + b.Count);
        foreach (var x in a)
        {
            if (!byPath.Remove(x.Path, out var y))
            {
                list.Add(new CompareFile(x.Path, x.Size, "missing", null, null, null, x.Hash is not null, null, null, x.Id, x.MTime, Elsewhere(hashB, x.Hash)));
                continue;
            }

            var hashed = x.Hash is not null && y.Hash is not null;
            var (state, crc) = x.Size != y.Size ? ("differs", (string?)null)
                : hashed && x.Hash != y.Hash ? ("corrupt", null)
                : ("matched", hashed ? "match" : "unchecked");
            list.Add(new CompareFile(x.Path, x.Size, state, y.RelPath, y.Id, y.Size, x.Hash is not null, crc, y.MTime, x.Id, x.MTime, null));
        }

        foreach (var y in byPath.Values)
        {
            list.Add(new CompareFile(y.Path, y.Size, "extra", y.RelPath, y.Id, y.Size, false, null, y.MTime, null, null, Elsewhere(hashA, y.Hash)));
        }

        list.Sort((p, q) => string.CompareOrdinal(p.Path, q.Path));
        return list;
    }

    private static Dictionary<string, string> HashIndex(List<FileAt> files)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in files.Where(f => f.Hash is not null && f.Size > 0).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            d.TryAdd(f.Hash!, f.Path);
        }

        return d;
    }

    private static string? Elsewhere(Dictionary<string, string> other, string? hash) => hash is not null && other.TryGetValue(hash, out var p) ? p : null;
}
