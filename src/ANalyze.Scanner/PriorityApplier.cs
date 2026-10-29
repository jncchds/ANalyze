using ANalyze.Domain;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANalyze.Scanner;

/// <summary>Applies <see cref="AppSettings.Priority"/> to the process at startup and whenever it changes.</summary>
internal sealed class PriorityApplier(SettingsStore settings, ILogger<PriorityApplier> log) : IHostedService
{
    private RunPriority? _applied;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += Apply;
        try
        {
            Apply(settings.Current);
        }
        catch (InvalidOperationException)
        {
            Apply(await settings.LoadAsync(cancellationToken));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= Apply;
        return Task.CompletedTask;
    }

    private void Apply(AppSettings s)
    {
        // Full priority at startup is the default: nothing to change.
        if (s.Priority == _applied || (_applied is null && s.Priority == RunPriority.Full))
        {
            _applied = s.Priority;
            return;
        }

        var background = s.Priority == RunPriority.Background;
        var error = Native.SetBackgroundPriority(background);
        _applied = s.Priority;
        if (error is null)
        {
            log.LogInformation(background
                ? "Background mode: low CPU priority (nice {Nice}), idle disk priority and one worker thread, so the server's other services come first"
                : "Full mode: normal priority and {Threads} worker threads", background ? Native.BackgroundNice : s.WorkerThreads());
        }
        else
        {
            log.LogWarning("Could not switch to {Mode} mode: {Error}", background ? "background" : "full", error);
        }
    }
}
