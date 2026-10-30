using System.IO.Hashing;
using System.Text;
using ANalyze.Domain;
using ANalyze.Infrastructure.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>What became of one archive file in an extraction.</summary>
public enum MemberState
{
    Matched,
    Missing,
    /// <summary>Smaller than in the archive (interrupted extraction).</summary>
    Smaller,
    /// <summary>Larger than in the archive, or a folder where the file should be.</summary>
    Differs,
}

/// <summary>One archive file and the file found for it on disk.</summary>
public sealed record MemberMatch(string Path, long Size, long? Crc, MemberState State, string? Target, long? EntryId, long? DiskSize, long DiskMTime);

/// <summary>An archive file as stored in <c>ArchiveMembers</c>.</summary>
public sealed record StoredMember(string Path, string Name, long Size, long? Crc);

/// <summary>
/// Finds where archives were extracted (scan phase "extractions", all roots). For each listed archive a few of its largest files
/// ("anchors") are looked up by size and name; each hit names a folder the archive's paths would start from (and how many leading
/// folders of those paths were left out). Folders that several anchors agree on are then checked file by file, by path and size.
/// Extra files in the folder do not matter: the archive must be contained in the folder, not equal to it.
/// </summary>
public static class ExtractionMatcher
{
    private const int Anchors = 5;
    private const int MaxCandidates = 8;
    private const int MaxHitsPerAnchor = 50;

    /// <summary>A partial extraction is reported only with this much evidence (or two matched files).</summary>
    private const long MinPartialBytes = 1 << 20;

    private readonly record struct Key(int RootId, string Base, string Strip);

    internal static long Run(SqliteConnection read, SqliteConnection write, ScanContext ctx)
    {
        ctx.StartPhase("extractions");
        var archives = new List<long>();
        using (var q = read.CreateCommand())
        {
            q.CommandText = "SELECT l.EntryId FROM ArchiveListings l JOIN Entries e ON e.Id = l.EntryId WHERE l.Error IS NULL AND l.Files > 0 ORDER BY l.EntryId";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                archives.Add(r.GetInt64(0));
            }
        }

        ctx.Total = archives.Count;
        ctx.Report(force: true);
        ctx.Log.LogInformation("Extracted archives: looking for the files of {Count} archives on all roots", archives.Count);
        using var finder = new Finder(read);
        long complete = 0, partial = 0;
        var pending = new List<(long Archive, List<ArchiveExtraction> Found)>();
        foreach (var id in archives)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var found = Find(finder, LoadMembers(read, id));
            foreach (var f in found)
            {
                f.ArchiveId = id;
            }

            complete += found.Count(f => f.State == ExtractionState.Complete);
            partial += found.Count(f => f.State == ExtractionState.Partial);
            pending.Add((id, found));
            ctx.Done++;
            ctx.Report();
            if (pending.Count >= 200)
            {
                Store(read, write, pending);
                pending.Clear();
                ctx.CheckYield();
            }

            if (ctx.ProgressLineDue())
            {
                ctx.Log.LogInformation("Extracted archives: {Done} of {Total} archives checked", ctx.Done, ctx.Total);
            }
        }

        Store(read, write, pending);
        ctx.Log.LogInformation("Extracted archives: {Complete} complete and {Partial} partial extractions found", complete, partial);
        return complete + partial;
    }

    public static List<StoredMember> LoadMembers(SqliteConnection c, long archiveId)
    {
        using var q = c.CreateCommand();
        q.CommandText = "SELECT Path, Name, Size, Crc FROM ArchiveMembers WHERE ArchiveId = $a ORDER BY Id";
        q.Parameters.AddWithValue("$a", archiveId);
        using var r = q.ExecuteReader();
        var list = new List<StoredMember>();
        while (r.Read())
        {
            list.Add(new StoredMember(r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        }

        return list;
    }

    /// <summary>The extractions of one archive (without <c>ArchiveId</c>), best first.</summary>
    public static List<ArchiveExtraction> Find(SqliteConnection c, IReadOnlyList<StoredMember> members)
    {
        using var finder = new Finder(c);
        return Find(finder, members);
    }

    private static List<ArchiveExtraction> Find(Finder finder, IReadOnlyList<StoredMember> members)
    {
        if (members.Count == 0)
        {
            return [];
        }

        // Largest files first, preferring sizes that occur once in the archive; tiny files only when there is nothing else.
        var sizeCounts = members.GroupBy(m => m.Size).ToDictionary(g => g.Key, g => g.Count());
        var anchors = members.Where(m => m.Size >= 4096).OrderBy(m => sizeCounts[m.Size] > 1).ThenByDescending(m => m.Size).Take(Anchors).ToList();
        if (anchors.Count == 0)
        {
            anchors = members.Where(m => m.Size > 0).OrderByDescending(m => m.Size).Take(Anchors).ToList();
        }

        var votes = new Dictionary<Key, int>();
        foreach (var a in anchors)
        {
            var parts = a.Path.Split('/');
            var seen = new HashSet<Key>();
            foreach (var (rootId, rel) in finder.ByNameAndSize(a.Name, a.Size))
            {
                // The longest tail of the archive path that the found path ends with.
                for (var strip = 0; strip < parts.Length; strip++)
                {
                    var tail = string.Join('/', parts[strip..]);
                    if (Matches(rel, tail))
                    {
                        var key = new Key(rootId, rel.Length == tail.Length ? "" : rel[..(rel.Length - tail.Length - 1)],
                            strip == 0 ? "" : string.Join('/', parts[..strip]) + "/");
                        if (seen.Add(key))
                        {
                            votes[key] = votes.GetValueOrDefault(key) + 1;
                        }

                        break;
                    }
                }
            }
        }

        var result = new List<ArchiveExtraction>();
        foreach (var (key, _) in votes.OrderByDescending(v => v.Value).ThenBy(v => v.Key.Strip.Length).Take(MaxCandidates))
        {
            var x = Evaluate(finder, members, key.RootId, key.Base, key.Strip, out _);
            if (x.Matched == x.Files || x.Matched >= 2 || x.MatchedBytes >= MinPartialBytes)
            {
                result.Add(x);
            }
        }

        return [.. result.OrderBy(x => x.State).ThenByDescending(x => x.MatchedBytes)];
    }

    /// <summary>Compares every archive file with the file at its place under <paramref name="basePath"/>.</summary>
    public static List<MemberMatch> Compare(SqliteConnection c, IReadOnlyList<StoredMember> members, int rootId, string basePath, string strip)
    {
        using var finder = new Finder(c);
        Evaluate(finder, members, rootId, basePath, strip, out var matches);
        return matches;
    }

    private static ArchiveExtraction Evaluate(Finder finder, IReadOnlyList<StoredMember> members, int rootId, string basePath, string strip, out List<MemberMatch> matches)
    {
        matches = new List<MemberMatch>(members.Count);
        var x = new ArchiveExtraction { RootId = rootId, BaseRelPath = basePath, Strip = strip, FolderRelPath = basePath, Fingerprint = "", Files = members.Count };
        var fp = new XxHash128();
        string[]? common = null;
        foreach (var m in members)
        {
            x.Bytes += m.Size;
            var inScope = strip.Length == 0 || m.Path.StartsWith(strip, StringComparison.Ordinal);
            var target = inScope ? Join(basePath, m.Path[strip.Length..]) : null;
            var found = target is null ? null : finder.At(rootId, target);
            var state = found switch
            {
                null => MemberState.Missing,
                { IsDir: true } => MemberState.Differs,
                { Size: var s } when s == m.Size => MemberState.Matched,
                { Size: var s } when s < m.Size => MemberState.Smaller,
                _ => MemberState.Differs,
            };
            switch (state)
            {
                case MemberState.Matched:
                    x.Matched++;
                    x.MatchedBytes += m.Size;
                    fp.Append(Encoding.UTF8.GetBytes($"{found!.Value.Id}:{found.Value.Size}:{found.Value.MTime};"));
                    var dir = found.Value.RelPath.Split('/')[..^1];
                    common = common is null ? dir : [.. common.Zip(dir).TakeWhile(p => p.First == p.Second).Select(p => p.First)];
                    break;
                case MemberState.Missing:
                    x.Missing++;
                    break;
                case MemberState.Smaller:
                    x.Smaller++;
                    break;
                default:
                    x.Differs++;
                    break;
            }

            matches.Add(new MemberMatch(m.Path, m.Size, m.Crc, state, found?.RelPath ?? target, found?.Id, found?.Size, found?.MTime ?? 0));
        }

        fp.Append(Encoding.UTF8.GetBytes($"|{x.Missing}:{x.Smaller}:{x.Differs}"));
        x.Fingerprint = Convert.ToHexStringLower(fp.GetCurrentHash());
        x.State = x.Matched == x.Files ? ExtractionState.Complete : ExtractionState.Partial;
        if (common is not null)
        {
            x.FolderRelPath = string.Join('/', common);
            x.FolderId = finder.At(rootId, x.FolderRelPath) is { IsDir: true } d ? d.Id : null;
        }

        return x;
    }

    private static bool Matches(string rel, string tail) =>
        rel.Length == tail.Length ? rel == tail : rel.Length > tail.Length && rel[rel.Length - tail.Length - 1] == '/' && rel.EndsWith(tail, StringComparison.Ordinal);

    private static string Join(string basePath, string rel) => basePath.Length == 0 ? rel : basePath + "/" + rel;

    /// <summary>
    /// Replaces the stored extractions of these archives. The same place keeps its id (links and queued checks stay valid) and its
    /// verification results while the matched files did not change.
    /// </summary>
    private static void Store(SqliteConnection read, SqliteConnection write, List<(long Archive, List<ArchiveExtraction> Found)> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var old = new Dictionary<(long, int, string, string), ArchiveExtraction>();
        using (var q = read.CreateCommand())
        {
            q.CommandText = $"""
                SELECT ArchiveId, RootId, BaseRelPath, Strip, Fingerprint, VerifiedAt, VerifiedOk, VerifiedBad, Unverified, BadPaths, Id
                FROM ArchiveExtractions WHERE ArchiveId IN ({string.Join(",", batch.Select(b => b.Archive))})
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                old[(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3))] = new ArchiveExtraction
                {
                    Id = r.GetInt64(10),
                    BaseRelPath = "",
                    Strip = "",
                    FolderRelPath = "",
                    Fingerprint = r.GetString(4),
                    VerifiedAt = r.IsDBNull(5) ? null : r.GetInt64(5),
                    VerifiedOk = r.GetInt32(6),
                    VerifiedBad = r.GetInt32(7),
                    Unverified = r.GetInt32(8),
                    BadPaths = r.IsDBNull(9) ? null : r.GetString(9),
                };
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var tx = write.BeginTransaction();
        using var del = write.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM ArchiveExtractions WHERE ArchiveId = $a";
        del.Parameters.Add(new SqliteParameter("$a", null));
        using var ins = write.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO ArchiveExtractions (Id, ArchiveId, State, RootId, BaseRelPath, Strip, FolderId, FolderRelPath, Files, Matched, Missing, Smaller, Differs,
                Bytes, MatchedBytes, Fingerprint, FoundAt, VerifiedAt, VerifiedOk, VerifiedBad, Unverified, BadPaths)
            VALUES ($id, $a, $state, $root, $base, $strip, $folder, $folderPath, $files, $matched, $missing, $smaller, $differs,
                $bytes, $matchedBytes, $fp, $now, $vAt, $vOk, $vBad, $vUn, $bad)
            """;
        foreach (var p in new[] { "$id", "$a", "$state", "$root", "$base", "$strip", "$folder", "$folderPath", "$files", "$matched", "$missing", "$smaller", "$differs",
                     "$bytes", "$matchedBytes", "$fp", "$vAt", "$vOk", "$vBad", "$vUn", "$bad" })
        {
            ins.Parameters.Add(new SqliteParameter(p, null));
        }

        ins.Parameters.AddWithValue("$now", now);
        foreach (var (archive, found) in batch)
        {
            del.Parameters["$a"].Value = archive;
            del.ExecuteNonQuery();
            foreach (var x in found)
            {
                var same = old.GetValueOrDefault((archive, x.RootId, x.BaseRelPath, x.Strip));
                var kept = same is not null && same.Fingerprint == x.Fingerprint ? same : null;
                ins.Parameters["$id"].Value = (object?)same?.Id ?? DBNull.Value;
                ins.Parameters["$a"].Value = archive;
                ins.Parameters["$state"].Value = (int)x.State;
                ins.Parameters["$root"].Value = x.RootId;
                ins.Parameters["$base"].Value = x.BaseRelPath;
                ins.Parameters["$strip"].Value = x.Strip;
                ins.Parameters["$folder"].Value = (object?)x.FolderId ?? DBNull.Value;
                ins.Parameters["$folderPath"].Value = x.FolderRelPath;
                ins.Parameters["$files"].Value = x.Files;
                ins.Parameters["$matched"].Value = x.Matched;
                ins.Parameters["$missing"].Value = x.Missing;
                ins.Parameters["$smaller"].Value = x.Smaller;
                ins.Parameters["$differs"].Value = x.Differs;
                ins.Parameters["$bytes"].Value = x.Bytes;
                ins.Parameters["$matchedBytes"].Value = x.MatchedBytes;
                ins.Parameters["$fp"].Value = x.Fingerprint;
                ins.Parameters["$vAt"].Value = (object?)kept?.VerifiedAt ?? DBNull.Value;
                ins.Parameters["$vOk"].Value = kept?.VerifiedOk ?? 0;
                ins.Parameters["$vBad"].Value = kept?.VerifiedBad ?? 0;
                ins.Parameters["$vUn"].Value = kept?.Unverified ?? 0;
                ins.Parameters["$bad"].Value = (object?)kept?.BadPaths ?? DBNull.Value;
                ins.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    /// <summary>Index lookups used while matching (prepared once per connection).</summary>
    private sealed class Finder : IDisposable
    {
        private readonly SqliteCommand _byName;
        private readonly SqliteCommand _at;

        public readonly record struct Found(long Id, string RelPath, long Size, long MTime, bool IsDir);

        public Finder(SqliteConnection c)
        {
            // The covering size index narrows by size; the name is checked on the few rows left.
            _byName = c.CreateCommand();
            _byName.CommandText = $"SELECT RootId, RelPath, Name FROM Entries WHERE IsDir = 0 AND Size = $s AND IsSymlink = 0 AND Name = $n LIMIT {MaxHitsPerAnchor * 2}";
            _byName.Parameters.Add(new SqliteParameter("$s", null));
            _byName.Parameters.Add(new SqliteParameter("$n", null));
            _at = c.CreateCommand();
            _at.CommandText = "SELECT Id, Size, MTime, IsDir, RelPath FROM Entries WHERE RootId = $r AND RelPath = $p AND IsSymlink = 0";
            _at.Parameters.Add(new SqliteParameter("$r", null));
            _at.Parameters.Add(new SqliteParameter("$p", null));
        }

        public void Dispose()
        {
            _byName.Dispose();
            _at.Dispose();
        }

        public List<(int RootId, string RelPath)> ByNameAndSize(string name, long size)
        {
            var list = new List<(int, string)>();
            foreach (var n in Variants(name))
            {
                _byName.Parameters["$s"].Value = size;
                _byName.Parameters["$n"].Value = n;
                using var r = _byName.ExecuteReader();
                // Name compares without case in SQLite; the path must match exactly.
                while (r.Read() && list.Count < MaxHitsPerAnchor)
                {
                    if (r.GetString(2) == n)
                    {
                        var rel = r.GetString(1);
                        list.Add((r.GetInt32(0), n == name ? rel : rel[..^n.Length] + name));
                    }
                }
            }

            return list;
        }

        public Found? At(int rootId, string relPath)
        {
            foreach (var p in Variants(relPath))
            {
                _at.Parameters["$r"].Value = rootId;
                _at.Parameters["$p"].Value = p;
                using var r = _at.ExecuteReader();
                if (r.Read())
                {
                    return new Found(r.GetInt64(0), r.GetString(4), r.GetInt64(1), r.GetInt64(2), r.GetBoolean(3));
                }
            }

            return null;
        }

        /// <summary>The name as stored, and its other Unicode form: macOS zips store decomposed names, Linux tools keep the bytes they get.</summary>
        private static IEnumerable<string> Variants(string s)
        {
            yield return s;
            if (!s.IsNormalized(NormalizationForm.FormC))
            {
                yield return s.Normalize(NormalizationForm.FormC);
            }
            else if (!s.IsNormalized(NormalizationForm.FormD))
            {
                yield return s.Normalize(NormalizationForm.FormD);
            }
        }
    }
}
