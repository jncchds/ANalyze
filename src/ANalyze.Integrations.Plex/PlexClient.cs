using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ANalyze.Integrations.Plex;

/// <summary>Minimal Plex Media Server API client (JSON responses).</summary>
public sealed class PlexClient(HttpClient http, string baseUrl, string token)
{
    private const int PageSize = 500;

    public sealed record Section(string Key, string Title, string Type, IReadOnlyList<string> Locations);

    public sealed record MediaFile(
        string RatingKey, string SectionKey, string SectionTitle, string Type, string Title, string? ShowTitle, string? ParentTitle,
        int? Season, int? Episode, int? Year, int ViewCount, long? LastViewedAt, long? AddedAt, string File, long Size, string? Resolution, int Versions);

    public async Task<(string Name, string? MachineId, string? Version)> ServerAsync(CancellationToken ct)
    {
        var root = await GetAsync<Container<object>>("/", ct);
        return (root?.MediaContainer?.FriendlyName ?? "Plex", root?.MediaContainer?.MachineIdentifier, root?.MediaContainer?.Version);
    }

    public async Task<IReadOnlyList<Section>> SectionsAsync(CancellationToken ct)
    {
        var res = await GetAsync<Container<DirectoryDto>>("/library/sections", ct);
        return (res?.MediaContainer?.Directory ?? [])
            .Select(d => new Section(d.Key ?? "", d.Title ?? "", d.Type ?? "", d.Location?.Select(l => l.Path ?? "").Where(p => p.Length > 0).ToList() ?? []))
            .ToList();
    }

    /// <summary>Every media part in a section: movies, episodes of shows, tracks of artists.</summary>
    public async IAsyncEnumerable<MediaFile> FilesAsync(Section section, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var type = section.Type switch
        {
            "movie" => 1,
            "show" => 4,
            "artist" => 10,
            _ => 0,
        };
        if (type == 0)
        {
            yield break;
        }

        for (var start = 0; ; start += PageSize)
        {
            var page = await GetAsync<Container<MetadataDto>>(
                $"/library/sections/{Uri.EscapeDataString(section.Key)}/all?type={type}&X-Plex-Container-Start={start}&X-Plex-Container-Size={PageSize}", ct);
            var items = page?.MediaContainer?.Metadata ?? [];
            foreach (var m in items)
            {
                var media = m.Media ?? [];
                foreach (var v in media)
                {
                    foreach (var p in v.Part ?? [])
                    {
                        if (string.IsNullOrEmpty(p.File))
                        {
                            continue;
                        }

                        yield return new MediaFile(
                            m.RatingKey ?? "", section.Key, section.Title, m.Type ?? "", m.Title ?? "",
                            m.GrandparentTitle, m.ParentTitle, m.ParentIndex, m.Index, m.Year,
                            m.ViewCount ?? 0, m.LastViewedAt, m.AddedAt, p.File, p.Size ?? 0, v.VideoResolution, media.Count);
                    }
                }
            }

            var total = page?.MediaContainer?.TotalSize ?? page?.MediaContainer?.Size ?? 0;
            if (items.Count < PageSize || start + items.Count >= total)
            {
                break;
            }
        }
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + path);
        req.Headers.Add("Accept", "application/json");
        req.Headers.Add("X-Plex-Token", token);
        req.Headers.Add("X-Plex-Client-Identifier", "analyze");
        req.Headers.Add("X-Plex-Product", "ANalyze");
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new HttpRequestException("Plex rejected the token (401).");
        }

        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record Container<T>(MediaContainerDto<T>? MediaContainer);

    private sealed record MediaContainerDto<T>(int? Size, int? TotalSize, string? FriendlyName, string? MachineIdentifier, string? Version, List<T>? Directory, List<T>? Metadata);

    private sealed record DirectoryDto(string? Key, string? Title, string? Type, List<LocationDto>? Location);

    private sealed record LocationDto(string? Path);

    private sealed record MetadataDto(
        string? RatingKey, string? Type, string? Title, string? GrandparentTitle, string? ParentTitle, int? ParentIndex, int? Index, int? Year,
        int? ViewCount, long? LastViewedAt, long? AddedAt, List<MediaDto>? Media);

    private sealed record MediaDto(string? VideoResolution, List<PartDto>? Part);

    private sealed record PartDto(string? File, long? Size);
}
