using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Scanner;
using Microsoft.EntityFrameworkCore;

namespace ANalyze.Actions;

public sealed record ManualPlanRequest(
    string? Name,
    ActionOp Op,
    IReadOnlyList<long> EntryIds,
    string? Destination,
    ConflictPolicy Conflict = ConflictPolicy.Rename,
    PlanSource Source = PlanSource.Manual);

public sealed record DedupePlanRequest(
    string? Name,
    DedupeKeep Keep,
    ActionOp Op,
    IReadOnlyList<string>? PreferredFolders,
    IReadOnlyList<string>? Hashes,
    int? RootId,
    string? PathPrefix,
    long MinSize = 0);

/// <summary>Builds draft plans. Nothing touches the disk until a plan is applied.</summary>
public sealed class PlanBuilder(IDbContextFactory<AnalyzeDbContext> dbf, RootRegistry roots)
{
    public const int MaxItems = 200_000;

    public async Task<ActionPlan> CreateAsync(ManualPlanRequest req, CancellationToken ct)
    {
        if (req.Op == ActionOp.Hardlink)
        {
            throw new ArgumentException("Hard links are created by the dedupe planner.");
        }

        if (req.Op == ActionOp.Move && string.IsNullOrWhiteSpace(req.Destination))
        {
            throw new ArgumentException("A move needs a destination.");
        }

        if (req.EntryIds.Count > MaxItems)
        {
            throw new ArgumentException($"Too many items (max {MaxItems}).");
        }

        await using var db = await dbf.CreateDbContextAsync(ct);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var entries = new List<Entry>(req.EntryIds.Count);
        foreach (var chunk in req.EntryIds.Distinct().Chunk(5000))
        {
            entries.AddRange(await db.Entries.AsNoTracking().Where(e => chunk.Contains(e.Id)).ToListAsync(ct));
        }

        // Selecting a folder covers everything inside it.
        entries = RemoveNested(entries.Where(e => e.RelPath.Length > 0).ToList());

        var plan = new ActionPlan
        {
            Name = string.IsNullOrWhiteSpace(req.Name) ? DefaultName(req.Op, entries.Count) : req.Name.Trim(),
            Source = req.Source,
            Status = PlanStatus.Draft,
            Conflict = req.Conflict,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var item = new ActionItem
            {
                EntryId = e.Id,
                RootId = e.RootId,
                Op = req.Op,
                SourcePath = e.RelPath,
                IsDir = e.IsDir,
                ExpectedSize = e.IsDir ? e.TotalSize : e.Size,
                ExpectedMTime = e.MTime,
            };

            if (req.Op == ActionOp.Move)
            {
                var facts = new TemplateFacts(e.Name, e.RelPath, e.Ext, e.CategoryId is { } c && categories.TryGetValue(c, out var n) ? n : null, e.Kind.ToString(), e.MTime, e.IsDir ? e.TotalSize : e.Size);
                var dest = req.Destination!;
                // A plain folder ("Archive/old") means "move into it, keep the name".
                if (!dest.Contains('{'))
                {
                    dest = dest.TrimEnd('/') + "/{name}";
                }

                try
                {
                    item.TargetPath = PathTemplate.Expand(dest, facts);
                }
                catch (ArgumentException ex)
                {
                    item.Included = false;
                    item.Warning = ex.Message;
                }

                if (item.TargetPath is { } t)
                {
                    ResolveTarget(item, t, req.Conflict, targets);
                }
            }

            MarkReadOnly(item);
            plan.Items.Add(item);
        }

        await MarkInUseAsync(db, plan.Items, ct);
        db.ActionPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>Badges integrations put on files another app is using right now (download clients).</summary>
    public static readonly string[] InUseStates = ["seeding", "downloading"];

    /// <summary>
    /// Leaves out items that are, contain or sit inside something a download client is seeding or downloading:
    /// trashing, moving or deleting them would break the torrent. Hard links keep the content, so they are allowed.
    /// </summary>
    private static async Task MarkInUseAsync(AnalyzeDbContext db, IEnumerable<ActionItem> items, CancellationToken ct)
    {
        var inUse = await (from a in db.Annotations.AsNoTracking()
                           join e in db.Entries.AsNoTracking() on a.EntryId equals e.Id
                           where a.State != null && InUseStates.Contains(a.State)
                           select new { e.RootId, e.RelPath, a.Label, a.State }).ToListAsync(ct);
        if (inUse.Count == 0)
        {
            return;
        }

        static bool Related(string a, string b) =>
            a == b || a.Length == 0 || b.Length == 0 || a.StartsWith(b + "/", StringComparison.Ordinal) || b.StartsWith(a + "/", StringComparison.Ordinal);

        foreach (var item in items.Where(i => i.Included && i.Op != ActionOp.Hardlink))
        {
            var hit = inUse.FirstOrDefault(u => u.RootId == item.RootId && Related(u.RelPath, item.SourcePath));
            if (hit is not null)
            {
                item.Included = false;
                item.Warning = $"In use ({hit.State}): {hit.Label}";
            }
        }
    }

    public async Task<ActionPlan> CreateDedupeAsync(DedupePlanRequest req, CancellationToken ct)
    {
        if (req.Op is not (ActionOp.Trash or ActionOp.Delete or ActionOp.Hardlink))
        {
            throw new ArgumentException("Dedupe can trash, delete or hard-link copies.");
        }

        await using var db = await dbf.CreateDbContextAsync(ct);
        var all = db.Entries.AsNoTracking();
        // Only hashes shared with another file: with "hash all files" on, every file has one.
        var q = all.Where(e => !e.IsDir && !e.IsSymlink && e.FullHash != null && e.Size >= req.MinSize && e.Size > 0
            && all.Any(o => o.FullHash == e.FullHash && o.Id != e.Id && !o.IsDir));
        if (req.Hashes is { Count: > 0 } hashes)
        {
            q = q.Where(e => hashes.Contains(e.FullHash!));
        }

        var rows = await q.Select(e => new { e.Id, e.RootId, e.RelPath, e.MTime, e.Size, e.Inode, e.Dev, e.FullHash })
            .OrderBy(e => e.FullHash).ToListAsync(ct);

        var plan = new ActionPlan
        {
            Name = string.IsNullOrWhiteSpace(req.Name) ? $"Dedupe: keep {req.Keep.ToString().ToLowerInvariant()}, {req.Op.ToString().ToLowerInvariant()} others" : req.Name.Trim(),
            Source = PlanSource.Dedupe,
            Status = PlanStatus.Draft,
            Conflict = ConflictPolicy.Skip,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var preferred = req.PreferredFolders ?? [];
        foreach (var group in rows.GroupBy(r => r.FullHash!))
        {
            var members = group.Select(r => new DupMember(r.Id, r.RootId, r.RelPath, r.MTime, r.Inode == 0 ? "i" + r.Id : $"{r.Dev}:{r.Inode}")).ToList();
            if (members.Select(m => m.Identity).Distinct().Count() < 2)
            {
                continue;
            }

            // Scope filters decide which copies may be touched; the kept copy can live anywhere.
            var keep = DedupeSelector.ChooseKeep(members, req.Keep, preferred);
            var keepRow = group.First(r => r.Id == keep.EntryId);
            foreach (var m in members)
            {
                if (m.Identity == keep.Identity)
                {
                    continue;
                }

                if (req.RootId is { } rid && m.RootId != rid)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(req.PathPrefix) && !(m.RelPath == req.PathPrefix || m.RelPath.StartsWith(req.PathPrefix.TrimEnd('/') + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                var row = group.First(r => r.Id == m.EntryId);
                var item = new ActionItem
                {
                    EntryId = m.EntryId,
                    RootId = m.RootId,
                    Op = req.Op,
                    SourcePath = m.RelPath,
                    KeepPath = keep.RelPath,
                    KeepRootId = keep.RootId,
                    ExpectedSize = row.Size,
                    ExpectedMTime = row.MTime,
                    ExpectedHash = group.Key,
                };

                if (req.Op == ActionOp.Hardlink && (keep.RootId != m.RootId || keepRow.Dev != row.Dev))
                {
                    item.Included = false;
                    item.Warning = "Kept copy is on another filesystem; a hard link is impossible";
                }

                MarkReadOnly(item);
                plan.Items.Add(item);
                if (plan.Items.Count >= MaxItems)
                {
                    break;
                }
            }
        }

        await MarkInUseAsync(db, plan.Items, ct);
        db.ActionPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        return plan;
    }

    private void MarkReadOnly(ActionItem item)
    {
        var root = roots.Get(item.RootId);
        if (root is null || root.ReadOnly)
        {
            item.Included = false;
            item.Warning = "Root is read-only";
        }
    }

    /// <summary>Applies the conflict policy against the disk and against other targets in the same plan.</summary>
    private void ResolveTarget(ActionItem item, string target, ConflictPolicy policy, HashSet<string> taken)
    {
        if (target == item.SourcePath)
        {
            item.Included = false;
            item.Warning = "Already in place";
            return;
        }

        if (item.IsDir && target.StartsWith(item.SourcePath + "/", StringComparison.Ordinal))
        {
            item.Included = false;
            item.Warning = "Cannot move a folder into itself";
            return;
        }

        var root = roots.Get(item.RootId);
        bool Exists(string rel) => taken.Contains(rel) || (root is not null && Path.Exists(RootRegistry.Combine(root.Path, rel)));

        if (Exists(target))
        {
            switch (policy)
            {
                case ConflictPolicy.Skip:
                    item.Included = false;
                    item.Warning = "Destination exists";
                    break;
                case ConflictPolicy.Overwrite:
                    item.Warning = "Will overwrite the existing destination";
                    break;
                default:
                    var n = 2;
                    var candidate = PathTemplate.WithSuffix(target, n);
                    while (Exists(candidate) && n < 10_000)
                    {
                        candidate = PathTemplate.WithSuffix(target, ++n);
                    }

                    target = candidate;
                    item.Warning = "Renamed to avoid a name clash";
                    break;
            }
        }

        item.TargetPath = target;
        taken.Add(target);
    }

    internal static List<Entry> RemoveNested(List<Entry> entries)
    {
        var dirs = entries.Where(e => e.IsDir).Select(e => (e.RootId, e.RelPath)).ToHashSet();
        return entries.Where(e =>
        {
            var p = e.RelPath;
            var slash = p.LastIndexOf('/');
            while (slash > 0)
            {
                p = p[..slash];
                if (dirs.Contains((e.RootId, p)))
                {
                    return false;
                }

                slash = p.LastIndexOf('/');
            }

            return true;
        }).ToList();
    }

    private static string DefaultName(ActionOp op, int count) => op switch
    {
        ActionOp.Trash => $"Trash {count} item(s)",
        ActionOp.Delete => $"Delete {count} item(s)",
        ActionOp.Move => $"Move {count} item(s)",
        _ => $"{op} {count} item(s)",
    };
}
