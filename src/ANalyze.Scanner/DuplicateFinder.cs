using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>
/// Finds duplicate content across all roots: same size → same quick hash → same full hash.
/// Hard links (same device and inode) share storage, so they are hashed once and never count as duplicates.
/// Hashes are cached in the Entries table and reset by the walker when a file changes.
/// </summary>
internal sealed class DuplicateFinder(SqliteConnection read, SqliteConnection write, ScanContext ctx, IReadOnlyDictionary<int, string> rootPaths, long minSize, int threads)
{
    private const int BatchFiles = 400;

    /// <summary>A batch is also stored once its files hold this much data.</summary>
    private const long BatchBytes = 8L << 30;

    /// <summary>Hard links share one identity; on platforms without inodes every file is its own identity.</summary>
    internal const string IdentitySql = "CASE WHEN Inode = 0 THEN 'i' || Id ELSE Dev || ':' || Inode END";

    internal static string CandidateSizesSql => $"""
        SELECT Size FROM Entries WHERE IsDir = 0 AND IsSymlink = 0 AND Size >= $min
        GROUP BY Size HAVING COUNT(DISTINCT {IdentitySql}) > 1
        """;

    private sealed class Row
    {
        public long Id;
        public int RootId;
        public required string RelPath;
        public long Size;
        public required string Identity;
        public long? Quick;
        public string? Full;
        public bool Failed;
    }

    /// <summary>Hashes the candidates; returns the number of sets of identical files (all roots).</summary>
    public async Task<long> RunAsync()
    {
        ctx.HashedBytes = 0;

        // Show the phase before the size search: on a large database it reads the whole size index.
        ctx.StartPhase("duplicates");
        ctx.Log.LogInformation("Finding duplicates: looking for files that share their size with another file");
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // The candidate sizes are found once and reused by the count and the file list below.
        using (var c = read.CreateCommand())
        {
            c.CommandText = $"""
                CREATE TEMP TABLE IF NOT EXISTS DupSizes (Size INTEGER PRIMARY KEY);
                DELETE FROM DupSizes;
                INSERT INTO DupSizes {CandidateSizesSql};
                """;
            c.Parameters.AddWithValue("$min", minSize);
            c.ExecuteNonQuery();
        }

        using (var c = read.CreateCommand())
        {
            c.CommandText = $"SELECT COUNT(*), COALESCE(SUM(Size), 0) FROM Entries INDEXED BY {AnalyzeDbContext.UnhashedIndex} WHERE IsDir=0 AND IsSymlink=0 AND FullHash IS NULL AND Size IN (SELECT Size FROM DupSizes)";
            using var r = c.ExecuteReader();
            r.Read();
            ctx.Total = r.GetInt64(0);
            ctx.TotalBytes = r.GetInt64(1);
            ctx.Report(force: true);
        }

        ctx.Log.LogInformation(
            "Finding duplicates: {Count} files ({Size}) share their size with another file and have no hash yet (found in {Time}); a quick hash of 3×64 KB comes first, the full hash only when that matches",
            ctx.Total, Format.Bytes(ctx.TotalBytes), Format.Duration(clock.Elapsed));

        using var q = read.CreateCommand();
        q.CommandText = $"""
            SELECT Id, RootId, RelPath, Size, {IdentitySql}, QuickHash, FullHash FROM Entries
            WHERE IsDir = 0 AND IsSymlink = 0 AND Size IN (SELECT Size FROM DupSizes)
            ORDER BY Size DESC, Id
            """;

        var batch = new List<List<Row>>();
        var batchCount = 0;
        long batchBytes = 0;
        List<Row>? group = null;
        using (var r = q.ExecuteReader())
        {
            while (r.Read())
            {
                ctx.Ct.ThrowIfCancellationRequested();
                var row = new Row
                {
                    Id = r.GetInt64(0),
                    RootId = r.GetInt32(1),
                    RelPath = r.GetString(2),
                    Size = r.GetInt64(3),
                    Identity = r.GetString(4),
                    Quick = r.IsDBNull(5) ? null : r.GetInt64(5),
                    Full = r.IsDBNull(6) ? null : r.GetString(6),
                };
                if (group is null || group[0].Size != row.Size)
                {
                    if (group is not null)
                    {
                        batch.Add(group);
                        batchCount += group.Count;
                        batchBytes += group.Count * group[0].Size;
                        if (batchCount >= BatchFiles || batchBytes >= BatchBytes)
                        {
                            await ProcessAsync(batch);
                            batch.Clear();
                            batchCount = 0;
                            batchBytes = 0;
                            if (ctx.ProgressLineDue())
                            {
                                ctx.Log.LogInformation("Finding duplicates: {Done} of {Total} files hashed, {Bytes} read", ctx.Done, ctx.Total, Format.Bytes(ctx.HashedBytes));
                            }
                        }
                    }

                    group = [];
                }

                group.Add(row);
            }
        }

        if (group is not null)
        {
            batch.Add(group);
        }

        if (batch.Count > 0)
        {
            await ProcessAsync(batch);
        }

        if (ctx.UnloggedProblems > 0)
        {
            ctx.Log.LogWarning("{Count} more files could not be read", ctx.UnloggedProblems);
        }

        ctx.Log.LogInformation("Finding duplicates: counting the sets of identical files");
        using var groups = read.CreateCommand();
        groups.CommandText = $"""
            DROP TABLE IF EXISTS temp.DupSizes;
            SELECT COUNT(*) FROM (SELECT FullHash FROM Entries WHERE FullHash IS NOT NULL AND IsDir = 0 AND IsSymlink = 0
            GROUP BY FullHash HAVING COUNT(DISTINCT {IdentitySql}) > 1)
            """;
        return (long)groups.ExecuteScalar()!;
    }

    private async Task ProcessAsync(List<List<Row>> groups)
    {
        // One representative per identity (hard links share content).
        var reps = groups.Select(g => g.GroupBy(r => r.Identity).Select(x => x.First()).ToList()).Where(g => g.Count > 1).ToList();

        // Stage 1: small files get a full hash directly; large ones a quick hash.
        var stage1 = reps.SelectMany(g => g).Where(r => r.Size <= Hashing.SmallFile ? r.Full is null : r.Quick is null).ToList();
        await HashAllAsync(stage1, full: r => r.Size <= Hashing.SmallFile);

        // Stage 2: full hash for large files whose quick hash collides with another identity.
        var stage2 = reps
            .Where(g => g[0].Size > Hashing.SmallFile)
            .SelectMany(g => g.Where(r => !r.Failed).GroupBy(r => r.Quick).Where(x => x.Count() > 1).SelectMany(x => x))
            .Where(r => r.Full is null)
            .ToList();
        await HashAllAsync(stage2, full: _ => true);

        // Copy results to hard-link siblings and store.
        using var tx = write.BeginTransaction();
        using var upd = write.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE Entries SET QuickHash = $q, FullHash = $f,
                HashedAt = CASE WHEN $f IS NOT NULL AND (FullHash IS NULL OR HashedAt IS NULL) THEN $now ELSE HashedAt END
            WHERE Id = $id
            """;
        upd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        upd.Parameters.Add(new SqliteParameter("$q", null));
        upd.Parameters.Add(new SqliteParameter("$f", null));
        upd.Parameters.Add(new SqliteParameter("$id", null));
        foreach (var g in groups)
        {
            var byIdentity = g.GroupBy(r => r.Identity).ToDictionary(x => x.Key, x => x.First());
            foreach (var row in g)
            {
                var rep = byIdentity[row.Identity];
                if (rep.Failed || (rep.Quick is null && rep.Full is null))
                {
                    continue;
                }

                upd.Parameters["$q"].Value = (object?)rep.Quick ?? DBNull.Value;
                upd.Parameters["$f"].Value = (object?)rep.Full ?? DBNull.Value;
                upd.Parameters["$id"].Value = row.Id;
                upd.ExecuteNonQuery();
            }
        }

        tx.Commit();
        ctx.Report();
    }

    private async Task HashAllAsync(List<Row> rows, Func<Row, bool> full)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ctx.Ct }, async (row, ct) =>
        {
            if (!rootPaths.TryGetValue(row.RootId, out var rootPath))
            {
                row.Failed = true;
                return;
            }

            var path = RootRegistry.Combine(rootPath, row.RelPath);
            ctx.Current = row.RelPath;
            try
            {
                if (full(row))
                {
                    row.Full = await Hashing.FullHashAsync(path, n => Interlocked.Add(ref ctx.HashedBytes, n), ct);
                    row.Quick ??= row.Size <= Hashing.SmallFile ? 0 : null;
                    Interlocked.Increment(ref ctx.Done);
                }
                else
                {
                    row.Quick = Hashing.QuickHash(path, row.Size);
                }

                Interlocked.Increment(ref ctx.Hashed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                row.Failed = true;
                Interlocked.Increment(ref ctx.Errors);
                ctx.Problem("Cannot read {Path}: {Error}", path, e.Message);
            }

            ctx.Report();
        });
    }
}
