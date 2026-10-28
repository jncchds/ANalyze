using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ANalyze.Integrations.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Integrations.Downloads;

/// <summary>
/// Shared part of download clients: items are torrents/downloads (file or folder), roots are the download folders.
/// Seeding and downloading items are badged so cleanup plans skip them; completed ones are cleanup candidates;
/// anything in the download folders the client does not know is an orphan.
/// </summary>
public abstract class DownloadIntegration(ItemServices services) : ItemIntegration(services)
{
    protected abstract string Example { get; }

    protected virtual IEnumerable<IntegrationField> AuthFields =>
    [
        new("username", "Username", "text", "Leave empty when the client does not ask for a login from this address."),
        new("password", "Password", "password"),
    ];

    public override IReadOnlyList<IntegrationField> Fields =>
    [
        IntegrationHttp.Url(Example),
        .. AuthFields,
        IntegrationHttp.Mappings($"e.g. {Name} container /downloads ↔ ANalyze /library/downloads"),
        new("oldDays", "Finished more than (days) ago", "number", "Downloads finished before this are listed as old.", JsonValue.Create(30)),
        IntegrationHttp.AllowInvalidCertificate(),
    ];

    protected string BaseUrl(IntegrationSettings s) => s.Require("url", $"{Name} URL").TrimEnd('/');

    protected static ExternalFolder Root(string path, string label) => new(path, "root", label, "downloads", IsRoot: true);

    /// <summary>Badge for the browser: only states that matter when cleaning up.</summary>
    protected static string? BadgeFor(string state) => state is "seeding" or "downloading" or "completed" or "error" ? state : null;

    protected static string Combine(string dir, string name) => dir.TrimEnd('/', '\\') + "/" + name;

    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };
}

public sealed class QbittorrentIntegration(ItemServices services) : DownloadIntegration(services)
{
    public override string Id => "qbittorrent";

    public override string Name => "qBittorrent";

    public override string Description => "Marks torrents that are seeding so cleanup never touches them, lists finished torrents and files in the download folders qBittorrent no longer knows.";

    public override string Icon => "download";

    protected override string Example => "http://192.168.1.10:8080";

    public override async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => await Try(async () =>
    {
        var api = await Api.ConnectAsync(Http(settings), BaseUrl(settings), settings, ct);
        var version = await api.TextAsync("app/version", ct);
        var torrents = await api.JsonAsync<List<TorrentDto>>("torrents/info", ct) ?? [];
        return new IntegrationTestResult(true, $"Connected to qBittorrent {version} — {torrents.Count} torrents");
    });

    protected override async Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var api = await Api.ConnectAsync(Http(settings), BaseUrl(settings), settings, ct);
        var prefs = await api.JsonAsync<PrefsDto>("app/preferences", ct);
        var defaultPath = prefs?.SavePath ?? await api.TextAsync("app/defaultSavePath", ct);
        var categories = await api.JsonAsync<Dictionary<string, CategoryDto>>("torrents/categories", ct) ?? [];
        var torrents = await api.JsonAsync<List<TorrentDto>>("torrents/info", ct) ?? [];
        progress.Report(new IntegrationProgress("reading torrents", torrents.Count, torrents.Count));

        var folders = new List<ExternalFolder>();
        if (!string.IsNullOrEmpty(defaultPath))
        {
            folders.Add(Root(defaultPath, "qBittorrent download folder"));
        }

        if (prefs is { TempPathEnabled: true, TempPath: { Length: > 0 } temp })
        {
            folders.Add(Root(temp, "qBittorrent incomplete folder"));
        }

        foreach (var (name, c) in categories)
        {
            var path = string.IsNullOrEmpty(c.SavePath) ? (defaultPath is null ? null : Combine(defaultPath, name))
                : c.SavePath.StartsWith('/') || c.SavePath.Contains(":\\") ? c.SavePath
                : defaultPath is null ? null : Combine(defaultPath, c.SavePath);
            if (path is not null)
            {
                folders.Add(Root(path, $"qBittorrent category: {name}"));
            }
        }

        foreach (var savePath in torrents.Select(t => t.SavePath).Where(p => !string.IsNullOrEmpty(p)).Distinct())
        {
            folders.Add(Root(savePath!, "qBittorrent download folder"));
        }

        var items = torrents.Select(t =>
        {
            var state = State(t.State);
            return new ExternalItem("torrent", t.Hash ?? t.Name ?? "?", t.Name ?? "?", t.ContentPath ?? Combine(t.SavePath ?? "", t.Name ?? ""), t.Size)
            {
                IsDir = null, State = state, SubTitle = string.IsNullOrEmpty(t.Category) ? null : t.Category,
                Detail = $"ratio {t.Ratio:0.00}" + (string.IsNullOrEmpty(t.Category) ? "" : $" · {t.Category}"),
                AddedAt = t.AddedOn > 0 ? t.AddedOn : null, LastUsedAt = t.CompletionOn > 0 ? t.CompletionOn : null,
                Label = $"qBittorrent: {t.Name}", Badge = BadgeFor(state),
            };
        }).ToList();
        var seeding = items.Count(i => i.State == "seeding");
        return new Snapshot(items, folders.DistinctBy(f => f.Path.TrimEnd('/')).ToList(), $"{seeding} seeding");
    }

    internal static string State(string? s) => s switch
    {
        "uploading" or "stalledUP" or "forcedUP" or "queuedUP" or "checkingUP" => "seeding",
        "pausedUP" or "stoppedUP" => "completed",
        "error" or "missingFiles" => "error",
        "moving" => "moving",
        _ => "downloading",
    };

    /// <summary>qBittorrent Web API v2 with cookie login.</summary>
    private sealed class Api(HttpClient http, string baseUrl, string? cookie)
    {
        public static async Task<Api> ConnectAsync(HttpClient http, string baseUrl, IntegrationSettings s, CancellationToken ct)
        {
            var user = s.GetString("username");
            if (user is null)
            {
                return new Api(http, baseUrl, null);
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v2/auth/login")
            {
                Content = new FormUrlEncodedContent([new("username", user), new("password", s.GetString("password") ?? "")]),
            };
            req.Headers.Referrer = new Uri(baseUrl + "/");
            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode || body.Contains("Fails", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException(res.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "qBittorrent refused the login (too many failed attempts?)."
                    : "qBittorrent rejected the username or password.");
            }

            var sid = res.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith("SID=", StringComparison.Ordinal))
                : null;
            return new Api(http, baseUrl, sid);
        }

        public async Task<string> TextAsync(string path, CancellationToken ct)
        {
            using var res = await SendAsync(path, ct);
            return (await res.Content.ReadAsStringAsync(ct)).Trim();
        }

        public async Task<T?> JsonAsync<T>(string path, CancellationToken ct)
        {
            using var res = await SendAsync(path, ct);
            return await res.Content.ReadFromJsonAsync<T>(SnakeJson, ct);
        }

        private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/v2/{path}");
            req.Headers.Referrer = new Uri(baseUrl + "/");
            if (cookie is not null)
            {
                req.Headers.Add("Cookie", cookie);
            }

            var res = await http.SendAsync(req, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                res.Dispose();
                throw new HttpRequestException("qBittorrent needs a login (set the username and password).");
            }

            res.EnsureSuccessStatusCode();
            return res;
        }
    }

    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record TorrentDto(string? Hash, string? Name, string? SavePath, string? ContentPath, long Size, string? State, double Ratio,
        long CompletionOn, long AddedOn, string? Category);

    private sealed record CategoryDto(string? Name, string? SavePath);

    private sealed record PrefsDto(string? SavePath, string? TempPath, bool TempPathEnabled);
}

public sealed class TransmissionIntegration(ItemServices services) : DownloadIntegration(services)
{
    public override string Id => "transmission";

    public override string Name => "Transmission";

    public override string Description => "Marks torrents that are seeding so cleanup never touches them, lists finished torrents and files in the download folders Transmission no longer knows.";

    public override string Icon => "download";

    protected override string Example => "http://192.168.1.10:9091";

    public override async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => await Try(async () =>
    {
        var rpc = new Rpc(Http(settings), RpcUrl(settings), settings);
        var session = await rpc.CallAsync<SessionDto>("session-get", null, ct);
        var torrents = await rpc.CallAsync<TorrentsDto>("torrent-get", new JsonObject { ["fields"] = new JsonArray("id") }, ct);
        return new IntegrationTestResult(true, $"Connected to Transmission {session?.Version} — {torrents?.Torrents?.Count ?? 0} torrents");
    });

    protected override async Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var rpc = new Rpc(Http(settings), RpcUrl(settings), settings);
        var session = await rpc.CallAsync<SessionDto>("session-get", null, ct);
        var fields = new JsonArray("id", "hashString", "name", "downloadDir", "sizeWhenDone", "percentDone", "status", "uploadRatio", "doneDate", "addedDate", "labels", "error");
        var torrents = (await rpc.CallAsync<TorrentsDto>("torrent-get", new JsonObject { ["fields"] = fields }, ct))?.Torrents ?? [];
        progress.Report(new IntegrationProgress("reading torrents", torrents.Count, torrents.Count));

        var folders = new List<ExternalFolder>();
        if (session?.DownloadDir is { Length: > 0 } dl)
        {
            folders.Add(Root(dl, "Transmission download folder"));
        }

        if (session is { IncompleteDirEnabled: true, IncompleteDir: { Length: > 0 } inc })
        {
            folders.Add(Root(inc, "Transmission incomplete folder"));
        }

        foreach (var dir in torrents.Select(t => t.DownloadDir).Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            folders.Add(Root(dir!, "Transmission download folder"));
        }

        var items = torrents.Select(t =>
        {
            var state = State(t.Status, t.PercentDone, t.Error);
            var label = t.Labels is { Count: > 0 } l ? string.Join(", ", l) : null;
            return new ExternalItem("torrent", t.HashString ?? t.Id.ToString(), t.Name ?? "?", Combine(t.DownloadDir ?? "", t.Name ?? ""), t.SizeWhenDone)
            {
                IsDir = null, State = state, SubTitle = label, Detail = $"ratio {Math.Max(0, t.UploadRatio):0.00}" + (label is null ? "" : $" · {label}"),
                AddedAt = t.AddedDate > 0 ? t.AddedDate : null, LastUsedAt = t.DoneDate > 0 ? t.DoneDate : null,
                Label = $"Transmission: {t.Name}", Badge = BadgeFor(state),
            };
        }).ToList();
        return new Snapshot(items, folders.DistinctBy(f => f.Path.TrimEnd('/')).ToList(), $"{items.Count(i => i.State == "seeding")} seeding");
    }

    /// <summary>Status: 0 stopped, 1 check queued, 2 checking, 3 download queued, 4 downloading, 5 seed queued, 6 seeding.</summary>
    internal static string State(int status, double percentDone, int error) => error is 3 ? "error" : status switch
    {
        5 or 6 => "seeding",
        3 or 4 => "downloading",
        _ when percentDone >= 1 => "completed",
        _ => "downloading",
    };

    private static string RpcUrl(IntegrationSettings s)
    {
        var url = s.Require("url", "Transmission URL").TrimEnd('/');
        return url.EndsWith("/rpc", StringComparison.OrdinalIgnoreCase) ? url : url + "/transmission/rpc";
    }

    /// <summary>Transmission RPC: answers 409 with a session id that must be sent back.</summary>
    private sealed class Rpc(HttpClient http, string url, IntegrationSettings s)
    {
        private string? _session;

        public async Task<T?> CallAsync<T>(string method, JsonObject? args, CancellationToken ct)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(new JsonObject { ["method"] = method, ["arguments"] = args?.DeepClone() ?? new JsonObject() }),
                };
                if (s.GetString("username") is { } user)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{s.GetString("password")}")));
                }

                if (_session is not null)
                {
                    req.Headers.Add("X-Transmission-Session-Id", _session);
                }

                using var res = await http.SendAsync(req, ct);
                if (res.StatusCode == System.Net.HttpStatusCode.Conflict && res.Headers.TryGetValues("X-Transmission-Session-Id", out var ids))
                {
                    _session = ids.First();
                    continue;
                }

                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new HttpRequestException("Transmission rejected the username or password (401).");
                }

                res.EnsureSuccessStatusCode();
                var body = await res.Content.ReadFromJsonAsync<RpcResponse<T>>(Json, ct);
                if (body?.Result is not "success")
                {
                    throw new HttpRequestException($"Transmission: {body?.Result ?? "no answer"}");
                }

                return body.Arguments;
            }

            throw new HttpRequestException("Transmission did not accept the session id.");
        }
    }

    private sealed record RpcResponse<T>(string? Result, T? Arguments);

    private sealed record SessionDto(
        [property: JsonPropertyName("download-dir")] string? DownloadDir,
        [property: JsonPropertyName("incomplete-dir")] string? IncompleteDir,
        [property: JsonPropertyName("incomplete-dir-enabled")] bool IncompleteDirEnabled,
        string? Version);

    private sealed record TorrentsDto(List<TorrentDto>? Torrents);

    private sealed record TorrentDto(int Id, string? HashString, string? Name, string? DownloadDir, long SizeWhenDone, double PercentDone, int Status,
        double UploadRatio, long DoneDate, long AddedDate, List<string>? Labels, int Error);
}

public sealed class SabnzbdIntegration(ItemServices services) : DownloadIntegration(services)
{
    public override string Id => "sabnzbd";

    public override string Name => "SABnzbd";

    public override string Description => "Marks finished and running downloads and lists files in the SABnzbd folders it no longer knows (leftovers of failed or cleared jobs).";

    public override string Icon => "download";

    protected override string Example => "http://192.168.1.10:8080";

    public override IReadOnlyList<string> DockerHints => ["sabnzbd"];

    protected override IEnumerable<IntegrationField> AuthFields => [new("apiKey", "API key", "password", "SABnzbd → Config → General → API Key.")];

    public override async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => await Try(async () =>
    {
        var version = await CallAsync<JsonObject>(settings, "version", "", ct);
        var queue = await CallAsync<QueueRoot>(settings, "queue", "", ct);
        return new IntegrationTestResult(true, $"Connected to SABnzbd {version?["version"]} — {queue?.Queue?.Slots?.Count ?? 0} in queue");
    });

    protected override async Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var misc = (await CallAsync<ConfigRoot>(settings, "get_config", "&section=misc", ct))?.Config?.Misc;
        var cats = (await CallAsync<ConfigRoot>(settings, "get_config", "&section=categories", ct))?.Config?.Categories ?? [];
        var history = (await CallAsync<HistoryRoot>(settings, "history", "&limit=10000", ct))?.History?.Slots ?? [];
        var queue = (await CallAsync<QueueRoot>(settings, "queue", "", ct))?.Queue?.Slots ?? [];
        progress.Report(new IntegrationProgress("reading history", history.Count, history.Count));

        var complete = Absolute(misc?.CompleteDir);
        var incomplete = Absolute(misc?.DownloadDir);
        var folders = new List<ExternalFolder>();
        if (complete is not null)
        {
            folders.Add(Root(complete, "SABnzbd complete folder"));
        }

        if (incomplete is not null)
        {
            folders.Add(Root(incomplete, "SABnzbd incomplete folder"));
        }

        foreach (var c in cats.Where(c => !string.IsNullOrEmpty(c.Dir)))
        {
            var dir = Absolute(c.Dir) ?? (complete is null ? null : Combine(complete, c.Dir!));
            if (dir is not null)
            {
                folders.Add(Root(dir, $"SABnzbd category: {c.Name}"));
            }
        }

        var items = new List<ExternalItem>();
        foreach (var h in history.Where(h => !string.IsNullOrEmpty(h.Storage)))
        {
            var state = string.Equals(h.Status, "Completed", StringComparison.OrdinalIgnoreCase) ? "completed" : "failed";
            items.Add(new ExternalItem("download", h.NzoId ?? h.Storage!, h.Name ?? "?", h.Storage!, h.Bytes)
            {
                IsDir = null, State = state, SubTitle = h.Category, Detail = h.Category, LastUsedAt = h.Completed > 0 ? h.Completed : null,
                Label = $"SABnzbd: {h.Name}", Badge = state,
            });
        }

        if (incomplete is not null)
        {
            foreach (var q in queue.Where(q => !string.IsNullOrEmpty(q.Filename)))
            {
                items.Add(new ExternalItem("download", q.NzoId ?? q.Filename!, q.Filename!, Combine(incomplete, q.Filename!), (long)(q.Mb * 1024 * 1024))
                {
                    IsDir = true, State = "downloading", SubTitle = q.Cat, Detail = q.Cat, Label = $"SABnzbd: {q.Filename}", Badge = "downloading",
                });
            }
        }

        return new Snapshot(items, folders.DistinctBy(f => f.Path.TrimEnd('/')).ToList(), $"{queue.Count} in queue");
    }

    private static string? Absolute(string? path) => path is { Length: > 0 } p && (p.StartsWith('/') || p.Contains(":\\")) ? p : null;

    private async Task<T?> CallAsync<T>(IntegrationSettings s, string mode, string extra, CancellationToken ct)
    {
        var key = mode == "version" ? "" : "&apikey=" + Uri.EscapeDataString(s.Require("apiKey", "SABnzbd API key"));
        using var res = await Http(s).GetAsync($"{BaseUrl(s)}/api?mode={mode}&output=json{key}{extra}", ct);
        res.EnsureSuccessStatusCode();
        var text = await res.Content.ReadAsStringAsync(ct);
        if (text.Contains("API Key Incorrect", StringComparison.OrdinalIgnoreCase) || text.Contains("API Key Required", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("SABnzbd rejected the API key.");
        }

        return JsonSerializer.Deserialize<T>(text, SnakeJson);
    }

    private static readonly JsonSerializerOptions SnakeJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record ConfigRoot(ConfigDto? Config);

    private sealed record ConfigDto(MiscDto? Misc, List<CategoryDto>? Categories);

    private sealed record MiscDto(string? CompleteDir, string? DownloadDir);

    private sealed record CategoryDto(string? Name, string? Dir);

    private sealed record HistoryRoot(HistoryDto? History);

    private sealed record HistoryDto(List<HistorySlot>? Slots);

    private sealed record HistorySlot(string? NzoId, string? Name, string? Storage, long Bytes, string? Status, string? Category, long Completed);

    private sealed record QueueRoot(QueueDto? Queue);

    private sealed record QueueDto(List<QueueSlot>? Slots);

    private sealed record QueueSlot(string? NzoId, string? Filename, double Mb, string? Cat);
}

public static class DependencyInjection
{
    public static IServiceCollection AddDownloadIntegrations(this IServiceCollection services) =>
        services.AddIntegration<QbittorrentIntegration>().AddIntegration<TransmissionIntegration>().AddIntegration<SabnzbdIntegration>();
}
