using Microsoft.EntityFrameworkCore;
using ANalyze.Domain;

namespace ANalyze.Web.Api;

/// <summary>
/// Shared filter for search, insights and "create a plan from these results". Every field is optional.
/// Bound from the query string (GET) or JSON body (POST).
/// </summary>
public sealed record SearchFilter
{
    public string? Q { get; init; }
    public int? RootId { get; init; }
    /// <summary>Restrict to a folder (relative path inside the root).</summary>
    public string? Path { get; init; }
    public int? CategoryId { get; init; }
    public bool? Uncategorized { get; init; }
    public FileKind? Kind { get; init; }
    /// <summary>Comma separated extensions.</summary>
    public string? Ext { get; init; }
    public string? Tag { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public int? OlderThanDays { get; init; }
    public int? NewerThanDays { get; init; }
    public int? NotAccessedDays { get; init; }
    public bool? Duplicates { get; init; }
    /// <summary>true = only folders, false/null = only files.</summary>
    public bool? Dirs { get; init; }
    public bool? EmptyDirs { get; init; }
    public bool? BrokenLinks { get; init; }
    public bool? Sparse { get; init; }
    public bool? Hardlinked { get; init; }
    /// <summary>Files whose content changed without a new size or mtime when re-read (possible corruption).</summary>
    public bool? Corrupt { get; init; }
    /// <summary>Archives found extracted: complete, verified (complete and every file matches the archive's CRC32), partial or any.</summary>
    public string? Extracted { get; init; }
    /// <summary>Folder name anywhere in the path (e.g. node_modules); with Dirs=true matches those folders themselves (outermost only).</summary>
    public string? Segment { get; init; }
    public string? Sort { get; init; }
    public bool? Asc { get; init; }

    /// <param name="extractions">Needed for <see cref="Extracted"/>.</param>
    public IQueryable<Entry> Apply(IQueryable<Entry> q, IQueryable<ArchiveExtraction>? extractions = null)
    {
        var all = q;
        var dirs = Dirs == true || EmptyDirs == true;
        q = q.Where(e => e.IsDir == dirs && e.RelPath != "");
        if (RootId is { } root)
        {
            q = q.Where(e => e.RootId == root);
            if (!string.IsNullOrWhiteSpace(Path))
            {
                var p = Path.Trim('/');
                var from = p + "/";
                var to = p + "0";
                q = q.Where(e => string.Compare(e.RelPath, from) >= 0 && string.Compare(e.RelPath, to) < 0);
            }
        }

        if (!string.IsNullOrWhiteSpace(Q))
        {
            foreach (var term in Q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // LIKE is case-insensitive for ASCII in SQLite; '*' works as a wildcard.
                var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace('*', '%') + "%";
                q = term.Contains('/')
                    ? q.Where(e => EF.Functions.Like(e.RelPath, like, "\\"))
                    : q.Where(e => EF.Functions.Like(e.Name, like, "\\"));
            }
        }

        if (CategoryId is { } cat)
        {
            q = q.Where(e => e.CategoryId == cat);
        }

        if (Uncategorized == true)
        {
            q = q.Where(e => e.CategoryId == null);
        }

        if (Kind is { } kind)
        {
            q = q.Where(e => e.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(Ext))
        {
            var exts = RuleEngine.SplitList(Ext).Select(x => x.ToLowerInvariant()).ToList();
            q = q.Where(e => e.Ext != null && exts.Contains(e.Ext));
        }

        if (!string.IsNullOrWhiteSpace(Tag))
        {
            var t = "|" + Tag.Trim().ToLowerInvariant() + "|";
            q = q.Where(e => e.Tags != null && e.Tags.Contains(t));
        }

        if (MinSize is { } min)
        {
            q = dirs ? q.Where(e => e.TotalSize >= min) : q.Where(e => e.Size >= min);
        }

        if (MaxSize is { } max)
        {
            q = dirs ? q.Where(e => e.TotalSize <= max) : q.Where(e => e.Size <= max);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (OlderThanDays is { } older)
        {
            var cutoff = now - (older * 86400L);
            q = q.Where(e => e.MTime < cutoff);
        }

        if (NewerThanDays is { } newer)
        {
            var cutoff = now - (newer * 86400L);
            q = q.Where(e => e.MTime >= cutoff);
        }

        if (NotAccessedDays is { } na)
        {
            var cutoff = now - (na * 86400L);
            q = q.Where(e => e.ATime < cutoff);
        }

        if (Duplicates == true)
        {
            // Every file may have a hash (backup verification): a duplicate needs another file with the same one that is not a hard link.
            q = q.Where(e => e.FullHash != null && all.Any(o => o.FullHash == e.FullHash && o.Id != e.Id && (o.Inode == 0 || o.Inode != e.Inode || o.Dev != e.Dev)));
        }

        if (Corrupt == true)
        {
            q = q.Where(e => e.CorruptAt != null);
        }

        if (!string.IsNullOrWhiteSpace(Extracted) && extractions is not null)
        {
            var found = Extracted.Trim().ToLowerInvariant() switch
            {
                "complete" => extractions.Where(x => x.State == ExtractionState.Complete),
                "verified" => extractions.Where(x => x.State == ExtractionState.Complete && x.VerifiedAt != null && x.VerifiedBad == 0 && x.Unverified == 0),
                "partial" => extractions.Where(x => x.State == ExtractionState.Partial),
                _ => extractions,
            };
            q = q.Where(e => found.Any(a => a.ArchiveId == e.Id));
        }

        if (EmptyDirs == true)
        {
            q = q.Where(e => e.FileCount == 0 && e.DirCount == 0);
        }

        if (BrokenLinks == true)
        {
            q = q.Where(e => e.BrokenLink);
        }

        if (Sparse == true)
        {
            q = q.Where(e => e.Size > 1048576 && e.AllocSize * 2 < e.Size);
        }

        if (Hardlinked == true)
        {
            q = q.Where(e => e.NLink > 1);
        }

        if (!string.IsNullOrWhiteSpace(Segment))
        {
            var names = RuleEngine.SplitList(Segment).ToList();
            if (dirs)
            {
                // The folders themselves, but not nested ones inside another match (node_modules/x/node_modules).
                q = q.Where(e => names.Contains(e.Name));
                foreach (var n in names)
                {
                    var inner = "/" + n + "/";
                    var lead = n + "/";
                    q = q.Where(e => !e.RelPath.Contains(inner) && !e.RelPath.StartsWith(lead));
                }
            }
            else
            {
                System.Linq.Expressions.Expression<Func<Entry, bool>>? any = null;
                foreach (var n in names)
                {
                    var inner = "/" + n + "/";
                    var lead = n + "/";
                    System.Linq.Expressions.Expression<Func<Entry, bool>> one = e => e.RelPath.Contains(inner) || e.RelPath.StartsWith(lead);
                    any = any is null ? one : OrElse(any, one);
                }

                if (any is not null)
                {
                    q = q.Where(any);
                }
            }
        }

        return q;
    }

    private static System.Linq.Expressions.Expression<Func<Entry, bool>> OrElse(
        System.Linq.Expressions.Expression<Func<Entry, bool>> a, System.Linq.Expressions.Expression<Func<Entry, bool>> b)
    {
        var p = a.Parameters[0];
        var body = new ReplaceParameter(b.Parameters[0], p).Visit(b.Body);
        return System.Linq.Expressions.Expression.Lambda<Func<Entry, bool>>(System.Linq.Expressions.Expression.OrElse(a.Body, body), p);
    }

    private sealed class ReplaceParameter(System.Linq.Expressions.ParameterExpression from, System.Linq.Expressions.ParameterExpression to)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override System.Linq.Expressions.Expression VisitParameter(System.Linq.Expressions.ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }

    public IQueryable<Entry> Order(IQueryable<Entry> q)
    {
        var asc = Asc == true;
        return (Sort ?? "size").ToLowerInvariant() switch
        {
            "name" => asc ? q.OrderBy(e => e.Name) : q.OrderByDescending(e => e.Name),
            "path" => asc ? q.OrderBy(e => e.RelPath) : q.OrderByDescending(e => e.RelPath),
            "mtime" => asc ? q.OrderBy(e => e.MTime) : q.OrderByDescending(e => e.MTime),
            "atime" => asc ? q.OrderBy(e => e.ATime) : q.OrderByDescending(e => e.ATime),
            "files" => asc ? q.OrderBy(e => e.FileCount) : q.OrderByDescending(e => e.FileCount),
            _ => asc ? q.OrderBy(e => e.IsDir ? e.TotalSize : e.Size) : q.OrderByDescending(e => e.IsDir ? e.TotalSize : e.Size),
        };
    }
}
