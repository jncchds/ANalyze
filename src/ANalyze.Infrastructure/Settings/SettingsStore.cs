using System.Text.Json;
using System.Text.Json.Serialization;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Infrastructure.Settings;

/// <summary>Caches <see cref="AppSettings"/> stored in the settings table and notifies listeners on change.</summary>
public sealed class SettingsStore(IServiceScopeFactory scopes, JobLock jobLock)
{
    private const string Key = "app";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private volatile AppSettings? _current;

    public event Action<AppSettings>? Changed;

    public AppSettings Current => _current ?? throw new InvalidOperationException("Settings not loaded yet.");

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyzeDbContext>();
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, ct);
        _current = row is null ? Defaults() : JsonSerializer.Deserialize<AppSettings>(row.Value, Json) ?? Defaults();
        return _current;
    }

    /// <summary>
    /// Applies <paramref name="settings"/> at once and stores them. Returns false when a job (a scan, a sync…) holds the database:
    /// the settings are then in effect but written once that job has finished.
    /// </summary>
    public async Task<bool> SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        _current = settings;
        Changed?.Invoke(settings);
        return await jobLock.WriteAsync("settings", PersistAsync, ct);
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AnalyzeDbContext>();
        var json = JsonSerializer.Serialize(Current, Json);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = Key, Value = json });
        }
        else
        {
            row.Value = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public static AppSettings Defaults() => new()
    {
        IgnorePatterns = [],
    };
}
