using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Actions;

public static class DependencyInjection
{
    public static IServiceCollection AddAnalyzeActions(this IServiceCollection services)
    {
        services.AddSingleton<PlanBuilder>();
        services.AddSingleton<PlanExecutor>();
        services.AddSingleton<PlanQueue>();
        services.AddSingleton<TrashService>();
        services.AddHostedService<PlanWorker>();
        services.AddHostedService<TrashJanitor>();
        return services;
    }
}
