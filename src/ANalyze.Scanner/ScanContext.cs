using System.Diagnostics;
using ANalyze.Infrastructure.Jobs;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>Mutable counters for a running scan; snapshots are reported to the <see cref="JobBoard"/>.</summary>
internal sealed class ScanContext(JobBoard board, long scanId, string title, DateTimeOffset startedAt, CancellationToken ct, ILogger? log = null)
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

    public void Report(string state = JobStates.Running, string? message = null, bool force = false) =>
        board.Report(new JobProgress
        {
            Kind = JobKinds.Scan,
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
