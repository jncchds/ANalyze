using System.Text.Json;
using ANalyze.Actions;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Scanner;
using ANalyze.Web.Api;
using Microsoft.Data.Sqlite;

namespace ANalyze.Web.Endpoints;

/// <summary>Archives whose files were found extracted (see <see cref="ExtractionMatcher"/>): list, per-file detail, CRC check, plans.</summary>
public static class ArchiveEndpoints
{
    public sealed record Place(long? Id, int RootId, string? Root, string RelPath);

    public sealed record ExtractionDto(
        long Id, ExtractionState State, Place Archive, long ArchiveSize, long ArchiveMTime, string? Format, Place Folder, string BaseRelPath, string Strip,
        int Files, int Matched, int Missing, int Smaller, int Differs, long Bytes, long MatchedBytes,
        long? VerifiedAt, int VerifiedOk, int VerifiedBad, int Unverified);

    public sealed record FileDto(string Path, long Size, string State, string? Target, long? EntryId, long? DiskSize, bool Checkable);

    public sealed record VerifyBody(long[]? Ids, string? State, int? RootId);

    public sealed record PlanBody(long[] Ids, string Remove, ActionOp Op);

    private const string Select = """
        SELECT x.Id, x.State, x.ArchiveId, a.RootId, a.RelPath, a.Size, a.MTime, l.Format, x.RootId, x.FolderId, x.FolderRelPath, x.BaseRelPath, x.Strip,
               x.Files, x.Matched, x.Missing, x.Smaller, x.Differs, x.Bytes, x.MatchedBytes, x.VerifiedAt, x.VerifiedOk, x.VerifiedBad, x.Unverified
        FROM ArchiveExtractions x JOIN Entries a ON a.Id = x.ArchiveId JOIN ArchiveListings l ON l.EntryId = x.ArchiveId
        """;

    public static void MapArchiveEndpoints(this RouteGroupBuilder api)
    {
        // ?state=complete|verified|partial|bad&rootId=&q=&sort=size|matched|name&offset=&limit=
        api.MapGet("/archives/extractions", (string? state, int? rootId, string? q, string? sort, int? offset, int? limit, SqliteSetup sqlite, RootRegistry roots) =>
        {
            using var c = sqlite.Open();
            var (where, args) = Where(state, rootId, q);
            var order = sort switch
            {
                "matched" => "x.MatchedBytes DESC",
                "name" => "a.Name",
                _ => "a.Size DESC",
            };
            var items = Sql.Query(c, $"{Select} WHERE 1 = 1 {where} ORDER BY {order}, x.Id LIMIT {Math.Clamp(limit ?? 50, 1, 500)} OFFSET {Math.Max(0, offset ?? 0)}",
                r => Map(r, roots), args);
            var total = Sql.Scalar<long>(c, $"SELECT COUNT(*) FROM ArchiveExtractions x JOIN Entries a ON a.Id = x.ArchiveId WHERE 1 = 1 {where}", args);
            var (scope, scopeArgs) = Where(null, rootId, null);
            var counts = Sql.Query(c, $"""
                SELECT COALESCE(SUM(x.State = 0), 0), COALESCE(SUM(x.State = 0 AND {Verified}), 0), COALESCE(SUM(x.State = 1), 0), COALESCE(SUM(x.VerifiedBad > 0), 0),
                       COALESCE(SUM(CASE WHEN x.State = 0 THEN a.Size END), 0)
                FROM ArchiveExtractions x JOIN Entries a ON a.Id = x.ArchiveId WHERE 1 = 1 {scope}
                """, r => new { complete = r.GetInt64(0), verified = r.GetInt64(1), partial = r.GetInt64(2), bad = r.GetInt64(3), completeArchiveBytes = r.GetInt64(4) },
                scopeArgs).First();
            var listed = Sql.Query(c, "SELECT COUNT(*), COALESCE(SUM(Error IS NOT NULL), 0) FROM ArchiveListings", r => new { archives = r.GetInt64(0), unreadable = r.GetInt64(1) }).First();
            return new { items, total, counts, listed };
        });

        // The extractions of one archive (entry drawer).
        api.MapGet("/archives/{entryId:long}/extractions", (long entryId, SqliteSetup sqlite, RootRegistry roots) =>
        {
            using var c = sqlite.Open();
            var listing = Sql.Query(c, "SELECT Format, Streamed, Files, FileBytes, Error, ListedAt FROM ArchiveListings WHERE EntryId = $id",
                r => new { format = r.NullableString(0), streamed = r.GetBoolean(1), files = r.GetInt32(2), bytes = r.GetInt64(3), error = r.NullableString(4), listedAt = r.GetInt64(5) },
                ("$id", entryId)).FirstOrDefault();
            var items = Sql.Query(c, $"{Select} WHERE x.ArchiveId = $id ORDER BY x.State, x.MatchedBytes DESC", r => Map(r, roots), ("$id", entryId));
            return new { listing, items };
        });

        // Every file of the archive and what was found for it; ?problems=true leaves out the matched files that are fine.
        api.MapGet("/archives/extractions/{id:long}", (long id, bool? problems, int? limit, SqliteSetup sqlite, RootRegistry roots) =>
        {
            using var c = sqlite.Open();
            var x = Sql.Query(c, $"{Select} WHERE x.Id = $id", r => Map(r, roots), ("$id", id)).FirstOrDefault();
            if (x is null)
            {
                return Results.NotFound();
            }

            var badJson = Sql.Scalar<string>(c, "SELECT BadPaths FROM ArchiveExtractions WHERE Id = $id", ("$id", id));
            var bad = badJson is null ? [] : JsonSerializer.Deserialize<HashSet<string>>(badJson) ?? [];
            var files = ExtractionMatcher.Compare(c, ExtractionMatcher.LoadMembers(c, x.Archive.Id!.Value), x.Folder.RootId, x.BaseRelPath, x.Strip)
                .Select(m => new FileDto(m.Path, m.Size, bad.Contains(m.Path) ? "corrupt" : m.State.ToString().ToLowerInvariant(), m.Target, m.EntryId, m.DiskSize, m.Crc is not null))
                .Where(f => problems != true || f.State != "matched")
                .OrderBy(f => f.State == "matched").ThenBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
            return Results.Ok(new { extraction = x, files = files.Take(Math.Clamp(limit ?? 2000, 1, 20000)), total = files.Count });
        });

        // Reads the extracted files and compares them with the archive's CRC32s (a job on the scan queue). Ids, or every extraction in a state.
        api.MapPost("/archives/extractions/verify", (VerifyBody body, SqliteSetup sqlite, ScanQueue queue) =>
        {
            var ids = body.Ids?.ToList();
            if (ids is null)
            {
                using var c = sqlite.Open();
                var (where, args) = Where(body.State ?? "complete", body.RootId, null);
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
        api.MapPost("/archives/extractions/plan", async (PlanBody body, SqliteSetup sqlite, PlanBuilder builder, CancellationToken ct) =>
        {
            if (body.Op is not (ActionOp.Trash or ActionOp.Delete))
            {
                return Results.Problem("Only trash or delete.", statusCode: 400);
            }

            var archives = body.Remove == "archive";
            var entryIds = new List<long>();
            using (var c = sqlite.Open())
            {
                foreach (var id in body.Ids.Distinct())
                {
                    var row = Sql.Query(c, "SELECT ArchiveId, State, RootId, BaseRelPath, Strip FROM ArchiveExtractions WHERE Id = $id",
                        r => (Archive: r.GetInt64(0), State: (ExtractionState)r.GetInt32(1), Root: r.GetInt32(2), Base: r.GetString(3), Strip: r.GetString(4)), ("$id", id)).FirstOrDefault();
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

            try
            {
                var name = archives ? "Remove extracted archives" : "Remove extracted copies of archives";
                var plan = await builder.CreateAsync(new ManualPlanRequest(name, body.Op, entryIds.Distinct().ToList(), null), ct);
                return Results.Ok(new { plan.Id, plan.Name, items = plan.Items.Count });
            }
            catch (ArgumentException e)
            {
                return Results.Problem(e.Message, statusCode: 400);
            }
        });
    }

    private const string Verified = "x.VerifiedAt IS NOT NULL AND x.VerifiedBad = 0 AND x.Unverified = 0";

    private static (string, (string, object?)[]) Where(string? state, int? rootId, string? q)
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
            sql += " AND (a.RootId = $root OR x.RootId = $root)";
            args.Add(("$root", rootId));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            sql += " AND (a.RelPath LIKE $q ESCAPE '\\' OR x.FolderRelPath LIKE $q ESCAPE '\\')";
            args.Add(("$q", "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
        }

        return (sql, [.. args]);
    }

    private static ExtractionDto Map(SqliteDataReader r, RootRegistry roots) => new(
        r.GetInt64(0), (ExtractionState)r.GetInt32(1),
        new Place(r.GetInt64(2), r.GetInt32(3), roots.Get(r.GetInt32(3))?.Name, r.GetString(4)), r.GetInt64(5), r.GetInt64(6), r.NullableString(7),
        new Place(r.NullableLong(9), r.GetInt32(8), roots.Get(r.GetInt32(8))?.Name, r.GetString(10)), r.GetString(11), r.GetString(12),
        r.GetInt32(13), r.GetInt32(14), r.GetInt32(15), r.GetInt32(16), r.GetInt32(17), r.GetInt64(18), r.GetInt64(19),
        r.NullableLong(20), r.GetInt32(21), r.GetInt32(22), r.GetInt32(23));
}
