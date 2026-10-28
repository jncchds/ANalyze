using ANalyze.Infrastructure.Jobs;

namespace ANalyze.Scanner;

/// <summary>Mutable counters for a running scan; snapshots are reported to the <see cref="JobBoard"/>.</summary>
internal sealed class ScanContext(JobBoard board, long scanId, string title, DateTimeOffset startedAt, CancellationToken ct)
{
    public CancellationToken Ct { get; } = ct;
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
            Bytes = Phase == "hashing" ? HashedBytes : BytesSeen,
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
}
