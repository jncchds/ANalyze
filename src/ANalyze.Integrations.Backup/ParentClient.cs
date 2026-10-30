using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ANalyze.Integrations.Backup;

/// <summary>Reads the backup export of the parent ANalyze (<see cref="BackupExport"/>).</summary>
internal sealed class ParentClient(HttpClient http, string url, string? token)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _base = Normalize(url);

    /// <summary>"http://nas:8080/", "http://nas:8080/api/v1" → "http://nas:8080/api/v1".</summary>
    internal static string Normalize(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            u = u[..^"/api/v1".Length];
        }

        return u + "/api/v1";
    }

    public Task<ExportInfo> InfoAsync(CancellationToken ct) => GetAsync<ExportInfo>("/backup/info", ct);

    public Task<ExportPage> FilesAsync(int rootId, string? after, CancellationToken ct) =>
        GetAsync<ExportPage>($"/backup/files?rootId={rootId}&limit=5000{(after is null ? "" : "&after=" + Uri.EscapeDataString(after))}", ct);

    /// <summary>The hash feed after <paramref name="after"/>; null when the parent is too old to have one.</summary>
    public async Task<HashPage?> HashesAsync(int rootId, string after, CancellationToken ct)
    {
        try
        {
            return await GetAsync<HashPage>($"/backup/hashes?rootId={rootId}&limit=5000&after={Uri.EscapeDataString(after)}", ct);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _base + path);
        if (token is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(token is null
                ? "The parent has a password: set ANalyze__ApiToken on the parent and enter that token here."
                : "The parent rejected the token: it must equal the parent's ANalyze__ApiToken.");
        }

        if (res.StatusCode == HttpStatusCode.NotFound && path.StartsWith("/backup/info", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("That server is not an ANalyze with backup support (update the parent to 0.1.4 or later).");
        }

        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidOperationException("Empty answer from the parent.");
    }
}
