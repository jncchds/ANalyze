using ANalyze.Infrastructure.Jobs;
using Microsoft.AspNetCore.SignalR;
using ANalyze.Infrastructure.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace ANalyze.Web.Infrastructure;

/// <summary>Pushes live job progress (scan, apply, AI) to the UI.</summary>
public sealed class JobHub(JobBoard board) : Hub
{
    public IReadOnlyCollection<JobProgress> GetStatus() => board.All;
}

public sealed class JobHubObserver(IHubContext<JobHub> hub, IMemoryCache cache) : IJobObserver
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _phases = new();

    public void OnProgress(JobProgress progress)
    {
        var phaseChanged = _phases.TryGetValue(progress.Kind, out var before) && before != progress.Phase;
        _phases[progress.Kind] = progress.Phase;
        if (!progress.IsActive || phaseChanged)
        {
            // Cached summaries (sidebar counters, overview) are stale once a job finishes or moves on to its next step.
            Caches.Invalidate(cache);
        }

        _ = hub.Clients.All.SendAsync("progress", progress);
    }
}

public static class Caches
{
    private static CancellationTokenSource _reset = new();

    public static Microsoft.Extensions.Primitives.IChangeToken Token => new Microsoft.Extensions.Primitives.CancellationChangeToken(_reset.Token);

    public static void Invalidate(IMemoryCache _)
    {
        var old = Interlocked.Exchange(ref _reset, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    public static async Task<T> GetAsync<T>(IMemoryCache cache, string key, Func<Task<T>> factory)
    {
        if (cache.TryGetValue(key, out T? v) && v is not null)
        {
            return v;
        }

        v = await factory();
        cache.Set(key, v, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) }.AddExpirationToken(Token));
        return v;
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
