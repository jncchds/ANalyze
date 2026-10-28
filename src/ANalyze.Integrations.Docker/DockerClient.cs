using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ANalyze.Integrations.Docker;

/// <summary>Minimal Docker Engine API client over the unix socket or TCP (e.g. a docker-socket-proxy). Read-only calls only.</summary>
public sealed class DockerClient
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();
    private readonly HttpClient _http;

    public DockerClient(string endpoint)
    {
        _http = Clients.GetOrAdd(endpoint.Trim(), Create);
    }

    public sealed record Mount(string? Type, string Source, string Destination);

    public sealed record Container(string Id, string Name, string Image, IReadOnlyList<Mount> Mounts, IReadOnlyList<int> PublicPorts, IReadOnlyList<int> PrivatePorts,
        IReadOnlyList<string> Ips, string? ComposeService, string? State);

    public async Task<string?> VersionAsync(CancellationToken ct) =>
        (await _http.GetFromJsonAsync<VersionDto>("/version", Json, ct))?.Version;

    public async Task<IReadOnlyList<Container>> ContainersAsync(CancellationToken ct)
    {
        var list = await _http.GetFromJsonAsync<List<ContainerDto>>("/containers/json", Json, ct) ?? [];
        return list.Select(c => new Container(
            c.Id,
            (c.Names?.FirstOrDefault() ?? c.Id[..Math.Min(12, c.Id.Length)]).TrimStart('/'),
            c.Image ?? "",
            (c.Mounts ?? []).Where(m => !string.IsNullOrEmpty(m.Source) && !string.IsNullOrEmpty(m.Destination)).Select(m => new Mount(m.Type, m.Source!, m.Destination!)).ToList(),
            (c.Ports ?? []).Where(p => p.PublicPort > 0).Select(p => p.PublicPort).Distinct().ToList(),
            (c.Ports ?? []).Select(p => p.PrivatePort).Distinct().ToList(),
            ((IEnumerable<NetworkDto>?)c.NetworkSettings?.Networks?.Values ?? []).Select(n => n.IPAddress).Where(ip => !string.IsNullOrEmpty(ip)).Select(ip => ip!).ToList(),
            c.Labels?.GetValueOrDefault("com.docker.compose.service"),
            c.State)).ToList();
    }

    private static HttpClient Create(string endpoint)
    {
        if (endpoint.StartsWith("unix://", StringComparison.OrdinalIgnoreCase) || endpoint.StartsWith('/'))
        {
            var path = endpoint.StartsWith('/') ? endpoint : endpoint["unix://".Length..];
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, ct) =>
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
            return new HttpClient(handler) { BaseAddress = new Uri("http://docker"), Timeout = TimeSpan.FromSeconds(15) };
        }

        var url = endpoint.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ? "http://" + endpoint["tcp://".Length..] : endpoint;
        return new HttpClient { BaseAddress = new Uri(url.TrimEnd('/')), Timeout = TimeSpan.FromSeconds(15) };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private sealed record VersionDto(string? Version);

    private sealed record ContainerDto(string Id, List<string>? Names, string? Image, string? State, List<MountDto>? Mounts, List<PortDto>? Ports,
        NetworkSettingsDto? NetworkSettings, Dictionary<string, string>? Labels);

    private sealed record MountDto(string? Type, string? Source, string? Destination);

    private sealed record PortDto(int PrivatePort, int PublicPort);

    private sealed record NetworkSettingsDto(Dictionary<string, NetworkDto>? Networks);

    private sealed record NetworkDto(string? IPAddress);
}
