using ANalyze.Infrastructure.Settings;
using System.Text.Json.Nodes;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Integrations.Docker;

/// <summary>
/// Reads the containers on this Docker host and works out, from the bind mounts, how every other app's paths translate to
/// ANalyze's paths: "/movies" in the Radarr container is host "/mnt/media/movies", which is "/library/movies" here. Other
/// integrations use these mappings automatically. Also labels folders that are mounted into other containers.
/// </summary>
public sealed class DockerIntegration(DockerState state, SqliteSetup sqlite, RootRegistry roots) : IIntegration
{
    public string Id => "docker";

    public string Name => "Docker";

    public string Description => "Finds the containers of your other apps and works out path mappings from their volumes, so the other integrations match files without manual mappings.";

    public string Icon => "container";

    public IReadOnlyList<string> DockerHints => [];

    public IReadOnlyList<IntegrationField> Fields { get; } =
    [
        new("endpoint", "Docker endpoint", "text",
            "Mount the socket read-only (-v /var/run/docker.sock:/var/run/docker.sock:ro) or use a socket proxy (tcp://docker-proxy:2375) that allows CONTAINERS.",
            JsonValue.Create("unix:///var/run/docker.sock")),
        new("self", "ANalyze container", "text", "Name of ANalyze's own container. Empty: found automatically from the hostname."),
        new("overrides", "Container per app", "text", "Only when guessing picks the wrong container: plex=my-plex; sonarr=tv", Placeholder: "plex=plex; radarr=radarr-4k"),
    ];

    public async Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct)
    {
        try
        {
            var snap = await state.RefreshAsync(settings, ct);
            var self = snap.Self is null
                ? snap.InContainer ? "ANalyze's own container was not found — set its name" : "ANalyze is not in a container (paths are host paths)"
                : $"ANalyze is \"{snap.Self.Name}\"";
            return new IntegrationTestResult(true, $"Docker {snap.Version} — {snap.Containers.Count} running containers; {self}");
        }
        catch (Exception e) when (e is HttpRequestException or System.Net.Sockets.SocketException or TaskCanceledException or InvalidOperationException or UriFormatException or IOException)
        {
            return new IntegrationTestResult(false, e.Message);
        }
    }

    public async Task<IntegrationSyncResult> SyncAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var snap = await state.RefreshAsync(settings, ct);
        if (snap.InContainer && snap.Self is null)
        {
            return new IntegrationSyncResult(false, "ANalyze's own container was not found; set its name in the Docker settings.");
        }

        // Label local folders that other containers mount.
        using var conn = sqlite.Open();
        var matcher = new PathMatcher(conn, roots, []);
        var notes = new Dictionary<(long, string), Annotations.Item>();
        foreach (var c in snap.Containers.Where(c => c.Id != snap.Self?.Id))
        {
            foreach (var m in c.Mounts)
            {
                foreach (var (from, to) in MountMath.Mappings([m], snap.SelfMounts))
                {
                    if (matcher.FindDir(to) is { } dir)
                    {
                        notes.TryAdd((dir, c.Name), new Annotations.Item(dir, "mount", $"{c.Name}: {from}", c.Image));
                    }
                }
            }
        }

        Annotations.Replace(conn, Id, notes.Values);
        return new IntegrationSyncResult(true, $"{snap.Containers.Count} containers, {notes.Count} mounted folders", snap.Containers.Count, notes.Count);
    }

    public void MapEndpoints(RouteGroupBuilder group) =>
        group.MapGet("/containers", async (IntegrationHost host, IServiceProvider sp, CancellationToken ct) =>
        {
            var settings = await host.LoadAsync(Id, ct);
            var snap = await state.GetAsync(settings, ct);
            var paths = sp.GetRequiredService<DockerPaths>();
            var apps = new List<(string Id, string Name, DockerClient.Container? Container, IReadOnlyList<PathMapping> Mappings)>();
            foreach (var i in host.All.Where(i => i.Id != Id))
            {
                var s = await host.LoadAsync(i.Id, ct);
                if (!s.Enabled)
                {
                    continue;
                }

                var c = DockerPaths.FindContainer(snap, i, s, settings);
                apps.Add((i.Id, i.Name, c, c is null ? [] : paths.MappingsFor(snap, c)));
            }

            return new
            {
                version = snap.Version,
                inContainer = snap.InContainer,
                self = snap.Self?.Name,
                selfMounts = snap.SelfMounts.Select(m => new { source = m.Source, destination = m.Destination }),
                containers = snap.Containers.Select(c => new
                {
                    c.Name, c.Image, c.State, isSelf = c.Id == snap.Self?.Id,
                    mounts = c.Mounts.Select(m => new { m.Type, m.Source, m.Destination }),
                    apps = apps.Where(a => a.Container?.Id == c.Id).Select(a => new { id = a.Id, name = a.Name, mappings = a.Mappings }),
                }),
                missing = apps.Where(a => a.Container is null).Select(a => new { id = a.Id, name = a.Name }),
            };
        });
}

/// <summary>Cached view of the Docker host (refreshed at most once a minute unless asked).</summary>
public sealed class DockerState
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snap;
    private string? _endpoint;
    private string? _selfName;

    public sealed record Snapshot(string? Version, IReadOnlyList<DockerClient.Container> Containers, DockerClient.Container? Self, bool InContainer,
        IReadOnlyList<DockerClient.Mount> SelfMounts, DateTimeOffset At);

    public async Task<Snapshot> GetAsync(IntegrationSettings settings, CancellationToken ct)
    {
        var snap = _snap;
        if (snap is not null && _endpoint == Endpoint(settings) && _selfName == settings.GetString("self") && DateTimeOffset.UtcNow - snap.At < TimeSpan.FromMinutes(1))
        {
            return snap;
        }

        return await RefreshAsync(settings, ct);
    }

    public async Task<Snapshot> RefreshAsync(IntegrationSettings settings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var endpoint = Endpoint(settings);
            var client = new DockerClient(endpoint);
            var version = await client.VersionAsync(ct);
            var containers = await client.ContainersAsync(ct);
            var inContainer = File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");
            var selfName = settings.GetString("self");
            var self = selfName is not null
                ? containers.FirstOrDefault(c => c.Name == selfName || c.ComposeService == selfName)
                : inContainer ? containers.FirstOrDefault(c => Environment.MachineName.Length >= 12 && c.Id.StartsWith(Environment.MachineName, StringComparison.Ordinal)) : null;
            // Outside a container ANalyze sees host paths, which is the same as mounting "/" at "/".
            IReadOnlyList<DockerClient.Mount> selfMounts = self?.Mounts ?? (inContainer ? [] : [new DockerClient.Mount("bind", "/", "/")]);
            _snap = new Snapshot(version, containers, self, inContainer, selfMounts, DateTimeOffset.UtcNow);
            _endpoint = endpoint;
            _selfName = selfName;
            return _snap;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Endpoint(IntegrationSettings s) => s.GetString("endpoint") ?? "unix:///var/run/docker.sock";
}

/// <summary>Automatic path mappings for the other integrations, from their container's mounts.</summary>
public sealed class DockerPaths(IServiceProvider sp, DockerState state, RootRegistry roots) : IPathMappingSource
{
    public async Task<AutoMappings?> ForAsync(IIntegration integration, IntegrationSettings settings, CancellationToken ct)
    {
        if (integration.Id == "docker" || integration.DockerHints.Count == 0)
        {
            return null;
        }

        // Resolved lazily: the host depends on every integration, including Docker.
        var host = sp.GetRequiredService<IntegrationHost>();
        var docker = await host.LoadAsync("docker", ct);
        if (!docker.Enabled)
        {
            return null;
        }

        var snap = await state.GetAsync(docker, ct);
        if (snap.InContainer && snap.Self is null)
        {
            return new AutoMappings("docker", null, [], "ANalyze's own container was not found");
        }

        var c = FindContainer(snap, integration, settings, docker);
        return c is null
            ? new AutoMappings("docker", null, [], $"No container found for {integration.Name}")
            : new AutoMappings("docker", c.Name, MappingsFor(snap, c));
    }

    /// <summary>Mappings from the container's paths to ANalyze's paths, only those that reach a scanned root.</summary>
    public IReadOnlyList<PathMapping> MappingsFor(DockerState.Snapshot snap, DockerClient.Container c) =>
        c.Id == snap.Self?.Id ? [] : MountMath.Mappings(c.Mounts, snap.SelfMounts)
            .Where(m => roots.All.Any(r => RootRegistry.IsUnder(r.Path, m.To) || RootRegistry.IsUnder(m.To, r.Path)))
            .DistinctBy(m => m.From)
            .Select(m => new PathMapping(m.From, m.To))
            .ToList();

    /// <summary>Override, then the URL's host (container name, compose service or IP), then a published port, then the image/name hints.</summary>
    public static DockerClient.Container? FindContainer(DockerState.Snapshot snap, IIntegration integration, IntegrationSettings settings, IntegrationSettings docker)
    {
        var others = snap.Containers.Where(c => c.Id != snap.Self?.Id).ToList();
        var overrides = (docker.GetString("overrides") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        if (overrides.TryGetValue(integration.Id, out var name))
        {
            return others.FirstOrDefault(c => c.Name == name || c.ComposeService == name);
        }

        if (integration.UrlField is { } field && Uri.TryCreate(settings.GetString(field) ?? "", UriKind.Absolute, out var url))
        {
            var byHost = others.Where(c => string.Equals(c.Name, url.Host, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.ComposeService, url.Host, StringComparison.OrdinalIgnoreCase) || c.Ips.Contains(url.Host)).ToList();
            if (byHost.Count == 1)
            {
                return byHost[0];
            }

            var byPort = others.Where(c => c.PublicPorts.Contains(url.Port)).ToList();
            if (byPort.Count == 1)
            {
                return byPort[0];
            }
        }

        var hints = integration.DockerHints;
        var byImage = others.Where(c => hints.Any(h => ImageName(c.Image).Contains(h, StringComparison.OrdinalIgnoreCase))).ToList();
        var byName = others.Where(c => hints.Any(h => c.Name.Contains(h, StringComparison.OrdinalIgnoreCase))).ToList();
        return byImage.FirstOrDefault(c => byName.Contains(c)) ?? (byImage.Count == 1 ? byImage[0] : null) ?? (byName.Count == 1 ? byName[0] : null) ?? byImage.FirstOrDefault();
    }

    /// <summary>"lscr.io/linuxserver/sonarr:latest" → "linuxserver/sonarr".</summary>
    private static string ImageName(string image)
    {
        var at = image.IndexOf('@');
        var noDigest = at >= 0 ? image[..at] : image;
        var slash = noDigest.LastIndexOf('/');
        var colon = noDigest.LastIndexOf(':');
        var noTag = colon > slash ? noDigest[..colon] : noDigest;
        var parts = noTag.Split('/');
        return parts.Length > 1 && parts[0].Contains('.') ? string.Join('/', parts[1..]) : noTag;
    }
}

/// <summary>Host paths of the folders ANalyze sees: its own container's bind mounts (destination → source), including those mounted at the same path.</summary>
public sealed class DockerHostPaths(IServiceProvider sp, DockerState state) : IHostPathSource
{
    public async Task<IReadOnlyList<HostPath>> GetAsync(CancellationToken ct)
    {
        var docker = await sp.GetRequiredService<IntegrationHost>().LoadAsync("docker", ct);
        if (!docker.Enabled)
        {
            return [];
        }

        var snap = await state.GetAsync(docker, ct);
        return snap.Self is null
            ? []
            : snap.Self.Mounts.Where(m => m.Type is null or "bind")
                .Select(m => new HostPath(m.Destination, m.Source)).ToList();
    }
}

/// <summary>Path arithmetic between two containers that share host folders.</summary>
public static class MountMath
{
    /// <summary>
    /// For each pair of mounts that overlap on the host, the prefix mapping from the other container's path to ours.
    /// Other "/movies" ← host "/mnt/media/movies"; ours "/library" ← host "/mnt/media" ⇒ "/movies" → "/library/movies".
    /// A path we see through several of our mounts (host "/mnt/main" at "/mnt/main" and its dataset "/mnt/main/media" at
    /// "/mnt/media") gives one candidate per mount, the most specific of ours first; callers keep the first that reaches a root.
    /// </summary>
    public static List<(string From, string To)> Mappings(IEnumerable<DockerClient.Mount> other, IEnumerable<DockerClient.Mount> self)
    {
        var selfList = self.OrderByDescending(m => Trim(m.Source).Length).ToList();
        var result = new List<(string From, string To)>();
        foreach (var o in other)
        {
            var os = Trim(o.Source);
            var od = Trim(o.Destination);
            foreach (var s in selfList)
            {
                var ss = Trim(s.Source);
                var sd = Trim(s.Destination);
                string from, to;
                if (Under(ss, os))
                {
                    from = od;
                    to = Join(sd, os[ss.Length..]);
                }
                else if (Under(os, ss))
                {
                    from = Join(od, ss[os.Length..]);
                    to = sd;
                }
                else
                {
                    continue;
                }

                if (from != to && !result.Contains((from, to)))
                {
                    result.Add((from, to));
                }
            }
        }

        return result;
    }

    private static string Trim(string p) => p.Length > 1 ? p.TrimEnd('/') : p;

    private static bool Under(string parent, string path) =>
        parent == "/" || path == parent || path.StartsWith(parent + "/", StringComparison.Ordinal);

    private static string Join(string a, string rest) =>
        rest.Length == 0 ? a : a == "/" ? (rest.StartsWith('/') ? rest : "/" + rest) : a + (rest.StartsWith('/') ? rest : "/" + rest);
}

public static class DependencyInjection
{
    public static IServiceCollection AddDockerIntegration(this IServiceCollection services)
    {
        services.AddSingleton<DockerState>();
        services.AddSingleton<DockerPaths>();
        services.AddSingleton<IPathMappingSource>(sp => sp.GetRequiredService<DockerPaths>());
        services.AddSingleton<IHostPathSource, DockerHostPaths>();
        return services.AddIntegration<DockerIntegration>();
    }
}
