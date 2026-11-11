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

    /// <summary>
    /// The parent reads one file and returns its <see cref="FileMap"/>; null when the file is not there. The answer streams progress
    /// lines, so the client's timeout only covers the wait for the first byte.
    /// </summary>
    public async Task<FileProbe?> ProbeAsync(int rootId, string relPath, int block, long compare, Action<long>? onRead, Action? onWaiting, CancellationToken ct)
    {
        using var req = Request($"/backup/probe?rootId={rootId}&path={Uri.EscapeDataString(relPath)}&block={block}&compare={compare}");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            return body.Contains("missing", StringComparison.Ordinal)
                ? null
                : throw new InvalidOperationException("The parent is too old to compare file contents: update its ANalyze.");
        }

        Check(res, token);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var l = JsonSerializer.Deserialize<ProbeLine>(line, Json);
            if (l?.Result is { } r)
            {
                return r;
            }

            if (l?.Error is { } e)
            {
                throw new InvalidOperationException("The parent could not read its copy: " + e);
            }

            if (l?.Read is { } n)
            {
                onRead?.Invoke(n);
            }
            else if (l?.Waiting == true)
            {
                onWaiting?.Invoke();
            }
        }

        throw new InvalidOperationException("The parent stopped answering before it finished reading its copy.");
    }

    /// <summary>
    /// The parent's copy of a file, streamed (the caller disposes the response); <paramref name="range"/> is passed on as the Range header.
    /// Null when the file is not there.
    /// </summary>
    public async Task<HttpResponseMessage?> ContentAsync(int rootId, string relPath, string? range, CancellationToken ct)
    {
        using var req = Request($"/backup/content?rootId={rootId}&path={Uri.EscapeDataString(relPath)}");
        if (!string.IsNullOrEmpty(range))
        {
            req.Headers.TryAddWithoutValidation("Range", range);
        }

        var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            return body.Contains("missing", StringComparison.Ordinal)
                ? null
                : throw new InvalidOperationException("The parent is too old to show its copies: update its ANalyze.");
        }

        if (res.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            try
            {
                Check(res, token);
            }
            catch
            {
                res.Dispose();
                throw;
            }
        }

        return res;
    }

    private HttpRequestMessage Request(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, _base + path);
        if (token is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return req;
    }

    private static void Check(HttpResponseMessage res, string? token)
    {
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException(token is null
                ? "The parent has a password: set ANalyze__ApiToken on the parent and enter that token here."
                : "The parent rejected the token: it must equal the parent's ANalyze__ApiToken.");
        }

        res.EnsureSuccessStatusCode();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var req = Request(path);
        using var res = await http.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound && path.StartsWith("/backup/info", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("That server is not an ANalyze with backup support (update the parent to 0.1.4 or later).");
        }

        Check(res, token);
        return await res.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new InvalidOperationException("Empty answer from the parent.");
    }
}
