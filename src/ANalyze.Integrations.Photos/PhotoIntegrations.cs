using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ANalyze.Domain;
using ANalyze.Integrations.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Integrations.Photos;

/// <summary>Shared part of photo libraries: items are photo/video files; the most useful view is copies of them outside the library.</summary>
public abstract class PhotoIntegration(ItemServices services) : ItemIntegration(services)
{
    public override ItemViews Views => new([FileKind.Image, FileKind.Video]);

    protected string BaseUrl(IntegrationSettings s) => s.Require("url", $"{Name} URL").TrimEnd('/');

    protected static string FileName(string path) => path[(path.Replace('\\', '/').LastIndexOf('/') + 1)..];

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    protected async Task<T?> SendAsync<T>(IntegrationSettings s, HttpRequestMessage req, CancellationToken ct)
    {
        using (req)
        {
            req.Headers.Add("Accept", "application/json");
            using var res = await Http(s).SendAsync(req, ct);
            if (res.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new HttpRequestException($"{Name} rejected the key ({(int)res.StatusCode}).");
            }

            res.EnsureSuccessStatusCode();
            return await res.Content.ReadFromJsonAsync<T>(Json, ct);
        }
    }
}

public sealed class ImmichIntegration(ItemServices services) : PhotoIntegration(services)
{
    private const int PageSize = 1000;

    public override string Id => "immich";

    public override string Name => "Immich";

    public override string Description => "Marks photos and videos Immich manages and finds copies of them elsewhere on disk (exports, old backups, phone dumps) that can go.";

    public override string Icon => "image";

    public override IReadOnlyList<string> DockerHints => ["immich-server", "immich_server"];

    public override IReadOnlyList<IntegrationField> Fields =>
    [
        IntegrationHttp.Url("http://192.168.1.10:2283"),
        new("apiKey", "API key", "password", "Immich → Account Settings → API Keys (an admin key also lists external libraries)."),
        IntegrationHttp.Mappings("e.g. Immich container /usr/src/app/upload ↔ ANalyze /library/immich"),
        IntegrationHttp.AllowInvalidCertificate(),
    ];

    public override async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => await Try(async () =>
    {
        var me = await SendAsync<UserDto>(settings, Get(settings, "users/me"), ct);
        var version = await SendAsync<VersionDto>(settings, Get(settings, "server/version"), ct);
        return new IntegrationTestResult(true, $"Connected to Immich {version?.Major}.{version?.Minor}.{version?.Patch} as {me?.Name ?? me?.Email}");
    });

    protected override async Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var baseUrl = BaseUrl(settings);
        List<LibraryDto> libraries;
        try
        {
            libraries = await SendAsync<List<LibraryDto>>(settings, Get(settings, "libraries"), ct) ?? [];
        }
        catch (HttpRequestException)
        {
            libraries = []; // needs an admin key; uploads still work
        }

        var names = libraries.ToDictionary(l => l.Id, l => l.Name ?? "Library");
        var folders = libraries.SelectMany(l => (l.ImportPaths ?? []).Select(p => new ExternalFolder(p, "library", $"Immich library: {l.Name}", "external", IsRoot: true))).ToList();
        var items = new List<ExternalItem>();
        for (var page = 1; ; page++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/search/metadata")
            {
                Content = JsonContent.Create(new { page, size = PageSize, withExif = true }),
            };
            req.Headers.Add("x-api-key", settings.Require("apiKey", "Immich API key"));
            var res = await SendAsync<SearchDto>(settings, req, ct);
            var batch = res?.Assets?.Items ?? [];
            foreach (var a in batch.Where(a => !string.IsNullOrEmpty(a.OriginalPath)))
            {
                var state = a.IsTrashed ? "trashed" : a.IsArchived ? "archived" : a.IsFavorite ? "favorite" : null;
                var library = a.LibraryId is { } id && names.TryGetValue(id, out var n) ? n : null;
                items.Add(new ExternalItem(a.Type == "VIDEO" ? "video" : "photo", a.Id, a.OriginalFileName ?? FileName(a.OriginalPath!), a.OriginalPath!, a.ExifInfo?.FileSizeInByte ?? 0)
                {
                    GroupKey = a.LibraryId is null ? "uploads" : "library:" + a.LibraryId, GroupTitle = library ?? "Uploads", State = state,
                    AddedAt = Unix(a.FileCreatedAt), Url = $"{baseUrl}/photos/{a.Id}", Label = "Immich", Badge = state,
                });
            }

            progress.Report(new IntegrationProgress("reading assets", items.Count, 0));
            if (batch.Count < PageSize || res?.Assets?.NextPage is null)
            {
                break;
            }
        }

        return new Snapshot(items, folders, $"{libraries.Count} external libraries");
    }

    private static HttpRequestMessage Get(IntegrationSettings s, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{s.Require("url", "Immich URL").TrimEnd('/')}/api/{path}");
        req.Headers.Add("x-api-key", s.Require("apiKey", "Immich API key"));
        return req;
    }

    private sealed record UserDto(string? Name, string? Email);

    private sealed record VersionDto(int Major, int Minor, int Patch);

    private sealed record LibraryDto(string Id, string? Name, List<string>? ImportPaths);

    private sealed record SearchDto(AssetPage? Assets);

    private sealed record AssetPage(List<AssetDto>? Items, string? NextPage);

    private sealed record AssetDto(string Id, string? Type, string? OriginalPath, string? OriginalFileName, string? FileCreatedAt, bool IsFavorite, bool IsArchived,
        bool IsTrashed, string? LibraryId, ExifDto? ExifInfo);

    private sealed record ExifDto(long? FileSizeInByte);
}

public sealed class PhotoPrismIntegration(ItemServices services) : PhotoIntegration(services)
{
    private const int PageSize = 1000;

    public override string Id => "photoprism";

    public override string Name => "PhotoPrism";

    public override string Description => "Marks photos and videos PhotoPrism has indexed, finds files in the originals folder it skipped and copies of library files elsewhere on disk.";

    public override string Icon => "image";

    public override IReadOnlyList<IntegrationField> Fields =>
    [
        IntegrationHttp.Url("http://192.168.1.10:2342"),
        new("token", "App password or access token", "password", "PhotoPrism → Settings → Account → Apps and Devices → Add."),
        new("originals", "Originals folder", "text", "PHOTOPRISM_ORIGINALS_PATH inside the PhotoPrism container.", JsonValue.Create("/photoprism/originals")),
        IntegrationHttp.Mappings("e.g. /photoprism/originals ↔ ANalyze /library/photos"),
        IntegrationHttp.AllowInvalidCertificate(),
    ];

    public override async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => await Try(async () =>
    {
        var photos = await SendAsync<List<PhotoDto>>(settings, Get(settings, "photos?count=1&offset=0&merged=true"), ct);
        var config = await SendAsync<JsonObject>(settings, Get(settings, "config"), ct);
        return new IntegrationTestResult(true, $"Connected to PhotoPrism {config?["version"]}{(photos?.Count > 0 ? "" : " — library is empty")}");
    });

    protected override async Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var baseUrl = BaseUrl(settings);
        var originals = (settings.GetString("originals") ?? "/photoprism/originals").TrimEnd('/');
        var items = new List<ExternalItem>();
        for (var offset = 0; ; offset += PageSize)
        {
            var batch = await SendAsync<List<PhotoDto>>(settings, Get(settings, $"photos?count={PageSize}&offset={offset}&merged=true&quality=0"), ct) ?? [];
            foreach (var p in batch)
            {
                foreach (var f in (p.Files ?? []).Where(f => f.Root is null or "/" && !f.Missing && !f.Sidecar && !string.IsNullOrEmpty(f.Name)))
                {
                    var state = p.Favorite ? "favorite" : p.Private ? "private" : null;
                    items.Add(new ExternalItem(p.Type is "video" or "live" or "animated" ? "video" : "photo", f.UID ?? f.Name!, FileName(f.Name!), originals + "/" + f.Name, f.Size)
                    {
                        GroupKey = p.UID, GroupTitle = string.IsNullOrEmpty(p.Title) ? FileName(f.Name!) : p.Title, State = state,
                        AddedAt = Unix(p.TakenAt), Url = $"{baseUrl}/library/browse?view=cards&q=uid:{p.UID}", Label = "PhotoPrism", Badge = state,
                    });
                }
            }

            progress.Report(new IntegrationProgress("reading photos", items.Count, 0));
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        return new Snapshot(items, [new ExternalFolder(originals, "library", "PhotoPrism originals", "originals", IsRoot: true)]);
    }

    private static HttpRequestMessage Get(IntegrationSettings s, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{s.Require("url", "PhotoPrism URL").TrimEnd('/')}/api/v1/{path}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", s.Require("token", "PhotoPrism token"));
        return req;
    }

    private sealed record PhotoDto(string? UID, string? Title, string? Type, string? TakenAt, bool Favorite, bool Private, List<FileDto>? Files);

    private sealed record FileDto(string? UID, string? Name, string? Root, long Size, bool Missing, bool Sidecar);
}

public static class DependencyInjection
{
    public static IServiceCollection AddPhotoIntegrations(this IServiceCollection services) =>
        services.AddIntegration<ImmichIntegration>().AddIntegration<PhotoPrismIntegration>();
}
