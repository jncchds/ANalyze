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
public sealed record ExportInfo(string App, string? Version, string Host, string HashAlgorithm, bool HashAllFiles, int VerifyAfterDays, IReadOnlyList<ExportRoot> Roots);

public sealed record ExportRoot(int Id, string Path, string Name, long Files, long Size, long Hashed, DateTimeOffset? LastScanAt);

/// <summary>One file, with short property names because there can be millions.</summary>
/// <param name="P">Path relative to the root.</param>
/// <param name="S">Size.</param>
/// <param name="M">Modification time, unix seconds.</param>
/// <param name="H">Full content hash, when computed.</param>
/// <param name="C">When the parent found the content changed without a new size or mtime (unix seconds).</param>
public sealed record ExportFile(string P, long S, long M, string? H = null, long? C = null);

/// <param name="Next">Pass as <c>after</c> for the next page; null on the last page.</param>
public sealed record ExportPage(IReadOnlyList<ExportFile> Items, string? Next);

/// <summary>
/// Read-only API another ANalyze uses to compare its backup with this instance (the parent): <c>GET /api/v1/backup/info</c> and
/// <c>GET /api/v1/backup/files?rootId=&amp;after=&amp;limit=</c> (files of one root in path order, paged by path).
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
                }).ToList());
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
                SELECT RelPath, Size, MTime, FullHash, CorruptAt FROM Entries
                WHERE RootId = $r AND RelPath > $after AND IsDir = 0 AND IsSymlink = 0
                ORDER BY RelPath LIMIT {take}
                """, r => new ExportFile(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4)),
                ("$r", rootId), ("$after", after ?? ""));
            return Results.Ok(new ExportPage(items, items.Count == take ? items[^1].P : null));
        });
    }

    internal static string? Version() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
}
