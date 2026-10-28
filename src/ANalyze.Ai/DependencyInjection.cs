using ANalyze.Infrastructure.Settings;
using ANalyze.Scanner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ANalyze.Ai;

public static class DependencyInjection
{
    public static IServiceCollection AddAnalyzeAi(this IServiceCollection services)
    {
        services.AddSingleton<IAiClientFactory, OpenAiClientFactory>();
        services.AddSingleton<AiService>();
        services.AddHostedService<AiAutoRun>();
        return services;
    }
}

/// <summary>Classifies new uncategorized files after each scan when enabled in settings.</summary>
internal sealed class AiAutoRun(ScanService scanner, AiService ai, SettingsStore settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        scanner.Completed += OnScanCompleted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        scanner.Completed -= OnScanCompleted;
        return Task.CompletedTask;
    }

    private void OnScanCompleted(long scanId)
    {
        var s = settings.Current.Ai;
        if (s.Enabled && s.AutoClassifyAfterScan)
        {
            ai.StartClassify(null, null);
        }
    }
}
