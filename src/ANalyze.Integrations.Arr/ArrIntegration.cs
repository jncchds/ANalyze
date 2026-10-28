using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ANalyze.Domain;
using ANalyze.Integrations.Items;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Integrations.Arr;

/// <summary>
/// Shared part of Sonarr, Radarr and Lidarr: the same API style (X-Api-Key, /api/v3 or /api/v1), root folders and an
/// "editor" endpoint to unmonitor titles. Items are the files the app manages; anything else in its root folders is untracked.
/// </summary>
public abstract class ArrIntegration(ItemServices services) : ItemIntegration(services)
{
    /// <summary>"v3" for Sonarr/Radarr, "v1" for Lidarr.</summary>
    protected abstract string ApiVersion { get; }

    /// <summary>Editor endpoint and id property used to unmonitor titles, e.g. ("movie/editor", "movieIds").</summary>
    protected abstract (string Path, string IdsProperty) Editor { get; }

    protected abstract string Example { get; }

    public override IReadOnlyList<IntegrationField> Fields =>
    [
        IntegrationHttp.Url(Example),
        new("apiKey", "API key", "password", $"{Name} → Settings → General → API Key."),
        IntegrationHttp.Mappings($"e.g. {Name} container /data ↔ ANalyze /library"),
        IntegrationHttp.AllowInvalidCertificate(),
    ];

    public override ItemViews Views => new([FileKind.Video, FileKind.Audio]);

    public override async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => await Try(async () =>
    {
        var status = await GetAsync<StatusDto>(settings, "system/status", ct);
        var roots = await RootFoldersAsync(settings, ct);
        var details = new JsonArray(roots.Select(r => (JsonNode)new JsonObject
        {
            ["title"] = "Root folder",
            ["type"] = Name,
            ["locations"] = new JsonArray(JsonValue.Create(r.Path)),
        }).ToArray());
        return new IntegrationTestResult(true, $"Connected to {status?.AppName ?? Name} {status?.Version} — {roots.Count} root folders", details);
    });

    protected override async Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var roots = await RootFoldersAsync(settings, ct);
        var folders = roots.Where(r => !string.IsNullOrEmpty(r.Path))
            .Select(r => new ExternalFolder(r.Path!, "root", $"{Name} root folder", Name, IsRoot: true)).ToList();
        var (items, titleFolders) = await TitlesAsync(settings, progress, ct);
        folders.AddRange(titleFolders);
        return new Snapshot(items, folders, $"{titleFolders.Count} titles");
    }

    /// <summary>Managed files and title folders.</summary>
    protected abstract Task<(List<ExternalItem> Items, List<ExternalFolder> Folders)> TitlesAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct);

    protected string BaseUrl(IntegrationSettings s) => s.Require("url", $"{Name} URL").TrimEnd('/');

    private async Task<List<RootFolderDto>> RootFoldersAsync(IntegrationSettings s, CancellationToken ct) =>
        await GetAsync<List<RootFolderDto>>(s, "rootfolder", ct) ?? [];

    protected async Task<T?> GetAsync<T>(IntegrationSettings s, string path, CancellationToken ct)
    {
        using var req = Request(s, HttpMethod.Get, path);
        using var res = await Http(s).SendAsync(req, ct);
        Check(res);
        return await res.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private HttpRequestMessage Request(IntegrationSettings s, HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{BaseUrl(s)}/api/{ApiVersion}/{path}");
        req.Headers.Add("X-Api-Key", s.Require("apiKey", $"{Name} API key"));
        req.Headers.Add("Accept", "application/json");
        return req;
    }

    private void Check(HttpResponseMessage res)
    {
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException($"{Name} rejected the API key (401).");
        }

        res.EnsureSuccessStatusCode();
    }

    /// <summary>POST /unmonitor { keys: [title ids] }: stops the app from downloading these titles again after cleanup.</summary>
    protected override void MapExtra(RouteGroupBuilder group) =>
        group.MapPost("/unmonitor", async (UnmonitorBody body, IntegrationHost host, CancellationToken ct) =>
        {
            var ids = body.Keys.Select(k => int.TryParse(k, out var id) ? id : (int?)null).OfType<int>().Distinct().ToArray();
            if (ids.Length == 0)
            {
                return Results.BadRequest();
            }

            var settings = await host.LoadAsync(Id, ct);
            try
            {
                using var req = Request(settings, HttpMethod.Put, Editor.Path);
                req.Content = JsonContent.Create(new JsonObject { [Editor.IdsProperty] = new JsonArray(ids.Select(i => (JsonNode)JsonValue.Create(i)).ToArray()), ["monitored"] = false });
                using var res = await Http(settings).SendAsync(req, ct);
                Check(res);
                return Results.Ok(new { unmonitored = ids.Length });
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                return Results.Problem(e.Message, statusCode: 502);
            }
        });

    public sealed record UnmonitorBody(List<string> Keys);

    protected static string? QualityName(QualityDto? q) => q?.Quality?.Name;

    protected static string FileName(string path) => path.Replace('\\', '/') is var p ? p[(p.LastIndexOf('/') + 1)..] : path;

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    protected sealed record StatusDto(string? AppName, string? Version);

    protected sealed record RootFolderDto(string? Path);

    public sealed record QualityDto(QualityNameDto? Quality);

    public sealed record QualityNameDto(string? Name);
}

public sealed class RadarrIntegration(ItemServices services) : ArrIntegration(services)
{
    public override string Id => "radarr";

    public override string Name => "Radarr";

    public override string Description => "Marks movie files Radarr manages, finds leftovers it does not track (old upgrades, extras, failed imports) and unmonitors movies you clean up.";

    public override string Icon => "film";

    protected override string ApiVersion => "v3";

    protected override (string, string) Editor => ("movie/editor", "movieIds");

    protected override string Example => "http://192.168.1.10:7878";

    public override ItemViews Views => new([FileKind.Video]);

    protected override async Task<(List<ExternalItem>, List<ExternalFolder>)> TitlesAsync(IntegrationSettings s, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var url = BaseUrl(s);
        var movies = await GetAsync<List<MovieDto>>(s, "movie", ct) ?? [];
        var items = new List<ExternalItem>();
        var folders = new List<ExternalFolder>();
        foreach (var m in movies)
        {
            var title = m.Year is > 0 ? $"{m.Title} ({m.Year})" : m.Title ?? "?";
            var link = m.TitleSlug is null ? null : $"{url}/movie/{m.TitleSlug}";
            if (!string.IsNullOrEmpty(m.Path))
            {
                folders.Add(new ExternalFolder(m.Path, "movie", title, "Radarr", m.Monitored ? null : "unmonitored", link));
            }

            if (m.MovieFile is { } f && (f.Path ?? (m.Path is null || f.RelativePath is null ? null : m.Path.TrimEnd('/') + "/" + f.RelativePath)) is { } path)
            {
                items.Add(new ExternalItem("movie", f.Id.ToString(), m.Title ?? "?", path, f.Size)
                {
                    GroupKey = m.Id.ToString(), GroupTitle = m.Title, Year = m.Year, State = m.Monitored ? "monitored" : "unmonitored",
                    Detail = QualityName(f.Quality), AddedAt = Unix(f.DateAdded), Url = link, Label = $"Radarr: {title}", Badge = m.Monitored ? null : "unmonitored",
                });
            }
        }

        progress.Report(new IntegrationProgress("reading movies", movies.Count, movies.Count));
        return (items, folders);
    }

    private sealed record MovieDto(int Id, string? Title, int? Year, string? Path, string? TitleSlug, bool Monitored, MovieFileDto? MovieFile);

    private sealed record MovieFileDto(int Id, string? Path, string? RelativePath, long Size, string? DateAdded, QualityDto? Quality);
}

public sealed class SonarrIntegration(ItemServices services) : ArrIntegration(services)
{
    public override string Id => "sonarr";

    public override string Name => "Sonarr";

    public override string Description => "Marks episode files Sonarr manages, finds leftovers it does not track and unmonitors shows you clean up.";

    public override string Icon => "tv";

    protected override string ApiVersion => "v3";

    protected override (string, string) Editor => ("series/editor", "seriesIds");

    protected override string Example => "http://192.168.1.10:8989";

    public override ItemViews Views => new([FileKind.Video]);

    protected override async Task<(List<ExternalItem>, List<ExternalFolder>)> TitlesAsync(IntegrationSettings s, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var url = BaseUrl(s);
        var series = await GetAsync<List<SeriesDto>>(s, "series", ct) ?? [];
        var items = new List<ExternalItem>();
        var folders = new List<ExternalFolder>();
        var done = 0;
        foreach (var show in series)
        {
            var link = show.TitleSlug is null ? null : $"{url}/series/{show.TitleSlug}";
            if (!string.IsNullOrEmpty(show.Path))
            {
                folders.Add(new ExternalFolder(show.Path, "show", show.Title ?? "?", "Sonarr", show.Monitored ? null : "unmonitored", link));
            }

            foreach (var f in await GetAsync<List<EpisodeFileDto>>(s, $"episodefile?seriesId={show.Id}", ct) ?? [])
            {
                var path = f.Path ?? (show.Path is null || f.RelativePath is null ? null : show.Path.TrimEnd('/') + "/" + f.RelativePath);
                if (path is null)
                {
                    continue;
                }

                var season = $"Season {f.SeasonNumber}";
                items.Add(new ExternalItem("episode", f.Id.ToString(), FileName(path), path, f.Size)
                {
                    GroupKey = show.Id.ToString(), GroupTitle = show.Title, SubTitle = season, Year = show.Year, State = show.Monitored ? "monitored" : "unmonitored",
                    Detail = QualityName(f.Quality), AddedAt = Unix(f.DateAdded), Url = link, Label = $"Sonarr: {show.Title} · {season}",
                    Badge = show.Monitored ? null : "unmonitored", Parents = [("season", $"{show.Title} · {season}")],
                });
            }

            progress.Report(new IntegrationProgress("reading series", ++done, series.Count, show.Title));
        }

        return (items, folders);
    }

    private sealed record SeriesDto(int Id, string? Title, int? Year, string? Path, string? TitleSlug, bool Monitored);

    private sealed record EpisodeFileDto(int Id, int SeasonNumber, string? Path, string? RelativePath, long Size, string? DateAdded, QualityDto? Quality);
}

public sealed class LidarrIntegration(ItemServices services) : ArrIntegration(services)
{
    public override string Id => "lidarr";

    public override string Name => "Lidarr";

    public override string Description => "Marks music files Lidarr manages, finds leftovers it does not track and unmonitors artists you clean up.";

    public override string Icon => "music";

    protected override string ApiVersion => "v1";

    protected override (string, string) Editor => ("artist/editor", "artistIds");

    protected override string Example => "http://192.168.1.10:8686";

    public override ItemViews Views => new([FileKind.Audio]);

    protected override async Task<(List<ExternalItem>, List<ExternalFolder>)> TitlesAsync(IntegrationSettings s, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var url = BaseUrl(s);
        var artists = await GetAsync<List<ArtistDto>>(s, "artist", ct) ?? [];
        var items = new List<ExternalItem>();
        var folders = new List<ExternalFolder>();
        var done = 0;
        foreach (var a in artists)
        {
            var link = a.ForeignArtistId is null ? null : $"{url}/artist/{a.ForeignArtistId}";
            if (!string.IsNullOrEmpty(a.Path))
            {
                folders.Add(new ExternalFolder(a.Path, "artist", a.ArtistName ?? "?", "Lidarr", a.Monitored ? null : "unmonitored", link));
            }

            var albums = (await GetAsync<List<AlbumDto>>(s, $"album?artistId={a.Id}", ct) ?? []).ToDictionary(x => x.Id, x => x.Title);
            foreach (var f in await GetAsync<List<TrackFileDto>>(s, $"trackfile?artistId={a.Id}", ct) ?? [])
            {
                if (f.Path is null)
                {
                    continue;
                }

                var album = f.AlbumId is { } id && albums.TryGetValue(id, out var t) ? t : null;
                items.Add(new ExternalItem("track", f.Id.ToString(), FileName(f.Path), f.Path, f.Size)
                {
                    GroupKey = a.Id.ToString(), GroupTitle = a.ArtistName, SubTitle = album, State = a.Monitored ? "monitored" : "unmonitored",
                    Detail = QualityName(f.Quality), AddedAt = Unix(f.DateAdded), Url = link, Label = $"Lidarr: {a.ArtistName}{(album is null ? "" : " – " + album)}",
                    Badge = a.Monitored ? null : "unmonitored", Parents = album is null ? null : [("album", $"{a.ArtistName} – {album}")],
                });
            }

            progress.Report(new IntegrationProgress("reading artists", ++done, artists.Count, a.ArtistName));
        }

        return (items, folders);
    }

    private sealed record ArtistDto(int Id, string? ArtistName, string? Path, string? ForeignArtistId, bool Monitored);

    private sealed record AlbumDto(int Id, string? Title);

    private sealed record TrackFileDto(int Id, int? AlbumId, string? Path, long Size, string? DateAdded, QualityDto? Quality);
}

public static class DependencyInjection
{
    public static IServiceCollection AddArrIntegrations(this IServiceCollection services) =>
        services.AddIntegration<SonarrIntegration>().AddIntegration<RadarrIntegration>().AddIntegration<LidarrIntegration>();
}
