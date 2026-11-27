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

/// <summary>
/// One file of a folder comparison to plan: removed (with <see cref="RemoveOp"/>) because <see cref="KeepId"/> holds the same content, or moved
/// to <see cref="MoveTo"/> (a path in the same root) because the other folder lacks it.
/// </summary>
public sealed record FolderPick(long EntryId, long? KeepId, string? MoveTo);

/// <summary>
/// One file of an extracted archive to extract again (<c>POST /archives/extractions/repair</c>): archive <see cref="ArchiveId"/>'s member
/// <see cref="MemberPath"/> goes to <see cref="DiskPath"/> on <see cref="RootId"/>, over the damaged file <see cref="EntryId"/> or, without one,
/// where the missing file belongs.
/// </summary>
public sealed record ArchivePick(long ArchiveId, string MemberPath, long Size, long? Crc, int RootId, string DiskPath, long? EntryId);

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
            UpdatedAt = DateTimeOffset.UtcNow,
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
        var plan = await LoadDraftAsync(db, planId, ct);
        var selected = req.EntryIds.ToHashSet();
        bool Kept(ActionItem i) => !(i.EntryId is { } e && selected.Contains(e));
        var taken = plan.Items.Where(i => i.Op == ActionOp.Move && i.TargetPath is not null && Kept(i)).Select(i => i.TargetPath!)
            .Concat(plan.Items.Where(i => i.Op == ActionOp.Restore && i.KeepPath is not null && Kept(i)).Select(i => i.KeepPath!))
            .ToHashSet(StringComparer.Ordinal);

        var items = await BuildItemsAsync(db, req, plan.Conflict, taken, ct);
        return await MergeAsync(db, plan, items, selected, ct);
    }

    /// <summary>Adds duplicates to remove (or hard-link) to a draft plan, like <see cref="AddAsync"/>: a file the plan has gets the new action.</summary>
    public async Task<PlanAddResult> AddDedupeAsync(int planId, DedupePlanRequest req, CancellationToken ct)
    {
        ValidateDedupe(req);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var plan = await LoadDraftAsync(db, planId, ct);
        var items = await BuildDedupeItemsAsync(db, req, ct);
        return await MergeAsync(db, plan, items, [], ct);
    }

    /// <summary>
    /// A plan from a folder comparison (<c>POST /duplicates/compare/plan</c>): removals are shaped like dedupe items (kept copy and content
    /// hash, re-checked at apply), moves like manual ones with the Skip policy. <paramref name="planId"/> adds to that draft instead.
    /// </summary>
    public async Task<PlanAddResult> PlanFolderAsync(int? planId, string? name, IReadOnlyList<FolderPick> picks, ActionOp removeOp, CancellationToken ct)
    {
        if (removeOp is not (ActionOp.Trash or ActionOp.Delete or ActionOp.Hardlink))
        {
            throw new ArgumentException("Copies can be trashed, deleted or hard-linked.");
        }

        if (picks.Count == 0)
        {
            throw new ArgumentException("Nothing to plan: no file of this folder is in the other one, or missing from it.");
        }

        if (picks.Count > MaxItems)
        {
            throw new ArgumentException($"Too many items (max {MaxItems}).");
        }

        await using var db = await dbf.CreateDbContextAsync(ct);
        var plan = planId is { } pid ? await LoadDraftAsync(db, pid, ct) : null;
        var taken = plan?.Items.Where(i => i.Op == ActionOp.Move && i.TargetPath is not null).Select(i => i.TargetPath!).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

        var ids = picks.Select(p => p.EntryId).Concat(picks.Where(p => p.KeepId is not null).Select(p => p.KeepId!.Value)).Distinct().ToList();
        var entries = new Dictionary<long, Entry>(ids.Count);
        foreach (var chunk in ids.Chunk(5000))
        {
            foreach (var e in await db.Entries.AsNoTracking().Where(e => chunk.Contains(e.Id)).ToListAsync(ct))
            {
                entries[e.Id] = e;
            }
        }

        var items = new List<ActionItem>(picks.Count);
        foreach (var p in picks)
        {
            if (!entries.TryGetValue(p.EntryId, out var e) || e.IsDir)
            {
                continue;
            }

            var item = new ActionItem
            {
                EntryId = e.Id,
                RootId = e.RootId,
                SourcePath = e.RelPath,
                ExpectedSize = e.Size,
                ExpectedMTime = e.MTime,
            };
            if (p.KeepId is { } kid)
            {
                item.Op = removeOp;
                if (!entries.TryGetValue(kid, out var k))
                {
                    continue;
                }

                item.KeepPath = k.RelPath;
                item.KeepRootId = k.RootId;
                item.ExpectedHash = e.FullHash;
                if (e.FullHash is null || e.FullHash != k.FullHash || e.Size == 0)
                {
                    (item.Included, item.Warning) = (false, "The other folder's file is not known to have the same content");
                }
                else if (e.Inode != 0 && e.Inode == k.Inode && e.Dev == k.Dev)
                {
                    (item.Included, item.Warning) = (false, "The same file (a hard link): removing it frees nothing");
                }
                else if (removeOp == ActionOp.Hardlink && (k.RootId != e.RootId || k.Dev != e.Dev))
                {
                    (item.Included, item.Warning) = (false, "Kept copy is on another filesystem; a hard link is impossible");
                }
            }
            else if (p.MoveTo is { Length: > 0 } to)
            {
                item.Op = ActionOp.Move;
                ResolveTarget(item, to, ConflictPolicy.Skip, taken);
            }
            else
            {
                continue;
            }

            MarkReadOnly(item);
            items.Add(item);
        }

        if (plan is not null)
        {
            return await MergeAsync(db, plan, items, [], ct);
        }

        var moves = items.Count(i => i.Op == ActionOp.Move);
        var created = new ActionPlan
        {
            Name = string.IsNullOrWhiteSpace(name)
                ? moves == 0 ? $"Folder copies: {removeOp.ToString().ToLowerInvariant()} {items.Count} file(s)" : $"Merge folders: move {moves}, {removeOp.ToString().ToLowerInvariant()} {items.Count - moves}"
                : name.Trim(),
            Source = moves == 0 ? PlanSource.Dedupe : PlanSource.Manual,
            Status = PlanStatus.Draft,
            Conflict = ConflictPolicy.Skip,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        created.Items.AddRange(items);
        await MarkInUseAsync(db, created.Items, ct);
        MarkKeptConflicts(created);
        db.ActionPlans.Add(created);
        await db.SaveChangesAsync(ct);
        return new PlanAddResult(created.Id, created.Name, created.Items.Count, created.Items.Count, 0);
    }

    /// <summary>
    /// A plan that extracts archive files again into an extraction (<see cref="ActionOp.Extract"/>): damaged copies are replaced — the
    /// damaged file goes to the trash, or with <paramref name="keepTemplate"/> (e.g. <c>{relDir}/~{name}</c>) is kept there — and missing
    /// files are created. <paramref name="planId"/> adds to that draft instead.
    /// </summary>
    public async Task<PlanAddResult> PlanArchiveAsync(int? planId, string? name, IReadOnlyList<ArchivePick> picks, string? keepTemplate, CancellationToken ct)
    {
        if (picks.Count == 0)
        {
            throw new ArgumentException("Nothing to repair: every file of the archive is there and fine.");
        }

        if (picks.Count > MaxItems)
        {
            throw new ArgumentException($"Too many items (max {MaxItems}).");
        }

        await using var db = await dbf.CreateDbContextAsync(ct);
        var plan = planId is { } pid ? await LoadDraftAsync(db, pid, ct) : null;
        var taken = plan?.Items.Where(i => i.Op == ActionOp.Move && i.TargetPath is not null).Select(i => i.TargetPath!)
            .Concat(plan.Items.Where(i => i.Op is ActionOp.Restore or ActionOp.Extract && i.KeepPath is not null).Select(i => i.KeepPath!))
            .Concat(plan.Items.Where(i => i.Op == ActionOp.Extract && i.EntryId is null).Select(i => i.SourcePath))
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var ids = picks.Where(p => p.EntryId is not null).Select(p => p.EntryId!.Value).Distinct().ToList();
        var entries = new Dictionary<long, Entry>(ids.Count);
        foreach (var chunk in ids.Chunk(5000))
        {
            foreach (var e in await db.Entries.AsNoTracking().Where(e => chunk.Contains(e.Id)).ToListAsync(ct))
            {
                entries[e.Id] = e;
            }
        }

        var items = new List<ActionItem>(picks.Count);
        // The same disk path from two copies of one archive: planned once.
        foreach (var p in picks.DistinctBy(p => (p.RootId, p.DiskPath)))
        {
            var item = new ActionItem
            {
                RootId = p.RootId,
                Op = ActionOp.Extract,
                SourcePath = p.DiskPath,
                TargetPath = p.MemberPath,
                ArchiveId = p.ArchiveId,
                ExpectedSize = p.Size,
                Warning = p.Crc is null ? "The archive has no CRC32 for it: only the size is checked" : null,
            };
            if (p.EntryId is { } id)
            {
                if (!entries.TryGetValue(id, out var e))
                {
                    continue;
                }

                item.EntryId = e.Id;
                (item.ExpectedSize, item.ExpectedMTime) = (e.Size, e.MTime);
                if (e.IsDir || e.IsSymlink)
                {
                    (item.Included, item.Warning) = (false, e.IsDir ? "A folder is where the file belongs" : "A symbolic link is where the file belongs");
                }
                else if (!string.IsNullOrWhiteSpace(keepTemplate))
                {
                    var facts = new TemplateFacts(e.Name, e.RelPath, e.Ext, e.CategoryId is { } c && categories.TryGetValue(c, out var n) ? n : null, e.Kind.ToString(), e.MTime, e.Size);
                    var warning = item.Warning;
                    item.Warning = null;
                    KeepLocal(item, keepTemplate, facts, taken);
                    item.Warning = (warning, item.Warning) switch
                    {
                        (null, var w) => w,
                        (var w, null) => w,
                        var (a, b) => $"{a}; {b}",
                    };
                }
            }
            else
            {
                var root = roots.Get(p.RootId);
                if (taken.Contains(p.DiskPath))
                {
                    (item.Included, item.Warning) = (false, "This plan already puts a file there");
                }
                else if (root is not null && Path.Exists(RootRegistry.Combine(root.Path, p.DiskPath)))
                {
                    (item.Included, item.Warning) = (false, "Something is at that path now (scan again)");
                }

                taken.Add(p.DiskPath);
            }

            MarkReadOnly(item);
            items.Add(item);
        }

        if (plan is not null)
        {
            return await MergeAsync(db, plan, items, [], ct);
        }

        var created = new ActionPlan
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Extract {items.Count} file(s) again from the archive" : name.Trim(),
            Source = PlanSource.Manual,
            Status = PlanStatus.Draft,
            Conflict = ConflictPolicy.Skip,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        created.Items.AddRange(items);
        db.ActionPlans.Add(created);
        await db.SaveChangesAsync(ct);
        return new PlanAddResult(created.Id, created.Name, created.Items.Count, created.Items.Count, 0);
    }

    private static async Task<ActionPlan> LoadDraftAsync(AnalyzeDbContext db, int planId, CancellationToken ct)
    {
        var plan = await db.ActionPlans.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new KeyNotFoundException($"Plan {planId} not found.");
        return plan.Status == PlanStatus.Draft ? plan : throw new InvalidOperationException("Only draft plans can be changed.");
    }

    /// <summary>
    /// Puts new items into a draft: items of the same entries (or of <paramref name="selected"/>) and items inside a new folder are
    /// replaced; a new item inside a folder the plan already covers is left out with a warning.
    /// </summary>
    private static async Task<PlanAddResult> MergeAsync(AnalyzeDbContext db, ActionPlan plan, List<ActionItem> items, HashSet<long> selected, CancellationToken ct)
    {
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
        MarkKeptConflicts(plan);
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new PlanAddResult(plan.Id, plan.Name, plan.Items.Count, items.Count, replaced.Count);
    }

    /// <summary>The item takes its file (or folder) away: trash or delete. A move or a hard link keeps the data.</summary>
    public static bool Removes(ActionItem i) => i.Op is ActionOp.Trash or ActionOp.Delete;

    /// <summary>The item's file is that path, or its folder contains it.</summary>
    public static bool Covers(ActionItem i, int rootId, string path) =>
        i.RootId == rootId && (i.SourcePath == path || (i.IsDir && Inside(path, i.SourcePath)));

    private const string KeptRemoved = "The kept copy of a duplicate this plan removes: only the first of the two that runs is applied";
    private const string KeepGone = "Its kept copy is removed by another item of this plan: only the first of the two that runs is applied";

    /// <summary>
    /// Flags items that remove a file another item keeps as the copy of a removed duplicate (and that duplicate's item). At apply the
    /// second one to run is skipped, so one copy always stays; the warning shows it in the review.
    /// </summary>
    private static void MarkKeptConflicts(ActionPlan plan)
    {
        foreach (var i in plan.Items.Where(i => i.Warning is KeptRemoved or KeepGone))
        {
            i.Warning = null;
        }

        var dupes = plan.Items.Where(i => i.Included && Removes(i) && i.KeepPath is not null).ToList();
        var plain = plan.Items.Where(i => i.Included && Removes(i) && i.KeepPath is null).ToList();
        if (dupes.Count == 0 || plain.Count == 0)
        {
            return;
        }

        // By path: the file itself or one of the folders above it.
        var byPath = plain.GroupBy(p => (p.RootId, p.SourcePath)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var d in dupes)
        {
            var root = d.KeepRootId ?? d.RootId;
            foreach (var path in SelfAndParents(d.KeepPath!))
            {
                if (byPath.TryGetValue((root, path), out var hits))
                {
                    foreach (var p in hits.Where(p => Covers(p, root, d.KeepPath!)))
                    {
                        p.Warning ??= KeptRemoved;
                        d.Warning ??= KeepGone;
                    }
                }
            }
        }
    }

    /// <summary>"a/b/c", "a/b", "a", "" (the root).</summary>
    private static IEnumerable<string> SelfAndParents(string path)
    {
        while (true)
        {
            yield return path;
            if (path.Length == 0)
            {
                yield break;
            }

            var cut = path.LastIndexOf('/');
            path = cut < 0 ? "" : path[..cut];
        }
    }

    private static bool Inside(string path, string dir) => dir.Length == 0 || path.StartsWith(dir + "/", StringComparison.Ordinal);

    private static void Validate(ManualPlanRequest req)
    {
        if (req.Op == ActionOp.Hardlink)
        {
            throw new ArgumentException("Hard links are created by the dedupe planner.");
        }

        if (req.Op == ActionOp.Extract)
        {
            throw new ArgumentException("Files are extracted again from an extracted archive's page (Repair from the archive).");
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
        ValidateDedupe(req);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var plan = new ActionPlan
        {
            Name = string.IsNullOrWhiteSpace(req.Name) ? $"Dedupe: keep {req.Keep.ToString().ToLowerInvariant()}, {req.Op.ToString().ToLowerInvariant()} others" : req.Name.Trim(),
            Source = PlanSource.Dedupe,
            Status = PlanStatus.Draft,
            Conflict = ConflictPolicy.Skip,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        plan.Items.AddRange(await BuildDedupeItemsAsync(db, req, ct));
        await MarkInUseAsync(db, plan.Items, ct);
        db.ActionPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        return plan;
    }

    private static void ValidateDedupe(DedupePlanRequest req)
    {
        if (req.Op is not (ActionOp.Trash or ActionOp.Delete or ActionOp.Hardlink))
        {
            throw new ArgumentException("Dedupe can trash, delete or hard-link copies.");
        }
    }

    /// <summary>One item per copy that is not kept, not yet in a plan.</summary>
    private async Task<List<ActionItem>> BuildDedupeItemsAsync(AnalyzeDbContext db, DedupePlanRequest req, CancellationToken ct)
    {
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

        var items = new List<ActionItem>();

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
                items.Add(item);
                if (items.Count >= MaxItems)
                {
                    break;
                }
            }
        }

        return items;
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
