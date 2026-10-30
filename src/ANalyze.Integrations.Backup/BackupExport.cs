using System.Reflection;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using ANalyze.Integrations.Items;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ANalyze.Integrations.Backup;

/// <summary>What a parent tells a backup instance about itself.</summary>
/// <param name="HashAlgorithm">Content hash used in <see cref="ExportFile.H"/>; a child only compares hashes of the same kind.</param>
/// <param name="Now">The parent's clock (unix seconds) when answering: where a child starts reading the hash feed after a sync. Null from older parents.</param>
public sealed record ExportInfo(string App, string? Version, string Host, string HashAlgorithm, bool HashAllFiles, int VerifyAfterDays, IReadOnlyList<ExportRoot> Roots, long? Now = null);

public sealed record ExportRoot(int Id, string Path, string Name, long Files, long Size, long Hashed, DateTimeOffset? LastScanAt);

/// <summary>One file or folder, with short property names because there can be millions.</summary>
/// <param name="P">Path relative to the root.</param>
/// <param name="S">Size.</param>
/// <param name="M">Modification time, unix seconds.</param>
/// <param name="H">Full content hash, when computed.</param>
/// <param name="C">When the parent found the content changed without a new size or mtime (unix seconds).</param>
/// <param name="D">A folder (S is then its total size).</param>
public sealed record ExportFile(string P, long S, long M, string? H = null, long? C = null, bool D = false);

/// <param name="Next">Pass as <c>after</c> for the next page; null on the last page.</param>
public sealed record ExportPage(IReadOnlyList<ExportFile> Items, string? Next);

/// <summary>Files hashed (or re-verified) after a point of the hash feed.</summary>
/// <param name="Next">Pass as <c>after</c> for the next page; null on the last page.</param>
/// <param name="Cursor">Where this page ends: pass it as <c>after</c> next time to get only newer hashes.</param>
public sealed record HashPage(IReadOnlyList<ExportFile> Items, string? Next, string Cursor);

/// <summary>
/// Read-only API another ANalyze uses to compare its backup with this instance (the parent): <c>GET /api/v1/backup/info</c>,
/// <c>GET /api/v1/backup/files?rootId=&amp;after=&amp;limit=</c> (files and folders of one root in path order, paged by path) and
/// <c>GET /api/v1/backup/hashes?rootId=&amp;after=&amp;limit=</c> (files of one root hashed after a cursor "hashedAt:id", oldest first).
/// Protected like the rest of the API; a child authenticates with <c>ANalyze__ApiToken</c>.
/// </summary>
public static class BackupExport
{
    public const string HashAlgorithm = "xxh128";
    public const int MaxPage = 20000;

    public static void MapBackupExport(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/backup");

        g.MapGet("/info", (SqliteSetup sqlite, RootRegistry roots, SettingsStore settings) =>
        {
            using var c = sqlite.Open();
            var counts = Db.Query(c, "SELECT RootId, COUNT(*), COALESCE(SUM(Size), 0), COUNT(FullHash) FROM Entries WHERE IsDir = 0 AND IsSymlink = 0 GROUP BY RootId",
                r => (Root: r.GetInt32(0), Files: r.GetInt64(1), Size: r.GetInt64(2), Hashed: r.GetInt64(3))).ToDictionary(x => x.Root);
            var s = settings.Current;
            return new ExportInfo("ANalyze", Version(), Environment.MachineName, HashAlgorithm, s.HashAllFiles, s.VerifyAfterDays,
                roots.All.Where(r => !r.Missing).Select(r =>
                {
                    var n = counts.GetValueOrDefault(r.Id);
                    return new ExportRoot(r.Id, r.Path, r.Name, n.Files, n.Size, n.Hashed, r.LastScanAt);
                }).ToList(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        });

        // Keyset paging on the (RootId, HashedAt) index; the cursor is "hashedAt:id" of the last file returned.
        g.MapGet("/hashes", (int rootId, string? after, int? limit, SqliteSetup sqlite, RootRegistry roots) =>
        {
            if (roots.Get(rootId) is null)
            {
                return Results.NotFound();
            }

            var (at, id) = ParseCursor(after);
            var take = Math.Clamp(limit ?? 5000, 1, MaxPage);
            using var c = sqlite.Open();
            var rows = Db.Query(c, $"""
                SELECT RelPath, Size, MTime, FullHash, CorruptAt, HashedAt, Id FROM Entries
                WHERE RootId = $r AND (HashedAt > $at OR (HashedAt = $at AND Id > $id)) AND IsDir = 0 AND IsSymlink = 0 AND FullHash IS NOT NULL
                ORDER BY HashedAt, Id LIMIT {take}
                """, r => (File: new ExportFile(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4)), At: r.GetInt64(5), Id: r.GetInt64(6)),
                ("$r", rootId), ("$at", at), ("$id", id));
            var cursor = rows.Count > 0 ? $"{rows[^1].At}:{rows[^1].Id}" : $"{at}:{id}";
            return Results.Ok(new HashPage(rows.Select(x => x.File).ToList(), rows.Count == take ? cursor : null, cursor));
        });

        g.MapGet("/files", (int rootId, string? after, int? limit, SqliteSetup sqlite, RootRegistry roots) =>
        {
            if (roots.Get(rootId) is null)
            {
                return Results.NotFound();
            }

            var take = Math.Clamp(limit ?? 5000, 1, MaxPage);
            using var c = sqlite.Open();
            // Keyset paging on the unique (RootId, RelPath) index.
            var items = Db.Query(c, $"""
                SELECT RelPath, CASE WHEN IsDir = 1 THEN TotalSize ELSE Size END, MTime, FullHash, CorruptAt, IsDir FROM Entries
                WHERE RootId = $r AND RelPath > $after AND IsSymlink = 0
                ORDER BY RelPath LIMIT {take}
                """, r => new ExportFile(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.GetBoolean(5)),
                ("$r", rootId), ("$after", after ?? ""));
            return Results.Ok(new ExportPage(items, items.Count == take ? items[^1].P : null));
        });
    }

    /// <summary>"hashedAt:id" → parts; anything else is the start of the feed.</summary>
    internal static (long At, long Id) ParseCursor(string? cursor)
    {
        var parts = (cursor ?? "").Split(':');
        return parts.Length == 2 && long.TryParse(parts[0], out var at) && long.TryParse(parts[1], out var id) ? (at, id) : (0, 0);
    }

    internal static string? Version() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
}
