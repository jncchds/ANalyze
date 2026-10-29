using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Scanner;

public static class DependencyInjection
{
    public static IServiceCollection AddAnalyzeScanner(this IServiceCollection services)
    {
        services.AddSingleton<Categorizer>();
        services.AddSingleton<ScanService>();
        services.AddSingleton<ScanQueue>();
        services.AddHostedService<ScanWorker>();
        services.AddHostedService<ScanScheduler>();
        services.AddHostedService<PriorityApplier>();
        return services;
    }
}
