using System.Diagnostics;
using ANalyze.Infrastructure.Jobs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>Mutable counters for a running scan; snapshots are reported to the <see cref="JobBoard"/>.</summary>
internal sealed class ScanContext(JobBoard board, long scanId, string title, DateTimeOffset startedAt, CancellationToken ct, ILogger? log = null, string kind = JobKinds.Scan)
{
    /// <summary>Per-item problems logged one by one per phase; beyond that only counted (and summed up at the end of the phase).</summary>
    public const int ProblemLines = 50;

    private readonly Stopwatch _sinceProgressLine = Stopwatch.StartNew();
    private int _problems;

    public CancellationToken Ct { get; } = ct;
    public ILogger Log { get; } = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    public long ScanId { get; } = scanId;
    public string Phase = "walking";
    public long FilesSeen;
    public long DirsSeen;
    public long BytesSeen;
    public long Added;
    public long Changed;
    public long Removed;
    public long Errors;
    public long Detected;
    public long Categorized;
    public long Hashed;
    public long HashedBytes;
    public long Done;
    public long Total;
    public long TotalBytes;
    public string? Current;

    /// <summary>The shared hold of background processing; null for work that runs alone (the walk).</summary>
    public SharedLease? Lease { get; init; }

    /// <summary>Called with the ids of files whose content hash was just stored (after the batch is committed).</summary>
    public Action<SqliteConnection, IReadOnlyList<long>>? OnHashed { get; init; }

    /// <summary>Called between batches before anything else: waits while the disks are busy with other work (background priority).</summary>
    public Action? Pace { get; init; }

    /// <summary>
    /// Call between batches, after the batch is stored: when an exclusive job (a walk, a plan) waits, stops the phase with
    /// <see cref="YieldException"/> so the processor can step aside and start over from fresh data.
    /// </summary>
    public void CheckYield()
    {
        Ct.ThrowIfCancellationRequested();
        Pace?.Invoke();
        if (Lease?.YieldWanted == true)
        {
            throw new YieldException();
        }
    }

    public void Report(string state = JobStates.Running, string? message = null, bool force = false) =>
        board.Report(new JobProgress
        {
            Kind = kind,
            State = state,
            Phase = Phase,
            Title = title,
            Done = Done,
            Total = Total,
            Bytes = Phase is "hashing" or "duplicates" or "verifying" or "archives" or "checking" ? HashedBytes : BytesSeen,
            TotalBytes = TotalBytes,
            Current = Current,
            Message = message,
            RefId = ScanId,
            StartedAt = startedAt,
            FinishedAt = state is JobStates.Running or JobStates.Queued ? null : DateTimeOffset.UtcNow,
            Counters = new Dictionary<string, long>
            {
                ["files"] = FilesSeen,
                ["dirs"] = DirsSeen,
                ["bytes"] = BytesSeen,
                ["added"] = Added,
                ["changed"] = Changed,
                ["removed"] = Removed,
                ["errors"] = Errors,
                ["detected"] = Detected,
                ["categorized"] = Categorized,
                ["hashed"] = Hashed,
            },
        }, force);

    /// <summary>Starts a phase: resets the counters and the per-phase problem budget.</summary>
    public void StartPhase(string phase, long total = 0, long totalBytes = 0)
    {
        Phase = phase;
        Done = 0;
        Total = total;
        TotalBytes = totalBytes;
        Current = null;
        Interlocked.Exchange(ref _problems, 0);
        _sinceProgressLine.Restart();
        Report(force: true);
    }

    /// <summary>Logs a problem with one file or folder (only the first <see cref="ProblemLines"/> per phase).</summary>
    public void Problem(string message, params object?[] args)
    {
        if (Interlocked.Increment(ref _problems) <= ProblemLines)
        {
#pragma warning disable CA2254 // The template is passed through from the callers' constant strings.
            Log.LogWarning(message, args);
#pragma warning restore CA2254
        }
    }

    /// <summary>Problems in the current phase that were not logged one by one.</summary>
    public int UnloggedProblems => Math.Max(0, _problems - ProblemLines);

    /// <summary>True about every 30 seconds: time for a "still working" line in the log.</summary>
    public bool ProgressLineDue()
    {
        if (_sinceProgressLine.Elapsed < TimeSpan.FromSeconds(30))
        {
            return false;
        }

        _sinceProgressLine.Restart();
        return true;
    }
}

/// <summary>Thrown by <see cref="ScanContext.CheckYield"/>: background processing steps aside for an exclusive job.</summary>
internal sealed class YieldException() : Exception("An exclusive job is waiting");
