using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANalyze.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddAnalyzeInfrastructure(this IServiceCollection services, string databasePath)
    {
        var sqlite = new SqliteSetup(databasePath);
        services.AddSingleton(sqlite);
        services.AddDbContextFactory<AnalyzeDbContext>(o => o
            .UseSqlite(sqlite.ConnectionString, x => x.CommandTimeout(120))
            .AddInterceptors(new SqlitePragmaInterceptor()));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<RootRegistry>();
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<JobLock>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<JobBoard>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }
}
