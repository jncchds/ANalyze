using System.Text.Json.Nodes;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace ANalyze.Integrations;

/// <summary>Path mappings for an integration: the configured ones first, then those found automatically (Docker).</summary>
public sealed class IntegrationPaths(
    IEnumerable<IPathMappingSource> sources,
    IEnumerable<IHostPathSource> hostSources,
    SettingsStore appSettings,
    RootRegistry roots,
    ILogger<IntegrationPaths> log)
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

    /// <summary>
    /// Configured mappings followed by automatic ones (a configured mapping wins for the same prefix). Targets outside every root
    /// are followed through ANalyze's own mounts (<see cref="PathAliases"/>), and folders ANalyze sees under two paths get a
    /// fallback mapping to the one inside a root.
    /// </summary>
    public async Task<IReadOnlyList<PathMapping>> ForAsync(IIntegration integration, IntegrationSettings settings, CancellationToken ct)
    {
        var auto = (await AutoAsync(integration, settings, ct)).SelectMany(a => a.Mappings).ToList();
        var mounts = await MountsAsync(ct);
        return PathAliases.Resolve(Merge(settings.GetMappings("mappings"), auto), mounts, p => roots.All.Any(r => RootRegistry.IsUnder(r.Path, p)));
    }

    /// <summary>
    /// Configured mappings, then automatic ones outside every configured prefix: a configured "/downloads" covers
    /// "/downloads/movies" too, so Docker never overrides any part of what was set by hand.
    /// </summary>
    public static List<PathMapping> Merge(IReadOnlyList<PathMapping> configured, IEnumerable<PathMapping> auto)
    {
        var prefixes = configured.Select(c => Norm(c.From)).Where(p => p.Length > 0).ToList();
        return [.. configured, .. auto.Where(m => !prefixes.Any(p => p == "/" || Norm(m.From) == p || Norm(m.From).StartsWith(p + "/", StringComparison.Ordinal))).DistinctBy(m => Norm(m.From))];
    }

    private static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');

    /// <summary>Where the folders ANalyze sees are on the host: configured host paths, then those found automatically (Docker).</summary>
    public async Task<IReadOnlyList<HostPath>> MountsAsync(CancellationToken ct)
    {
        var list = appSettings.Current.HostPaths.Where(h => !string.IsNullOrWhiteSpace(h.Path) && !string.IsNullOrWhiteSpace(h.Host)).ToList();
        foreach (var source in hostSources)
        {
            try
            {
                list.AddRange((await source.GetAsync(ct)).Where(a => list.All(c => c.Path.TrimEnd('/') != a.Path.TrimEnd('/'))));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogDebug(e, "Host paths from {Source} failed", source.GetType().Name);
            }
        }

        return list;
    }
}

/// <summary>
/// One host folder can be visible to ANalyze under several paths: "/mnt/main" mounted at "/mnt/main" and its ZFS dataset
/// "/mnt/main/media" mounted again at "/mnt/media". An app's path that maps to "/mnt/main/media/…" while only "/mnt/media" is a
/// root is the same file as "/mnt/media/…"; these helpers find that path.
/// </summary>
public static class PathAliases
{
    /// <summary>
    /// The path inside a root for <paramref name="path"/> (ANalyze's path, or a host path), through the mounts; the path itself
    /// when it already is inside a root, null when no mount leads into one. The most specific mount wins.
    /// </summary>
    public static string? IntoRoot(string path, IReadOnlyList<HostPath> mounts, Func<string, bool> inRoot)
    {
        path = Trim(path);
        if (inRoot(path))
        {
            return path;
        }

        // The host path(s) of this path: through the mount it sits in, or the path itself when it is already a host path.
        var hosts = mounts.Where(m => Under(Trim(m.Path), path)).OrderByDescending(m => Trim(m.Path).Length)
            .Select(m => Rebase(path, Trim(m.Path), Trim(m.Host))).Append(path).Distinct().ToList();
        foreach (var host in hosts)
        {
            foreach (var m in mounts.Where(m => Under(Trim(m.Host), host)).OrderByDescending(m => Trim(m.Host).Length))
            {
                var local = Rebase(host, Trim(m.Host), Trim(m.Path));
                if (local != path && inRoot(local))
                {
                    return local;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="mappings"/> with targets outside every root moved into one where a mount leads there, followed by
    /// fallback mappings for paths that are themselves outside every root but reach one through the mounts (an app that reports
    /// ANalyze's other path or a host path). A fallback never shadows a mapping that covers the same prefix.
    /// </summary>
    public static List<PathMapping> Resolve(IReadOnlyList<PathMapping> mappings, IReadOnlyList<HostPath> mounts, Func<string, bool> inRoot)
    {
        var result = mappings.Select(m => inRoot(Trim(m.To)) ? m : IntoRoot(m.To, mounts, inRoot) is { } to ? m with { To = to } : m).ToList();
        var froms = mounts.Select(m => Trim(m.Path)).Concat(mounts.Select(m => Trim(m.Host))).Distinct();
        foreach (var from in froms)
        {
            if (from == "/" || inRoot(from) || result.Any(r => Under(Trim(r.From), from)) || IntoRoot(from, mounts, inRoot) is not { } to)
            {
                continue;
            }

            result.Add(new PathMapping(from, to));
        }

        return result;
    }

    private static string Trim(string p)
    {
        p = p.Replace('\\', '/');
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    private static bool Under(string parent, string path) =>
        parent == "/" || path == parent || path.StartsWith(parent + "/", StringComparison.Ordinal);

    private static string Rebase(string path, string from, string to)
    {
        var rest = from == "/" ? path : path[from.Length..];
        if (rest.Length == 0)
        {
            return to;
        }

        return to == "/" ? (rest.StartsWith('/') ? rest : "/" + rest) : to + (rest.StartsWith('/') ? rest : "/" + rest);
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
