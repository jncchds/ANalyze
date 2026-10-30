using System.Threading.Channels;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Settings;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>Queues scan requests; one scan runs at a time and can be cancelled.</summary>
public sealed class ScanQueue(JobBoard board)
{
    private readonly Channel<ScanRequest> _channel = Channel.CreateUnbounded<ScanRequest>();
    private CancellationTokenSource? _current;
    private int _queued;

    internal ChannelReader<ScanRequest> Reader => _channel.Reader;

    public int Queued => _queued;

    public bool IsRunning => _current is not null;

    public void Enqueue(ScanRequest request)
    {
        Interlocked.Increment(ref _queued);
        _channel.Writer.TryWrite(request);
        if (!IsRunning)
        {
            board.Report(new JobProgress { Kind = JobKinds.Scan, State = JobStates.Queued, Title = "Waiting to start", StartedAt = DateTimeOffset.UtcNow }, force: true);
        }
    }

    public bool Cancel()
    {
        // Drop queued requests too: "cancel" means stop scanning now.
        while (_channel.Reader.TryRead(out _))
        {
            Interlocked.Decrement(ref _queued);
        }

        var cts = _current;
        if (cts is null)
        {
            return false;
        }

        cts.Cancel();
        return true;
    }

    internal CancellationToken Begin(CancellationToken stopping)
    {
        Interlocked.Decrement(ref _queued);
        _current = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        return _current.Token;
    }

    internal void End()
    {
        _current?.Dispose();
        _current = null;
    }
}

/// <summary>Runs queued scans one after the other; <see cref="ScanService"/> takes the job lock itself (alone for the walk).</summary>
internal sealed class ScanWorker(ScanQueue queue, ScanService scanner, ILogger<ScanWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var req in queue.Reader.ReadAllAsync(stoppingToken))
        {
            var ct = queue.Begin(stoppingToken);
            try
            {
                await scanner.RunAsync(req, ct);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogInformation("Scan cancelled before it started");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Scan worker failed");
            }
            finally
            {
                queue.End();
            }
        }
    }
}

/// <summary>Starts scans on the cron schedule from settings, and once on startup when enabled.</summary>
internal sealed class ScanScheduler(ScanQueue queue, SettingsStore settings, TimeProvider time, ILogger<ScanScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);
        if (settings.Current.ScanOnStartup)
        {
            queue.Enqueue(new ScanRequest(Reason: "Startup scan"));
        }

        DateTimeOffset? next = null;
        string? cron = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = settings.Current.ScanCron?.Trim();
            if (current != cron)
            {
                cron = current;
                next = Next(cron, time.GetUtcNow());
            }

            if (next is { } n && time.GetUtcNow() >= n)
            {
                if (!queue.IsRunning && queue.Queued == 0)
                {
                    queue.Enqueue(new ScanRequest(Reason: "Scheduled scan"));
                }

                next = Next(cron, time.GetUtcNow());
            }

            await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
        }
    }

    private DateTimeOffset? Next(string? cron, DateTimeOffset from)
    {
        if (string.IsNullOrEmpty(cron))
        {
            return null;
        }

        try
        {
            return CronExpression.Parse(cron).GetNextOccurrence(from, TimeZoneInfo.Local);
        }
        catch (CronFormatException e)
        {
            log.LogWarning("Invalid scan schedule '{Cron}': {Error}", cron, e.Message);
            return null;
        }
    }
}
