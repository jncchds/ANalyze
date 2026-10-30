using ANalyze.Domain;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Logging;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>What the processor does after a walk; requests that arrive while it works are merged into one.</summary>
/// <param name="Scope">Where detection, categorizing and archive listing look (hashing and duplicates always cover everything).</param>
/// <param name="Full">A full scan: also verify old hashes and look for extractions again.</param>
/// <param name="Changed">The walk added, changed or removed something.</param>
/// <param name="ScanId">The scan whose history row gets the hash count.</param>
public sealed record ProcessRequest(Scope Scope, bool Full, bool SkipHashing, bool Changed, long? ScanId)
{
    /// <summary>Re-apply the rules in scope (off for the pass that resumes unfinished work at startup).</summary>
    public bool Categorize { get; init; } = true;

    public static ProcessRequest Merge(ProcessRequest a, ProcessRequest b) => new(
        MergeScope(a.Scope, b.Scope), a.Full || b.Full, a.SkipHashing && b.SkipHashing, a.Changed || b.Changed, b.ScanId ?? a.ScanId)
    {
        Categorize = a.Categorize || b.Categorize,
    };

    /// <summary>The smallest scope that covers both.</summary>
    public static Scope MergeScope(Scope a, Scope b)
    {
        if (a.RootId is null || b.RootId is null || a.RootId != b.RootId)
        {
            return a == b ? a : Scope.Everything;
        }

        if (string.IsNullOrEmpty(a.SubPath) || string.IsNullOrEmpty(b.SubPath))
        {
            return new Scope(a.RootId, null);
        }

        if (a.SubPath == b.SubPath || b.SubPath.StartsWith(a.SubPath + "/", StringComparison.Ordinal))
        {
            return a;
        }

        return a.SubPath.StartsWith(b.SubPath + "/", StringComparison.Ordinal) ? b : new Scope(a.RootId, null);
    }
}

/// <summary>Told about every batch of content hashes stored (e.g. the backup comparison re-checks those files at once).</summary>
public interface IHashListener
{
    /// <summary>Runs on the processor's write connection, after the batch is committed.</summary>
    void OnHashed(SqliteConnection conn, IReadOnlyList<long> entryIds);
}

/// <summary>
/// Everything after a scan's walk, in the background under a shared <see cref="JobLock"/>: detect → categorize → list archives and
/// find their extractions → hash every file (if enabled) → duplicates → verify old hashes (full scans) → optimize. Every phase works
/// from the state in the index (not detected yet, no hash yet…), so it can stop at any batch: when an exclusive job (the next walk,
/// a plan) waits, the processor lets it run and then starts over from the first phase not finished yet — from the start, with the
/// scopes merged, when another walk asked for processing meanwhile.
/// </summary>
public sealed class ScanProcessor(
    SqliteSetup sqlite,
    IDbContextFactory<AnalyzeDbContext> dbf,
    RootRegistry roots,
    SettingsStore settings,
    Categorizer categorizer,
    JobBoard board,
    JobLock jobLock,
    IEnumerable<IHashListener> listeners,
    ILogger<ScanProcessor> log)
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private ProcessRequest? _pending;
    private CancellationTokenSource? _current;

    /// <summary>Raised after a pass finished (types, categories and hashes are current).</summary>
    public event Action? Processed;

    public bool IsRunning => _current is not null;

    /// <summary>Nothing running and nothing waiting.</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return _current is null && _pending is null;
            }
        }
    }

    public void Request(ProcessRequest request)
    {
        lock (_gate)
        {
            _pending = _pending is null ? request : ProcessRequest.Merge(_pending, request);
        }

        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    /// <summary>Stops the running pass and drops the waiting request.</summary>
    public bool Cancel()
    {
        lock (_gate)
        {
            _pending = null;
        }

        var cts = _current;
        if (cts is null)
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    internal async Task RunLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            await _signal.WaitAsync(stopping);
            ProcessRequest? req;
            CancellationTokenSource? cts = null;
            lock (_gate)
            {
                // Taken and marked running at once, so IsIdle never sees a gap between the two.
                req = TakePending();
                if (req is not null)
                {
                    _current = cts = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                }
            }

            if (req is not null)
            {
                try
                {
                    await RunAsync(req, cts!);
                }
                finally
                {
                    lock (_gate)
                    {
                        _current = null;
                    }

                    cts!.Dispose();
                }
            }
        }
    }

    private ProcessRequest? TakePending()
    {
        lock (_gate)
        {
            var p = _pending;
            _pending = null;
            return p;
        }
    }

    private async Task RunAsync(ProcessRequest req, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var started = DateTimeOffset.UtcNow;
        var done = new HashSet<string>();
        long hashed = 0;
        string state = JobStates.Completed;
        string? message = null;
        ScanContext? ctx = null;
        var disks = new DiskLoad(roots.All.Where(r => !r.Missing).Select(r => r.Path));
        _lastPace = 0;
        log.LogInformation("Processing started: types, categories, archives and hashes in the background");
        try
        {
            while (true)
            {
                if (jobLock.ExclusiveWanted)
                {
                    board.Report(new JobProgress
                    {
                        Kind = JobKinds.Processing, State = JobStates.Queued, Title = "Processing", StartedAt = started,
                        Message = $"Waiting for {jobLock.Holder ?? "a scan"}",
                    }, force: true);
                }

                using var lease = await jobLock.AcquireSharedAsync("processing", ct);
                ScanContext? current = null;
                ctx = current = new ScanContext(board, req.ScanId ?? 0, "Processing", started, ct, log, JobKinds.Processing)
                {
                    Lease = lease,
                    OnHashed = Notify,
                    Pace = () => Pace(current!, disks),
                };
                try
                {
                    await PhasesAsync(ctx, () => req, r => req = r, done);
                    hashed += ctx.Hashed;
                    break;
                }
                catch (YieldException)
                {
                    hashed += ctx.Hashed;
                    log.LogInformation("Processing steps aside for {Holder} and continues afterwards", jobLock.Holder ?? "a scan");
                }
                catch (RestartException)
                {
                    hashed += ctx.Hashed;
                    log.LogInformation("Processing starts over: another scan finished meanwhile");
                }
            }

            message = hashed > 0 ? $"{hashed:N0} files hashed" : null;
        }
        catch (OperationCanceledException)
        {
            (state, message) = (JobStates.Cancelled, "Cancelled");
        }
        catch (Exception e)
        {
            log.LogError(e, "Processing failed");
            (state, message) = (JobStates.Failed, e.Message);
        }

        if (ctx is not null)
        {
            ctx.Phase = state;
            ctx.Report(state, message, force: true);
        }

        log.LogInformation("Processing {State} in {Time}{Message}", state, Format.Duration(DateTimeOffset.UtcNow - started), message is null ? "" : ": " + message);
        if (req.ScanId is { } scanId && hashed > 0)
        {
            try
            {
                await using var db = await dbf.CreateDbContextAsync(CancellationToken.None);
                await db.Scans.Where(s => s.Id == scanId).ExecuteUpdateAsync(u => u.SetProperty(s => s.Hashed, s => s.Hashed + hashed), CancellationToken.None);
            }
            catch (Exception e) when (e is SqliteException or InvalidOperationException)
            {
                log.LogWarning(e, "Could not store the hash count of scan {Id}", scanId);
            }
        }

        if (state == JobStates.Completed)
        {
            Processed?.Invoke();
        }
    }

    private async Task PhasesAsync(ScanContext ctx, Func<ProcessRequest> get, Action<ProcessRequest> set, HashSet<string> done)
    {
        var s = settings.Current;
        var rootPaths = roots.All.ToDictionary(r => r.Id, r => r.Path);
        using var conn = sqlite.Open();
        using var write = sqlite.Open();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // A walk that finished meanwhile: start over with both scopes.
        void TakeNewer()
        {
            if (TakePending() is { } p)
            {
                set(ProcessRequest.Merge(get(), p));
                done.Clear();
                throw new RestartException();
            }
        }

        async Task Phase(string name, bool when, Func<Task> body)
        {
            TakeNewer();
            if (!when || done.Contains(name))
            {
                return;
            }

            ctx.Pace?.Invoke();
            clock.Restart();
            await body();
            done.Add(name);
        }

        var req = get();
        await Phase("detecting", true, () =>
        {
            Detector.Run(conn, ctx, rootPaths, req.Scope, s.SniffUnknownFiles);
            return Task.CompletedTask;
        });

        await Phase("categorizing", req.Categorize, async () =>
        {
            ctx.StartPhase("categorizing");
            var engine = await categorizer.LoadEngineAsync(ctx.Ct);
            log.LogInformation("Categorizing: applying {Rules} rules to the files in scope", engine.Count);
            ctx.Categorized = Categorizer.Run(conn, engine, req.Scope, (n, total) =>
            {
                ctx.Done = n;
                ctx.Total = total;
                ctx.Report();
            }, ctx.Ct, ctx.CheckYield);
            log.LogInformation("Categorizing finished in {Time}: {Changed} files got another category or tags", Format.Duration(clock.Elapsed), ctx.Categorized);
        });

        await Phase("archives", s.FindExtractedArchives, () =>
        {
            var listed = ArchiveIndexer.Run(conn, write, ctx, rootPaths, req.Scope, s);
            // Extractions can be on any root: look again after full scans and whenever something changed.
            if (req.Full || req.Changed || listed > 0)
            {
                ExtractionMatcher.Run(conn, write, ctx);
            }

            log.LogInformation("Archive phases finished in {Time}", Format.Duration(clock.Elapsed));
            return Task.CompletedTask;
        });

        var hasher = new ContentHasher(conn, write, ctx, rootPaths, s.WorkerThreads());
        await Phase("hashing", !req.SkipHashing && s.HashAllFiles, async () =>
        {
            var before = ctx.Hashed;
            await hasher.HashMissingAsync();
            log.LogInformation("Hashing finished in {Time}: {Count} files hashed", Format.Duration(clock.Elapsed), ctx.Hashed - before);
        });

        await Phase("duplicates", !req.SkipHashing && s.FindDuplicates, async () =>
        {
            var groups = await new DuplicateFinder(conn, write, ctx, rootPaths, Math.Max(1, s.MinDuplicateSize), s.WorkerThreads()).RunAsync();
            log.LogInformation("Duplicate search finished in {Time}: {Groups} sets of identical files", Format.Duration(clock.Elapsed), groups);
        });

        // Verification re-reads a lot of data: only after full scans.
        await Phase("verifying", !req.SkipHashing && req.Full && s.VerifyAfterDays > 0, async () =>
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-s.VerifyAfterDays).ToUnixTimeSeconds();
            var changed = await hasher.VerifyAsync(cutoff, Math.Max(0, s.VerifyMaxGb) * (1L << 30));
            log.LogInformation("Verification finished in {Time}: {Files} files re-read", Format.Duration(clock.Elapsed), ctx.Done);
            if (changed > 0)
            {
                log.LogWarning("{Count} files changed content without a new size or modification time (possible corruption)", changed);
            }
        });

        await Phase("finishing", true, () =>
        {
            ctx.StartPhase("finishing");
            ScanService.Optimize(conn);
            return Task.CompletedTask;
        });
    }

    /// <summary>Disks busier than this with other work make background priority wait.</summary>
    private const double BusyDisk = 0.5;

    private static readonly TimeSpan PaceEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PaceWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan PaceWait = TimeSpan.FromSeconds(10);
    private long _lastPace;

    /// <summary>
    /// Background priority: between batches (no I/O of ours in flight) measures how busy the roots' disks are; while other work keeps
    /// them more than half busy, waits and measures again. Checked at most every few seconds; an exclusive job waiting ends the wait.
    /// </summary>
    private void Pace(ScanContext ctx, DiskLoad disks)
    {
        if (settings.Current.Priority != RunPriority.Background || !DiskLoad.Supported
            || Environment.TickCount64 - Interlocked.Read(ref _lastPace) < PaceEvery.TotalMilliseconds)
        {
            return;
        }

        var waited = false;
        while (true)
        {
            var load = disks.MeasureAsync(PaceWindow, ctx.Ct).GetAwaiter().GetResult();
            Interlocked.Exchange(ref _lastPace, Environment.TickCount64);
            if (load is not { } busy || busy < BusyDisk || ctx.Lease?.YieldWanted == true || settings.Current.Priority != RunPriority.Background)
            {
                if (waited)
                {
                    log.LogInformation("Processing continues: the disks are quiet again");
                    ctx.Current = null;
                    ctx.Report(force: true);
                }

                return;
            }

            if (!waited)
            {
                log.LogInformation("Processing waits: the disks are {Busy:P0} busy with other work (background priority)", busy);
                waited = true;
            }

            ctx.Current = $"waiting: disks {busy:P0} busy with other work";
            ctx.Report(force: true);
            Task.Delay(PaceWait, ctx.Ct).GetAwaiter().GetResult();
        }
    }

    private void Notify(SqliteConnection conn, IReadOnlyList<long> ids)
    {
        foreach (var l in listeners)
        {
            try
            {
                l.OnHashed(conn, ids);
            }
            catch (Exception e) when (e is SqliteException or InvalidOperationException)
            {
                log.LogWarning(e, "{Listener} could not use the new hashes", l.GetType().Name);
            }
        }
    }

    private sealed class RestartException() : Exception("Another scan finished");
}

/// <summary>Runs the processor; at startup it resumes work a restart interrupted (types and hashes still missing).</summary>
internal sealed class ScanProcessorWorker(ScanProcessor processor, SettingsStore settings, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loop = processor.RunLoopAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromSeconds(10), time, stoppingToken);
        if (!settings.Current.ScanOnStartup)
        {
            processor.Request(new ProcessRequest(Scope.Everything, Full: false, SkipHashing: false, Changed: false, ScanId: null) { Categorize = false });
        }

        await loop;
    }
}
