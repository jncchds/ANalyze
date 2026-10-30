using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Routing;

namespace ANalyze.Integrations;

/// <summary>
/// A plugin that cross-references the scanned files with another system (Plex, Jellyfin, *arr, Immich…).
/// Register with <c>services.AddIntegration&lt;T&gt;()</c>. The host provides settings storage (with encrypted
/// secrets), sync scheduling, job progress, annotations shown in the file browser and a route group for the
/// plugin's own API at /api/v1/integrations/{Id}. The UI renders the settings form from <see cref="Fields"/>
/// and can add a dedicated page registered under the same id.
/// </summary>
public interface IIntegration
{
    /// <summary>Stable id used in URLs, settings keys and annotation sources (lowercase, no spaces).</summary>
    string Id { get; }

    string Name { get; }

    string Description { get; }

    /// <summary>lucide icon name for the UI.</summary>
    string Icon { get; }

    /// <summary>Settings form definition. Values are stored as JSON under these keys.</summary>
    IReadOnlyList<IntegrationField> Fields { get; }

    /// <summary>Checks the connection with the given (possibly unsaved) settings.</summary>
    Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct);

    /// <summary>Pulls data from the external system and refreshes annotations. Report progress through <paramref name="progress"/>.</summary>
    Task<IntegrationSyncResult> SyncAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct);

    /// <summary>Plugin-specific endpoints, mapped under /api/v1/integrations/{Id} (already authorized).</summary>
    void MapEndpoints(RouteGroupBuilder group);

    /// <summary>Words that identify this app's Docker container by image or name (used for automatic path mappings).</summary>
    IReadOnlyList<string> DockerHints => [Id];

    /// <summary>Settings key holding the app's URL, used to find its container. Null when the app has no URL.</summary>
    string? UrlField => Fields.Any(f => f.Key == "url") ? "url" : null;

    /// <summary>
    /// Also sync (with "sync after scan") once the background processing after a scan has finished, not only right after the walk:
    /// for integrations that use content hashes.
    /// </summary>
    bool SyncAfterProcessing => false;
}

/// <summary>Supplies path mappings found automatically (e.g. from Docker mounts) for an integration.</summary>
public interface IPathMappingSource
{
    Task<AutoMappings?> ForAsync(IIntegration integration, IntegrationSettings settings, CancellationToken ct);
}

/// <param name="Source">Who found them, e.g. "docker".</param>
/// <param name="Container">The other app's container, when known.</param>
public sealed record AutoMappings(string Source, string? Container, IReadOnlyList<PathMapping> Mappings, string? Note = null);

/// <param name="Type">text, url, password (secret), bool, number, mappings (list of From → To path pairs), select.</param>
public sealed record IntegrationField(
    string Key,
    string Label,
    string Type,
    string? Help = null,
    JsonNode? Default = null,
    IReadOnlyList<string>? Options = null,
    string? Placeholder = null)
{
    public bool Secret => Type == "password";
}

public sealed record IntegrationTestResult(bool Ok, string Message, JsonNode? Details = null);

public sealed record IntegrationSyncResult(bool Ok, string Message, long Items = 0, long Matched = 0);

public sealed record IntegrationProgress(string? Phase, long Done, long Total, string? Current = null);

/// <summary>Path prefix translation between how another system sees files and how ANalyze sees them.</summary>
public sealed record PathMapping(string From, string To);
