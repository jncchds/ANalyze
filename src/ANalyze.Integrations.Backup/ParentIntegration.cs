using System.Text.Json;
using System.Text.Json.Nodes;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using ANalyze.Integrations.Items;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ANalyze.Integrations.Backup;

/// <summary>What the last sync found out about the parent, for the Backup page.</summary>
public sealed record BackupSnapshot(
    DateTimeOffset SyncedAt,
    string ParentName,
    string Url,
    ExportInfo Info,
    IReadOnlyList<BackupTarget> Targets,
    IReadOnlyList<PathMapping> Detected,
    long Unmapped,
    bool HashesCompared);

/// <summary>
/// Parent–child link between two ANalyze instances: this instance (the child) holds a backup of the parent's files. A sync reads the
/// parent's file list with sizes, dates and content hashes (<see cref="BackupExport"/>), compares it with the files scanned here and
/// shows which files are backed up (verified by content or by size and date), outdated, corrupt (same size and date, other content —
/// and which copy changed, when known), missing, or only in the backup.
/// </summary>
public sealed class ParentIntegration(
    IHttpClientFactory httpFactory,
    SqliteSetup sqlite,
    RootRegistry roots,
    SettingsStore appSettings,
    IDbContextFactory<AnalyzeDbContext> dbf,
    ILogger<ParentIntegration> log) : IIntegration
{
    public const string SnapshotKey = "backup:parent";

    public string Id => "parent";

    public string Name => "ANalyze parent";

    public string Description =>
        "This instance is a backup of another ANalyze (the parent): shows which of its files are backed up here, verified, outdated, corrupt or missing, and what is only in the backup.";

    public string Icon => "shield-check";

    public IReadOnlyList<string> DockerHints => [];

    // The parent is on another machine: no Docker container to look for.
    public string? UrlField => null;

    public IReadOnlyList<IntegrationField> Fields { get; } =
    [
        new("url", "Parent URL", "url", "Address of the parent ANalyze as seen from this container.", Placeholder: "http://nas1:8080"),
        new("token", "Parent API token", "password", "The parent's ANalyze__ApiToken. Needed when the parent has a password."),
        new("mappings", "Path mappings", "mappings",
            "Parent path → path here, e.g. /library → /library/nas1. Leave empty when the backup has the same paths; otherwise it is detected from sample files (\"Suggest\" fills it in)."),
        new("mtimeTolerance", "Date tolerance (seconds)", "number", "Modification times this close count as equal (FAT and some copy tools round them).", JsonValue.Create(2)),
        IntegrationHttp.AllowInvalidCertificate(),
    ];

    private ParentClient Client(IntegrationSettings s) =>
        new(IntegrationHttp.Create(httpFactory, s), s.Require("url", "parent URL"), s.GetString("token"));

    private static string ParentName(IntegrationSettings s) =>
        Uri.TryCreate(s.GetString("url") ?? "", UriKind.Absolute, out var u) ? u.Host : "parent";

    public async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct)
    {
        try
        {
            var info = await Client(settings).InfoAsync(ct);
            var mappings = settings.GetMappings("mappings");
            var files = info.Roots.Sum(r => r.Files);
            var hashed = info.Roots.Sum(r => r.Hashed);
            var lines = new List<string>
            {
                $"{info.App} {info.Version} — {info.Roots.Count} roots, {files:N0} files, {Pct(hashed, files)} with a content hash.",
            };
            foreach (var r in info.Roots)
            {
                var local = IntegrationSettings.MapPath(r.Path, mappings);
                var here = roots.All.Any(x => !x.Missing && RootRegistry.IsUnder(x.Path, local)) && Directory.Exists(local);
                lines.Add($"{r.Path} → {local}{(here ? "" : " (not here: the sync detects the folder from sample files, or press Suggest / add a mapping)")}");
            }

            if (!info.HashAllFiles)
            {
                lines.Add("The parent does not hash every file: turn on Settings → \"Hash every file\" there to verify content and find corruption.");
            }

            if (!appSettings.Current.HashAllFiles)
            {
                lines.Add("This instance does not hash every file: turn on Settings → \"Hash every file\" here too.");
            }

            return new IntegrationTestResult(true, string.Join("\n", lines));
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException or UriFormatException or NotSupportedException)
        {
            return new IntegrationTestResult(false, e.Message);
        }
    }

    public async Task<IntegrationSyncResult> SyncAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var client = Client(settings);
        progress.Report(new IntegrationProgress("connecting", 0, 0));
        var info = await client.InfoAsync(ct);
        var compareHashes = info.HashAlgorithm == BackupExport.HashAlgorithm;
        var name = ParentName(settings);

        using var conn = sqlite.Open();
        var sync = new BackupSync(conn, roots, Math.Max(0, settings.GetInt("mtimeTolerance", 2)), compareHashes);
        var result = await sync.RunAsync(info, settings.GetMappings("mappings"), client.FilesAsync, progress, ct);
        progress.Report(new IntegrationProgress("labels", result.Files, result.Files));
        BackupAnnotations.Write(conn, Id, name, result.Targets);

        await SaveSnapshotAsync(new BackupSnapshot(DateTimeOffset.UtcNow, name, settings.GetString("url") ?? "", info, result.Targets, result.Detected, result.Unmapped, compareHashes), ct);

        var c = result.Counts;
        var backedUp = c[BackupStates.Verified] + c[BackupStates.Matched];
        var parts = new List<string> { $"{backedUp:N0} of {result.Files:N0} backed up ({c[BackupStates.Verified]:N0} verified)" };
        foreach (var (state, word) in new[] { (BackupStates.Corrupt, "corrupt"), (BackupStates.Outdated, "outdated"), (BackupStates.Missing, "missing"), (BackupStates.Extra, "only here") })
        {
            if (c[state] > 0)
            {
                parts.Add($"{c[state]:N0} {word}");
            }
        }

        if (result.Detected.Count > 0)
        {
            parts.Add("detected mappings: " + string.Join(", ", result.Detected.Select(m => $"{m.From} → {m.To}")));
        }

        if (result.Unmapped > 0)
        {
            parts.Add($"{result.Unmapped:N0} files map to no folder here");
        }

        log.LogInformation("Backup comparison with {Parent}: {Summary}", name, string.Join("; ", parts));
        return new IntegrationSyncResult(true, string.Join(" · ", parts), result.Files, backedUp);
    }

    private async Task SaveSnapshotAsync(BackupSnapshot snap, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(snap, SnapshotJson);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == SnapshotKey, ct);
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = SnapshotKey, Value = json });
        }
        else
        {
            row.Value = json;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    internal async Task<BackupSnapshot?> LoadSnapshotAsync(CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SnapshotKey, ct);
        return row is null ? null : JsonSerializer.Deserialize<BackupSnapshot>(row.Value, SnapshotJson);
    }

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <summary>Mappings from sample files of every parent root, ignoring the configured ones.</summary>
    internal async Task<IReadOnlyList<PathMapping>> SuggestAsync(IntegrationSettings settings, CancellationToken ct)
    {
        var client = Client(settings);
        var info = await client.InfoAsync(ct);
        using var conn = sqlite.Open();
        var matcher = new PathMatcher(conn, roots, []);
        foreach (var r in info.Roots)
        {
            var page = await client.FilesAsync(r.Id, null, ct);
            var prefix = r.Path.TrimEnd('/');
            matcher.Detect(page.Items.Take(200).Select(f => (prefix + "/" + f.P, f.S)), 200);
        }

        return matcher.Detected.DistinctBy(m => m.From).ToList();
    }

    public void MapEndpoints(RouteGroupBuilder group) => BackupEndpoints.Map(group, this);

    private static string Pct(long part, long whole) => whole == 0 ? "0%" : $"{part * 100.0 / whole:0.#}%";
}

public static class BackupDependencyInjection
{
    public static IServiceCollection AddBackupIntegration(this IServiceCollection services) => services.AddIntegration<ParentIntegration>();
}
