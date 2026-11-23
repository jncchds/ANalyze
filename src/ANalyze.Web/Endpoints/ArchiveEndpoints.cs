using System.Globalization;
using System.Text.Json;
using ANalyze.Actions;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Scanner;
using ANalyze.Web.Api;
using System.Data.Common;

namespace ANalyze.Web.Endpoints;

/// <summary>Archives whose files were found extracted (see <see cref="ExtractionMatcher"/>): list, per-file detail, CRC check, plans.</summary>
public static class ArchiveEndpoints
{
    public sealed record Place(long? Id, int RootId, string? Root, string RelPath);

    public sealed record ExtractionDto(
        long Id, ExtractionState State, Place Archive, long ArchiveSize, long ArchiveMTime, string? Format, Place Folder, string BaseRelPath, string Strip,
        int Files, int Matched, int Missing, int Smaller, int Differs, long Bytes, long MatchedBytes,
        long? VerifiedAt, int VerifiedOk, int VerifiedBad, int Unverified)
    {
        /// <summary>In a group: every extraction into this folder, one per identical copy of the archive (they compare the same files).</summary>
        public IReadOnlyList<long>? Ids { get; init; }
    }

    /// <summary>
    /// One archive — with its identical copies (same content hash) — and every folder it was found extracted to, complete ones first.
    /// </summary>
    /// <param name="Hash">The archive's content hash, shared by every copy; null when it has not been calculated (then copies are not known).</param>
    public sealed record ArchiveGroupDto(Place Archive, long ArchiveSize, long ArchiveMTime, string? Format, string? Hash, IReadOnlyList<Place> Copies, IReadOnlyList<ExtractionDto> Extractions);

    /// <param name="Crc">
    /// For an extracted file: <c>match</c> (its CRC32 was checked and equals the archive's), <c>differs</c>, <c>unchecked</c> (not checked, or
    /// the check could not read it or found it changed) or <c>none</c> (the archive stores no CRC32 for it); null when it was not found.
    /// </param>
    /// <param name="State">matched, missing, smaller, differs, corrupt, or <c>extra</c>: a file on disk, in one of the archive's folders, that the archive does not have.</param>
    /// <param name="DiskMTime">The modification time of the file on disk, when one was found.</param>
    public sealed record FileDto(string Path, long Size, string State, string? Target, long? EntryId, long? DiskSize, bool Checkable, string? Crc, long? DiskMTime = null);

    /// <summary>Folders searched for files the archive does not have, and files listed at most.</summary>
    private const int MaxExtraFolders = 2000, MaxExtras = 5000;

    public sealed record VerifyBody(long[]? Ids, string? State, int? RootId);

    public sealed record PlanBody(long[] Ids, string Remove, ActionOp Op, int? PlanId);

    private const string Select = """
        SELECT x.Id, x.State, x.ArchiveId, a.RootId, a.RelPath, a.Size, a.MTime, l.Format, x.RootId, x.FolderId, x.FolderRelPath, x.BaseRelPath, x.Strip,
               x.Files, x.Matched, x.Missing, x.Smaller, x.Differs, x.Bytes, x.MatchedBytes, x.VerifiedAt, x.VerifiedOk, x.VerifiedBad, x.Unverified,
               a.FullHash
        FROM ArchiveExtractions x JOIN Entries a ON a.Id = x.ArchiveId JOIN ArchiveListings l ON l.EntryId = x.ArchiveId
        """;

    public static void MapArchiveEndpoints(this RouteGroupBuilder api)
    {
        // ?state=complete|verified|partial|bad&rootId=&q=&sort=size|matched|name&offset=&limit=
        api.MapGet("/archives/extractions", (string? state, int? rootId, string? q, string? sort, int? offset, int? limit, DbSetup database, RootRegistry roots) =>
        {
            using var c = database.Open();
            // Paged by archive content: one archive (or identical copies of it) can be found extracted in several folders; those come in one group.
            var (where, args) = Where(c, state, rootId, q);
            var order = sort switch
            {
                "matched" => "MAX(x.MatchedBytes) DESC",
                "name" => "MIN(a.Name)",
                _ => "MAX(a.Size) DESC",
            };
            var page = Sql.Query(c, $"""
                SELECT {GroupKey} FROM ArchiveExtractions x JOIN Entries a ON a.Id = x.ArchiveId WHERE 1 = 1 {where}
                GROUP BY {GroupKey} ORDER BY {order}, MIN(x.ArchiveId) LIMIT {Math.Clamp(limit ?? 50, 1, 500)} OFFSET {Math.Max(0, offset ?? 0)}
                """, r => r.GetString(0), args);
            var keys = page.Select((k, i) => ($"@k{i}", (object?)k)).ToArray();
            var rows = page.Count == 0 ? [] : Sql.Query(c,
                $"{Select} WHERE {GroupKey} IN ({string.Join(",", keys.Select(k => k.Item1))}) {where} ORDER BY x.State, x.MatchedBytes DESC, x.Id",
                r => (Hash: r.NullableString(24), Id: r.GetInt64(2), X: Map(r, roots)), [.. args, .. keys]);
            var byKey = rows.ToLookup(r => r.Hash ?? r.Id.ToString(CultureInfo.InvariantCulture));
            var items = page.Where(byKey.Contains).Select(k => Group(byKey[k].First().Hash, [.. byKey[k].Select(r => r.X)])).ToList();
            var total = Sql.Scalar<long>(c, $"SELECT COUNT(DISTINCT {GroupKey}) FROM ArchiveExtractions x JOIN Entries a ON a.Id = x.ArchiveId WHERE 1 = 1 {where}", args);
            // Counted per archive content, so an archive extracted twice counts once; the bytes are those of every copy.
            var (scope, scopeArgs) = Where(c, null, rootId, null);
            var counts = Sql.Query(c, $"""
                SELECT COALESCE(SUM(g.Complete), 0), COALESCE(SUM(g.Verified), 0), COALESCE(SUM(g.Partial), 0), COALESCE(SUM(g.Bad), 0),
                       COALESCE(SUM(CASE WHEN g.Complete = 1 THEN g.Size * g.Copies END), 0)
                FROM (SELECT {GroupKey} AS K, MAX(a.Size) AS Size, COUNT(DISTINCT x.ArchiveId) AS Copies,
                             MAX(CASE WHEN x.State = 0 THEN 1 ELSE 0 END) AS Complete, MAX(CASE WHEN x.State = 1 THEN 1 ELSE 0 END) AS Partial,
                             MAX(CASE WHEN x.State = 0 AND {Verified} THEN 1 ELSE 0 END) AS Verified, MAX(CASE WHEN x.VerifiedBad > 0 THEN 1 ELSE 0 END) AS Bad
                      FROM ArchiveExtractions x JOIN Entries a ON a.Id = x.ArchiveId WHERE 1 = 1 {scope} GROUP BY {GroupKey}) g
                """, r => new { complete = r.GetInt64(0), verified = r.GetInt64(1), partial = r.GetInt64(2), bad = r.GetInt64(3), completeArchiveBytes = r.GetInt64(4) },
                scopeArgs).First();
            var listed = Sql.Query(c, "SELECT COUNT(*), COALESCE(SUM(CASE WHEN Error IS NOT NULL THEN 1 ELSE 0 END), 0) FROM ArchiveListings", r => new { archives = r.GetInt64(0), unreadable = r.GetInt64(1) }).First();
            return new { items, total, counts, listed };
        });

        // The extractions of one archive (entry drawer).
        api.MapGet("/archives/{entryId:long}/extractions", (long entryId, DbSetup database, RootRegistry roots) =>
        {
            using var c = database.Open();
            var listing = Sql.Query(c, "SELECT Format, Streamed, Files, FileBytes, Error, ListedAt FROM ArchiveListings WHERE EntryId = @id",
                r => new { format = r.NullableString(0), streamed = r.GetBoolean(1), files = r.GetInt32(2), bytes = r.GetInt64(3), error = r.NullableString(4), listedAt = r.GetInt64(5) },
                ("@id", entryId)).FirstOrDefault();
            var items = Sql.Query(c, $"{Select} WHERE x.ArchiveId = @id ORDER BY x.State, x.MatchedBytes DESC", r => Map(r, roots), ("@id", entryId));
            return new { listing, items };
        });

        // Every file of the archive and what was found for it; ?problems=true leaves out the matched files that are fine, ?extras=true adds
        // the files on disk in the archive's folders that the archive does not have (the comparison page).
        api.MapGet("/archives/extractions/{id:long}", (long id, bool? problems, bool? extras, int? limit, DbSetup database, RootRegistry roots) =>
        {
            using var c = database.Open();
            var x = Sql.Query(c, $"{Select} WHERE x.Id = @id", r => Map(r, roots), ("@id", id)).FirstOrDefault();
            if (x is null)
            {
                return Results.NotFound();
            }

            var badJson = Sql.Scalar<string>(c, "SELECT BadPaths FROM ArchiveExtractions WHERE Id = @id", ("@id", id));
            var bad = badJson is null ? [] : JsonSerializer.Deserialize<HashSet<string>>(badJson) ?? [];
            var members = ExtractionMatcher.Compare(c, ExtractionMatcher.LoadMembers(c, x.Archive.Id!.Value), x.Folder.RootId, x.BaseRelPath, x.Strip);
            // The check stores counts and the paths that differ, not each file that matched: a matched file is called a CRC32 match only when
            // the counts leave no doubt (no file skipped by the check except those without a CRC32, every differing path kept).
            var noCrc = members.Count(m => m.State == MemberState.Matched && m.Crc is null);
            var confirmed = x.VerifiedAt is not null && x.Unverified <= noCrc && x.VerifiedBad == bad.Count;
            var files = members
                .Select(m => new FileDto(m.Path, m.Size, bad.Contains(m.Path) ? "corrupt" : m.State.ToString().ToLowerInvariant(), m.Target, m.EntryId, m.DiskSize, m.Crc is not null,
                    m.State != MemberState.Matched ? null : m.Crc is null ? "none" : bad.Contains(m.Path) ? "differs" : confirmed ? "match" : "unchecked",
                    m.EntryId is null ? null : m.DiskMTime))
                .Where(f => problems != true || f.State != "matched")
                .ToList();
            var extrasCapped = false;
            if (extras == true)
            {
                var found = Extras(c, x.Folder.RootId, members);
                extrasCapped = found.Count > MaxExtras;
                files.AddRange(found.Take(MaxExtras));
            }

            files = [.. files.OrderBy(f => f.State == "matched").ThenBy(f => f.Path, StringComparer.Ordinal)];
            return Results.Ok(new { extraction = x, files = files.Take(Math.Clamp(limit ?? 2000, 1, 100_000)), total = files.Count, extrasCapped });
        });

        // Reads the extracted files and compares them with the archive's CRC32s (a job on the scan queue). Ids, or every extraction in a state.
        api.MapPost("/archives/extractions/verify", (VerifyBody body, DbSetup database, ScanQueue queue) =>
        {
            var ids = body.Ids?.ToList();
            if (ids is null)
            {
                using var c = database.Open();
                var (where, args) = Where(c, body.State ?? "complete", body.RootId, null);
                ids = Sql.Query(c, $"SELECT x.Id FROM ArchiveExtractions x JOIN Entries a ON a.Id = x.ArchiveId WHERE 1 = 1 {where}", r => r.GetInt64(0), args);
            }

            if (ids.Count == 0)
            {
                return Results.Ok(new { queued = 0 });
            }

            queue.Enqueue(new ScanRequest(Reason: "Checking extracted archives") { VerifyExtractions = ids });
            return Results.Ok(new { queued = ids.Count });
        });

        // Draft plan that removes the archives (complete extractions only) or the extracted copies of their files.
        api.MapPost("/archives/extractions/plan", async (PlanBody body, DbSetup database, PlanBuilder builder, CancellationToken ct) =>
        {
            if (body.Op is not (ActionOp.Trash or ActionOp.Delete))
            {
                return Results.Problem("Only trash or delete.", statusCode: 400);
            }

            var archives = body.Remove == "archive";
            var entryIds = new List<long>();
            using (var c = database.Open())
            {
                foreach (var id in body.Ids.Distinct())
                {
                    var row = Sql.Query(c, "SELECT ArchiveId, State, RootId, BaseRelPath, Strip FROM ArchiveExtractions WHERE Id = @id",
                        r => (Archive: r.GetInt64(0), State: (ExtractionState)r.GetInt32(1), Root: r.GetInt32(2), Base: r.GetString(3), Strip: r.GetString(4)), ("@id", id)).FirstOrDefault();
                    if (row.Archive == 0)
                    {
                        continue;
                    }

                    if (archives)
                    {
                        if (row.State != ExtractionState.Complete)
                        {
                            return Results.Problem("Only archives whose files are all extracted can be removed.", statusCode: 400);
                        }

                        entryIds.Add(row.Archive);
                    }
                    else
                    {
                        entryIds.AddRange(ExtractionMatcher.Compare(c, ExtractionMatcher.LoadMembers(c, row.Archive), row.Root, row.Base, row.Strip)
                            .Where(m => m.State == MemberState.Matched && m.EntryId is not null).Select(m => m.EntryId!.Value));
                    }
                }
            }

            if (entryIds.Count == 0)
            {
                return Results.Problem("Nothing to remove.", statusCode: 400);
            }

            var name = archives ? "Remove extracted archives" : "Remove extracted copies of archives";
            var req = new ManualPlanRequest(name, body.Op, entryIds.Distinct().ToList(), null);
            return await PlanEndpoints.Run(body.PlanId, () => builder.CreateAsync(req, ct), id => builder.AddAsync(id, req, ct));
        });
    }

    /// <summary>
    /// Files on disk, in a folder the archive has files in, that the archive does not have — added or renamed after the extraction, or
    /// unrelated files in the folder it was extracted into. Their path is the one they would have in the archive.
    /// </summary>
    private static List<FileDto> Extras(DbConnection c, int rootId, List<MemberMatch> members)
    {
        // Disk folder -> archive folder, and every disk path that belongs to an archive file.
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            if (m.Target is null)
            {
                continue;
            }

            known.Add(m.Target);
            folders.TryAdd(Parent(m.Target), Parent(m.Path));
        }

        var list = new List<FileDto>();
        foreach (var (disk, archive) in folders.OrderBy(f => f.Key, StringComparer.Ordinal).Take(MaxExtraFolders))
        {
            var dirId = Sql.Scalar<long?>(c, "SELECT Id FROM Entries WHERE RootId = @r AND RelPath = @p AND IsDir = TRUE", ("@r", rootId), ("@p", disk));
            if (dirId is null)
            {
                continue;
            }

            var children = Sql.Query(c, $"SELECT Id, RelPath, Name, Size, MTime FROM Entries WHERE ParentId = @p AND IsDir = FALSE AND IsSymlink = FALSE LIMIT {MaxExtras + 1}",
                r => (Id: r.GetInt64(0), RelPath: r.GetString(1), Name: r.GetString(2), Size: r.GetInt64(3), MTime: r.GetInt64(4)), ("@p", dirId));
            foreach (var e in children.Where(e => !known.Contains(e.RelPath)))
            {
                list.Add(new FileDto(archive.Length == 0 ? e.Name : archive + "/" + e.Name, e.Size, "extra", e.RelPath, e.Id, e.Size, false, null, e.MTime));
            }

            if (list.Count > MaxExtras)
            {
                break;
            }
        }

        return list;
    }

    private static string Parent(string path) => path.LastIndexOf('/') is var i and >= 0 ? path[..i] : "";

    /// <summary>Identical archives (same content hash) are one group; an archive not hashed yet is its own.</summary>
    private const string GroupKey = "COALESCE(a.FullHash, CAST(x.ArchiveId AS TEXT))";

    /// <summary>
    /// The extractions of one archive content: one row per folder (the copies of the archive match the same files there), the
    /// most complete and most recently checked one standing for the others.
    /// </summary>
    private static ArchiveGroupDto Group(string? hash, List<ExtractionDto> xs)
    {
        var copies = xs.Select(x => x.Archive).DistinctBy(p => p.Id).OrderBy(p => p.RootId).ThenBy(p => p.RelPath, StringComparer.Ordinal).ToList();
        var folders = xs.GroupBy(x => (x.Folder.RootId, x.BaseRelPath, x.Strip))
            .Select(g =>
            {
                var best = g.OrderBy(x => x.State).ThenByDescending(x => x.VerifiedAt ?? -1).ThenBy(x => x.Id).First();
                return best with { Ids = [.. g.Select(x => x.Id)] };
            })
            .OrderBy(x => x.State).ThenByDescending(x => x.MatchedBytes).ThenBy(x => x.Id)
            .ToList();
        var first = xs.First(x => x.Archive.Id == copies[0].Id);
        return new ArchiveGroupDto(copies[0], first.ArchiveSize, first.ArchiveMTime, first.Format, hash, copies, folders);
    }

    private const string Verified = "x.VerifiedAt IS NOT NULL AND x.VerifiedBad = 0 AND x.Unverified = 0";

    private static (string, (string, object?)[]) Where(DbConnection c, string? state, int? rootId, string? q)
    {
        var sql = state switch
        {
            "complete" => " AND x.State = 0",
            "verified" => $" AND x.State = 0 AND {Verified}",
            "unverified" => " AND x.State = 0 AND x.VerifiedAt IS NULL",
            "partial" => " AND x.State = 1",
            "bad" => " AND x.VerifiedBad > 0",
            _ => "",
        };
        var args = new List<(string, object?)>();
        if (rootId is not null)
        {
            sql += " AND (a.RootId = @root OR x.RootId = @root)";
            args.Add(("@root", rootId));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            sql += $" AND (a.RelPath {c.Like()} @q ESCAPE '\\' OR x.FolderRelPath {c.Like()} @q ESCAPE '\\')";
            args.Add(("@q", "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        }

        return (sql, [.. args]);
    }

    private static ExtractionDto Map(DbDataReader r, RootRegistry roots) => new(
        r.GetInt64(0), (ExtractionState)r.GetInt32(1),
        new Place(r.GetInt64(2), r.GetInt32(3), roots.Get(r.GetInt32(3))?.Name, r.GetString(4)), r.GetInt64(5), r.GetInt64(6), r.NullableString(7),
        new Place(r.NullableLong(9), r.GetInt32(8), roots.Get(r.GetInt32(8))?.Name, r.GetString(10)), r.GetString(11), r.GetString(12),
        r.GetInt32(13), r.GetInt32(14), r.GetInt32(15), r.GetInt32(16), r.GetInt32(17), r.GetInt64(18), r.GetInt64(19),
        r.NullableLong(20), r.GetInt32(21), r.GetInt32(22), r.GetInt32(23));
}
