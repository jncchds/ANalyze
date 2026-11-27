using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Logging;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANalyze.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAnalyzeInfrastructure(this IServiceCollection services, DbSetup database)
    {
        services.AddSingleton(database);
        services.AddSingleton<IDbContextFactory<AnalyzeDbContext>>(new AnalyzeDbContextFactory(database));
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AnalyzeDbContext>>().CreateDbContext());
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<RootRegistry>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<JobLock>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<JobBoard>();
        services.AddSingleton<JobCancels>();
        services.AddSingleton<ActivityLog>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
