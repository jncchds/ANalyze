using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ANalyze.Actions;

/// <summary>Values a destination template can use.</summary>
public sealed record TemplateFacts(string Name, string RelPath, string? Ext, string? Category, string Kind, long MTime, long Size);

/// <summary>
/// Expands destination templates such as "Sorted/{category}/{mtime:yyyy}/{name}" into a path relative to the root.
/// Tokens: {name} {stem} {ext} {category} {kind} {relDir} {parent} {mtime:format} {size:bucket}.
/// Token values never contain '/' (except {relDir}) and never resolve to "." or "..".
/// </summary>
public static partial class PathTemplate
{
    [GeneratedRegex(@"\{(?<name>[a-zA-Z]+)(?::(?<fmt>[^}]+))?\}")]
    private static partial Regex Token();

    public static readonly string[] Tokens = ["name", "stem", "ext", "category", "kind", "relDir", "parent", "mtime", "size"];

    public static string Expand(string template, TemplateFacts f)
    {
        var dot = f.Name.LastIndexOf('.');
        var stem = dot > 0 ? f.Name[..dot] : f.Name;
        var slash = f.RelPath.LastIndexOf('/');
        var relDir = slash > 0 ? f.RelPath[..slash] : "";
        var parent = relDir.Length == 0 ? "" : relDir[(relDir.LastIndexOf('/') + 1)..];

        var expanded = Token().Replace(template, m =>
        {
            var fmt = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            return m.Groups["name"].Value.ToLowerInvariant() switch
            {
                "name" => Clean(f.Name),
                "stem" => Clean(stem),
                "ext" => Clean(string.IsNullOrEmpty(f.Ext) ? "no-extension" : f.Ext),
                "category" => Clean(f.Category ?? "Uncategorized"),
                "kind" => Clean(f.Kind),
                "reldir" => string.Join('/', relDir.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Clean)),
                "parent" => Clean(parent.Length == 0 ? "root" : parent),
                "mtime" => Clean(DateTimeOffset.FromUnixTimeSeconds(f.MTime).ToLocalTime().ToString(fmt ?? "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                "size" => SizeBucket(f.Size),
                _ => m.Value,
            };
        });

        return Normalize(expanded);
    }

    /// <summary>Collapses empty segments; rejects '.' and '..' so the result stays inside the root.</summary>
    public static string Normalize(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts)
        {
            if (p is "." or "..")
            {
                throw new ArgumentException("Destination must not contain '.' or '..' segments.");
            }
        }

        if (parts.Length == 0)
        {
            throw new ArgumentException("Destination is empty.");
        }

        return string.Join('/', parts);
    }

    private static string Clean(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            sb.Append(c is '/' or '\\' or '\0' || char.IsControl(c) ? '_' : c);
        }

        var s = sb.ToString().Trim();
        return s is "" or "." or ".." ? "_" : s;
    }

    private static string SizeBucket(long size) => size switch
    {
        < 1L << 20 => "under 1 MB",
        < 100L << 20 => "1-100 MB",
        < 1L << 30 => "100 MB-1 GB",
        < 10L << 30 => "1-10 GB",
        _ => "over 10 GB",
    };

    /// <summary>"name.ext" → "name (2).ext".</summary>
    public static string WithSuffix(string relPath, int n)
    {
        var slash = relPath.LastIndexOf('/');
        var dir = slash >= 0 ? relPath[..(slash + 1)] : "";
        var name = relPath[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{dir}{name[..dot]} ({n}){name[dot..]}" : $"{dir}{name} ({n})";
    }
}
