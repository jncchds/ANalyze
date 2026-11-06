using ANalyze.Infrastructure.Jobs;
using Microsoft.AspNetCore.SignalR;
using ANalyze.Infrastructure.Logging;

namespace ANalyze.Web.Infrastructure;

/// <summary>Pushes live job progress (scan, apply, AI) to the UI.</summary>
public sealed class JobHub(JobBoard board) : Hub
{
    public IReadOnlyCollection<JobProgress> GetStatus() => board.All;
}

public sealed class JobHubObserver(IHubContext<JobHub> hub, Caches caches) : IJobObserver
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _phases = new();

    public void OnProgress(JobProgress progress)
    {
        var phaseChanged = _phases.TryGetValue(progress.Kind, out var before) && before != progress.Phase;
        _phases[progress.Kind] = progress.Phase;
        if (!progress.IsActive || phaseChanged)
        {
            // Cached summaries (sidebar counters, overview) are stale once a job finishes or moves on to its next step.
            caches.Invalidate();
        }

        _ = hub.Clients.All.SendAsync("progress", progress);
    }
}

/// <summary>
/// Results of whole-index aggregates (overview, insights, sidebar counters), served stale-while-revalidate: once computed, a value is
/// returned at once even when stale, while one background computation per key brings it up to date and then tells the UI
/// ("refreshed" with the key) to fetch it again. On a slow or busy disk (a scan running) the pages stay usable instead of waiting
/// for, and piling up, aggregates over millions of rows. Only the very first request for a key waits.
/// </summary>
public sealed class Caches(IHubContext<JobHub> hub, ILogger<Caches> log)
{
    /// <summary>A fresh value is recomputed after this long anyway (changes that no job reports).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>A stale value is recomputed at most this often, so a long scan's many steps do not keep the disks busy with aggregates.</summary>
    public TimeSpan MinGap { get; set; } = TimeSpan.FromSeconds(60);

    private sealed class Slot
    {
        public object? Value;
        public long Version = -1;
        public DateTimeOffset At;
        public Task? Running;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Slot> _slots = new();
    private long _version;

    /// <summary>Marks every cached value stale; each is recomputed in the background on its next request.</summary>
    public void Invalidate() => Interlocked.Increment(ref _version);

    public async Task<T> GetAsync<T>(string key, Func<Task<T>> factory)
        where T : class
    {
        var slot = _slots.GetOrAdd(key, _ => new Slot());
        Task running;
        lock (slot)
        {
            var now = DateTimeOffset.UtcNow;
            var stale = slot.Version != Interlocked.Read(ref _version) || now - slot.At > MaxAge;
            if (stale && slot.Running is null)
            {
                // A value computed less than MinGap ago is kept that long before the next computation starts.
                var wait = slot.Value is null ? TimeSpan.Zero : slot.At + MinGap - now;
                slot.Running = Task.Run(() => RefreshAsync(key, slot, factory, wait));
            }

            if (slot.Value is T value)
            {
                return value;
            }

            running = slot.Running!;
        }

        // Nothing computed yet: wait for the computation (its error, if any, goes to this request).
        await running;
        lock (slot)
        {
            return (T)slot.Value!;
        }
    }

    private async Task RefreshAsync<T>(string key, Slot slot, Func<Task<T>> factory, TimeSpan wait)
        where T : class
    {
        var hadValue = slot.Value is not null;
        try
        {
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait);
            }

            // Invalidations during the computation leave the result stale, so the next request starts another.
            var version = Interlocked.Read(ref _version);
            var v = await factory();
            lock (slot)
            {
                (slot.Value, slot.Version, slot.At) = (v, version, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception e) when (hadValue)
        {
            // The stale value stays; the next request tries again.
            log.LogWarning(e, "Refreshing {Key} failed", key);
            return;
        }
        finally
        {
            lock (slot)
            {
                slot.Running = null;
            }
        }

        if (hadValue)
        {
            _ = hub.Clients.All.SendAsync("refreshed", key);
        }
    }
}

/// <summary>Sends new activity log lines to the UI ("logs" event), batched twice a second.</summary>
public sealed class LogPusher(ActivityLog activity, IHubContext<JobHub> hub) : BackgroundService
{
    private readonly System.Threading.Channels.Channel<LogLine> _queue =
        System.Threading.Channels.Channel.CreateBounded<LogLine>(new System.Threading.Channels.BoundedChannelOptions(5000)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        activity.Written += Enqueue;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(500, stoppingToken);
                var batch = new List<LogLine>();
                while (_queue.Reader.TryRead(out var l))
                {
                    batch.Add(l);
                }

                await hub.Clients.All.SendAsync("logs", batch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            activity.Written -= Enqueue;
        }
    }

    private void Enqueue(LogLine line) => _queue.Writer.TryWrite(line);
}
