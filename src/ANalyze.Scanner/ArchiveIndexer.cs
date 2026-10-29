using System.IO.Hashing;
using ANalyze.Infrastructure.Logging;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace ANalyze.Scanner;

/// <summary>
/// Scan phase "archives": stores the file list of every archive that has none (or changed). zip, rar and 7z are listed from their
/// headers, which also carry each file's CRC32. tar and single-file compressed archives have no index: with
/// <see cref="AppSettings.ListStreamedArchives"/> they are decompressed once and the CRC32s are computed on the way.
/// </summary>
internal static class ArchiveIndexer
{
    public const int MaxMembers = 100_000;
    private const int Batch = 64;

    public static readonly string[] HeaderFormats = ["zip", "zipx", "rar", "7z"];
    public static readonly string[] StreamedFormats = ["tar", "tgz", "gz", "tbz", "tbz2", "bz2", "txz", "xz", "tzst", "zst"];

    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase) { ".DS_Store", "Thumbs.db", "desktop.ini" };

    public readonly record struct Member(string Path, string Name, long Size, long? Crc);

    public sealed record Listed(string? Format, bool Streamed, List<Member> Members, string? Error);

    private sealed record Candidate(long Id, int RootId, string RelPath, string Name, long Size, long MTime, string Ext);

    /// <summary>Lists new and changed archives in scope and drops listings of archives that are gone; returns how many listings changed.</summary>
    public static long Run(SqliteConnection read, SqliteConnection write, ScanContext ctx, IReadOnlyDictionary<int, string> rootPaths, Scope scope, AppSettings s)
    {
        ctx.StartPhase("archives");
        var removed = DropStale(read, write);

        var streamed = s.ListStreamedArchives;
        var cap = s.StreamedArchiveMaxGb > 0 ? s.StreamedArchiveMaxGb * (1L << 30) : long.MaxValue;
        var todo = new List<Candidate>();
        using (var q = read.CreateCommand())
        {
            var header = string.Join(",", HeaderFormats.Select(f => $"'{f}'"));
            var stream = string.Join(",", StreamedFormats.Select(f => $"'{f}'"));
            var formats = streamed ? $"(Ext IN ({header}) OR (Ext IN ({stream}) AND e.Size <= $cap))" : $"Ext IN ({header})";
            q.CommandText = $"""
                SELECT e.Id, e.RootId, e.RelPath, e.Name, e.Size, e.MTime, e.Ext FROM Entries e
                LEFT JOIN ArchiveListings l ON l.EntryId = e.Id
                WHERE {formats} AND IsDir = 0 AND IsSymlink = 0 AND e.Size > 0 {scope.Sql}
                  AND (l.EntryId IS NULL OR l.Size <> e.Size OR l.MTime <> e.MTime)
                ORDER BY e.Id
                """;
            q.Parameters.AddWithValue("$cap", cap);
            scope.Bind(q);
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var c = new Candidate(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetString(6));
                if (!IsLaterVolume(c.Name))
                {
                    todo.Add(c);
                }
            }
        }

        ctx.Total = todo.Count;
        ctx.TotalBytes = todo.Where(c => IsStreamed(c.Ext)).Sum(c => c.Size);
        ctx.HashedBytes = 0;
        ctx.Report(force: true);
        ctx.Log.LogInformation("Archives: listing the files inside {Count} new or changed archives{Streamed}", todo.Count,
            ctx.TotalBytes > 0 ? $" ({Format.Bytes(ctx.TotalBytes)} of tar/compressed archives are decompressed for it)" : "");

        long failed = 0;
        foreach (var chunk in todo.Chunk(Batch))
        {
            var results = new Listed[chunk.Length];
            Parallel.For(0, chunk.Length, new ParallelOptions { MaxDegreeOfParallelism = s.WorkerThreads(), CancellationToken = ctx.Ct }, i =>
            {
                var c = chunk[i];
                ctx.Current = c.RelPath;
                results[i] = rootPaths.TryGetValue(c.RootId, out var rootPath)
                    ? List(RootRegistry.Combine(rootPath, c.RelPath), c.Name, IsStreamed(c.Ext), n => Interlocked.Add(ref ctx.HashedBytes, n), ctx.Ct)
                    : new Listed(null, false, [], "Root not available");
                Interlocked.Increment(ref ctx.Done);
                ctx.Report();
            });

            Store(write, chunk, results);
            for (var i = 0; i < chunk.Length; i++)
            {
                if (results[i].Error is { } error)
                {
                    failed++;
                    ctx.Problem("Cannot list archive {Path}: {Error}", chunk[i].RelPath, error);
                }
            }

            if (ctx.ProgressLineDue())
            {
                ctx.Log.LogInformation("Archives: {Done} of {Total} listed", ctx.Done, ctx.Total);
            }
        }

        if (todo.Count > 0 || removed > 0)
        {
            ctx.Log.LogInformation("Archives: {Listed} listed ({Failed} could not be read), {Removed} listings of removed or changed archives dropped",
                todo.Count - failed, failed, removed);
        }

        return todo.Count + removed;
    }

    /// <summary>Lists one archive. Never throws for unreadable or damaged archives: the error is returned instead.</summary>
    public static Listed List(string path, string name, bool streamed, Action<long>? progress = null, CancellationToken ct = default)
    {
        var members = new Dictionary<string, Member>(StringComparer.Ordinal);
        string? format = null;
        try
        {
            if (!streamed)
            {
                using var archive = ArchiveFactory.OpenArchive(path);
                format = archive.Type.ToString().ToLowerInvariant();
                foreach (var e in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (e.IsDirectory || !string.IsNullOrEmpty(e.LinkTarget) || Normalize(e.Key) is not { } p)
                    {
                        continue;
                    }

                    if (members.Count >= MaxMembers && !members.ContainsKey(p))
                    {
                        return new Listed(format, false, [], $"More than {MaxMembers} files");
                    }

                    // A CRC of 0 for a non-empty file means the format did not store one (7z, rar5 without CRCs).
                    long? crc = e.Size > 0 && e.Crc == 0 ? null : e.Crc & 0xFFFFFFFF;
                    members[p] = new Member(p, NameOf(p), e.Size, crc);
                }

                return new Listed(format, false, [.. members.Values], null);
            }

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            using var counted = new CountingStream(fs, progress);
            using var reader = ReaderFactory.OpenReader(counted, new ReaderOptions { LeaveStreamOpen = true });
            format = reader.Type.ToString().ToLowerInvariant();
            var buffer = new byte[1 << 20];
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var e = reader.Entry;
                if (e.IsDirectory || !string.IsNullOrEmpty(e.LinkTarget))
                {
                    continue;
                }

                // A compressed single file (x.gz) may not store its name: it is the archive's name without the extension.
                var p = Normalize(string.IsNullOrEmpty(e.Key) ? Path.GetFileNameWithoutExtension(name) : e.Key);
                if (p is null)
                {
                    continue;
                }

                if (members.Count >= MaxMembers && !members.ContainsKey(p))
                {
                    return new Listed(format, true, [], $"More than {MaxMembers} files");
                }

                var crc = new Crc32();
                long size = 0;
                using (var es = reader.OpenEntryStream())
                {
                    int n;
                    while ((n = es.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        crc.Append(buffer.AsSpan(0, n));
                        size += n;
                    }
                }

                members[p] = new Member(p, NameOf(p), size, crc.GetCurrentHashAsUInt32());
            }

            return new Listed(format, true, [.. members.Values], null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new Listed(format, streamed, [], e.Message);
        }
    }

    /// <summary>
    /// Archive path → '/'-separated relative path; null for entries that are never extracted as such (macOS resource forks,
    /// Finder/Explorer metadata) or that would leave the folder ("..").
    /// </summary>
    public static string? Normalize(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var part in key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                return null;
            }

            parts.Add(part);
        }

        if (parts.Count == 0 || parts[0] == "__MACOSX" || JunkNames.Contains(parts[^1]))
        {
            return null;
        }

        return string.Join('/', parts);
    }

    public static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static bool IsStreamed(string ext) => StreamedFormats.Contains(ext);

    /// <summary>Second and later parts of a multi-volume rar ("x.part2.rar"): the first part lists the archive.</summary>
    private static bool IsLaterVolume(string name)
    {
        var m = System.Text.RegularExpressions.Regex.Match(name, @"\.part0*(\d+)\.rar$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success && m.Groups[1].Value != "1";
    }

    /// <summary>Drops listings (with their members and extractions) of archives that were removed or changed since they were listed.</summary>
    private static long DropStale(SqliteConnection read, SqliteConnection write)
    {
        var stale = new List<long>();
        using (var q = read.CreateCommand())
        {
            q.CommandText = """
                SELECT l.EntryId FROM ArchiveListings l LEFT JOIN Entries e ON e.Id = l.EntryId
                WHERE e.Id IS NULL OR e.Size <> l.Size OR e.MTime <> l.MTime
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                stale.Add(r.GetInt64(0));
            }
        }

        if (stale.Count > 0)
        {
            using var tx = write.BeginTransaction();
            foreach (var id in stale)
            {
                Delete(write, tx, id);
            }

            tx.Commit();
        }

        // Extractions of archives whose destination root was removed.
        using var orphans = write.CreateCommand();
        orphans.CommandText = "DELETE FROM ArchiveExtractions WHERE RootId NOT IN (SELECT Id FROM Roots)";
        orphans.ExecuteNonQuery();
        return stale.Count;
    }

    private static void Delete(SqliteConnection write, SqliteTransaction tx, long archiveId)
    {
        using var d = write.CreateCommand();
        d.Transaction = tx;
        d.CommandText = """
            DELETE FROM ArchiveMembers WHERE ArchiveId = $a;
            DELETE FROM ArchiveExtractions WHERE ArchiveId = $a;
            DELETE FROM ArchiveListings WHERE EntryId = $a;
            """;
        d.Parameters.AddWithValue("$a", archiveId);
        d.ExecuteNonQuery();
    }

    private static void Store(SqliteConnection write, Candidate[] chunk, Listed[] results)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var tx = write.BeginTransaction();
        using var listing = write.CreateCommand();
        listing.Transaction = tx;
        listing.CommandText = """
            INSERT INTO ArchiveListings (EntryId, Size, MTime, Format, Streamed, ListedAt, Error, Files, FileBytes)
            VALUES ($id, $size, $mtime, $format, $streamed, $now, $error, $files, $bytes)
            """;
        foreach (var p in new[] { "$id", "$size", "$mtime", "$format", "$streamed", "$error", "$files", "$bytes" })
        {
            listing.Parameters.Add(new SqliteParameter(p, null));
        }

        listing.Parameters.AddWithValue("$now", now);
        using var member = write.CreateCommand();
        member.Transaction = tx;
        member.CommandText = "INSERT INTO ArchiveMembers (ArchiveId, Path, Name, Size, Crc) VALUES ($a, $p, $n, $s, $c)";
        foreach (var p in new[] { "$a", "$p", "$n", "$s", "$c" })
        {
            member.Parameters.Add(new SqliteParameter(p, null));
        }

        for (var i = 0; i < chunk.Length; i++)
        {
            var (c, r) = (chunk[i], results[i]);
            Delete(write, tx, c.Id);
            listing.Parameters["$id"].Value = c.Id;
            listing.Parameters["$size"].Value = c.Size;
            listing.Parameters["$mtime"].Value = c.MTime;
            listing.Parameters["$format"].Value = (object?)r.Format ?? DBNull.Value;
            listing.Parameters["$streamed"].Value = r.Streamed;
            listing.Parameters["$error"].Value = (object?)r.Error ?? DBNull.Value;
            listing.Parameters["$files"].Value = r.Members.Count;
            listing.Parameters["$bytes"].Value = r.Members.Sum(m => m.Size);
            listing.ExecuteNonQuery();
            member.Parameters["$a"].Value = c.Id;
            foreach (var m in r.Members)
            {
                member.Parameters["$p"].Value = m.Path;
                member.Parameters["$n"].Value = m.Name;
                member.Parameters["$s"].Value = m.Size;
                member.Parameters["$c"].Value = (object?)m.Crc ?? DBNull.Value;
                member.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>Reports how many compressed bytes were read.</summary>
    private sealed class CountingStream(Stream inner, Action<long>? progress) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            progress?.Invoke(n);
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            progress?.Invoke(n);
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
