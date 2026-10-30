using ANalyze.Infrastructure.Roots;
using Microsoft.Data.Sqlite;

namespace ANalyze.Integrations.Backup;

/// <summary>States of <c>BackupFiles</c> rows.</summary>
public static class BackupStates
{
    /// <summary>File: the parent has a file at that path. Folder: at least 80% of its direct content is synchronized, or it is identical to the parent's.</summary>
    public const string Synced = "synced";

    /// <summary>File in a synchronized folder whose copy on the parent differs in size or content hash.</summary>
    public const string Corrupt = "corrupt";

    /// <summary>File in a synchronized folder that the parent does not have.</summary>
    public const string Missing = "missing";

    /// <summary>File not on the parent outside synchronized folders, or a folder that is not synchronized.</summary>
    public const string Unsynced = "unsynced";

    public static readonly string[] Files = [Synced, Corrupt, Missing, Unsynced];

    /// <summary>Share of a folder's direct content that must be synchronized for the folder to count as synchronized.</summary>
    public const double FolderThreshold = 0.8;
}

/// <summary>Where a parent root lands here.</summary>
public sealed record BackupTarget(int ParentRootId, string ParentPath, string LocalPath, int? RootId, string? RelPath, long? EntryId);

public sealed record BackupSyncResult(
    IReadOnlyList<BackupTarget> Targets,
    IReadOnlyList<PathMapping> Mappings,
    IReadOnlyList<PathMapping> Detected,
    IReadOnlyDictionary<string, long> Files,
    long Folders,
    long SyncedFolders,
    long Unmapped);

/// <summary>
/// Compares the local files and folders inside the backup folders with the parent ANalyze (the backup server) and replaces the
/// <c>BackupFiles</c> table. Rules:
/// <list type="bullet">
/// <item>a file is synchronized when the parent has a file at the same (mapped) path;</item>
/// <item>a folder is synchronized when at least 80% of its direct files and sub-folders (by count) are synchronized, or when it is
/// identical to the parent's folder (same entries, every file the same size and hash — this also covers empty folders);</item>
/// <item>inside a synchronized folder, a file whose parent copy differs in size or hash is corrupt, and a file the parent lacks is missing.</item>
/// </list>
/// Paths are translated with the mappings (parent path → local path); a parent root whose sample files are mostly not found at the mapped
/// path gets a mapping detected from file names and sizes.
/// </summary>
public sealed class BackupSync(SqliteConnection conn, RootRegistry roots)
{
    public delegate Task<ExportPage> FetchPage(int rootId, string? after, CancellationToken ct);

    private const int Sample = 200;

    public static bool FolderSynced(int children, int synced, bool identical) =>
        identical || (children > 0 && synced >= children * BackupStates.FolderThreshold);

    /// <summary>Different size, or both content hashes known and different.</summary>
    public static bool Differs(long size, string? hash, long parentSize, string? parentHash) =>
        size != parentSize || (hash is not null && parentHash is not null && hash != parentHash);

    public static string FileState(bool onParent, bool differs, bool folderSynced) =>
        onParent ? (folderSynced && differs ? BackupStates.Corrupt : BackupStates.Synced) : (folderSynced ? BackupStates.Missing : BackupStates.Unsynced);

    private sealed class Node
    {
        public long Id;
        public long? ParentId;
        public bool IsDir;
        public int Depth;
        public bool OnParent;
        public bool Differs;
        public bool Verified;
        public long? ParentSize;
        public string? ParentHash;
        public int ParentChildren;
        public int Children;
        public int SyncedChildren;
        public bool AllIdentical = true;
        public bool Identical;
        public bool Synced;
        public string State = BackupStates.Unsynced;
    }

    public async Task<BackupSyncResult> RunAsync(ExportInfo info, IReadOnlyList<PathMapping> configured, FetchPage fetch, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var mappings = configured.ToList();
        var detected = new List<PathMapping>();
        long seen = 0, unmapped = 0;
        var total = info.Roots.Sum(r => r.Files);

        Exec("DROP TABLE IF EXISTS temp.ParentStage");
        Exec("CREATE TEMP TABLE ParentStage (RootId INTEGER NOT NULL, RelPath TEXT NOT NULL, ParentRel TEXT, IsDir INTEGER NOT NULL, Size INTEGER NOT NULL, Hash TEXT)");
        Exec("CREATE UNIQUE INDEX temp.IX_ParentStage_Path ON ParentStage (RootId, RelPath)");
        Exec("CREATE INDEX temp.IX_ParentStage_Parent ON ParentStage (RootId, ParentRel)");
        using var ins = Cmd("INSERT OR IGNORE INTO ParentStage (RootId, RelPath, ParentRel, IsDir, Size, Hash) VALUES ($r, $p, $parent, $d, $s, $h)",
            "$r", "$p", "$parent", "$d", "$s", "$h");

        // 1. The parent's tree, at the local paths it maps to.
        var targets = new List<BackupTarget>();
        foreach (var root in info.Roots)
        {
            var parentRoot = root.Path.TrimEnd('/');
            string Abs(string rel) => rel.Length == 0 ? parentRoot : parentRoot + "/" + rel;

            var page = await fetch(root.Id, null, ct);
            var sample = page.Items.Where(f => !f.D).Take(Sample).ToList();
            if (sample.Count > 0 && Found(sample, Abs, mappings) * 5 < sample.Count)
            {
                // Mostly not where the mappings say: learn the prefix from files found by name and size.
                var matcher = new PathMatcher(conn, roots, mappings);
                matcher.Detect(sample.Select(f => (Abs(f.P), f.S)), Sample);
                var better = matcher.Detected.Where(d => configured.All(c => c.From.TrimEnd('/') != d.From.TrimEnd('/'))).ToList();
                if (better.Count > 0 && Found(sample, Abs, [.. better, .. mappings]) > Found(sample, Abs, mappings))
                {
                    detected.AddRange(better);
                    mappings.InsertRange(0, better);
                }
            }

            // Backed-up folders: the root itself when it maps to a folder here, else every mapping that starts inside it
            // (the backup may live in a sub-folder of the server's root, e.g. /backup/nas1 → /library).
            var starts = new List<string> { parentRoot };
            if (Resolve(IntegrationSettings.MapPath(parentRoot, mappings)).Root is null)
            {
                starts = mappings.Select(m => m.From.TrimEnd('/'))
                    .Where(f => f.StartsWith(parentRoot + "/", StringComparison.Ordinal) && Resolve(IntegrationSettings.MapPath(f, mappings)).Root is not null)
                    .Distinct().DefaultIfEmpty(parentRoot).ToList();
            }

            foreach (var start in starts)
            {
                var localPath = IntegrationSettings.MapPath(start, mappings);
                var (tRoot, tRel) = Resolve(localPath);
                targets.Add(new BackupTarget(root.Id, start, localPath, tRoot, tRel, tRoot is { } tr ? DirId(tr, tRel!) : null));
                if (tRoot is { } t)
                {
                    // The folder itself exists on the parent; it is nobody's child here.
                    Insert(ins, t, tRel!, null, true, 0, null);
                }
            }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                using (var tx = conn.BeginTransaction())
                {
                    ins.Transaction = tx;
                    foreach (var f in page.Items)
                    {
                        seen += f.D ? 0 : 1;
                        if (Resolve(IntegrationSettings.MapPath(Abs(f.P), mappings)) is ({ } r, { } rel))
                        {
                            Insert(ins, r, rel, Parent(rel), f.D, f.S, f.H);
                        }
                        else
                        {
                            unmapped++;
                        }
                    }

                    tx.Commit();
                    ins.Transaction = null;
                }

                progress.Report(new IntegrationProgress("reading the parent", seen, total, root.Path));
                if (page.Next is null)
                {
                    break;
                }

                page = await fetch(root.Id, page.Next, ct);
            }
        }

        // 2. Local entries inside the backup folders (outermost targets only), deepest first so folders see their children's results.
        progress.Report(new IntegrationProgress("comparing", 0, 0));
        var scopes = targets.Where(t => t.EntryId is not null).Select(t => (Root: t.RootId!.Value, Rel: t.RelPath!)).Distinct().ToList();
        scopes = scopes.Where(s => !scopes.Any(o => o != s && o.Root == s.Root && (o.Rel.Length == 0 || s.Rel.StartsWith(o.Rel + "/", StringComparison.Ordinal)))).ToList();
        var nodes = new Dictionary<long, Node>();
        foreach (var (root, rel) in scopes)
        {
            using var q = conn.CreateCommand();
            var range = rel.Length == 0 ? "" : "AND (e.RelPath = $rel OR (e.RelPath >= $from AND e.RelPath < $to))";
            q.CommandText = $"""
                SELECT e.Id, e.ParentId, e.IsDir, e.Depth, CASE WHEN e.IsDir = 1 THEN e.TotalSize ELSE e.Size END, e.FullHash,
                       p.IsDir, p.Size, p.Hash,
                       CASE WHEN e.IsDir = 1 THEN (SELECT COUNT(*) FROM ParentStage c WHERE c.RootId = e.RootId AND c.ParentRel = e.RelPath) ELSE 0 END
                FROM Entries e LEFT JOIN ParentStage p ON p.RootId = e.RootId AND p.RelPath = e.RelPath
                WHERE e.RootId = $r {range} AND e.IsSymlink = 0
                """;
            q.Parameters.AddWithValue("$r", root);
            q.Parameters.AddWithValue("$rel", rel);
            q.Parameters.AddWithValue("$from", rel + "/");
            q.Parameters.AddWithValue("$to", rel + "0");
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var isDir = r.GetBoolean(2);
                var onParent = !r.IsDBNull(6) && r.GetBoolean(6) == isDir;
                var size = r.GetInt64(4);
                var hash = r.IsDBNull(5) ? null : r.GetString(5);
                var pSize = onParent ? r.GetInt64(7) : 0;
                var pHash = onParent && !r.IsDBNull(8) ? r.GetString(8) : null;
                nodes[r.GetInt64(0)] = new Node
                {
                    Id = r.GetInt64(0),
                    ParentId = r.IsDBNull(1) ? null : r.GetInt64(1),
                    IsDir = isDir,
                    Depth = r.GetInt32(3),
                    OnParent = onParent,
                    ParentSize = onParent ? pSize : null,
                    ParentHash = isDir ? null : pHash,
                    Differs = onParent && !isDir && Differs(size, hash, pSize, pHash),
                    Verified = onParent && !isDir && hash is not null && hash == pHash,
                    ParentChildren = r.GetInt32(9),
                };
            }
        }

        foreach (var n in nodes.Values.OrderByDescending(n => n.Depth))
        {
            if (n.IsDir)
            {
                n.Identical = n.OnParent && n.AllIdentical && n.ParentChildren == n.Children;
                n.Synced = FolderSynced(n.Children, n.SyncedChildren, n.Identical);
                n.State = n.Synced ? BackupStates.Synced : BackupStates.Unsynced;
            }
            else
            {
                n.Identical = n.OnParent && !n.Differs;
            }

            if (n.ParentId is { } pid && nodes.TryGetValue(pid, out var parent))
            {
                parent.Children++;
                if (n.IsDir ? n.Synced : n.OnParent)
                {
                    parent.SyncedChildren++;
                }

                parent.AllIdentical &= n.Identical;
            }
        }

        var files = BackupStates.Files.ToDictionary(s => s, _ => 0L);
        foreach (var n in nodes.Values.Where(n => !n.IsDir))
        {
            var folderSynced = n.ParentId is { } pid && nodes.TryGetValue(pid, out var p) && p.Synced;
            n.State = FileState(n.OnParent, n.Differs, folderSynced);
            files[n.State]++;
        }

        // 3. Replace the table.
        Exec("DROP TABLE IF EXISTS temp.BackupResult");
        Exec("CREATE TEMP TABLE BackupResult (Id INTEGER PRIMARY KEY, State TEXT NOT NULL, ParentSize INTEGER, ParentHash TEXT, Verified INTEGER NOT NULL, Children INTEGER NOT NULL, Synced INTEGER NOT NULL)");
        using (var tx = conn.BeginTransaction())
        {
            using var res = Cmd("INSERT INTO BackupResult VALUES ($id, $state, $psize, $phash, $v, $c, $s)", "$id", "$state", "$psize", "$phash", "$v", "$c", "$s");
            res.Transaction = tx;
            foreach (var n in nodes.Values)
            {
                res.Parameters["$id"].Value = n.Id;
                res.Parameters["$state"].Value = n.State;
                res.Parameters["$psize"].Value = (object?)n.ParentSize ?? DBNull.Value;
                res.Parameters["$phash"].Value = (object?)n.ParentHash ?? DBNull.Value;
                res.Parameters["$v"].Value = n.Verified;
                res.Parameters["$c"].Value = n.Children;
                res.Parameters["$s"].Value = n.SyncedChildren;
                res.ExecuteNonQuery();
            }

            Exec("DELETE FROM BackupFiles", tx);
            Exec("""
                INSERT INTO BackupFiles (State, IsDir, EntryId, LocalRootId, LocalRelPath, Size, ParentSize, ParentHash, Verified, Children, SyncedChildren)
                SELECT b.State, e.IsDir, e.Id, e.RootId, e.RelPath, CASE WHEN e.IsDir = 1 THEN e.TotalSize ELSE e.Size END, b.ParentSize, b.ParentHash, b.Verified, b.Children, b.Synced
                FROM BackupResult b JOIN Entries e ON e.Id = b.Id
                """, tx);
            tx.Commit();
        }

        Exec("DROP TABLE temp.BackupResult");
        Exec("DROP TABLE temp.ParentStage");
        var folders = nodes.Values.Count(n => n.IsDir);
        return new BackupSyncResult(targets, mappings, detected, files, folders, nodes.Values.Count(n => n.IsDir && n.Synced), unmapped);
    }

    /// <summary>How many of the sample files exist as files at their mapped local path.</summary>
    private int Found(IEnumerable<ExportFile> sample, Func<string, string> abs, IReadOnlyList<PathMapping> mappings)
    {
        using var find = Cmd("SELECT 1 FROM Entries WHERE RootId = $r AND RelPath = $p AND IsDir = 0", "$r", "$p");
        var n = 0;
        foreach (var f in sample)
        {
            if (Resolve(IntegrationSettings.MapPath(abs(f.P), mappings)) is ({ } r, { } rel))
            {
                find.Parameters["$r"].Value = r;
                find.Parameters["$p"].Value = rel;
                n += find.ExecuteScalar() is null ? 0 : 1;
            }
        }

        return n;
    }

    private (int? Root, string? Rel) Resolve(string path) => Resolve(roots, path);

    /// <summary>Local root and relative path for an absolute path, by the longest root that contains it (never the trash).</summary>
    internal static (int? Root, string? Rel) Resolve(RootRegistry roots, string path)
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

    /// <summary>The parent path of a local path, through the mappings in reverse; null when no mapping leads there.</summary>
    public static string? ToParentPath(string localPath, IReadOnlyList<PathMapping> mappings)
    {
        var best = mappings
            .Select(m => (From: m.From.TrimEnd('/'), To: m.To.TrimEnd('/')))
            .Where(m => m.To.Length > 0 && (localPath == m.To || localPath.StartsWith(m.To + "/", StringComparison.Ordinal)))
            .OrderByDescending(m => m.To.Length)
            .FirstOrDefault();
        return best.To is null ? null : best.From + localPath[best.To.Length..];
    }

    private static void Insert(SqliteCommand ins, int root, string rel, string? parent, bool dir, long size, string? hash)
    {
        ins.Parameters["$r"].Value = root;
        ins.Parameters["$p"].Value = rel;
        ins.Parameters["$parent"].Value = (object?)parent ?? DBNull.Value;
        ins.Parameters["$d"].Value = dir;
        ins.Parameters["$s"].Value = size;
        ins.Parameters["$h"].Value = (object?)hash ?? DBNull.Value;
        ins.ExecuteNonQuery();
    }

    internal static string Parent(string rel) => rel.LastIndexOf('/') is var i and > 0 ? rel[..i] : "";

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
}
