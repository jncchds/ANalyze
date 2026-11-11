using System.Collections.Concurrent;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Integrations.Items;
using Microsoft.Extensions.Logging;

namespace ANalyze.Integrations.Backup;

/// <summary>Progress and outcome of one file comparison.</summary>
/// <param name="Status">running, done or failed.</param>
/// <param name="Phase">What it waits for: "reading" or "waiting" (the parent reads another file first).</param>
public sealed record CompareStatus(long EntryId, string Status, string? Phase, DateTimeOffset StartedAt, long Size, long ParentSize,
    long Read, long ParentRead, DiffReport? Report, string? Error);

/// <summary>
/// Compares one file with its copy on the parent to explain how they differ: both instances read their copy at the same time into a
/// <see cref="FileMap"/> (the parent through <c>/backup/probe</c>) and <see cref="BackupDiff"/> tells what happened. Runs in the
/// background, one at a time; the last results stay in memory for the page to poll.
/// </summary>
public sealed class BackupCompare(IntegrationHost host, ParentIntegration plugin, RootRegistry roots, SqliteSetup sqlite, ILogger<BackupCompare> log)
{
    private const int Keep = 100;

    private sealed class Run
    {
        public required long EntryId;
        public required DateTimeOffset StartedAt;
        public readonly CancellationTokenSource Cts = new();
        public volatile string Status = "running";
        public volatile string? Phase = "waiting";
        public long Size;
        public long ParentSize;
        public long Read;
        public long ParentRead;
        public DiffReport? Report;
        public string? Error;

        public CompareStatus Snapshot() => new(EntryId, Status, Status == "running" ? Phase : null, StartedAt, Size, ParentSize,
            Interlocked.Read(ref Read), Interlocked.Read(ref ParentRead), Report, Error);
    }

    private readonly ConcurrentDictionary<long, Run> _runs = new();

    /// <summary>Local reads one at a time, like the parent's: the disks may be slow.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CompareStatus? Get(long entryId) => _runs.TryGetValue(entryId, out var r) ? r.Snapshot() : null;

    /// <summary>Starts a comparison, or returns the one already running for that file.</summary>
    public CompareStatus Start(long entryId)
    {
        Run? created = null;
        var run = _runs.AddOrUpdate(entryId,
            _ => created = New(),
            (_, old) => old.Status == "running" ? old : created = New());
        if (ReferenceEquals(run, created))
        {
            _ = Task.Run(() => ExecuteAsync(run));
        }

        Trim();
        return run.Snapshot();

        Run New() => new() { EntryId = entryId, StartedAt = DateTimeOffset.UtcNow };
    }

    public bool Cancel(long entryId)
    {
        if (_runs.TryGetValue(entryId, out var r) && r.Status == "running")
        {
            r.Cts.Cancel();
            return true;
        }

        return false;
    }

    private void Trim()
    {
        foreach (var old in _runs.Values.Where(r => r.Status != "running").OrderByDescending(r => r.StartedAt).Skip(Keep))
        {
            _runs.TryRemove(old.EntryId, out _);
        }
    }

    private async Task ExecuteAsync(Run run)
    {
        var ct = run.Cts.Token;
        try
        {
            run.Report = await CompareAsync(run, ct);
            run.Status = "done";
        }
        catch (OperationCanceledException)
        {
            run.Error = "Cancelled.";
            run.Status = "failed";
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or FormatException or KeyNotFoundException or UriFormatException)
        {
            log.LogInformation("Backup comparison of entry {Entry} failed: {Message}", run.EntryId, e.Message);
            run.Error = e.Message;
            run.Status = "failed";
        }
    }

    private async Task<DiffReport> CompareAsync(Run run, CancellationToken ct)
    {
        var settings = await host.LoadAsync(plugin.Id, ct);
        var snap = await plugin.LoadSnapshotAsync(ct) ?? throw new InvalidOperationException("Compare the backup first.");

        (int Root, string Rel, long? ParentSize, string? ParentHash, string? Hash, long? HashedAt, long? CorruptAt) row;
        using (var c = sqlite.Open())
        {
            row = Db.Query(c, """
                SELECT b.LocalRootId, b.LocalRelPath, b.ParentSize, b.ParentHash, e.FullHash, e.HashedAt, e.CorruptAt
                FROM BackupFiles b JOIN Entries e ON e.Id = b.EntryId WHERE b.EntryId = $e AND b.IsDir = 0
                """, r => (r.GetInt32(0), r.GetString(1), r.IsDBNull(2) ? (long?)null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? (long?)null : r.GetInt64(5), r.IsDBNull(6) ? (long?)null : r.GetInt64(6)),
                ("$e", run.EntryId)).FirstOrDefault();
        }

        if (row.Rel is null)
        {
            throw new InvalidOperationException("This file is not part of the backup comparison.");
        }

        if (row.ParentSize is null)
        {
            throw new InvalidOperationException($"{snap.ParentName} does not have this file.");
        }

        var parentPath = BackupEndpoints.ParentPath(roots, snap, row.Root, row.Rel) ?? throw new InvalidOperationException("No path on the parent maps to this file.");
        var parentRoot = snap.Info.Roots
            .Where(r => parentPath.StartsWith(r.Path.TrimEnd('/') + "/", StringComparison.Ordinal))
            .MaxBy(r => r.Path.Length) ?? throw new InvalidOperationException($"{parentPath} is in none of {snap.ParentName}'s roots.");
        var parentRel = parentPath[(parentRoot.Path.TrimEnd('/').Length + 1)..];

        var local = roots.ResolveForRead(row.Root, row.Rel);
        var size = new FileInfo(local).Length;
        run.Size = size;
        run.ParentSize = row.ParentSize.Value;
        var block = FileMap.ChooseBlock(Math.Max(size, row.ParentSize.Value));
        var compare = Math.Min(size, row.ParentSize.Value);

        // Both copies are read at the same time; the parent's read stops when this side fails.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var parentTask = plugin.Client(settings).ProbeAsync(parentRoot.Id, parentRel, block, size,
            n => { Interlocked.Exchange(ref run.ParentRead, n); run.Phase = "reading"; }, () => run.Phase = "waiting", stop.Token);

        FileMap here;
        await _gate.WaitAsync(ct);
        try
        {
            run.Phase = "reading";
            here = await FileMap.ComputeAsync(local, block, compare, n => Interlocked.Add(ref run.Read, n), ct);
            var probe = await parentTask ?? throw new InvalidOperationException($"{snap.ParentName} no longer has this file ({parentPath}).");
            run.ParentSize = probe.Size;
            Interlocked.Exchange(ref run.ParentRead, probe.Size);
            var parent = FileMap.FromProbe(probe);

            // The parent's copy changed size since the sync, or it chose another block size: read here again to match it.
            if (parent.Block != here.Block || here.Compare != Math.Min(here.Size, parent.Size))
            {
                Interlocked.Exchange(ref run.Read, 0);
                here = await FileMap.ComputeAsync(local, parent.Block, Math.Min(here.Size, parent.Size), n => Interlocked.Add(ref run.Read, n), ct);
            }

            return BackupDiff.Analyze(here, parent,
                new DiffIndex(row.Hash, row.HashedAt, row.CorruptAt),
                new DiffIndex(probe.Index?.Hash ?? row.ParentHash, probe.Index?.HashedAt, probe.Index?.CorruptAt),
                snap.ParentName);
        }
        finally
        {
            _gate.Release();
            if (!parentTask.IsCompleted)
            {
                await stop.CancelAsync();
                await parentTask.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }
    }
}
