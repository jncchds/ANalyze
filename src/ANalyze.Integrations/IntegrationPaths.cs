using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace ANalyze.Integrations;

/// <summary>Path mappings for an integration: the configured ones first, then those found automatically (Docker).</summary>
public sealed class IntegrationPaths(IEnumerable<IPathMappingSource> sources, ILogger<IntegrationPaths> log)
{
    public async Task<IReadOnlyList<AutoMappings>> AutoAsync(IIntegration integration, IntegrationSettings settings, CancellationToken ct)
    {
        var list = new List<AutoMappings>();
        foreach (var source in sources)
        {
            try
            {
                if (await source.ForAsync(integration, settings, ct) is { } found)
                {
                    list.Add(found);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Automatic mappings are a convenience; never fail a sync because of them.
                log.LogDebug(e, "Automatic path mappings failed for {Integration}", integration.Id);
            }
        }

        return list;
    }

    /// <summary>Configured mappings followed by automatic ones (a configured mapping wins for the same prefix).</summary>
    public async Task<IReadOnlyList<PathMapping>> ForAsync(IIntegration integration, IntegrationSettings settings, CancellationToken ct)
    {
        var configured = settings.GetMappings("mappings");
        var auto = (await AutoAsync(integration, settings, ct)).SelectMany(a => a.Mappings)
            .Where(m => configured.All(c => c.From.TrimEnd('/') != m.From.TrimEnd('/')));
        return [.. configured, .. auto];
    }
}

/// <summary>Shared HTTP clients for integrations (one tolerant of self-signed certificates).</summary>
public static class IntegrationHttp
{
    public const string Name = "integrations";
    public const string Insecure = "integrations-insecure";

    public static HttpClient Create(IHttpClientFactory factory, IntegrationSettings settings) =>
        factory.CreateClient(settings.GetBool("allowInvalidCertificate") ? Insecure : Name);

    /// <summary>Fields most HTTP integrations share.</summary>
    public static IntegrationField Url(string placeholder, string? help = null) =>
        new("url", "Server URL", "url", help ?? "Address of the server as seen from the ANalyze container.", Placeholder: placeholder);

    public static IntegrationField Mappings(string example) =>
        new("mappings", "Path mappings", "mappings",
            $"Only needed when the app sees the files under other paths ({example}). Leave empty to detect them from Docker mounts " +
            "(Docker integration) or from sample files; \"Suggest\" fills them in.");

    public static IntegrationField AllowInvalidCertificate() =>
        new("allowInvalidCertificate", "Accept self-signed certificates", "bool", "For https URLs with a certificate the container does not trust.", JsonValue.Create(false));

    /// <summary>Required string setting, with a readable error when it is missing.</summary>
    public static string Require(this IntegrationSettings s, string key, string label) =>
        s.GetString(key) ?? throw new InvalidOperationException($"Set the {label}.");
}
