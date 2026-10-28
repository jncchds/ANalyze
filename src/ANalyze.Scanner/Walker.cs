using System.Text.RegularExpressions;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Roots;
using Microsoft.Data.Sqlite;

namespace ANalyze.Scanner;

/// <summary>
/// Walks a directory tree and mirrors it into the Entries table incrementally: unchanged entries
/// (same type, size, mtime and inode) are not touched, changed files are reset for detection and hashing,
/// vanished ones are deleted with their subtree. Directory aggregates are computed bottom-up on the way back.
/// </summary>
internal sealed class Walker : IDisposable
{
    private const int CommitEvery = 5000;

    private readonly SqliteConnection _db;
    private readonly ScanContext _ctx;
    private readonly int _rootId;
    private readonly string _rootPath;
    private readonly bool _crossFs;
    private readonly IgnoreMatcher _ignore;
    private SqliteTransaction _tx;
    private int _pending;

    private readonly SqliteCommand _selChildren;
    private readonly SqliteCommand _insert;
    private readonly SqliteCommand _updateFile;
    private readonly SqliteCommand _updateATime;
    private readonly SqliteCommand _updateDir;
    private readonly SqliteCommand _deleteOne;
    private readonly SqliteCommand _deleteTree;

    public Walker(SqliteConnection db, ScanContext ctx, int rootId, string rootPath, bool crossFs, IgnoreMatcher ignore)
    {
        _db = db;
        _ctx = ctx;
        _rootId = rootId;
        _rootPath = rootPath;
        _crossFs = crossFs;
        _ignore = ignore;
        _tx = db.BeginTransaction();

        _selChildren = Cmd("SELECT Id, Name, IsDir, IsSymlink, Size, MTime, Inode, ATime, TotalSize, TotalAlloc, FileCount, DirCount FROM Entries WHERE ParentId = $p",
            "$p");
        _insert = Cmd("""
            INSERT INTO Entries (RootId, ParentId, Name, RelPath, Depth, IsDir, IsSymlink, LinkTarget, BrokenLink, Size, AllocSize, MTime, ATime, CTime,
                Inode, Dev, NLink, Ext, Mime, Kind, Detected, CategoryId, CategorySource, Tags, QuickHash, FullHash, Error, TotalSize, TotalAlloc, FileCount, DirCount)
            VALUES ($root, $parent, $name, $rel, $depth, $isDir, $isLink, $target, $broken, $size, $alloc, $mtime, $atime, $ctime,
                $inode, $dev, $nlink, $ext, NULL, 0, $detected, NULL, 0, NULL, NULL, NULL, 0, $size, $alloc, $files, 0);
            SELECT last_insert_rowid();
            """, "$root", "$parent", "$name", "$rel", "$depth", "$isDir", "$isLink", "$target", "$broken", "$size", "$alloc", "$mtime", "$atime", "$ctime",
            "$inode", "$dev", "$nlink", "$ext", "$detected", "$files");
        _updateFile = Cmd("""
            UPDATE Entries SET IsSymlink=$isLink, LinkTarget=$target, BrokenLink=$broken, Size=$size, AllocSize=$alloc, MTime=$mtime, ATime=$atime, CTime=$ctime,
                Inode=$inode, Dev=$dev, NLink=$nlink, Detected=$detected, QuickHash=NULL, FullHash=NULL, Error=0, TotalSize=$size, TotalAlloc=$alloc
            WHERE Id=$id
            """, "$isLink", "$target", "$broken", "$size", "$alloc", "$mtime", "$atime", "$ctime", "$inode", "$dev", "$nlink", "$detected", "$id");
        _updateATime = Cmd("UPDATE Entries SET ATime=$atime, NLink=$nlink WHERE Id=$id", "$atime", "$nlink", "$id");
        _updateDir = Cmd("""
            UPDATE Entries SET TotalSize=$size, TotalAlloc=$alloc, FileCount=$files, DirCount=$dirs, MTime=$mtime, ATime=$atime, CTime=$ctime,
                Inode=$inode, Dev=$dev, NLink=$nlink, Error=$error
            WHERE Id=$id
            """, "$size", "$alloc", "$files", "$dirs", "$mtime", "$atime", "$ctime", "$inode", "$dev", "$nlink", "$error", "$id");
        _deleteOne = Cmd("DELETE FROM Entries WHERE Id=$id", "$id");
        _deleteTree = Cmd("DELETE FROM Entries WHERE RootId=$root AND RelPath >= $from AND RelPath < $to", "$root", "$from", "$to");
    }

    public readonly record struct Agg(long Size, long Alloc, long Files, long Dirs, bool Error);

    private readonly record struct Existing(long Id, bool IsDir, bool IsSymlink, long Size, long MTime, long Inode, long ATime, Agg Agg);

    /// <summary>Creates the root directory entry if needed; returns its id.</summary>
    public long EnsureRootEntry(FileStat st)
    {
        using var find = Cmd("SELECT Id FROM Entries WHERE RootId=$r AND RelPath=''", "$r");
        find.Parameters["$r"].Value = _rootId;
        if (find.ExecuteScalar() is long id)
        {
            return id;
        }

        return Insert(null, Path.GetFileName(_rootPath), "", 0, st, isDir: true, linkTarget: null, broken: false);
    }

    /// <summary>Creates entries for directories between <paramref name="startRel"/> and <paramref name="targetRel"/> that exist on disk.</summary>
    public (long Id, string Rel, int Depth) EnsureDirChain(long startId, string startRel, int depth, string targetRel)
    {
        if (targetRel == startRel || (startRel.Length > 0 && !targetRel.StartsWith(startRel + "/", StringComparison.Ordinal)))
        {
            return (startId, startRel, depth);
        }

        var rest = startRel.Length == 0 ? targetRel : targetRel[(startRel.Length + 1)..];
        var (id, rel) = (startId, startRel);
        foreach (var name in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var childRel = rel.Length == 0 ? name : rel + "/" + name;
            var st = Native.LStat(RootRegistry.Combine(_rootPath, childRel));
            if (!st.IsDir)
            {
                break;
            }

            using var find = Cmd("SELECT Id FROM Entries WHERE RootId=$r AND RelPath=$p", "$r", "$p");
            find.Parameters["$r"].Value = _rootId;
            find.Parameters["$p"].Value = childRel;
            id = find.ExecuteScalar() is long existing ? existing : Insert(id, name, childRel, depth + 1, st, isDir: true, linkTarget: null, broken: false);
            rel = childRel;
            depth++;
        }

        return (id, rel, depth);
    }

    /// <summary>Scans one directory recursively and stores its aggregates.</summary>
    public Agg ScanDirectory(long dirId, string fullPath, string relPath, int depth, long rootDev)
    {
        var agg = Walk(dirId, fullPath, relPath, depth, rootDev);
        var st = Native.LStat(fullPath);
        UpdateDir(dirId, agg, st);
        Commit();
        return agg;
    }

    private Agg Walk(long dirId, string fullPath, string relPath, int depth, long rootDev)
    {
        _ctx.Ct.ThrowIfCancellationRequested();
        _ctx.DirsSeen++;
        _ctx.Current = relPath.Length == 0 ? "/" : relPath;
        _ctx.Report();

        var existing = LoadChildren(dirId);
        IEnumerable<string> names;
        try
        {
            names = Directory.EnumerateFileSystemEntries(fullPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            }).Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Unreadable directory: keep what we knew about it instead of deleting its contents.
            _ctx.Errors++;
            var kept = new Agg(0, 0, 0, 0, true);
            foreach (var c in existing.Values)
            {
                kept = Add(kept, c.IsDir ? c.Agg : new Agg(c.Size, c.Agg.Alloc, 1, 0, false), c.IsDir);
            }

            return kept;
        }

        var total = new Agg(0, 0, 0, 0, false);
        foreach (var name in names)
        {
            _ctx.Ct.ThrowIfCancellationRequested();
            var childRel = relPath.Length == 0 ? name : relPath + "/" + name;
            if (_ignore.IsIgnored(childRel, name, depth + 1))
            {
                continue;
            }

            var childFull = Path.Combine(fullPath, name);
            var st = Native.LStat(childFull);
            if (!st.Exists)
            {
                continue;
            }

            existing.Remove(name, out var old);
            var hadOld = old.Id != 0;

            if (st.IsDir)
            {
                if (hadOld && !old.IsDir)
                {
                    DeleteOne(old.Id);
                    hadOld = false;
                }

                long id;
                if (!hadOld)
                {
                    id = Insert(dirId, name, childRel, depth + 1, st, isDir: true, linkTarget: null, broken: false);
                    _ctx.Added++;
                }
                else
                {
                    id = old.Id;
                }

                Agg sub;
                if (!_crossFs && rootDev != 0 && st.Dev != rootDev)
                {
                    // Another filesystem mounted below the root: listed, not descended into.
                    sub = new Agg(0, 0, 0, 0, false);
                }
                else
                {
                    sub = Walk(id, childFull, childRel, depth + 1, rootDev);
                }

                if (!hadOld || old.Agg != sub || old.MTime != st.MTime)
                {
                    UpdateDir(id, sub, st);
                }

                total = Add(total, sub, isDir: true);
            }
            else
            {
                if (hadOld && old.IsDir)
                {
                    DeleteTree(old.Id, childRel);
                    hadOld = false;
                }

                string? target = null;
                var broken = false;
                if (st.IsSymlink)
                {
                    try
                    {
                        target = new FileInfo(childFull).LinkTarget;
                        broken = target is not null && !Path.Exists(Path.Combine(fullPath, target));
                    }
                    catch (IOException)
                    {
                        broken = true;
                    }
                }

                var size = st.IsRegular ? st.Size : 0;
                var alloc = st.IsRegular ? st.AllocSize : 0;
                if (!hadOld)
                {
                    Insert(dirId, name, childRel, depth + 1, st, isDir: false, target, broken);
                    _ctx.Added++;
                }
                else if (old.Size != size || old.MTime != st.MTime || old.Inode != st.Inode || old.IsSymlink != st.IsSymlink)
                {
                    UpdateFile(old.Id, st, target, broken);
                    _ctx.Changed++;
                }
                else if (Math.Abs(old.ATime - st.ATime) > 86400)
                {
                    _updateATime.Parameters["$atime"].Value = st.ATime;
                    _updateATime.Parameters["$nlink"].Value = st.NLink;
                    _updateATime.Parameters["$id"].Value = old.Id;
                    Exec(_updateATime);
                }

                _ctx.FilesSeen++;
                _ctx.BytesSeen += size;
                total = Add(total, new Agg(size, alloc, 1, 0, false), isDir: false);
            }
        }

        foreach (var (name, gone) in existing)
        {
            var goneRel = relPath.Length == 0 ? name : relPath + "/" + name;
            if (gone.IsDir)
            {
                DeleteTree(gone.Id, goneRel);
            }
            else
            {
                DeleteOne(gone.Id);
            }
        }

        return total;
    }

    private static Agg Add(Agg a, Agg b, bool isDir) =>
        new(a.Size + b.Size, a.Alloc + b.Alloc, a.Files + b.Files, a.Dirs + b.Dirs + (isDir ? 1 : 0), a.Error || b.Error);

    private Dictionary<string, Existing> LoadChildren(long parentId)
    {
        var d = new Dictionary<string, Existing>(StringComparer.Ordinal);
        _selChildren.Parameters["$p"].Value = parentId;
        using var r = _selChildren.ExecuteReader();
        while (r.Read())
        {
            d[r.GetString(1)] = new Existing(
                r.GetInt64(0), r.GetBoolean(2), r.GetBoolean(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7),
                new Agg(r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), false));
        }

        return d;
    }

    private long Insert(long? parentId, string name, string relPath, int depth, FileStat st, bool isDir, string? linkTarget, bool broken)
    {
        var p = _insert.Parameters;
        var size = !isDir && st.IsRegular ? st.Size : 0;
        var alloc = !isDir && st.IsRegular ? st.AllocSize : 0;
        p["$root"].Value = _rootId;
        p["$parent"].Value = (object?)parentId ?? DBNull.Value;
        p["$name"].Value = name;
        p["$rel"].Value = relPath;
        p["$depth"].Value = depth;
        p["$isDir"].Value = isDir;
        p["$isLink"].Value = st.IsSymlink;
        p["$target"].Value = (object?)linkTarget ?? DBNull.Value;
        p["$broken"].Value = broken;
        p["$size"].Value = size;
        p["$alloc"].Value = alloc;
        p["$mtime"].Value = st.MTime;
        p["$atime"].Value = st.ATime;
        p["$ctime"].Value = st.CTime;
        p["$inode"].Value = st.Inode;
        p["$dev"].Value = st.Dev;
        p["$nlink"].Value = st.NLink;
        p["$ext"].Value = isDir ? DBNull.Value : (object?)FileTypes.ExtensionOf(name) ?? DBNull.Value;
        // Directories and symlinks need no content detection.
        p["$detected"].Value = isDir || !st.IsRegular;
        p["$files"].Value = isDir ? 0 : 1;
        var id = (long)_insert.ExecuteScalar()!;
        Tick();
        return id;
    }

    private void UpdateFile(long id, FileStat st, string? linkTarget, bool broken)
    {
        var p = _updateFile.Parameters;
        p["$isLink"].Value = st.IsSymlink;
        p["$target"].Value = (object?)linkTarget ?? DBNull.Value;
        p["$broken"].Value = broken;
        p["$size"].Value = st.IsRegular ? st.Size : 0;
        p["$alloc"].Value = st.IsRegular ? st.AllocSize : 0;
        p["$mtime"].Value = st.MTime;
        p["$atime"].Value = st.ATime;
        p["$ctime"].Value = st.CTime;
        p["$inode"].Value = st.Inode;
        p["$dev"].Value = st.Dev;
        p["$nlink"].Value = st.NLink;
        p["$detected"].Value = !st.IsRegular;
        p["$id"].Value = id;
        Exec(_updateFile);
    }

    private void UpdateDir(long id, Agg agg, FileStat st)
    {
        var p = _updateDir.Parameters;
        p["$size"].Value = agg.Size;
        p["$alloc"].Value = agg.Alloc;
        p["$files"].Value = agg.Files;
        p["$dirs"].Value = agg.Dirs;
        p["$mtime"].Value = st.MTime;
        p["$atime"].Value = st.ATime;
        p["$ctime"].Value = st.CTime;
        p["$inode"].Value = st.Inode;
        p["$dev"].Value = st.Dev;
        p["$nlink"].Value = st.NLink;
        p["$error"].Value = agg.Error;
        p["$id"].Value = id;
        Exec(_updateDir);
    }

    private void DeleteOne(long id)
    {
        _deleteOne.Parameters["$id"].Value = id;
        _ctx.Removed += Exec(_deleteOne);
    }

    private void DeleteTree(long id, string relPath)
    {
        DeleteOne(id);
        _deleteTree.Parameters["$root"].Value = _rootId;
        _deleteTree.Parameters["$from"].Value = relPath + "/";
        // '0' is the character after '/', so this range is exactly the paths below relPath.
        _deleteTree.Parameters["$to"].Value = relPath + "0";
        _ctx.Removed += Exec(_deleteTree);
    }

    private int Exec(SqliteCommand cmd)
    {
        var n = cmd.ExecuteNonQuery();
        Tick();
        return n;
    }

    private void Tick()
    {
        if (++_pending >= CommitEvery)
        {
            Commit();
        }
    }

    public void Commit()
    {
        _tx.Commit();
        _tx.Dispose();
        _tx = _db.BeginTransaction();
        foreach (var c in new[] { _selChildren, _insert, _updateFile, _updateATime, _updateDir, _deleteOne, _deleteTree })
        {
            c.Transaction = _tx;
        }

        _pending = 0;
    }

    private SqliteCommand Cmd(string sql, params string[] parameters)
    {
        var c = _db.CreateCommand();
        c.CommandText = sql;
        c.Transaction = _tx;
        foreach (var p in parameters)
        {
            c.Parameters.Add(new SqliteParameter(p, null));
        }

        c.Prepare();
        return c;
    }

    public void Dispose()
    {
        _tx.Commit();
        _tx.Dispose();
        foreach (var c in new[] { _selChildren, _insert, _updateFile, _updateATime, _updateDir, _deleteOne, _deleteTree })
        {
            c.Dispose();
        }
    }
}

/// <summary>Ignore globs: patterns with a '/' match the relative path, others match the name at any depth.</summary>
public sealed class IgnoreMatcher
{
    private readonly List<Regex> _pathPatterns = [];
    private readonly List<Regex> _namePatterns = [];

    public IgnoreMatcher(IEnumerable<string> patterns)
    {
        foreach (var raw in patterns)
        {
            var p = raw.Trim().Trim('/');
            if (p.Length == 0 || p.StartsWith('#'))
            {
                continue;
            }

            (p.Contains('/') ? _pathPatterns : _namePatterns).Add(Domain.RuleEngine.GlobToRegex(p));
        }
    }

    public bool IsIgnored(string relPath, string name, int depth)
    {
        if (depth == 1 && (name == RootRegistry.TrashFolder || name.StartsWith(".analyze-probe-", StringComparison.Ordinal)))
        {
            return true;
        }

        foreach (var r in _namePatterns)
        {
            if (r.IsMatch(name))
            {
                return true;
            }
        }

        foreach (var r in _pathPatterns)
        {
            if (r.IsMatch(relPath))
            {
                return true;
            }
        }

        return false;
    }
}
