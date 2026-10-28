using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Roots;
using Microsoft.Data.Sqlite;

namespace ANalyze.Scanner;

/// <summary>
/// Hashes files beyond duplicate candidates: <see cref="HashMissingAsync"/> gives every file a full hash (for backup verification),
/// <see cref="VerifyAsync"/> re-reads files hashed long ago and flags those whose content changed although size and mtime did not
/// (<c>Entries.CorruptAt</c>). Files whose size or mtime changed since the walk are skipped; the next scan resets them.
/// </summary>
internal sealed class ContentHasher(SqliteConnection read, SqliteConnection write, ScanContext ctx, IReadOnlyDictionary<int, string> rootPaths, int threads)
{
    private const int Batch = 400;

    private sealed class Row
    {
        public long Id;
        public int RootId;
        public required string RelPath;
        public long Size;
        public long MTime;
        public required string Identity;
        public string? OldFull;
        public long? Quick;
        public string? Full;
        public bool Failed;
    }

    /// <summary>Full hash for every non-empty file that has none. Hard links are read once.</summary>
    public Task<long> HashMissingAsync() =>
        RunAsync("hashing", "FullHash IS NULL", $"ORDER BY {DuplicateFinder.IdentitySql}", [], maxBytes: 0, verify: false);

    /// <summary>Re-reads files hashed before <paramref name="olderThan"/> (oldest first, up to <paramref name="maxBytes"/>; 0 = all).</summary>
    public Task<long> VerifyAsync(long olderThan, long maxBytes) =>
        RunAsync("verifying", "FullHash IS NOT NULL AND COALESCE(HashedAt, 0) < $cut", "ORDER BY COALESCE(HashedAt, 0), Id", [("$cut", olderThan)], maxBytes, verify: true);

    private async Task<long> RunAsync(string phase, string where, string order, (string, object)[] args, long maxBytes, bool verify)
    {
        ctx.Phase = phase;
        ctx.Done = 0;
        ctx.HashedBytes = 0;
        var filter = $"IsDir = 0 AND IsSymlink = 0 AND Size > 0 AND {where}";
        using (var c = read.CreateCommand())
        {
            c.CommandText = $"SELECT COUNT(*), COALESCE(SUM(Size), 0) FROM Entries WHERE {filter}";
            foreach (var (n, v) in args)
            {
                c.Parameters.AddWithValue(n, v);
            }

            using var r = c.ExecuteReader();
            r.Read();
            ctx.Total = r.GetInt64(0);
            ctx.TotalBytes = maxBytes > 0 ? Math.Min(maxBytes, r.GetInt64(1)) : r.GetInt64(1);
        }

        ctx.Report(force: true);
        if (ctx.Total == 0)
        {
            return 0;
        }

        using var q = read.CreateCommand();
        q.CommandText = $"SELECT Id, RootId, RelPath, Size, MTime, {DuplicateFinder.IdentitySql}, FullHash FROM Entries WHERE {filter} {order}";
        foreach (var (n, v) in args)
        {
            q.Parameters.AddWithValue(n, v);
        }

        long budget = 0;
        long changed = 0;
        var batch = new List<Row>();
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
                    MTime = r.GetInt64(4),
                    Identity = r.GetString(5),
                    OldFull = r.IsDBNull(6) ? null : r.GetString(6),
                };
                if (maxBytes > 0 && budget > 0 && budget + row.Size > maxBytes)
                {
                    break;
                }

                budget += row.Size;
                // Keep hard-link siblings in one batch so they share one read.
                if (batch.Count >= Batch && batch[^1].Identity != row.Identity)
                {
                    changed += await ProcessAsync(batch, verify);
                    batch.Clear();
                }

                batch.Add(row);
            }
        }

        if (batch.Count > 0)
        {
            changed += await ProcessAsync(batch, verify);
        }

        return changed;
    }

    /// <summary>Hashes and stores one batch; returns how many files changed silently (verification only).</summary>
    private async Task<long> ProcessAsync(List<Row> rows, bool verify)
    {
        var reps = verify ? rows : rows.GroupBy(r => r.Identity).Select(g => g.First()).ToList();
        await Parallel.ForEachAsync(reps, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ctx.Ct }, async (row, ct) =>
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
                if (!Unchanged(path, row))
                {
                    row.Failed = true;
                    return;
                }

                row.Quick = row.Size <= Hashing.SmallFile ? 0 : Hashing.QuickHash(path, row.Size);
                row.Full = await Hashing.FullHashAsync(path, n => Interlocked.Add(ref ctx.HashedBytes, n), ct);
                // Written to while being read: leave it to the next scan.
                if (!Unchanged(path, row))
                {
                    row.Failed = true;
                    return;
                }

                Interlocked.Increment(ref ctx.Hashed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                row.Failed = true;
                Interlocked.Increment(ref ctx.Errors);
            }

            Interlocked.Increment(ref ctx.Done);
            ctx.Report();
        });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long changed = 0;
        using var tx = write.BeginTransaction();
        using var upd = write.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = """
            UPDATE Entries SET QuickHash = $q, FullHash = $f, HashedAt = $now, CorruptAt = CASE WHEN $bad THEN $now ELSE CorruptAt END
            WHERE Id = $id AND Size = $size AND MTime = $mtime
            """;
        foreach (var p in new[] { "$q", "$f", "$bad", "$id", "$size", "$mtime" })
        {
            upd.Parameters.Add(new SqliteParameter(p, null));
        }

        upd.Parameters.AddWithValue("$now", now);
        var byIdentity = reps.ToDictionary(r => verify ? "#" + r.Id : r.Identity);
        foreach (var row in rows)
        {
            var rep = byIdentity[verify ? "#" + row.Id : row.Identity];
            if (rep.Failed || rep.Full is null)
            {
                continue;
            }

            var bad = verify && row.OldFull is not null && row.OldFull != rep.Full;
            if (bad)
            {
                changed++;
            }

            upd.Parameters["$q"].Value = (object?)rep.Quick ?? DBNull.Value;
            upd.Parameters["$f"].Value = rep.Full;
            upd.Parameters["$bad"].Value = bad;
            upd.Parameters["$id"].Value = row.Id;
            upd.Parameters["$size"].Value = row.Size;
            upd.Parameters["$mtime"].Value = row.MTime;
            upd.ExecuteNonQuery();
        }

        tx.Commit();
        ctx.Report();
        return changed;
    }

    private static bool Unchanged(string path, Row row)
    {
        var st = Native.LStat(path);
        return st.Exists && st.IsRegular && st.Size == row.Size && st.MTime == row.MTime;
    }
}
