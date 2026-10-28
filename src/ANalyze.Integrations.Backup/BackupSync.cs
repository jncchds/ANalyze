using ANalyze.Infrastructure.Roots;
using Microsoft.Data.Sqlite;

namespace ANalyze.Integrations.Backup;

/// <summary>States of <c>BackupFiles</c> rows.</summary>
public static class BackupStates
{
    /// <summary>Same content: both full hashes are equal (or both files are empty).</summary>
    public const string Verified = "verified";

    /// <summary>Same size and modification time; content not compared because a hash is missing on one side.</summary>
    public const string Matched = "matched";

    /// <summary>Another version: size or modification time differ.</summary>
    public const string Outdated = "outdated";

    /// <summary>Same size and modification time but different content: one copy is damaged.</summary>
    public const string Corrupt = "corrupt";

    /// <summary>On the parent, not here.</summary>
    public const string Missing = "missing";

    /// <summary>Here, not on the parent (deleted there, or never part of it).</summary>
    public const string Extra = "extra";

    public static readonly string[] All = [Verified, Matched, Outdated, Corrupt, Missing, Extra];

    /// <summary>Corrupt side: the parent's copy changed.</summary>
    public const string SideSource = "source";

    /// <summary>Corrupt side: this instance's copy changed.</summary>
    public const string SideBackup = "backup";
}

/// <summary>The local copy of a parent file, as scanned here.</summary>
public sealed record LocalFile(long Id, long Size, long MTime, string? Hash, long? CorruptAt);

/// <summary>A file's row from the previous sync, used to tell which side of a corrupt pair changed.</summary>
public sealed record PreviousRow(string State, string? Hash, string? LocalHash, string? Side);

/// <summary>Where a parent root lands here.</summary>
public sealed record BackupTarget(int ParentRootId, string ParentPath, string LocalPath, int? RootId, string? RelPath, long? EntryId);

public sealed record BackupSyncResult(IReadOnlyList<BackupTarget> Targets, IReadOnlyList<PathMapping> Detected, IReadOnlyDictionary<string, long> Counts, long Files, long Unmapped);

/// <summary>
/// Compares the parent's file list with the local index and replaces the <c>BackupFiles</c> table: every parent file gets a state
/// (verified, matched, outdated, corrupt, missing) and every local file inside a backup target that the parent does not have is "extra".
/// Paths are translated with the configured mappings (parent path → local path); a parent root whose sample files are mostly not found
/// at the mapped path gets a mapping detected from file names and sizes.
/// </summary>
public sealed class BackupSync(SqliteConnection conn, RootRegistry roots, int mtimeTolerance, bool compareHashes = true)
{
    public delegate Task<ExportPage> FetchPage(int rootId, string? after, CancellationToken ct);

    private const int Sample = 200;

    /// <summary>Decides the state of one parent file. <paramref name="previous"/> is only asked for corrupt files.</summary>
    public static (string State, string? Side) Classify(ExportFile p, LocalFile? l, int tolerance, bool compareHashes, Func<PreviousRow?> previous)
    {
        if (l is null)
        {
            return (BackupStates.Missing, null);
        }

        var sameSize = p.S == l.Size;
        var sameTime = Math.Abs(p.M - l.MTime) <= tolerance;
        if (sameSize && p.S == 0)
        {
            return (BackupStates.Verified, null);
        }

        if (compareHashes && p.H is not null && l.Hash is not null)
        {
            if (p.H == l.Hash)
            {
                return (BackupStates.Verified, null);
            }

            return sameSize && sameTime ? (BackupStates.Corrupt, SideOf(p, l, previous())) : (BackupStates.Outdated, null);
        }

        return sameSize && sameTime ? (BackupStates.Matched, null) : (BackupStates.Outdated, null);
    }

    /// <summary>Which copy changed: the one whose own verification flagged it, else the one that no longer has the content both shared before.</summary>
    private static string? SideOf(ExportFile p, LocalFile l, PreviousRow? prev)
    {
        if (p.C is not null && l.CorruptAt is null)
        {
            return BackupStates.SideSource;
        }

        if (l.CorruptAt is not null && p.C is null)
        {
            return BackupStates.SideBackup;
        }

        if (prev is null)
        {
            return null;
        }

        if (prev.State == BackupStates.Verified && prev.Hash is { } agreed)
        {
            return p.H == agreed ? BackupStates.SideBackup : l.Hash == agreed ? BackupStates.SideSource : null;
        }

        return prev.State == BackupStates.Corrupt && prev.Hash == p.H && prev.LocalHash == l.Hash ? prev.Side : null;
    }

    public async Task<BackupSyncResult> RunAsync(ExportInfo info, IReadOnlyList<PathMapping> configured, FetchPage fetch, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var mappings = configured.ToList();
        var detected = new List<PathMapping>();
        var counts = BackupStates.All.ToDictionary(s => s, _ => 0L);
        long files = 0, unmapped = 0;
        var total = info.Roots.Sum(r => r.Files);

        Exec("DROP TABLE IF EXISTS temp.BackupStage");
        Exec("""
            CREATE TEMP TABLE BackupStage (State TEXT NOT NULL, EntryId INTEGER, LocalRootId INTEGER, LocalRelPath TEXT, SourcePath TEXT,
                Size INTEGER NOT NULL, MTime INTEGER NOT NULL, Hash TEXT, LocalSize INTEGER, LocalMTime INTEGER, LocalHash TEXT, Side TEXT)
            """);
        Exec("CREATE INDEX temp.IX_BackupStage_EntryId ON BackupStage (EntryId)");

        using var find = Cmd("SELECT Id, Size, MTime, FullHash, CorruptAt, IsDir FROM Entries WHERE RootId = $r AND RelPath = $p", "$r", "$p");
        using var prev = Cmd("SELECT State, Hash, LocalHash, Side FROM BackupFiles WHERE EntryId = $e LIMIT 1", "$e");
        using var ins = Cmd("""
            INSERT INTO BackupStage (State, EntryId, LocalRootId, LocalRelPath, SourcePath, Size, MTime, Hash, LocalSize, LocalMTime, LocalHash, Side)
            VALUES ($state, $entry, $root, $rel, $src, $size, $mtime, $hash, $lsize, $lmtime, $lhash, $side)
            """, "$state", "$entry", "$root", "$rel", "$src", "$size", "$mtime", "$hash", "$lsize", "$lmtime", "$lhash", "$side");

        var targets = new List<BackupTarget>();
        foreach (var root in info.Roots)
        {
            var parentRoot = root.Path.TrimEnd('/');
            string Abs(string rel) => rel.Length == 0 ? parentRoot : parentRoot + "/" + rel;

            var page = await fetch(root.Id, null, ct);
            if (page.Items.Count > 0 && Found(page.Items.Take(Sample), Abs, mappings) * 5 < Math.Min(Sample, page.Items.Count))
            {
                // Mostly not where the mappings say: learn the prefix from files found by name and size.
                var matcher = new PathMatcher(conn, roots, mappings);
                matcher.Detect(page.Items.Take(Sample).Select(f => (Abs(f.P), f.S)), Sample);
                var better = matcher.Detected.Where(d => configured.All(c => c.From.TrimEnd('/') != d.From.TrimEnd('/'))).ToList();
                if (better.Count > 0 && Found(page.Items.Take(Sample), Abs, [.. better, .. mappings]) > Found(page.Items.Take(Sample), Abs, mappings))
                {
                    detected.AddRange(better);
                    mappings.InsertRange(0, better);
                }
            }

            var localRoot = IntegrationSettings.MapPath(parentRoot, mappings);
            var (tRoot, tRel) = Resolve(localRoot);
            targets.Add(new BackupTarget(root.Id, root.Path, localRoot, tRoot, tRel, tRoot is { } tr ? DirId(tr, tRel!) : null));

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using (var tx = conn.BeginTransaction())
                {
                    find.Transaction = tx;
                    prev.Transaction = tx;
                    ins.Transaction = tx;
                    foreach (var f in page.Items)
                    {
                        var abs = Abs(f.P);
                        var (lRoot, lRel) = Resolve(IntegrationSettings.MapPath(abs, mappings));
                        LocalFile? local = null;
                        if (lRoot is { } lr)
                        {
                            local = Find(find, lr, lRel!);
                        }
                        else
                        {
                            unmapped++;
                        }

                        var (state, side) = Classify(f, local, mtimeTolerance, compareHashes, () => Previous(prev, local!.Id));
                        counts[state]++;
                        files++;
                        Set(ins, "$state", state);
                        Set(ins, "$entry", local?.Id);
                        Set(ins, "$root", lRoot);
                        Set(ins, "$rel", lRel);
                        Set(ins, "$src", abs);
                        Set(ins, "$size", f.S);
                        Set(ins, "$mtime", f.M);
                        Set(ins, "$hash", f.H);
                        Set(ins, "$lsize", local?.Size);
                        Set(ins, "$lmtime", local?.MTime);
                        Set(ins, "$lhash", local?.Hash);
                        Set(ins, "$side", side);
                        ins.ExecuteNonQuery();
                    }

                    tx.Commit();
                }

                progress.Report(new IntegrationProgress("comparing", files, total, root.Path));
                if (page.Next is null)
                {
                    break;
                }

                page = await fetch(root.Id, page.Next, ct);
            }
        }

        // Files inside the backup folders the parent does not have. Nested targets are covered by the outer one.
        progress.Report(new IntegrationProgress("files only here", files, total));
        var scopes = targets.Where(t => t.RootId is not null && t.EntryId is not null).Select(t => (Root: t.RootId!.Value, Rel: t.RelPath!)).Distinct().ToList();
        scopes = scopes.Where(s => !scopes.Any(o => o != s && o.Root == s.Root && (o.Rel.Length == 0 || s.Rel.StartsWith(o.Rel + "/", StringComparison.Ordinal)))).ToList();
        foreach (var (root, rel) in scopes)
        {
            using var extra = conn.CreateCommand();
            var range = rel.Length == 0 ? "" : "AND e.RelPath >= $from AND e.RelPath < $to";
            extra.CommandText = $"""
                INSERT INTO BackupStage (State, EntryId, LocalRootId, LocalRelPath, Size, MTime, LocalSize, LocalMTime, LocalHash)
                SELECT '{BackupStates.Extra}', e.Id, e.RootId, e.RelPath, e.Size, e.MTime, e.Size, e.MTime, e.FullHash FROM Entries e
                WHERE e.RootId = $r {range} AND e.IsDir = 0 AND e.IsSymlink = 0
                  AND NOT EXISTS (SELECT 1 FROM BackupStage s WHERE s.EntryId = e.Id)
                """;
            extra.Parameters.AddWithValue("$r", root);
            extra.Parameters.AddWithValue("$from", rel + "/");
            extra.Parameters.AddWithValue("$to", rel + "0");
            counts[BackupStates.Extra] += extra.ExecuteNonQuery();
        }

        using (var tx = conn.BeginTransaction())
        {
            Exec("DELETE FROM BackupFiles", tx);
            Exec("""
                INSERT INTO BackupFiles (State, EntryId, LocalRootId, LocalRelPath, SourcePath, Size, MTime, Hash, LocalSize, LocalMTime, LocalHash, Side)
                SELECT State, EntryId, LocalRootId, LocalRelPath, SourcePath, Size, MTime, Hash, LocalSize, LocalMTime, LocalHash, Side FROM BackupStage
                """, tx);
            tx.Commit();
        }

        Exec("DROP TABLE temp.BackupStage");
        return new BackupSyncResult(targets, detected, counts, files, unmapped);
    }

    /// <summary>How many of the files exist at their mapped local path.</summary>
    private int Found(IEnumerable<ExportFile> sample, Func<string, string> abs, IReadOnlyList<PathMapping> mappings)
    {
        using var find = Cmd("SELECT Id, Size, MTime, FullHash, CorruptAt, IsDir FROM Entries WHERE RootId = $r AND RelPath = $p", "$r", "$p");
        var n = 0;
        foreach (var f in sample)
        {
            if (Resolve(IntegrationSettings.MapPath(abs(f.P), mappings)) is ({ } r, { } rel) && Find(find, r, rel) is not null)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>Local root and relative path for an absolute path, by the longest root that contains it.</summary>
    private (int? Root, string? Rel) Resolve(string path)
    {
        path = path.Length > 1 ? path.TrimEnd('/') : path;
        var root = roots.All.Where(r => !r.Missing && RootRegistry.IsUnder(r.Path, path)).OrderByDescending(r => r.Path.Length).FirstOrDefault();
        if (root is null)
        {
            return (null, null);
        }

        var prefix = root.Path.TrimEnd('/');
        var rel = path.Length <= prefix.Length ? "" : path[(prefix.Length + 1)..];
        // The trash is not part of the backup.
        return rel == RootRegistry.TrashFolder || rel.StartsWith(RootRegistry.TrashFolder + "/", StringComparison.Ordinal) ? (null, null) : (root.Id, rel);
    }

    private static LocalFile? Find(SqliteCommand find, int root, string rel)
    {
        find.Parameters["$r"].Value = root;
        find.Parameters["$p"].Value = rel;
        using var r = find.ExecuteReader();
        if (!r.Read() || r.GetBoolean(5))
        {
            return null;
        }

        return new LocalFile(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4));
    }

    private static PreviousRow? Previous(SqliteCommand prev, long entryId)
    {
        prev.Parameters["$e"].Value = entryId;
        using var r = prev.ExecuteReader();
        return r.Read() ? new PreviousRow(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)) : null;
    }

    private long? DirId(int root, string rel)
    {
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT Id FROM Entries WHERE RootId = $r AND RelPath = $p AND IsDir = 1";
        q.Parameters.AddWithValue("$r", root);
        q.Parameters.AddWithValue("$p", rel);
        return q.ExecuteScalar() as long?;
    }

    private SqliteCommand Cmd(string sql, params string[] names)
    {
        var c = conn.CreateCommand();
        c.CommandText = sql;
        foreach (var n in names)
        {
            c.Parameters.Add(new SqliteParameter(n, null));
        }

        return c;
    }

    private void Exec(string sql, SqliteTransaction? tx = null)
    {
        using var c = conn.CreateCommand();
        c.Transaction = tx;
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static void Set(SqliteCommand c, string name, object? value) => c.Parameters[name].Value = value ?? DBNull.Value;
}
