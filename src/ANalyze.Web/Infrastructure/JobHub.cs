using ANalyze.Infrastructure.Jobs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;

namespace ANalyze.Web.Infrastructure;

/// <summary>Pushes live job progress (scan, apply, AI) to the UI.</summary>
public sealed class JobHub(JobBoard board) : Hub
{
    public IReadOnlyCollection<JobProgress> GetStatus() => board.All;
}

public sealed class JobHubObserver(IHubContext<JobHub> hub, IMemoryCache cache) : IJobObserver
{
    public void OnProgress(JobProgress progress)
    {
        if (!progress.IsActive)
        {
            // Cached summaries (sidebar counters, overview) are stale once a job finishes.
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
