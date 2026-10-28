using System.Text.Json;
using System.Text.Json.Serialization;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Infrastructure.Settings;

/// <summary>Caches <see cref="AppSettings"/> stored in the settings table and notifies listeners on change.</summary>
public sealed class SettingsStore(IServiceScopeFactory scopes)
{
    private const string Key = "app";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private volatile AppSettings? _current;
    private readonly SemaphoreSlim _lock = new(1, 1);

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

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AnalyzeDbContext>();
            var json = JsonSerializer.Serialize(settings, Json);
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
            _current = settings;
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke(settings);
    }

    public static AppSettings Defaults() => new()
    {
        IgnorePatterns = [],
    };
}
