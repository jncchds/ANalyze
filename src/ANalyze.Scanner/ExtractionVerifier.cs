using System.IO.Hashing;
using System.Text.Json;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Logging;
using ANalyze.Infrastructure.Roots;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>
/// Checks extracted files against their archive: each matched file is read and its CRC32 compared with the CRC32 the archive
/// stores for it (read from the headers, or computed when a tar/compressed archive was listed). The archive itself is not read.
/// </summary>
internal sealed class ExtractionVerifier(SqliteConnection read, SqliteConnection write, ScanContext ctx, IReadOnlyDictionary<int, string> rootPaths, int threads)
{
    private const int MaxBadPaths = 500;

    private sealed record Job(long Id, long ArchiveId, int RootId, string Base, string Strip, string ArchivePath, List<MemberMatch> Files);

    public async Task<(int Ok, int Bad)> RunAsync(IReadOnlyList<long> ids)
    {
        ctx.StartPhase("checking");
        var jobs = new List<Job>();
        foreach (var id in ids.Distinct())
        {
            using var q = read.CreateCommand();
            q.CommandText = """
                SELECT x.ArchiveId, x.RootId, x.BaseRelPath, x.Strip, e.RelPath FROM ArchiveExtractions x JOIN Entries e ON e.Id = x.ArchiveId WHERE x.Id = $id
                """;
            q.Parameters.AddWithValue("$id", id);
            using var r = q.ExecuteReader();
            if (!r.Read())
            {
                continue;
            }

            var (archiveId, rootId, basePath, strip, archivePath) = (r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4));
            var files = ExtractionMatcher.Compare(read, ExtractionMatcher.LoadMembers(read, archiveId), rootId, basePath, strip)
                .Where(m => m.State == MemberState.Matched).ToList();
            jobs.Add(new Job(id, archiveId, rootId, basePath, strip, archivePath, files));
        }

        ctx.Total = jobs.Sum(j => j.Files.Count);
        ctx.TotalBytes = jobs.Sum(j => j.Files.Sum(f => f.Size));
        ctx.HashedBytes = 0;
        ctx.Report(force: true);
        ctx.Log.LogInformation("Checking {Count} extracted archives: reading {Files} files ({Size}) and comparing them with the archives' CRC32s",
            jobs.Count, ctx.Total, Format.Bytes(ctx.TotalBytes));

        int ok = 0, bad = 0;
        foreach (var job in jobs)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var result = await CheckAsync(job);
            ok += result.Ok;
            bad += result.Bad.Count;
            Store(job, result.Ok, result.Bad, result.Unverified);
            if (result.Bad.Count > 0)
            {
                ctx.Log.LogWarning("{Archive}: {Bad} extracted files differ from the archive, e.g. {Path}", job.ArchivePath, result.Bad.Count, result.Bad[0]);
            }
            else
            {
                ctx.Log.LogInformation("{Archive}: {Ok} extracted files match the archive{Unverified}", job.ArchivePath, result.Ok,
                    result.Unverified > 0 ? $" ({result.Unverified} could not be checked)" : "");
            }
        }

        if (ctx.UnloggedProblems > 0)
        {
            ctx.Log.LogWarning("{Count} more files could not be read", ctx.UnloggedProblems);
        }

        return (ok, bad);
    }

    private async Task<(int Ok, List<string> Bad, int Unverified)> CheckAsync(Job job)
    {
        int ok = 0, unverified = 0;
        var bad = new System.Collections.Concurrent.ConcurrentBag<string>();
        if (!rootPaths.TryGetValue(job.RootId, out var rootPath))
        {
            return (0, [], job.Files.Count);
        }

        await Parallel.ForEachAsync(job.Files, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ctx.Ct }, async (f, ct) =>
        {
            var path = RootRegistry.Combine(rootPath, f.Target!);
            ctx.Current = f.Target;
            try
            {
                if (f.Crc is not { } expected || !Unchanged(path, f))
                {
                    Interlocked.Increment(ref unverified);
                    return;
                }

                var crc = await Crc32Async(path, ct);
                if (!Unchanged(path, f))
                {
                    Interlocked.Increment(ref unverified);
                }
                else if (crc == expected)
                {
                    Interlocked.Increment(ref ok);
                }
                else
                {
                    bad.Add(f.Path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref unverified);
                Interlocked.Increment(ref ctx.Errors);
                ctx.Problem("Cannot read {Path}: {Error}", path, e.Message);
            }
            finally
            {
                Interlocked.Increment(ref ctx.Done);
                ctx.Report();
            }
        });

        return (ok, [.. bad.Order(StringComparer.Ordinal)], unverified);
    }

    private async Task<long> Crc32Async(string path, CancellationToken ct)
    {
        var crc = new Crc32();
        var buffer = new byte[1 << 20];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int n;
        while ((n = await fs.ReadAsync(buffer, ct)) > 0)
        {
            crc.Append(buffer.AsSpan(0, n));
            Interlocked.Add(ref ctx.HashedBytes, n);
        }

        return crc.GetCurrentHashAsUInt32();
    }

    private static bool Unchanged(string path, MemberMatch f)
    {
        var st = Native.LStat(path);
        return st.Exists && st.IsRegular && st.Size == f.Size && st.MTime == f.DiskMTime;
    }

    private void Store(Job job, int ok, List<string> bad, int unverified)
    {
        using var u = write.CreateCommand();
        u.CommandText = """
            UPDATE ArchiveExtractions SET VerifiedAt = $now, VerifiedOk = $ok, VerifiedBad = $bad, Unverified = $un, BadPaths = $paths WHERE Id = $id
            """;
        u.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        u.Parameters.AddWithValue("$ok", ok);
        u.Parameters.AddWithValue("$bad", bad.Count);
        u.Parameters.AddWithValue("$un", unverified);
        u.Parameters.AddWithValue("$paths", bad.Count == 0 ? DBNull.Value : JsonSerializer.Serialize(bad.Take(MaxBadPaths)));
        u.Parameters.AddWithValue("$id", job.Id);
        u.ExecuteNonQuery();
    }
}
