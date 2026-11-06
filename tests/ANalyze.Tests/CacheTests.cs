using ANalyze.Web.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Tests;

public class CacheTests
{
    private sealed class Box(int n)
    {
        public int N { get; } = n;
    }

    [Fact]
    public async Task Stale_values_are_served_at_once_and_recomputed_once_in_the_background()
    {
        await using var app = new TestApp();
        var caches = app.Services.GetRequiredService<Caches>();
        caches.MinGap = TimeSpan.Zero;
        var calls = 0;
        var gate = new TaskCompletionSource();

        async Task<Box> Compute()
        {
            var n = Interlocked.Increment(ref calls);
            if (n > 1)
            {
                await gate.Task;
            }

            return new Box(n);
        }

        // The first request waits for the value; concurrent first requests share one computation.
        var first = await Task.WhenAll(caches.GetAsync("k", Compute), caches.GetAsync("k", Compute));
        Assert.All(first, b => Assert.Equal(1, b.N));
        Assert.Equal(1, calls);

        // Stale: the old value comes back at once while one recomputation runs (blocked here on the gate).
        caches.Invalidate();
        Assert.Equal(1, (await caches.GetAsync("k", Compute)).N);
        Assert.Equal(1, (await caches.GetAsync("k", Compute)).N);
        await WaitFor(() => Volatile.Read(ref calls) == 2);
        Assert.Equal(2, calls);

        gate.SetResult();
        await WaitFor(async () => (await caches.GetAsync("k", Compute)).N == 2);
        Assert.Equal(2, calls);
    }

    private static Task WaitFor(Func<bool> done) => WaitFor(() => Task.FromResult(done()));

    private static async Task WaitFor(Func<Task<bool>> done)
    {
        for (var i = 0; i < 200 && !await done(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(await done());
    }
}
