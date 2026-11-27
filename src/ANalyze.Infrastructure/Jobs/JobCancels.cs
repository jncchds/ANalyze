using System.Collections.Concurrent;

namespace ANalyze.Infrastructure.Jobs;

/// <summary>
/// Cancellation of the jobs that have no queue of their own (integration syncs, applying rules), by job kind, so
/// <c>POST /jobs/{kind}/cancel</c> can stop any job the sidebar lists.
/// </summary>
public sealed class JobCancels
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    /// <summary>Registers a running job; disposing the result unregisters it and disposes its token source.</summary>
    public Registration Begin(string kind)
    {
        var cts = new CancellationTokenSource();
        _running[kind] = cts;
        return new Registration(this, kind, cts);
    }

    public bool Cancel(string kind)
    {
        if (!_running.TryGetValue(kind, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    public IReadOnlyCollection<string> Running => [.. _running.Keys];

    public sealed class Registration(JobCancels owner, string kind, CancellationTokenSource cts) : IDisposable
    {
        public CancellationToken Token => cts.Token;

        public void Dispose()
        {
            owner._running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(kind, cts));
            cts.Dispose();
        }
    }
}
