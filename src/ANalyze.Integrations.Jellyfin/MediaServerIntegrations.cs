using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ANalyze.Domain;
using ANalyze.Integrations.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Integrations.Jellyfin;

/// <summary>
/// Jellyfin and Emby share the same API (X-Emby-Token, /Users/{id}/Items, /Library/VirtualFolders). Items are movie, episode and track
/// files with the chosen user's watch state; library folders are roots, so media inside them the server does not know is untracked.
/// </summary>
public abstract class MediaServerIntegration(ItemServices services) : ItemIntegration(services)
{
    private const int PageSize = 500;

    /// <summary>"" for Jellyfin, "/emby" for Emby.</summary>
    protected abstract string ApiPrefix { get; }

    protected abstract string Example { get; }

    /// <summary>Web link to an item.</summary>
    protected abstract string ItemUrl(string baseUrl, string itemId, string? serverId);

    public override IReadOnlyList<IntegrationField> Fields =>
    [
        IntegrationHttp.Url(Example),
        new("apiKey", "API key", "password", $"{Name} → Dashboard → API Keys → +."),
        new("user", "User", "text", "Whose watch state to use. Empty: the first administrator."),
        IntegrationHttp.Mappings($"e.g. {Name} container /data/movies ↔ ANalyze /library/Movies"),
        new("watchedDays", "Watched long ago (days)", "number", "Titles last watched before this are cleanup candidates.", JsonValue.Create(180)),
        new("unwatchedDays", "Never watched, added more than (days) ago", "number", null, JsonValue.Create(365)),
        IntegrationHttp.AllowInvalidCertificate(),
    ];

    public override ItemViews Views => new([FileKind.Video, FileKind.Audio]);

    public override async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => await Try(async () =>
    {
        var info = await GetAsync<SystemInfoDto>(settings, "/System/Info", ct);
        var user = await UserAsync(settings, ct);
        var folders = await GetAsync<List<VirtualFolderDto>>(settings, "/Library/VirtualFolders", ct) ?? [];
        var details = new JsonArray(folders.Select(f => (JsonNode)new JsonObject
        {
            ["title"] = f.Name,
            ["type"] = f.CollectionType ?? "mixed",
            ["locations"] = new JsonArray((f.Locations ?? []).Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
        }).ToArray());
        return new IntegrationTestResult(true, $"Connected to {info?.ServerName} ({Name} {info?.Version}) as {user.Name} — {folders.Count} libraries", details);
    });

    protected override async Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var baseUrl = BaseUrl(settings);
        var info = await GetAsync<SystemInfoDto>(settings, "/System/Info", ct);
        var user = await UserAsync(settings, ct);
        var libraries = await GetAsync<List<VirtualFolderDto>>(settings, "/Library/VirtualFolders", ct) ?? [];
        var folders = libraries.SelectMany(l => (l.Locations ?? []).Select(p => new ExternalFolder(p, "library", $"{Name} library: {l.Name}", l.CollectionType, IsRoot: true))).ToList();

        var items = new List<ExternalItem>();
        for (var start = 0; ; start += PageSize)
        {
            var page = await GetAsync<ItemsDto>(settings,
                $"/Users/{user.Id}/Items?Recursive=true&IncludeItemTypes=Movie,Episode,Audio&Fields=Path,MediaSources,DateCreated,ProductionYear" +
                $"&EnableImages=false&EnableUserData=true&StartIndex={start}&Limit={PageSize}", ct);
            var batch = page?.Items ?? [];
            foreach (var m in batch)
            {
                items.AddRange(ToItems(m, ItemUrl(baseUrl, m.Id ?? "", info?.Id)));
            }

            progress.Report(new IntegrationProgress("reading items", start + batch.Count, page?.TotalRecordCount ?? 0));
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        return new Snapshot(items, folders, $"{libraries.Count} libraries, user {user.Name}");
    }

    private IEnumerable<ExternalItem> ToItems(ItemDto m, string url)
    {
        var played = m.UserData?.Played == true || m.UserData?.PlayCount > 0;
        var lastPlayed = Unix(m.UserData?.LastPlayedDate);
        var sources = (m.MediaSources ?? []).Where(s => !string.IsNullOrEmpty(s.Path)).ToList();
        if (sources.Count == 0 && !string.IsNullOrEmpty(m.Path))
        {
            sources.Add(new MediaSourceDto(m.Path, 0));
        }

        foreach (var s in sources)
        {
            var common = new ExternalItem(Kind(m.Type), m.Id + ":" + s.Path, m.Name ?? "?", s.Path!, s.Size ?? 0)
            {
                Year = m.ProductionYear, State = m.Type == "Audio" ? null : played ? "watched" : "unwatched", PlayCount = m.UserData?.PlayCount ?? (played ? 1 : 0),
                LastUsedAt = lastPlayed, AddedAt = Unix(m.DateCreated), Url = url, Badge = m.Type == "Audio" ? null : played ? "watched" : "unwatched",
                Detail = sources.Count > 1 ? $"{sources.Count} versions" : null,
            };
            yield return m.Type switch
            {
                "Episode" => common with
                {
                    GroupKey = "show:" + (m.SeriesId ?? m.SeriesName), GroupTitle = m.SeriesName, SubTitle = m.SeasonName,
                    Label = $"{Name}: {m.SeriesName} {Code(m)} · {m.Name}".Trim(),
                    Parents = [("season", $"{m.SeriesName} · {m.SeasonName}"), ("show", m.SeriesName ?? "")],
                },
                "Audio" => common with
                {
                    GroupKey = "artist:" + (m.AlbumArtist ?? "?"), GroupTitle = m.AlbumArtist, SubTitle = m.Album,
                    Label = $"{Name}: {m.AlbumArtist} – {m.Name}", Parents = [("album", $"{m.AlbumArtist} – {m.Album}"), ("artist", m.AlbumArtist ?? "")],
                },
                _ => common with
                {
                    GroupKey = "movie:" + m.Id, GroupTitle = m.Name, Label = m.ProductionYear is { } y ? $"{Name}: {m.Name} ({y})" : $"{Name}: {m.Name}",
                    Parents = [("movie", m.ProductionYear is { } y2 ? $"{m.Name} ({y2})" : m.Name ?? "")],
                },
            };
        }
    }

    private static string Kind(string? type) => type switch { "Episode" => "episode", "Audio" => "track", _ => "movie" };

    private static string? Code(ItemDto m) => m.ParentIndexNumber is { } s && m.IndexNumber is { } e ? $"S{s:00}E{e:00}" : null;

    private string BaseUrl(IntegrationSettings s) => s.Require("url", $"{Name} URL").TrimEnd('/');

    private async Task<UserDto> UserAsync(IntegrationSettings s, CancellationToken ct)
    {
        var users = await GetAsync<List<UserDto>>(s, "/Users", ct) ?? [];
        var wanted = s.GetString("user");
        return (wanted is null
                   ? users.FirstOrDefault(u => u.Policy?.IsAdministrator == true) ?? users.FirstOrDefault()
                   : users.FirstOrDefault(u => string.Equals(u.Name, wanted, StringComparison.OrdinalIgnoreCase)))
               ?? throw new InvalidOperationException(wanted is null ? $"{Name} has no users." : $"{Name} user \"{wanted}\" not found.");
    }

    private async Task<T?> GetAsync<T>(IntegrationSettings s, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl(s) + ApiPrefix + path);
        req.Headers.Add("X-Emby-Token", s.Require("apiKey", $"{Name} API key"));
        req.Headers.Add("Accept", "application/json");
        using var res = await Http(s).SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException($"{Name} rejected the API key (401).");
        }

        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private sealed record SystemInfoDto(string? ServerName, string? Version, string? Id);

    private sealed record UserDto(string Id, string? Name, PolicyDto? Policy);

    private sealed record PolicyDto(bool IsAdministrator);

    private sealed record VirtualFolderDto(string? Name, string? CollectionType, List<string>? Locations);

    private sealed record ItemsDto(List<ItemDto>? Items, int TotalRecordCount);

    private sealed record ItemDto(
        string? Id, string? Name, string? Type, string? Path, int? ProductionYear, string? SeriesName, string? SeriesId, string? SeasonName,
        int? IndexNumber, int? ParentIndexNumber, string? Album, string? AlbumArtist, string? DateCreated, UserDataDto? UserData, List<MediaSourceDto>? MediaSources);

    private sealed record UserDataDto(bool Played, int? PlayCount, string? LastPlayedDate);

    private sealed record MediaSourceDto(string? Path, long? Size);
}

public sealed class JellyfinIntegration(ItemServices services) : MediaServerIntegration(services)
{
    public override string Id => "jellyfin";

    public override string Name => "Jellyfin";

    public override string Description => "Marks Jellyfin movies, shows and music in the browser, ranks titles by size and watch state and finds media Jellyfin does not know.";

    public override string Icon => "clapperboard";

    protected override string ApiPrefix => "";

    protected override string Example => "http://192.168.1.10:8096";

    protected override string ItemUrl(string baseUrl, string itemId, string? serverId) => $"{baseUrl}/web/index.html#/details?id={itemId}&serverId={serverId}";
}

public sealed class EmbyIntegration(ItemServices services) : MediaServerIntegration(services)
{
    public override string Id => "emby";

    public override string Name => "Emby";

    public override string Description => "Marks Emby movies, shows and music in the browser, ranks titles by size and watch state and finds media Emby does not know.";

    public override string Icon => "clapperboard";

    protected override string ApiPrefix => "/emby";

    protected override string Example => "http://192.168.1.10:8096";

    public override IReadOnlyList<string> DockerHints => ["emby", "embyserver"];

    protected override string ItemUrl(string baseUrl, string itemId, string? serverId) => $"{baseUrl}/web/index.html#!/item?id={itemId}&serverId={serverId}";
}

public static class DependencyInjection
{
    public static IServiceCollection AddMediaServerIntegrations(this IServiceCollection services) =>
        services.AddIntegration<JellyfinIntegration>().AddIntegration<EmbyIntegration>();
}
