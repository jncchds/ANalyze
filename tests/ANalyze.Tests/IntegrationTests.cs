using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Integrations;
using ANalyze.Integrations.Docker;
using ANalyze.Integrations.Downloads;
using ANalyze.Integrations.Items;

namespace ANalyze.Tests;

public class MountMathTests
{
    private static DockerClient.Mount M(string source, string destination) => new("bind", source, destination);

    [Fact]
    public void Other_mount_inside_ours()
    {
        // Radarr: host /mnt/media/movies → /movies; ANalyze: host /mnt/media → /library.
        var map = MountMath.Mappings([M("/mnt/media/movies", "/movies")], [M("/mnt/media", "/library")]);
        Assert.Equal([("/movies", "/library/movies")], map);
    }

    [Fact]
    public void Our_mount_inside_other()
    {
        // qBittorrent: host /mnt/data → /data; ANalyze: host /mnt/data/torrents → /library/torrents.
        var map = MountMath.Mappings([M("/mnt/data", "/data")], [M("/mnt/data/torrents/", "/library/torrents")]);
        Assert.Equal([("/data/torrents", "/library/torrents")], map);
    }

    [Fact]
    public void Unrelated_and_identical_mounts_give_nothing()
    {
        Assert.Empty(MountMath.Mappings([M("/srv/config", "/config")], [M("/mnt/media", "/library")]));
        Assert.Empty(MountMath.Mappings([M("/mnt/media", "/mnt/media")], [M("/", "/")]));
    }

    [Fact]
    public void Outside_a_container_paths_are_host_paths() =>
        Assert.Equal([("/tv", "/mnt/media/tv")], MountMath.Mappings([M("/mnt/media/tv", "/tv")], [M("/", "/")]));

    [Fact]
    public void A_folder_seen_twice_gives_both_candidates_most_specific_first()
    {
        // ANalyze: /mnt/main → /mnt/main and the dataset /mnt/main/media → /mnt/media; Plex: /mnt/main/media → /media/data.
        var self = new[] { M("/mnt/main", "/mnt/main"), M("/mnt/main/media", "/mnt/media") };
        Assert.Equal([("/media/data", "/mnt/media"), ("/media/data", "/mnt/main/media")], MountMath.Mappings([M("/mnt/main/media", "/media/data")], self));
    }
}

public class MappingPriorityTests
{
    [Fact]
    public void Manual_mappings_cover_their_whole_prefix_and_docker_fills_the_rest()
    {
        var merged = IntegrationPaths.Merge(
            [new PathMapping("/downloads", "/mnt/media/dl")],
            [new PathMapping("/downloads/movies", "/mnt/elsewhere"), new PathMapping("/downloads", "/x"), new PathMapping("/tv", "/mnt/media/tv")]);
        Assert.Equal([new PathMapping("/downloads", "/mnt/media/dl"), new PathMapping("/tv", "/mnt/media/tv")], merged);
        Assert.Equal("/mnt/media/dl/movies/a.mkv", IntegrationSettings.MapPath("/downloads/movies/a.mkv", merged));
    }

    [Fact]
    public void The_latest_mapping_for_the_same_prefix_wins() =>
        Assert.Equal("/new/a", IntegrationSettings.MapPath("/data/a", [new PathMapping("/data", "/old"), new PathMapping("/data/", "/new")]));

    [Fact]
    public void The_longest_prefix_still_wins_over_order() =>
        Assert.Equal("/m/a", IntegrationSettings.MapPath("/data/movies/a", [new PathMapping("/data/movies", "/m"), new PathMapping("/data", "/d")]));
}

public class PathAliasTests
{
    // ANalyze's mounts from the issue: /mnt/main → /mnt/main, /mnt/main/media → /mnt/media.
    private static readonly HostPath[] Mounts = [new("/mnt/main", "/mnt/main"), new("/mnt/media", "/mnt/main/media")];

    private static Func<string, bool> Roots(params string[] roots) => p => roots.Any(r => RootRegistry.IsUnder(r, p));

    [Theory]
    [InlineData("/mnt/main/media", "/mnt/media")]
    [InlineData("/mnt/main/media/Movies/A/a.mkv", "/mnt/media/Movies/A/a.mkv")]
    [InlineData("/mnt/media/TV", "/mnt/media/TV")]
    public void Paths_through_the_parent_mount_reach_the_dataset_root(string path, string expected) =>
        Assert.Equal(expected, PathAliases.IntoRoot(path, Mounts, Roots("/mnt/media")));

    [Fact]
    public void Dataset_paths_reach_a_parent_root() =>
        Assert.Equal("/mnt/main/media/Movies", PathAliases.IntoRoot("/mnt/media/Movies", Mounts, Roots("/mnt/main")));

    [Fact]
    public void Paths_no_mount_leads_into_a_root_stay_unresolved() =>
        Assert.Null(PathAliases.IntoRoot("/mnt/main/backups", Mounts, Roots("/mnt/media")));

    [Fact]
    public void Mapping_targets_and_unmapped_paths_are_moved_into_the_root()
    {
        // qBittorrent configured as /downloads → /mnt/main/media (the host path); only /mnt/media is scanned.
        var resolved = PathAliases.Resolve([new PathMapping("/downloads", "/mnt/main/media")], Mounts, Roots("/mnt/media"));
        Assert.Equal("/mnt/media/Movies/x.mkv", IntegrationSettings.MapPath("/downloads/Movies/x.mkv", resolved));
        // An app that reports the path through /mnt/main (or the host path) finds the file too.
        Assert.Equal("/mnt/media/TV/y.mkv", IntegrationSettings.MapPath("/mnt/main/media/TV/y.mkv", resolved));
        // Other paths are untouched.
        Assert.Equal("/config/z", IntegrationSettings.MapPath("/config/z", resolved));
    }

    [Fact]
    public void Mappings_already_inside_a_root_are_kept()
    {
        var resolved = PathAliases.Resolve([new PathMapping("/media/data", "/mnt/main/media")], Mounts, Roots("/mnt/main"));
        Assert.Equal(new PathMapping("/media/data", "/mnt/main/media"), resolved[0]);
        // ANalyze's other path for the dataset leads into the /mnt/main root.
        Assert.Equal("/mnt/main/media/a", IntegrationSettings.MapPath("/mnt/media/a", resolved));
    }
}

public class DockerMatchTests
{
    private static DockerClient.Container C(string name, string image, int port = 0, string? service = null) =>
        new(name + "-id", name, image, [], port > 0 ? [port] : [], [], [], service, "running");

    private static DockerState.Snapshot Snap(params DockerClient.Container[] cs) => new("27", cs, null, false, [], DateTimeOffset.UtcNow);

    private static IntegrationSettings Url(string url) => new() { Values = new() { ["url"] = url } };

    private sealed class Fake(string id, params string[] hints) : IIntegration
    {
        public string Id => id;
        public string Name => id;
        public string Description => "";
        public string Icon => "";
        public IReadOnlyList<IntegrationField> Fields { get; } = [new("url", "URL", "url")];
        public IReadOnlyList<string> DockerHints => hints.Length > 0 ? hints : [id];
        public Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => throw new NotSupportedException();
        public Task<IntegrationSyncResult> SyncAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct) => throw new NotSupportedException();
        public void MapEndpoints(Microsoft.AspNetCore.Routing.RouteGroupBuilder group) { }
    }

    [Fact]
    public void Finds_by_url_host_then_port_then_image()
    {
        var snap = Snap(C("tv", "lscr.io/linuxserver/sonarr:latest", 8989), C("radarr", "ghcr.io/hotio/radarr", 7878), C("plex", "plexinc/pms-docker", 32400, service: "media"));
        var none = new IntegrationSettings();
        Assert.Equal("tv", DockerPaths.FindContainer(snap, new Fake("sonarr"), Url("http://tv:8989"), none)?.Name);
        Assert.Equal("radarr", DockerPaths.FindContainer(snap, new Fake("radarr"), Url("http://192.168.1.10:7878"), none)?.Name);
        Assert.Equal("plex", DockerPaths.FindContainer(snap, new Fake("plex"), Url("http://media:32400"), none)?.Name);
        Assert.Equal("tv", DockerPaths.FindContainer(snap, new Fake("sonarr"), new IntegrationSettings(), none)?.Name);
        Assert.Null(DockerPaths.FindContainer(snap, new Fake("lidarr"), new IntegrationSettings(), none));
        var overrides = new IntegrationSettings { Values = new() { ["overrides"] = "sonarr=radarr" } };
        Assert.Equal("radarr", DockerPaths.FindContainer(snap, new Fake("sonarr"), Url("http://tv:8989"), overrides)?.Name);
    }
}

public class DownloadStateTests
{
    [Theory]
    [InlineData("uploading", "seeding")]
    [InlineData("stalledUP", "seeding")]
    [InlineData("pausedUP", "completed")]
    [InlineData("stoppedUP", "completed")]
    [InlineData("downloading", "downloading")]
    [InlineData("missingFiles", "error")]
    public void Qbittorrent(string state, string expected) => Assert.Equal(expected, QbittorrentIntegration.State(state));

    [Theory]
    [InlineData(6, 1.0, 0, "seeding")]
    [InlineData(4, 0.5, 0, "downloading")]
    [InlineData(0, 1.0, 0, "completed")]
    [InlineData(0, 0.3, 0, "downloading")]
    [InlineData(0, 1.0, 3, "error")]
    public void Transmission(int status, double done, int error, string expected) => Assert.Equal(expected, TransmissionIntegration.State(status, done, error));
}

/// <summary>A download-client-like plugin that reports sample files under another prefix (/data instead of the library path).</summary>
internal sealed class FakeItems(ItemServices services) : ItemIntegration(services)
{
    public override string Id => "fake";
    public override string Name => "Fake";
    public override string Description => "Test";
    public override string Icon => "box";
    public override IReadOnlyList<IntegrationField> Fields { get; } = [IntegrationHttp.Mappings("test")];

    public override Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct) => Task.FromResult(new IntegrationTestResult(true, "ok"));

    protected override Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct) =>
        Task.FromResult(new Snapshot(
        [
            new ExternalItem("torrent", "a", "IMG_0001", "/data/Photos/2021/IMG_0001.jpg", 300_000) { State = "seeding", Badge = "seeding" },
            new ExternalItem("torrent", "b", "IMG_0002", "/data/Photos/2021/IMG_0002.jpg", 300_000) { State = "completed", Badge = "completed" },
            new ExternalItem("torrent", "c", "Gone", "/data/gone.mkv", 5_000) { State = "completed" },
        ],
        [new ExternalFolder("/data/Photos", "root", "Fake downloads", IsRoot: true)]));
}

public class ItemIntegrationTests
{
    [Fact]
    public async Task Sync_matches_files_finds_untracked_and_copies_and_protects_seeding()
    {
        await using var app = new TestApp { ExtraServices = s => s.AddIntegration<FakeItems>() };
        using var http = app.CreateClient();
        await app.ScanAsync(http);

        (await http.PutAsJsonAsync("/api/v1/integrations/fake", new { enabled = true, syncAfterScan = false, values = new { } })).EnsureSuccessStatusCode();
        (await http.PostAsync("/api/v1/integrations/fake/sync", null)).EnsureSuccessStatusCode();
        JsonElement fake = default;
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            fake = (await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations")).EnumerateArray().First(x => x.GetProperty("id").GetString() == "fake");
            if (fake.GetProperty("status").TryGetProperty("lastResult", out var r) && r.ValueKind == JsonValueKind.Object)
            {
                break;
            }
        }

        var result = fake.GetProperty("status").GetProperty("lastResult");
        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetProperty("message").GetString());
        Assert.Equal(2, result.GetProperty("matched").GetInt64());

        var summary = await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations/fake/summary");
        Assert.Equal(3, summary.GetProperty("totals").GetProperty("items").GetInt64());
        Assert.Equal(1, summary.GetProperty("roots").GetArrayLength());

        var unmatched = await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations/fake/unmatched");
        Assert.Equal("/data/gone.mkv", unmatched[0].GetProperty("externalPath").GetString());

        // Photos/ holds IMG_0001 (1).jpg and the hard link Photos/link.jpg besides the two known files.
        var untracked = await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations/fake/untracked?mode=files");
        var names = untracked.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("relPath").GetString()).Order().ToList();
        Assert.Contains("Photos/2021/IMG_0001 (1).jpg", names);
        Assert.DoesNotContain("Photos/2021/IMG_0001.jpg", names);

        // Same content as IMG_0001 elsewhere: the backup and the "(1)" copy; the hard link to IMG_0002 is not a copy.
        var copies = await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations/fake/copies");
        var copyPaths = copies.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("relPath").GetString()).ToList();
        Assert.Contains("Backup/Photos/IMG_0001.jpg", copyPaths);
        Assert.DoesNotContain("Photos/link.jpg", copyPaths);

        // A cleanup plan leaves the seeding file alone.
        var group = await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations/fake/group?key=a");
        var seeding = group[0].GetProperty("entryId").GetInt64();

        // A label in the browser opens the title it belongs to; a folder with several titles opens the list.
        var located = await http.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/fake/locate?entryId={seeding}");
        Assert.Equal("a", located.GetProperty("key").GetString());
        var photos = summary.GetProperty("roots")[0].GetProperty("entryId").GetInt64();
        located = await http.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/fake/locate?entryId={photos}");
        Assert.Equal(JsonValueKind.Null, located.TryGetProperty("key", out var k) ? k.ValueKind : JsonValueKind.Null);
        var byKey = await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations/fake/groups?key=a");
        Assert.Equal("IMG_0001", Assert.Single(byKey.GetProperty("items").EnumerateArray()).GetProperty("title").GetString());
        var res = await http.PostAsJsonAsync("/api/v1/plans", new { entryIds = new[] { seeding }, op = "trash" }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        Assert.Equal(0, plan.GetProperty("summary").GetProperty("included").GetInt32());
    }
}
