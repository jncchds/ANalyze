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
    IReadOnlyList<PathMapping> Mappings,
    IReadOnlyList<PathMapping> Detected,
    long Unmapped,
    bool HashesCompared);

/// <summary>
/// Parent–child link between two ANalyze instances: the parent is the backup server of this instance (the child). A sync reads the
/// parent's files and folders with sizes and content hashes (<see cref="BackupExport"/>), compares them with what is scanned here
/// (<see cref="BackupSync"/> has the rules) and shows which files and folders are synchronized, and which files in synchronized
/// folders are corrupt (other size or hash on the parent) or missing from the parent.
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
        "The parent is the ANalyze on your backup server: shows which files and folders here are synchronized to it, and which files in synchronized folders are corrupt or missing there.";

    public string Icon => "shield-check";

    public IReadOnlyList<string> DockerHints => [];

    // The parent is on another machine: no Docker container to look for.
    public string? UrlField => null;

    public IReadOnlyList<IntegrationField> Fields { get; } =
    [
        new("url", "Parent URL", "url", "Address of the ANalyze on the backup server, as seen from this container.", Placeholder: "http://backup-nas:8080"),
        new("token", "Parent API token", "password", "The parent's ANalyze__ApiToken. Needed when the parent has a password."),
        new("mappings", "Path mappings", "mappings",
            "Parent path → path here, e.g. /backup/nas1 → /library. Leave empty when the backup has the same paths; otherwise it is detected from sample files (\"Suggest\" fills it in)."),
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
                lines.Add("The parent does not hash every file: turn on Settings → \"Hash every file\" there, otherwise corrupt files are found by size only.");
            }

            if (!appSettings.Current.HashAllFiles)
            {
                lines.Add("This instance does not hash every file: turn on Settings → \"Hash every file\" here too, otherwise corrupt files are found by size only.");
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

        // Hashes of another kind cannot be compared: sizes only.
        BackupSync.FetchPage fetch = compareHashes
            ? client.FilesAsync
            : async (root, after, c) =>
            {
                var page = await client.FilesAsync(root, after, c);
                return page with { Items = page.Items.Select(x => x with { H = null }).ToList() };
            };

        using var conn = sqlite.Open();
        var result = await new BackupSync(conn, roots).RunAsync(info, settings.GetMappings("mappings"), fetch, progress, ct);
        progress.Report(new IntegrationProgress("labels", 0, 0));
        BackupAnnotations.Write(conn, Id, name, result.Targets);

        await SaveSnapshotAsync(new BackupSnapshot(DateTimeOffset.UtcNow, name, settings.GetString("url") ?? "", info, result.Targets, result.Mappings, result.Detected, result.Unmapped, compareHashes), ct);

        var f = result.Files;
        var all = f.Values.Sum();
        var parts = new List<string>
        {
            $"{f[BackupStates.Synced] + f[BackupStates.Corrupt]:N0} of {all:N0} files synchronized",
            $"{result.SyncedFolders:N0} of {result.Folders:N0} folders",
        };
        foreach (var (state, word) in new[] { (BackupStates.Corrupt, "corrupt"), (BackupStates.Missing, "missing") })
        {
            if (f[state] > 0)
            {
                parts.Add($"{f[state]:N0} {word}");
            }
        }

        if (result.Detected.Count > 0)
        {
            parts.Add("detected mappings: " + string.Join(", ", result.Detected.Select(m => $"{m.From} → {m.To}")));
        }

        if (result.Targets.All(t => t.EntryId is null))
        {
            parts.Add("none of the parent's folders was found here — add a path mapping");
        }

        log.LogInformation("Backup comparison with {Parent}: {Summary}", name, string.Join("; ", parts));
        return new IntegrationSyncResult(true, string.Join(" · ", parts), all, f[BackupStates.Synced] + f[BackupStates.Corrupt]);
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
            matcher.Detect(page.Items.Where(f => !f.D).Take(200).Select(f => (prefix + "/" + f.P, f.S)), 200);
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
