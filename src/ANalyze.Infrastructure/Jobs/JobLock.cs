namespace ANalyze.Infrastructure.Jobs;

/// <summary>
/// Serializes heavy writers (scan, action apply, AI classification) so they never fight over the SQLite write lock
/// or see each other's half-finished state.
/// </summary>
public sealed class JobLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? Holder { get; private set; }

    public async Task<IDisposable> AcquireAsync(string holder, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        Holder = holder;
        return new Releaser(this);
    }

    private sealed class Releaser(JobLock owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                owner.Holder = null;
                owner._gate.Release();
            }
        }
    }
}
