using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Scanner;
using Microsoft.EntityFrameworkCore;

namespace ANalyze.Actions;

/// <summary>
/// A selection to plan. <see cref="Destination"/> is the move target template; for a restore it is where the local file is kept
/// instead of going to the trash (e.g. <c>{relDir}/~{name}</c>; null = the trash).
/// </summary>
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

/// <summary>What adding a selection to a plan did: items now in the plan, items added, items they replaced.</summary>
public sealed record PlanAddResult(int Id, string Name, int Items, int Added, int Replaced);

/// <summary>Builds draft plans. Nothing touches the disk until a plan is applied.</summary>
public sealed class PlanBuilder(IDbContextFactory<AnalyzeDbContext> dbf, RootRegistry roots, IEnumerable<IBackupSource> backups)
{
    public const int MaxItems = 200_000;

    public async Task<ActionPlan> CreateAsync(ManualPlanRequest req, CancellationToken ct)
    {
        Validate(req);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var items = await BuildItemsAsync(db, req, req.Conflict, new HashSet<string>(StringComparer.Ordinal), ct);
        var plan = new ActionPlan
        {
            Name = string.IsNullOrWhiteSpace(req.Name) ? DefaultName(req.Op, items.Count) : req.Name.Trim(),
            Source = req.Source,
            Status = PlanStatus.Draft,
            Conflict = req.Conflict,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        plan.Items.AddRange(items);
        await MarkInUseAsync(db, plan.Items, ct);
        db.ActionPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        return plan;
    }

    /// <summary>
    /// Adds a selection to a draft plan. An entry the plan already has gets the new action instead (this is also how an item's action
    /// is changed); a new folder replaces the plan's items inside it, and a new item inside a folder the plan already covers is left
    /// out with a warning. Move targets follow the plan's conflict policy.
    /// </summary>
    public async Task<PlanAddResult> AddAsync(int planId, ManualPlanRequest req, CancellationToken ct)
    {
        Validate(req);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var plan = await db.ActionPlans.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new KeyNotFoundException($"Plan {planId} not found.");
        if (plan.Status != PlanStatus.Draft)
        {
            throw new InvalidOperationException("Only draft plans can be changed.");
        }

        var selected = req.EntryIds.ToHashSet();
        bool Kept(ActionItem i) => !(i.EntryId is { } e && selected.Contains(e));
        var taken = plan.Items.Where(i => i.Op == ActionOp.Move && i.TargetPath is not null && Kept(i)).Select(i => i.TargetPath!)
            .Concat(plan.Items.Where(i => i.Op == ActionOp.Restore && i.KeepPath is not null && Kept(i)).Select(i => i.KeepPath!))
            .ToHashSet(StringComparer.Ordinal);

        var items = await BuildItemsAsync(db, req, plan.Conflict, taken, ct);
        var ids = items.Where(i => i.EntryId is not null).Select(i => i.EntryId!.Value).Concat(selected).ToHashSet();
        var dirs = items.Where(i => i.IsDir).Select(i => (i.RootId, i.SourcePath)).ToList();
        var replaced = plan.Items
            .Where(i => (i.EntryId is { } e && ids.Contains(e)) || dirs.Any(d => d.RootId == i.RootId && Inside(i.SourcePath, d.SourcePath)))
            .ToList();
        foreach (var r in replaced)
        {
            plan.Items.Remove(r);
            db.ActionItems.Remove(r);
        }

        var covering = plan.Items.Where(i => i.IsDir).ToList();
        foreach (var item in items)
        {
            if (covering.FirstOrDefault(d => d.RootId == item.RootId && Inside(item.SourcePath, d.SourcePath)) is { } d)
            {
                item.Included = false;
                item.Warning = $"Inside {d.SourcePath}, which this plan already covers ({d.Op.ToString().ToLowerInvariant()})";
            }
        }

        await MarkInUseAsync(db, items, ct);
        plan.Items.AddRange(items);
        await db.SaveChangesAsync(ct);
        return new PlanAddResult(plan.Id, plan.Name, plan.Items.Count, items.Count, replaced.Count);
    }

    private static bool Inside(string path, string dir) => dir.Length == 0 || path.StartsWith(dir + "/", StringComparison.Ordinal);

    private static void Validate(ManualPlanRequest req)
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
    }

    /// <summary>The items for a selection, not yet in a plan. <paramref name="taken"/> holds paths other items of the plan will create.</summary>
    private async Task<List<ActionItem>> BuildItemsAsync(AnalyzeDbContext db, ManualPlanRequest req, ConflictPolicy conflict, HashSet<string> taken, CancellationToken ct)
    {
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var entries = new List<Entry>(req.EntryIds.Count);
        foreach (var chunk in req.EntryIds.Distinct().Chunk(5000))
        {
            entries.AddRange(await db.Entries.AsNoTracking().Where(e => chunk.Contains(e.Id)).ToListAsync(ct));
        }

        // Selecting a folder covers everything inside it.
        entries = RemoveNested(entries.Where(e => e.RelPath.Length > 0).ToList());
        var copies = new Dictionary<long, BackupCopy>();
        if (req.Op == ActionOp.Restore)
        {
            entries = await DamagedInsideAsync(db, entries, ct);
            foreach (var b in backups)
            {
                foreach (var (id, copy) in await b.FindAsync(entries.Select(e => e.Id).ToList(), ct))
                {
                    copies.TryAdd(id, copy);
                }
            }
        }

        var items = new List<ActionItem>(entries.Count);
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

            var facts = new TemplateFacts(e.Name, e.RelPath, e.Ext, e.CategoryId is { } c && categories.TryGetValue(c, out var n) ? n : null, e.Kind.ToString(), e.MTime, e.IsDir ? e.TotalSize : e.Size);
            if (req.Op == ActionOp.Restore)
            {
                CheckRestore(item, e, copies.GetValueOrDefault(e.Id));
                if (!string.IsNullOrWhiteSpace(req.Destination) && !e.IsDir)
                {
                    KeepLocal(item, req.Destination, facts, taken);
                }
            }

            if (req.Op == ActionOp.Move)
            {
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
                    ResolveTarget(item, t, conflict, taken);
                }
            }

            MarkReadOnly(item);
            items.Add(item);
        }

        return items;
    }

    /// <summary>
    /// A restore that keeps the local file: it is moved to the expanded template (e.g. <c>{relDir}/~{name}</c>) instead of the trash.
    /// A path that exists already (on disk or in the plan) gets a " (2)" suffix.
    /// </summary>
    private void KeepLocal(ActionItem item, string template, TemplateFacts facts, HashSet<string> taken)
    {
        string keep;
        try
        {
            keep = PathTemplate.Expand(template, facts);
        }
        catch (ArgumentException ex)
        {
            item.Included = false;
            item.Warning = ex.Message;
            return;
        }

        if (keep == item.SourcePath)
        {
            item.Included = false;
            item.Warning = "The kept copy would take the file's own place";
            return;
        }

        var root = roots.Get(item.RootId);
        bool Exists(string rel) => taken.Contains(rel) || (root is not null && Path.Exists(RootRegistry.Combine(root.Path, rel)));
        if (Exists(keep))
        {
            var n = 2;
            var candidate = PathTemplate.WithSuffix(keep, n);
            while (Exists(candidate) && n < 10_000)
            {
                candidate = PathTemplate.WithSuffix(keep, ++n);
            }

            keep = candidate;
            item.Warning ??= "Kept copy renamed to avoid a name clash";
        }

        item.KeepPath = keep;
        item.KeepRootId = item.RootId;
        taken.Add(keep);
    }

    /// <summary>
    /// A restore replaces files only: a selected folder stands for the damaged files inside it that the backup has. A selection without
    /// any folder stays as it is (each file is checked by <see cref="CheckRestore"/>).
    /// </summary>
    private async Task<List<Entry>> DamagedInsideAsync(AnalyzeDbContext db, List<Entry> entries, CancellationToken ct)
    {
        var dirs = entries.Where(e => e.IsDir).ToList();
        if (dirs.Count == 0)
        {
            return entries;
        }

        var damaged = new HashSet<long>();
        foreach (var b in backups)
        {
            damaged.UnionWith(await b.DamagedAsync(ct));
        }

        var files = entries.Where(e => !e.IsDir).ToList();
        var seen = files.Select(e => e.Id).ToHashSet();
        foreach (var chunk in damaged.Where(id => !seen.Contains(id)).Chunk(5000))
        {
            var inside = await db.Entries.AsNoTracking().Where(e => chunk.Contains(e.Id)).ToListAsync(ct);
            files.AddRange(inside.Where(f => dirs.Any(d => d.RootId == f.RootId && (d.RelPath.Length == 0 || f.RelPath.StartsWith(d.RelPath + "/", StringComparison.Ordinal)))));
        }

        return files;
    }

    /// <summary>Only damaged files with a copy on the backup are restored by default; the others are listed but left out.</summary>
    private static void CheckRestore(ActionItem item, Entry e, BackupCopy? copy)
    {
        item.TargetPath = copy?.Location;
        (item.Included, item.Warning) = (e, copy) switch
        {
            ({ IsSymlink: true }, _) => (false, "A symbolic link: nothing to restore"),
            (_, null) => (false, "Not on the backup"),
            (_, { Identical: true }) => (false, "Same content as the backup's copy"),
            (_, { Corrupt: false }) => (false, "Not damaged — include it to replace it with the backup's copy anyway"),
            _ => (true, null),
        };
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
        ActionOp.Restore => $"Restore {count} file(s) from the backup",
        _ => $"{op} {count} item(s)",
    };
}
