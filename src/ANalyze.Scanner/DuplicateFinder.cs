using ANalyze.Infrastructure.Roots;
using Microsoft.Data.Sqlite;

namespace ANalyze.Scanner;

/// <summary>
/// Finds duplicate content across all roots: same size → same quick hash → same full hash.
/// Hard links (same device and inode) share storage, so they are hashed once and never count as duplicates.
/// Hashes are cached in the Entries table and reset by the walker when a file changes.
/// </summary>
internal sealed class DuplicateFinder(SqliteConnection read, SqliteConnection write, ScanContext ctx, IReadOnlyDictionary<int, string> rootPaths, long minSize, int threads)
{
    private const int BatchFiles = 400;

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

    public async Task RunAsync()
    {
        ctx.Phase = "hashing";
        ctx.Done = 0;
        ctx.HashedBytes = 0;
        using (var c = read.CreateCommand())
        {
            c.CommandText = $"SELECT COUNT(*), COALESCE(SUM(Size), 0) FROM Entries WHERE IsDir=0 AND IsSymlink=0 AND FullHash IS NULL AND Size IN ({CandidateSizesSql})";
            c.Parameters.AddWithValue("$min", minSize);
            using var r = c.ExecuteReader();
            r.Read();
            ctx.Total = r.GetInt64(0);
            ctx.TotalBytes = r.GetInt64(1);
        }

        ctx.Report(force: true);

        using var q = read.CreateCommand();
        q.CommandText = $"""
            SELECT Id, RootId, RelPath, Size, {IdentitySql}, QuickHash, FullHash FROM Entries
            WHERE IsDir = 0 AND IsSymlink = 0 AND Size IN ({CandidateSizesSql})
            ORDER BY Size DESC, Id
            """;
        q.Parameters.AddWithValue("$min", minSize);

        var batch = new List<List<Row>>();
        var batchCount = 0;
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
                        if (batchCount >= BatchFiles)
                        {
                            await ProcessAsync(batch);
                            batch.Clear();
                            batchCount = 0;
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
        upd.CommandText = "UPDATE Entries SET QuickHash = $q, FullHash = $f WHERE Id = $id";
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
            }

            ctx.Report();
        });
    }
}
