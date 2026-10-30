using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ANalyze.Infrastructure.Jobs;

/// <summary>
/// Reader/writer lock over the index. <b>Exclusive</b> holders change the tree itself (the scan's walk, applying a plan, removing
/// ignored folders) and run alone. <b>Shared</b> holders only fill in or read data about entries that exist (type detection,
/// categorizing, hashing, duplicates, AI, integration syncs, settings saves) and run side by side. Exclusive requests win: once one
/// waits, no new shared holder starts, and running ones give way at their next <see cref="SharedLease.YieldAsync"/> (between batches).
/// Shared work must therefore cope with entries that vanished or changed while it waited: write only where the row is still the
/// same (same size and mtime), skip ids that no longer exist.
/// Small writes from requests (settings saves) go through <see cref="WriteAsync"/>: written at once unless an exclusive job runs or
/// waits, otherwise kept (latest one per key) and written as soon as it is done.
/// </summary>
public sealed class JobLock(ILogger<JobLock>? log = null)
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _pending = new();
    private readonly Dictionary<string, int> _shared = new();
    private readonly ILogger _log = log ?? NullLogger<JobLock>.Instance;
    private TaskCompletionSource _noReaders = Done();
    private TaskCompletionSource _noWriters = Done();
    private int _readers;
    private int _writers;
    private int _flushing;

    /// <summary>The exclusive holder (running), or null.</summary>
    public string? Holder { get; private set; }

    /// <summary>True while an exclusive job runs or waits: shared work should give way at its next batch.</summary>
    public bool ExclusiveWanted => Volatile.Read(ref _writers) > 0;

    /// <summary>Names of the shared holders running now.</summary>
    public IReadOnlyCollection<string> SharedHolders
    {
        get
        {
            lock (_sync)
            {
                return [.. _shared.Keys];
            }
        }
    }

    /// <summary>Keys of writes waiting for the exclusive job to finish.</summary>
    public IReadOnlyCollection<string> PendingWrites => [.. _pending.Keys];

    /// <summary>Runs alone: waits for running exclusive and shared holders; new shared holders wait for it.</summary>
    public async Task<IDisposable> AcquireAsync(string holder, CancellationToken ct)
    {
        lock (_sync)
        {
            if (_writers++ == 0)
            {
                _noWriters = New();
            }
        }

        var gate = false;
        try
        {
            await _exclusive.WaitAsync(ct);
            gate = true;
            while (true)
            {
                Task wait;
                lock (_sync)
                {
                    if (_readers == 0)
                    {
                        Holder = holder;
                        break;
                    }

                    wait = _noReaders.Task;
                }

                await wait.WaitAsync(ct);
            }
        }
        catch
        {
            if (gate)
            {
                _exclusive.Release();
            }

            LeaveWriter();
            throw;
        }

        return new Releaser(this);
    }

    /// <summary>Runs next to other shared holders; waits while an exclusive job runs or waits.</summary>
    public async Task<SharedLease> AcquireSharedAsync(string holder, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_sync)
            {
                if (_writers == 0)
                {
                    EnterReader(holder);
                    return new SharedLease(this, holder);
                }

                wait = _noWriters.Task;
            }

            await wait.WaitAsync(ct);
        }
    }

    private bool TryAcquireShared(string holder)
    {
        lock (_sync)
        {
            if (_writers != 0)
            {
                return false;
            }

            EnterReader(holder);
            return true;
        }
    }

    private void EnterReader(string holder)
    {
        if (_readers++ == 0)
        {
            _noReaders = New();
        }

        _shared[holder] = _shared.GetValueOrDefault(holder) + 1;
    }

    internal void ReleaseShared(string holder)
    {
        lock (_sync)
        {
            if (_shared.TryGetValue(holder, out var n))
            {
                if (n <= 1)
                {
                    _shared.Remove(holder);
                }
                else
                {
                    _shared[holder] = n - 1;
                }
            }

            if (--_readers == 0)
            {
                _noReaders.TrySetResult();
            }
        }
    }

    private void LeaveWriter()
    {
        lock (_sync)
        {
            if (--_writers == 0)
            {
                _noWriters.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="write"/> now (shared) unless an exclusive job runs or waits (true; errors are thrown), else queues it
    /// under <paramref name="key"/>, replacing an older queued write with the same key, and returns false. A queued write runs once
    /// the exclusive job is done; it should read the value to store when it runs, so the latest one is written.
    /// </summary>
    public async Task<bool> WriteAsync(string key, Func<CancellationToken, Task> write, CancellationToken ct)
    {
        var holder = "write:" + key;
        if (TryAcquireShared(holder))
        {
            try
            {
                _pending.TryRemove(key, out _);
                await write(ct);
            }
            finally
            {
                ReleaseShared(holder);
            }

            return true;
        }

        _pending[key] = write;
        _log.LogInformation("Saving {Key} once {Holder} has finished (the index is being changed)", key, Holder ?? "the running job");
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
                using (await AcquireSharedAsync("saving settings", CancellationToken.None))
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

    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource Done()
    {
        var t = New();
        t.SetResult();
        return t;
    }

    private sealed class Releaser(JobLock owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                lock (owner._sync)
                {
                    owner.Holder = null;
                }

                owner._exclusive.Release();
                owner.LeaveWriter();
            }
        }
    }
}

/// <summary>A shared hold on the <see cref="JobLock"/>. Long work calls <see cref="YieldAsync"/> between batches.</summary>
public sealed class SharedLease : IDisposable
{
    private readonly JobLock _owner;
    private readonly string _holder;
    private bool _held = true;

    internal SharedLease(JobLock owner, string holder)
    {
        _owner = owner;
        _holder = holder;
    }

    /// <summary>An exclusive job waits: finish the current batch, then call <see cref="YieldAsync"/>.</summary>
    public bool YieldWanted => _owner.ExclusiveWanted;

    /// <summary>
    /// Lets a waiting exclusive job run and takes the lease back afterwards. Returns true when it did (the index may have changed
    /// meanwhile: re-read what the work is based on), false when nothing waited.
    /// </summary>
    public async Task<bool> YieldAsync(CancellationToken ct)
    {
        if (!_owner.ExclusiveWanted)
        {
            return false;
        }

        _held = false;
        _owner.ReleaseShared(_holder);
        var lease = await _owner.AcquireSharedAsync(_holder, ct);
        // The new lease's hold is this one's from now on.
        lease.Detach();
        _held = true;
        return true;
    }

    private void Detach() => _held = false;

    public void Dispose()
    {
        if (_held)
        {
            _held = false;
            _owner.ReleaseShared(_holder);
        }
    }
}
