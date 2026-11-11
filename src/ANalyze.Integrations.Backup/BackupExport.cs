using System.Reflection;
using System.Text.Json.Serialization;
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

/// <param name="Files">Files under the root (from the root folder's totals; symlinks included, partial while a scan walks).</param>
/// <param name="Hashed">No longer sent (counting it scanned the whole index); left out of the JSON so older children read 0.</param>
public sealed record ExportRoot(int Id, string Path, string Name, long Files, long Size,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Hashed, DateTimeOffset? LastScanAt);

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
/// <c>GET /api/v1/backup/hashes?rootId=&amp;after=&amp;limit=</c> (files of one root hashed after a cursor "hashedAt:id", oldest first) and
/// <c>GET /api/v1/backup/probe</c> (block fingerprints of one file, to explain how a copy differs: <see cref="MapBackupProbe"/>).
/// Protected like the rest of the API; a child authenticates with <c>ANalyze__ApiToken</c>.
/// </summary>
public static class BackupExport
{
    public const string HashAlgorithm = "xxh128";
    public const int MaxPage = 20000;

    public static void MapBackupExport(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/backup");
        g.MapBackupProbe();

        g.MapGet("/info", (SqliteSetup sqlite, RootRegistry roots, SettingsStore settings) =>
        {
            using var c = sqlite.Open();
            // The root folder's row already holds the totals: one lookup per root on the (RootId, RelPath) index, no scan of all files.
            var s = settings.Current;
            return new ExportInfo("ANalyze", Version(), Environment.MachineName, HashAlgorithm, s.HashAllFiles, s.VerifyAfterDays,
                roots.All.Where(r => !r.Missing).Select(r =>
                {
                    var n = Db.Query(c, "SELECT FileCount, TotalSize FROM Entries WHERE RootId = $r AND RelPath = ''",
                        x => (Files: x.GetInt64(0), Size: x.GetInt64(1)), ("$r", r.Id)).FirstOrDefault();
                    return new ExportRoot(r.Id, r.Path, r.Name, n.Files, n.Size, null, r.LastScanAt);
                }).ToList(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        });

        // Keyset paging on the (RootId, HashedAt) index; the cursor is "hashedAt:id" of the last file returned.
        // "HashedAt >= $at" lets SQLite seek to the cursor; with only the OR it walks the root's index from the start on every page.
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
                WHERE RootId = $r AND HashedAt >= $at AND (HashedAt > $at OR Id > $id) AND IsDir = 0 AND IsSymlink = 0 AND FullHash IS NOT NULL
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

    /// <summary>One file read for a child at a time: probes read whole files and the disks may be slow.</summary>
    private static readonly SemaphoreSlim ProbeGate = new(1, 1);

    private static readonly System.Text.Json.JsonSerializerOptions LineJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// <c>GET /backup/probe?rootId=&amp;path=&amp;block=&amp;compare=</c>: reads one file and answers its <see cref="FileMap"/> as NDJSON
    /// (<see cref="ProbeLine"/>: progress lines while waiting and reading, then the result), so a long read keeps the connection alive.
    /// 404 with <c>{"missing":true}</c> when the file is not there (an older parent answers 404 without a body).
    /// </summary>
    public static void MapBackupProbe(this RouteGroupBuilder g) =>
        g.MapGet("/probe", async (int rootId, string path, int? block, long? compare, HttpContext http, SqliteSetup sqlite, RootRegistry roots, CancellationToken ct) =>
        {
            var rel = path.Trim('/');
            string full;
            try
            {
                full = roots.ResolveForRead(rootId, rel);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or FileNotFoundException or KeyNotFoundException or InvalidOperationException)
            {
                return Results.NotFound(new { missing = true });
            }

            if (!File.Exists(full))
            {
                return Results.NotFound(new { missing = true });
            }

            ProbeIndex? index;
            using (var c = sqlite.Open())
            {
                index = Db.Query(c, "SELECT Size, MTime, FullHash, HashedAt, CorruptAt FROM Entries WHERE RootId = $r AND RelPath = $p AND IsDir = 0",
                    r => new ProbeIndex(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4)),
                    ("$r", rootId), ("$p", rel)).FirstOrDefault();
            }

            var res = http.Response;
            res.ContentType = "application/x-ndjson";
            res.Headers.CacheControl = "no-store";
            await res.StartAsync(ct);
            var writeLock = new SemaphoreSlim(1, 1);
            async Task Write(ProbeLine line)
            {
                await writeLock.WaitAsync(ct);
                try
                {
                    await res.WriteAsync(System.Text.Json.JsonSerializer.Serialize(line, LineJson) + "\n", ct);
                    await res.Body.FlushAsync(ct);
                }
                finally
                {
                    writeLock.Release();
                }
            }

            while (!await ProbeGate.WaitAsync(TimeSpan.FromSeconds(5), ct))
            {
                await Write(new ProbeLine(Waiting: true));
            }

            try
            {
                // Some multiple of 4 KiB, and never more than 64k blocks however small the child asked for.
                var size = new FileInfo(full).Length;
                var b = (int)Math.Clamp(block ?? FileMap.ChooseBlock(size), Math.Max(4096L, (size + 65535) / 65536), 1L << 30);
                b = (b + 4095) / 4096 * 4096;
                long read = 0;
                using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
                var mapTask = FileMap.ComputeAsync(full, b, compare ?? size, n => Interlocked.Add(ref read, n), ct);
                while (!mapTask.IsCompleted)
                {
                    await Task.WhenAny(mapTask, ticker.WaitForNextTickAsync(ct).AsTask());
                    if (!mapTask.IsCompleted)
                    {
                        await Write(new ProbeLine(Read: Interlocked.Read(ref read)));
                    }
                }

                await Write(new ProbeLine(Result: (await mapTask).ToProbe(index)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Write(new ProbeLine(Error: e.Message));
            }
            finally
            {
                ProbeGate.Release();
            }

            return Results.Empty;
        });

    /// <summary>"hashedAt:id" → parts; anything else is the start of the feed.</summary>
    internal static (long At, long Id) ParseCursor(string? cursor)
    {
        var parts = (cursor ?? "").Split(':');
        return parts.Length == 2 && long.TryParse(parts[0], out var at) && long.TryParse(parts[1], out var id) ? (at, id) : (0, 0);
    }

    internal static string? Version() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
}
