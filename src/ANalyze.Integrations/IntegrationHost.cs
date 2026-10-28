using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Settings;
using ANalyze.Scanner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ANalyze.Integrations;

public sealed record IntegrationStatus(bool Running, DateTimeOffset? LastSync, IntegrationSyncResult? LastResult);

/// <summary>Stores settings of every integration, runs syncs and reports their progress.</summary>
public sealed class IntegrationHost(
    IEnumerable<IIntegration> integrations,
    IDbContextFactory<AnalyzeDbContext> dbf,
    SecretProtector secrets,
    JobBoard board,
    Microsoft.Extensions.Configuration.IConfiguration config,
    ILogger<IntegrationHost> log)
{
    private readonly object _gate = new();
    public const string Masked = "********";
    private readonly ConcurrentDictionary<string, IntegrationStatus> _status = new();
    private readonly ConcurrentDictionary<string, IntegrationSettings> _cache = new();

    public IReadOnlyList<IIntegration> All { get; } = integrations.OrderBy(i => i.Name).ToList();

    public IIntegration? Get(string id) => All.FirstOrDefault(i => i.Id == id);

    public IntegrationStatus Status(string id) => _status.GetValueOrDefault(id) ?? new IntegrationStatus(false, null, null);

    private static string Key(string id) => "integration:" + id;

    /// <summary>Settings with secrets decrypted (for the integration itself).</summary>
    public async Task<IntegrationSettings> LoadAsync(string id, CancellationToken ct)
    {
        if (_cache.TryGetValue(id, out var cached))
        {
            return Clone(cached);
        }

        var integration = Get(id) ?? throw new KeyNotFoundException(id);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key(id), ct);
        var s = row is null ? FromConfiguration(integration) : JsonSerializer.Deserialize<IntegrationSettings>(row.Value, IntegrationSettings.Json) ?? new IntegrationSettings();
        foreach (var f in integration.Fields)
        {
            if (s.Values[f.Key] is null && f.Default is not null)
            {
                s.Values[f.Key] = f.Default.DeepClone();
            }

            if (f.Secret && s.GetString(f.Key) is { } enc)
            {
                s.Values[f.Key] = secrets.Unprotect(enc);
            }
        }

        _cache[id] = s;
        return Clone(s);
    }

    /// <summary>Settings for the UI: secrets replaced by a mask.</summary>
    public async Task<IntegrationSettings> LoadMaskedAsync(string id, CancellationToken ct)
    {
        var s = await LoadAsync(id, ct);
        foreach (var f in Get(id)!.Fields.Where(f => f.Secret))
        {
            s.Values[f.Key] = s.GetString(f.Key) is null ? null : Masked;
        }

        return s;
    }

    /// <summary>Resolves masked secrets in settings coming from the UI back to the stored values.</summary>
    public async Task<IntegrationSettings> MergeAsync(string id, IntegrationSettings incoming, CancellationToken ct)
    {
        var current = await LoadAsync(id, ct);
        foreach (var f in Get(id)!.Fields.Where(f => f.Secret))
        {
            if (incoming.GetString(f.Key) == Masked)
            {
                incoming.Values[f.Key] = current.Values[f.Key]?.DeepClone();
            }
        }

        return incoming;
    }

    public async Task SaveAsync(string id, IntegrationSettings incoming, CancellationToken ct)
    {
        var integration = Get(id) ?? throw new KeyNotFoundException(id);
        var merged = await MergeAsync(id, incoming, ct);
        var stored = Clone(merged);
        foreach (var f in integration.Fields.Where(f => f.Secret))
        {
            stored.Values[f.Key] = secrets.Protect(stored.GetString(f.Key));
        }

        await using var db = await dbf.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(stored, IntegrationSettings.Json);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key(id), ct);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = Key(id), Value = json });
        }
        else
        {
            row.Value = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        _cache[id] = merged;
    }

    public bool StartSync(string id)
    {
        var integration = Get(id);
        if (integration is null)
        {
            return false;
        }

        lock (_gate)
        {
            var previous = Status(id);
            if (previous.Running)
            {
                return false;
            }

            _status[id] = previous with { Running = true };
        }

        _ = Task.Run(async () =>
        {
            var started = DateTimeOffset.UtcNow;
            var kind = "integration:" + id;
            IntegrationSyncResult result;
            try
            {
                var settings = await LoadAsync(id, CancellationToken.None);
                board.Report(new JobProgress { Kind = kind, State = JobStates.Running, Title = integration.Name + " sync", StartedAt = started }, force: true);
                var progress = new Progress<IntegrationProgress>(p => board.Report(new JobProgress
                {
                    Kind = kind, State = JobStates.Running, Title = integration.Name + " sync", Phase = p.Phase, Done = p.Done, Total = p.Total, Current = p.Current, StartedAt = started,
                }));
                result = await integration.SyncAsync(settings, progress, CancellationToken.None);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "{Integration} sync failed", integration.Name);
                result = new IntegrationSyncResult(false, e.Message);
            }

            _status[id] = new IntegrationStatus(false, DateTimeOffset.UtcNow, result);
            board.Report(new JobProgress
            {
                Kind = kind, State = result.Ok ? JobStates.Completed : JobStates.Failed, Title = integration.Name + " sync",
                Message = result.Message, Done = result.Items, StartedAt = started, FinishedAt = DateTimeOffset.UtcNow,
            }, force: true);
        });
        return true;
    }

    /// <summary>
    /// Initial settings from configuration, e.g. ANalyze__Integrations__plex__Enabled=true, ANalyze__Integrations__plex__url=…
    /// Used until the settings are saved in the UI.
    /// </summary>
    private IntegrationSettings FromConfiguration(IIntegration integration)
    {
        var section = config.GetSection($"ANalyze:Integrations:{integration.Id}");
        var s = new IntegrationSettings
        {
            Enabled = bool.TryParse(section["Enabled"], out var en) && en,
            SyncAfterScan = !bool.TryParse(section["SyncAfterScan"], out var sas) || sas,        };
        foreach (var f in integration.Fields)
        {
            var v = section[f.Key];
            if (v is null)
            {
                continue;
            }

            s.Values[f.Key] = f.Type switch
            {
                "bool" => JsonValue.Create(bool.TryParse(v, out var b) && b),
                "number" => int.TryParse(v, out var n) ? JsonValue.Create(n) : null,
                // "mappings" from env: "/data/movies=/library/Movies;/data/tv=/library/TV"
                "mappings" => new JsonArray(v.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2))
                    .Where(p => p.Length == 2)
                    .Select(p => (JsonNode)new JsonObject { ["from"] = p[0].Trim(), ["to"] = p[1].Trim() })
                    .ToArray()),
                // Secrets are stored encrypted, so encrypt the configured value like a saved one.
                _ => JsonValue.Create(f.Secret ? secrets.Protect(v) : v),
            };
        }

        return s;
    }

    private static IntegrationSettings Clone(IntegrationSettings s) => new()
    {
        Enabled = s.Enabled,
        SyncAfterScan = s.SyncAfterScan,        Values = (JsonObject)s.Values.DeepClone(),
    };
}

/// <summary>Replaces one integration's annotations in bulk.</summary>
public static class Annotations
{
    public sealed record Item(long EntryId, string Kind, string Label, string? Detail = null, string? State = null, string? Url = null);

    public static void Replace(Microsoft.Data.Sqlite.SqliteConnection conn, string source, IEnumerable<Item> items)
    {
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM Annotations WHERE Source = $s";
            del.Parameters.AddWithValue("$s", source);
            del.ExecuteNonQuery();
        }

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT INTO Annotations (EntryId, Source, Kind, Label, Detail, State, Url) VALUES ($e, $s, $k, $l, $d, $st, $u)";
        foreach (var p in new[] { "$e", "$s", "$k", "$l", "$d", "$st", "$u" })
        {
            ins.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter(p, null));
        }

        foreach (var a in items)
        {
            ins.Parameters["$e"].Value = a.EntryId;
            ins.Parameters["$s"].Value = source;
            ins.Parameters["$k"].Value = a.Kind;
            ins.Parameters["$l"].Value = a.Label;
            ins.Parameters["$d"].Value = (object?)a.Detail ?? DBNull.Value;
            ins.Parameters["$st"].Value = (object?)a.State ?? DBNull.Value;
            ins.Parameters["$u"].Value = (object?)a.Url ?? DBNull.Value;
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }
}

public static class DependencyInjection
{
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        services.AddSingleton<IntegrationHost>();
        services.AddSingleton<IntegrationPaths>();
        services.AddSingleton<Items.ItemServices>();
        services.AddHttpClient(IntegrationHttp.Name, c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddHttpClient(IntegrationHttp.Insecure, c => c.Timeout = TimeSpan.FromMinutes(2))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                // Only used when the user enables "accept self-signed certificates" for their own server.
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            });
        services.AddHostedService<IntegrationAutoSync>();
        return services;
    }

    /// <summary>Registers one integration plugin.</summary>
    public static IServiceCollection AddIntegration<T>(this IServiceCollection services) where T : class, IIntegration
    {
        services.AddSingleton<T>();
        services.AddSingleton<IIntegration>(sp => sp.GetRequiredService<T>());
        return services;
    }
}

/// <summary>Re-syncs enabled integrations after each completed full scan so matches follow renames and moves.</summary>
internal sealed class IntegrationAutoSync(ScanService scanner, IntegrationHost host) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        scanner.Completed += OnScan;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        scanner.Completed -= OnScan;
        return Task.CompletedTask;
    }

    private void OnScan(long scanId) => _ = Task.Run(async () =>
    {
        foreach (var i in host.All)
        {
            var s = await host.LoadAsync(i.Id, CancellationToken.None);
            if (s.Enabled && s.SyncAfterScan)
            {
                host.StartSync(i.Id);
            }
        }
    });
}
