using System.Collections.Concurrent;

namespace ANalyze.Infrastructure.Jobs;

public static class JobKinds
{
    public const string Scan = "scan";
    public const string Apply = "apply";
    public const string Ai = "ai";
}

public static class JobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>Live state of a background job (scan, plan apply, AI run), pushed to the UI over SignalR.</summary>
public sealed record JobProgress
{
    public required string Kind { get; init; }
    public required string State { get; init; }
    public string? Phase { get; init; }
    public string? Title { get; init; }
    public long Done { get; init; }
    public long Total { get; init; }
    public long Bytes { get; init; }
    public long TotalBytes { get; init; }
    public string? Current { get; init; }
    public string? Message { get; init; }
    public long? RefId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public IReadOnlyDictionary<string, long>? Counters { get; init; }

    public bool IsActive => State is JobStates.Running or JobStates.Queued;
}

public interface IJobObserver
{
    void OnProgress(JobProgress progress);
}

/// <summary>Holds the latest progress per job kind and forwards it to observers (throttled while running).</summary>
public sealed class JobBoard(IEnumerable<IJobObserver> observers, TimeProvider time)
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(300);
    private readonly ConcurrentDictionary<string, JobProgress> _latest = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public IReadOnlyCollection<JobProgress> All => _latest.Values.ToList();

    public JobProgress? Get(string kind) => _latest.TryGetValue(kind, out var p) ? p : null;

    public void Report(JobProgress progress, bool force = false)
    {
        var previous = Get(progress.Kind);
        _latest[progress.Kind] = progress;
        var now = time.GetUtcNow();
        var stateChanged = previous is null || previous.State != progress.State || previous.Phase != progress.Phase;
        if (!force && !stateChanged && _lastSent.TryGetValue(progress.Kind, out var last) && now - last < Throttle)
        {
            return;
        }

        _lastSent[progress.Kind] = now;
        foreach (var o in observers)
        {
            o.OnProgress(progress);
        }
    }
}
