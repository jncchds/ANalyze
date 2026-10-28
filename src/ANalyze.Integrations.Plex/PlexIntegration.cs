using System.Text.Json.Nodes;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ANalyze.Integrations.Plex;

/// <summary>
/// Cross-references Plex libraries with the scanned files: which files and folders are Plex movies, shows or music,
/// and which titles take the most space. Plex often runs in its own container where the same files have other paths,
/// so matching tries configured mappings, then mappings detected from sample files, then file name + size.
/// </summary>
public sealed class PlexIntegration(IHttpClientFactory http, SqliteSetup sqlite, RootRegistry roots, ILogger<PlexIntegration> log) : IIntegration
{
    public const string HttpClientName = "plex";
    public const string HttpClientInsecure = "plex-insecure";

    public string Id => "plex";

    public string Name => "Plex";

    public string Description => "Marks Plex movies, shows and music in the browser and ranks titles by size and watch state.";

    public string Icon => "clapperboard";

    public IReadOnlyList<IntegrationField> Fields { get; } =
    [
        new("url", "Server URL", "url", "Address of Plex Media Server as seen from the ANalyze container.", Placeholder: "http://192.168.1.10:32400"),
        new("token", "X-Plex-Token", "password", "Plex Web → any item → ⋯ → Get Info → View XML: the token is in the URL (X-Plex-Token=…)."),
        new("mappings", "Path mappings", "mappings",
            "Only needed when Plex sees the files under other paths (e.g. Plex container /data/movies ↔ ANalyze /library/Movies). " +
            "Leave empty to detect automatically; use \"Suggest\" to fill it in."),
        new("allowInvalidCertificate", "Accept self-signed certificates", "bool", "For https URLs with a certificate the container does not trust.", JsonValue.Create(false)),
        new("watchedDays", "Watched long ago (days)", "number", "Titles last watched before this are cleanup candidates.", JsonValue.Create(180)),
        new("unwatchedDays", "Never watched, added more than (days) ago", "number", null, JsonValue.Create(365)),
    ];

    internal PlexClient Client(IntegrationSettings s)
    {
        var url = s.GetString("url") ?? throw new InvalidOperationException("Set the Plex server URL.");
        var token = s.GetString("token") ?? throw new InvalidOperationException("Set the Plex token.");
        return new PlexClient(http.CreateClient(s.GetBool("allowInvalidCertificate") ? HttpClientInsecure : HttpClientName), url, token);
    }

    public async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct)
    {
        try
        {
            var client = Client(settings);
            var (name, _, version) = await client.ServerAsync(ct);
            var sections = await client.SectionsAsync(ct);
            var details = new JsonArray(sections.Select(s => (JsonNode)new JsonObject
            {
                ["title"] = s.Title,
                ["type"] = s.Type,
                ["locations"] = new JsonArray(s.Locations.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray()),
            }).ToArray());
            return new IntegrationTestResult(true, $"Connected to {name} {version} — {sections.Count} libraries", details);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException or UriFormatException)
        {
            return new IntegrationTestResult(false, e.Message);
        }
    }

    public async Task<IntegrationSyncResult> SyncAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var client = Client(settings);
        var (_, machineId, _) = await client.ServerAsync(ct);
        var baseUrl = settings.GetString("url")!.TrimEnd('/');
        var sections = await client.SectionsAsync(ct);
        var files = new List<PlexClient.MediaFile>();
        foreach (var section in sections)
        {
            await foreach (var f in client.FilesAsync(section, ct))
            {
                files.Add(f);
                if (files.Count % 500 == 0)
                {
                    progress.Report(new IntegrationProgress("reading " + section.Title, files.Count, 0));
                }
            }
        }

        progress.Report(new IntegrationProgress("matching", 0, files.Count));
        using var conn = sqlite.Open();
        var matcher = new PathMatcher(conn, roots, settings.GetMappings("mappings"));
        matcher.Detect(files.Select(f => (f.File, f.Size)));
        var stored = Store(conn, files, sections, matcher, baseUrl, machineId, progress);
        log.LogInformation("Plex sync: {Files} files, {Matched} matched ({Detected})", files.Count, stored.Matched, string.Join(", ", matcher.Detected.Select(m => $"{m.From} → {m.To}")));
        var extra = matcher.Detected.Count > 0 ? $"; detected mappings: {string.Join(", ", matcher.Detected.Select(m => $"{m.From} → {m.To}"))}" : "";
        return new IntegrationSyncResult(true, $"{files.Count} files from {sections.Count} libraries, {stored.Matched} found on disk{extra}", files.Count, stored.Matched);
    }

    private (int Matched, int Annotations) Store(SqliteConnection conn, List<PlexClient.MediaFile> files, IReadOnlyList<PlexClient.Section> sections, PathMatcher matcher,
        string baseUrl, string? machineId, IProgress<IntegrationProgress> progress)
    {
        string? Link(string ratingKey) => machineId is null ? null
            : $"{baseUrl}/web/index.html#!/server/{machineId}/details?key={Uri.EscapeDataString("/library/metadata/" + ratingKey)}";

        var notes = new Dictionary<(long, string), Annotations.Item>();
        void Note(long entryId, string kind, string label, string? detail = null, string? state = null, string? url = null) =>
            notes.TryAdd((entryId, kind), new Annotations.Item(entryId, kind, label, detail, state, url));

        // Library folders themselves.
        var libraryDirs = new HashSet<long>();
        foreach (var s in sections)
        {
            foreach (var loc in s.Locations)
            {
                if (matcher.FindDir(loc) is { } dirId)
                {
                    libraryDirs.Add(dirId);
                    Note(dirId, "library", $"Plex library: {s.Title}", s.Type);
                }
            }
        }

        var matched = 0;
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM PlexItems";
            del.ExecuteNonQuery();
        }

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO PlexItems (RatingKey, SectionKey, SectionTitle, Type, Title, ShowTitle, ParentTitle, Season, Episode, Year, ViewCount,
                LastViewedAt, AddedAt, PlexPath, Size, Resolution, Versions, EntryId, MatchedBy, Url)
            VALUES ($rk, $sk, $st, $type, $title, $show, $parent, $season, $ep, $year, $views, $last, $added, $path, $size, $res, $versions, $entry, $by, $url)
            """;
        foreach (var p in new[] { "$rk", "$sk", "$st", "$type", "$title", "$show", "$parent", "$season", "$ep", "$year", "$views", "$last", "$added", "$path", "$size", "$res", "$versions", "$entry", "$by", "$url" })
        {
            ins.Parameters.Add(new SqliteParameter(p, null));
        }

        var i = 0;
        foreach (var f in files)
        {
            var (entryId, by) = matcher.Match(f.File, f.Size);
            var url = Link(f.RatingKey);
            object V(object? v) => v ?? DBNull.Value;
            ins.Parameters["$rk"].Value = f.RatingKey;
            ins.Parameters["$sk"].Value = f.SectionKey;
            ins.Parameters["$st"].Value = f.SectionTitle;
            ins.Parameters["$type"].Value = f.Type;
            ins.Parameters["$title"].Value = f.Title;
            ins.Parameters["$show"].Value = V(f.ShowTitle);
            ins.Parameters["$parent"].Value = V(f.ParentTitle);
            ins.Parameters["$season"].Value = V(f.Season);
            ins.Parameters["$ep"].Value = V(f.Episode);
            ins.Parameters["$year"].Value = V(f.Year);
            ins.Parameters["$views"].Value = f.ViewCount;
            ins.Parameters["$last"].Value = V(f.LastViewedAt);
            ins.Parameters["$added"].Value = V(f.AddedAt);
            ins.Parameters["$path"].Value = f.File;
            ins.Parameters["$size"].Value = f.Size;
            ins.Parameters["$res"].Value = V(f.Resolution);
            ins.Parameters["$versions"].Value = f.Versions;
            ins.Parameters["$entry"].Value = V(entryId);
            ins.Parameters["$by"].Value = V(by);
            ins.Parameters["$url"].Value = V(url);
            ins.ExecuteNonQuery();

            if (entryId is { } id)
            {
                matched++;
                var watched = f.Type == "track" ? null : f.ViewCount > 0 ? "watched" : "unwatched";
                var (parent, grand) = matcher.Parents(id);
                switch (f.Type)
                {
                    case "movie":
                        var movie = f.Year is { } y ? $"{f.Title} ({y})" : f.Title;
                        Note(id, "movie", movie, f.Resolution, watched, url);
                        if (parent is { } mp && !libraryDirs.Contains(mp))
                        {
                            Note(mp, "movie", movie, null, watched, url);
                        }

                        break;
                    case "episode":
                        var code = f.Season is { } se && f.Episode is { } ep ? $"S{se:00}E{ep:00}" : null;
                        Note(id, "episode", $"{f.ShowTitle} {code} · {f.Title}".Trim(), f.Resolution, watched, url);
                        if (parent is { } sp && !libraryDirs.Contains(sp))
                        {
                            Note(sp, "season", $"{f.ShowTitle} · {f.ParentTitle ?? "Season " + f.Season}");
                            if (grand is { } gp && !libraryDirs.Contains(gp))
                            {
                                Note(gp, "show", f.ShowTitle ?? f.Title);
                            }
                        }

                        break;
                    case "track":
                        Note(id, "track", $"{f.ShowTitle} – {f.Title}", null, null, url);
                        if (parent is { } ap && !libraryDirs.Contains(ap))
                        {
                            Note(ap, "album", $"{f.ShowTitle} – {f.ParentTitle}");
                            if (grand is { } gp && !libraryDirs.Contains(gp))
                            {
                                Note(gp, "artist", f.ShowTitle ?? "");
                            }
                        }

                        break;
                }
            }

            if (++i % 1000 == 0)
            {
                progress.Report(new IntegrationProgress("matching", i, files.Count));
            }
        }

        tx.Commit();
        Annotations.Replace(conn, Id, notes.Values);
        return (matched, notes.Count);
    }

    public void MapEndpoints(RouteGroupBuilder group) => PlexEndpoints.Map(group, this);
}
