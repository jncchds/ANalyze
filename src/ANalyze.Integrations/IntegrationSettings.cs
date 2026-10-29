using System.Text.Json;
using System.Text.Json.Nodes;

namespace ANalyze.Integrations;

/// <summary>Settings of one integration. Secret values are plain text here; they are encrypted only at rest.</summary>
public sealed class IntegrationSettings
{
    public bool Enabled { get; set; }

    /// <summary>Re-sync after every completed scan.</summary>
    public bool SyncAfterScan { get; set; } = true;

    public JsonObject Values { get; set; } = [];

    public string? GetString(string key) =>
        Values[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    public bool GetBool(string key, bool fallback = false) =>
        Values[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public int GetInt(string key, int fallback = 0) =>
        Values[key] is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : fallback;

    public IReadOnlyList<PathMapping> GetMappings(string key) =>
        Values[key] is JsonArray a
            ? a.Select(n => n?.Deserialize<PathMapping>(Json)).Where(m => m is not null && !string.IsNullOrWhiteSpace(m.From)).Select(m => m!).ToList()
            : [];

    /// <summary>Applies the longest matching mapping; without one the path is returned unchanged.</summary>
    public static string MapPath(string externalPath, IReadOnlyList<PathMapping> mappings)
    {
        var p = externalPath.Replace('\\', '/');
        // Longest prefix wins; for the same prefix the later mapping (the one added last) wins.
        var best = mappings
            .Select((m, i) => (From: m.From.Replace('\\', '/').TrimEnd('/'), To: m.To.TrimEnd('/'), Index: i))
            .Where(m => m.From.Length > 0 && (p == m.From || p.StartsWith(m.From + "/", StringComparison.Ordinal)))
            .OrderByDescending(m => m.From.Length)
            .ThenByDescending(m => m.Index)
            .FirstOrDefault();
        return best.From is null ? p : best.To + p[best.From.Length..];
    }

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
