using System.Text.RegularExpressions;

namespace ANalyze.Web.Infrastructure;

/// <summary>RELEASE_NOTES.md (embedded at build time) parsed into versions and bullets for the UI.</summary>
public static partial class ReleaseNotes
{
    public sealed record Item(string Kind, string Text);

    public sealed record Release(string Version, string? Date, IReadOnlyList<Item> Items);

    private static readonly Lazy<IReadOnlyList<Release>> Embedded = new(() =>
    {
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("RELEASE_NOTES.md");
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static IReadOnlyList<Release> All => Embedded.Value;

    /// <summary>Reads "## v1.2.3 — 2026-10-28" headings and "- feat: text" bullets; continuation lines join the previous bullet.</summary>
    public static IReadOnlyList<Release> Parse(string markdown)
    {
        var releases = new List<Release>();
        string? version = null, date = null;
        var items = new List<Item>();

        void Flush()
        {
            if (version is not null)
            {
                releases.Add(new Release(version, date, items));
            }
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (Heading().Match(line) is { Success: true } h)
            {
                Flush();
                version = h.Groups["v"].Value;
                date = h.Groups["d"].Success ? h.Groups["d"].Value : null;
                items = [];
            }
            else if (version is not null && Bullet().Match(line) is { Success: true } b)
            {
                var kind = b.Groups["k"].Success ? b.Groups["k"].Value.ToLowerInvariant() : "other";
                items.Add(new Item(kind, b.Groups["t"].Value.Trim()));
            }
            else if (version is not null && items.Count > 0 && line.StartsWith("  ", StringComparison.Ordinal) && line.Trim().Length > 0)
            {
                items[^1] = items[^1] with { Text = items[^1].Text + " " + line.Trim() };
            }
        }

        Flush();
        return releases;
    }

    [GeneratedRegex(@"^##\s+v?(?<v>[0-9][^\s]*)(?:\s+[—–-]\s+(?<d>\d{4}-\d{2}-\d{2}))?")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^[-*]\s+(?:(?<k>[A-Za-z]+):\s+)?(?<t>.+)$")]
    private static partial Regex Bullet();
}
