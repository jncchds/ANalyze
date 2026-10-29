using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ANalyze.Infrastructure.Jobs;

/// <summary>
/// Serializes everything that writes to the database in the background (scan, action apply, AI classification, rule apply,
/// integration syncs) so they run one after the other and never fight over the SQLite write lock or see each other's
/// half-finished state. Small writes from requests (settings saves) go through <see cref="WriteAsync"/>: written at once
/// when nothing runs, otherwise kept (latest one per key) and written as soon as the running job is done.
/// </summary>
public sealed class JobLock(ILogger<JobLock>? log = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _pending = new();
    private readonly ILogger _log = log ?? NullLogger<JobLock>.Instance;
    private int _flushing;

    public string? Holder { get; private set; }

    /// <summary>Keys of writes waiting for the running job to finish.</summary>
    public IReadOnlyCollection<string> PendingWrites => [.. _pending.Keys];

    public async Task<IDisposable> AcquireAsync(string holder, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        Holder = holder;
        return new Releaser(this);
    }

    /// <summary>
    /// Runs <paramref name="write"/> now when no job holds the lock (true; errors are thrown), else queues it under
    /// <paramref name="key"/>, replacing an older queued write with the same key, and returns false. A queued write runs once
    /// the lock is free; it should read the value to store when it runs, so the latest one is written.
    /// </summary>
    public async Task<bool> WriteAsync(string key, Func<CancellationToken, Task> write, CancellationToken ct)
    {
        if (_gate.Wait(0))
        {
            Holder = "write:" + key;
            try
            {
                _pending.TryRemove(key, out _);
                await write(ct);
            }
            finally
            {
                Holder = null;
                _gate.Release();
            }

            if (!_pending.IsEmpty)
            {
                ScheduleFlush();
            }

            return true;
        }

        _pending[key] = write;
        _log.LogInformation("Saving {Key} once {Holder} has finished (the database is busy)", key, Holder ?? "the running job");
        ScheduleFlush();
        return false;
    }

    private void ScheduleFlush()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                using (await AcquireAsync("saving settings", CancellationToken.None))
                {
                    foreach (var key in _pending.Keys.ToList())
                    {
                        if (!_pending.TryRemove(key, out var write))
                        {
                            continue;
                        }

                        try
                        {
                            await write(CancellationToken.None);
                            _log.LogInformation("Saved {Key}", key);
                        }
                        catch (Exception e)
                        {
                            _log.LogError(e, "Saving {Key} failed", key);
                        }
                    }
                }

                Volatile.Write(ref _flushing, 0);
                // A write queued while this flush was finishing would otherwise wait for the next one.
                if (_pending.IsEmpty || Interlocked.Exchange(ref _flushing, 1) == 1)
                {
                    return;
                }
            }
        });
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
