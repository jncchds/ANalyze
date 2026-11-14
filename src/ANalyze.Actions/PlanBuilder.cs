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
    PlanSource Source = PlanSource.Manual)
{
    /// <summary>Add the items to this draft plan instead of creating one (its name and conflict policy stay).</summary>
    public int? PlanId { get; init; }

    /// <summary>
    /// For <see cref="ActionOp.Restore"/>: where the damaged file goes instead of the trash, as a path template
    /// (<c>{relDir}/~{name}</c> keeps it next to the restored file). Empty: the trash.
    /// </summary>
    public string? KeepDamaged { get; init; }

    /// <summary>List the items but leave them out: a recorded decision to keep them as they are.</summary>
    public bool LeaveOut { get; init; }
}

public sealed record DedupePlanRequest(
    string? Name,
    DedupeKeep Keep,
    ActionOp Op,
    IReadOnlyList<string>? PreferredFolders,
    IReadOnlyList<string>? Hashes,
    int? RootId,
    string? PathPrefix,
    long MinSize = 0,
    int? PlanId = null);

/// <summary>Builds draft plans. Nothing touches the disk until a plan is applied.</summary>
public sealed class PlanBuilder(IDbContextFactory<AnalyzeDbContext> dbf, RootRegistry roots, IEnumerable<IBackupSource> backups)
{
    public const int MaxItems = 200_000;

    /// <summary>Default place for a damaged file that is kept when the backup's copy replaces it.</summary>
    public const string KeepDamagedDefault = "{relDir}/~{name}";

    /// <summary>Warning of items listed by a "keep it" decision (<see cref="ManualPlanRequest.LeaveOut"/>).</summary>
    public const string KeptNote = "Kept as it is (your decision)";

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

        var plan = await OpenAsync(db, req.PlanId, entries.Select(e => e.Id).ToList(), () => new ActionPlan
        {
            Name = string.IsNullOrWhiteSpace(req.Name) ? DefaultName(req.Op, entries.Count, !string.IsNullOrWhiteSpace(req.KeepDamaged)) : req.Name.Trim(),
            Source = req.Source,
            Status = PlanStatus.Draft,
            Conflict = req.Conflict,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        var conflict = plan.Conflict;
        var added = new List<ActionItem>(entries.Count);

        // Paths this plan already writes to (in an existing plan too), per root.
        var targets = new HashSet<(int Root, string Path)>();
        if (req.PlanId is not null)
        {
            foreach (var t in await db.ActionItems.AsNoTracking().Where(i => i.PlanId == plan.Id && (i.Op == ActionOp.Move || i.Op == ActionOp.Restore))
                .Select(i => new { i.RootId, i.Op, i.TargetPath, i.KeepPath }).ToListAsync(ct))
            {
                if ((t.Op == ActionOp.Move ? t.TargetPath : t.KeepPath) is { } path)
                {
                    targets.Add((t.RootId, path));
                }
            }
        }

        foreach (var e in entries)
        {
            var facts = new TemplateFacts(e.Name, e.RelPath, e.Ext, e.CategoryId is { } c && categories.TryGetValue(c, out var n) ? n : null, e.Kind.ToString(), e.MTime, e.IsDir ? e.TotalSize : e.Size);
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

            if (req.Op == ActionOp.Restore)
            {
                CheckRestore(item, e, copies.GetValueOrDefault(e.Id));
                if (item.TargetPath is not null && !e.IsDir && !string.IsNullOrWhiteSpace(req.KeepDamaged))
                {
                    KeepDamagedAt(item, req.KeepDamaged, facts, targets);
                }
            }

            if (req.Op == ActionOp.Move)
            {
                try
                {
                    item.TargetPath = PathTemplate.Expand(WithName(req.Destination!), facts);
                }
                catch (ArgumentException ex)
                {
                    item.Included = false;
                    item.Warning = ex.Message;
                }

                if (item.TargetPath is { } t)
                {
                    ResolveTarget(item, t, conflict, targets);
                }
            }

            MarkReadOnly(item);
            if (req.LeaveOut && item.Included)
            {
                item.Included = false;
                item.Warning = KeptNote;
            }

            added.Add(item);
        }

        await MarkInUseAsync(db, added, ct);
        return await SaveAsync(db, plan, added, ct);
    }

    /// <summary>A plain folder ("Archive/old") means "into it, keeping the name".</summary>
    private static string WithName(string template) => template.Contains('{') ? template : template.TrimEnd('/') + "/{name}";

    /// <summary>
    /// The plan new items go to: a new one, or the draft <paramref name="planId"/>. Items it already has for the same entries are
    /// replaced (the latest decision about a file wins).
    /// </summary>
    private static async Task<ActionPlan> OpenAsync(AnalyzeDbContext db, int? planId, IReadOnlyList<long> entryIds, Func<ActionPlan> create, CancellationToken ct)
    {
        if (planId is not { } id)
        {
            return create();
        }

        var plan = await db.ActionPlans.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new ArgumentException($"Plan #{id} does not exist.");
        if (plan.Status != PlanStatus.Draft)
        {
            throw new ArgumentException("Only draft plans can take more items.");
        }

        if (await db.ActionItems.CountAsync(i => i.PlanId == id, ct) + entryIds.Count > MaxItems)
        {
            throw new ArgumentException($"Too many items in one plan (max {MaxItems}).");
        }

        foreach (var chunk in entryIds.Chunk(5000))
        {
            await db.ActionItems.Where(i => i.PlanId == id && i.EntryId != null && chunk.Contains(i.EntryId.Value)).ExecuteDeleteAsync(ct);
        }

        return plan;
    }

    /// <summary>Saves the new items; the returned plan lists only them (<see cref="ActionPlan.Items"/>).</summary>
    private static async Task<ActionPlan> SaveAsync(AnalyzeDbContext db, ActionPlan plan, List<ActionItem> added, CancellationToken ct)
    {
        if (plan.Id == 0)
        {
            plan.Items.AddRange(added);
            db.ActionPlans.Add(plan);
        }
        else
        {
            foreach (var item in added)
            {
                item.PlanId = plan.Id;
            }

            db.ActionItems.AddRange(added);
        }

        await db.SaveChangesAsync(ct);
        if (plan.Items.Count == 0)
        {
            plan.Items.AddRange(added);
        }

        return plan;
    }

    /// <summary>
    /// The damaged file is kept at the expanded template instead of going to the trash (<see cref="ActionItem.KeepPath"/>); a taken
    /// name gets a number.
    /// </summary>
    private void KeepDamagedAt(ActionItem item, string template, TemplateFacts facts, HashSet<(int Root, string Path)> taken)
    {
        string keep;
        try
        {
            keep = PathTemplate.Expand(WithName(template.Trim()), facts);
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
            item.Warning = "The damaged file cannot be kept under its own name";
            return;
        }

        if (keep == RootRegistry.TrashFolder || keep.StartsWith(RootRegistry.TrashFolder + "/", StringComparison.Ordinal))
        {
            item.Included = false;
            item.Warning = "The damaged file cannot be kept in the trash folder";
            return;
        }

        item.KeepPath = Unique(item.RootId, keep, taken);
        taken.Add((item.RootId, item.KeepPath));
    }

    private bool Exists(int rootId, string rel, HashSet<(int Root, string Path)> taken) =>
        taken.Contains((rootId, rel)) || (roots.Get(rootId) is { } root && Path.Exists(RootRegistry.Combine(root.Path, rel)));

    /// <summary>"name.ext", or "name (2).ext"… when that is on disk or taken by another item of the plan.</summary>
    private string Unique(int rootId, string rel, HashSet<(int Root, string Path)> taken)
    {
        var candidate = rel;
        for (var n = 2; Exists(rootId, candidate, taken) && n < 10_000; n++)
        {
            candidate = PathTemplate.WithSuffix(rel, n);
        }

        return candidate;
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

        var added = new List<ActionItem>();
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
                added.Add(item);
                if (added.Count >= MaxItems)
                {
                    break;
                }
            }

            if (added.Count >= MaxItems)
            {
                break;
            }
        }

        await MarkInUseAsync(db, added, ct);
        plan = await OpenAsync(db, req.PlanId, added.Select(i => i.EntryId!.Value).ToList(), () => plan, ct);
        return await SaveAsync(db, plan, added, ct);
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
    private void ResolveTarget(ActionItem item, string target, ConflictPolicy policy, HashSet<(int Root, string Path)> taken)
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

        if (Exists(item.RootId, target, taken))
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
                    target = Unique(item.RootId, target, taken);
                    item.Warning = "Renamed to avoid a name clash";
                    break;
            }
        }

        item.TargetPath = target;
        taken.Add((item.RootId, target));
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

    private static string DefaultName(ActionOp op, int count, bool keepDamaged = false) => op switch
    {
        ActionOp.Trash => $"Trash {count} item(s)",
        ActionOp.Delete => $"Delete {count} item(s)",
        ActionOp.Move => $"Move {count} item(s)",
        ActionOp.Restore when keepDamaged => $"Restore {count} file(s) from the backup, keep the damaged ones",
        ActionOp.Restore => $"Restore {count} file(s) from the backup",
        _ => $"{op} {count} item(s)",
    };
}
