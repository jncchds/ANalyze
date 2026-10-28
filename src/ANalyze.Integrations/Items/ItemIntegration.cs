using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ANalyze.Integrations.Items;

/// <summary>A file or folder another app knows about, as that app reports it.</summary>
public sealed record ExternalItem(string Kind, string ExternalId, string Title, string Path, long Size)
{
    public string? GroupKey { get; init; }
    public string? GroupTitle { get; init; }
    public string? SubTitle { get; init; }
    public int? Year { get; init; }
    /// <summary>True: a folder, false: a file, null: unknown (tries a file, then a folder).</summary>
    public bool? IsDir { get; init; } = false;
    public string? State { get; init; }
    public string? Detail { get; init; }
    public long? AddedAt { get; init; }
    public long? LastUsedAt { get; init; }
    public int PlayCount { get; init; }
    public string? Url { get; init; }
    /// <summary>Label in the browser; defaults to <see cref="Title"/>.</summary>
    public string? Label { get; init; }
    /// <summary>Badge in the browser (watched, seeding…). Seeding/downloading also make cleanup plans skip the item.</summary>
    public string? Badge { get; init; }
    /// <summary>Labels for the parent and grandparent folders (season/show, album/artist), when they are not library folders.</summary>
    public IReadOnlyList<(string Kind, string Label)>? Parents { get; init; }
}

/// <summary>A folder another app knows about. Root folders (libraries, download folders) are searched for untracked files.</summary>
public sealed record ExternalFolder(string Path, string Kind, string Label, string? Detail = null, string? State = null, string? Url = null, bool IsRoot = false);

public sealed record Snapshot(IReadOnlyList<ExternalItem> Items, IReadOnlyList<ExternalFolder> Folders, string? Summary = null);

/// <summary>What the generic views look for.</summary>
/// <param name="UntrackedKinds">File kinds that count as "untracked" inside root folders; null = every file.</param>
public sealed record ItemViews(IReadOnlyList<FileKind>? UntrackedKinds = null);

public sealed class ItemServices(IHttpClientFactory http, SqliteSetup sqlite, RootRegistry roots, IntegrationPaths paths, ILoggerFactory logs)
{
    public IHttpClientFactory Http => http;
    public SqliteSetup Sqlite => sqlite;
    public RootRegistry Roots => roots;
    public IntegrationPaths Paths => paths;
    public ILoggerFactory Logs => logs;
}

/// <summary>
/// Base for integrations that list files from another app: subclasses fetch a <see cref="Snapshot"/>; this class matches it to the
/// scanned files, stores <see cref="IntegrationItem"/> rows and annotations, and maps the shared endpoints (summary, titles by size,
/// untracked files, copies, unmatched, mapping suggestions).
/// </summary>
public abstract class ItemIntegration(ItemServices services) : IIntegration
{
    public const string RootKind = "root";

    /// <summary>Badges that mean "the app is using this file right now"; cleanup plans leave such entries out.</summary>
    public static readonly string[] InUseStates = ["seeding", "downloading"];

    protected ItemServices Services { get; } = services;

    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string Icon { get; }

    public abstract IReadOnlyList<IntegrationField> Fields { get; }

    public virtual IReadOnlyList<string> DockerHints => [Id];

    public virtual ItemViews Views => new();

    public abstract Task<IntegrationTestResult> TestAsync(IntegrationSettings settings, CancellationToken ct);

    protected abstract Task<Snapshot> FetchAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct);

    protected HttpClient Http(IntegrationSettings settings) => IntegrationHttp.Create(Services.Http, settings);

    /// <summary>Runs <paramref name="action"/> and turns the usual connection errors into a failed test result.</summary>
    protected static async Task<IntegrationTestResult> Try(Func<Task<IntegrationTestResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Text.Json.JsonException or UriFormatException or NotSupportedException)
        {
            return new IntegrationTestResult(false, e.Message);
        }
    }

    public async Task<IntegrationSyncResult> SyncAsync(IntegrationSettings settings, IProgress<IntegrationProgress> progress, CancellationToken ct)
    {
        var snap = await FetchAsync(settings, progress, ct);
        var mappings = await Services.Paths.ForAsync(this, settings, ct);
        progress.Report(new IntegrationProgress("matching", 0, snap.Items.Count));
        using var conn = Services.Sqlite.Open();
        var matcher = new PathMatcher(conn, Services.Roots, mappings);
        matcher.Detect(snap.Items.Select(i => (i.Path, i.Size, i.IsDir ?? false)));
        var matched = Store(conn, snap, matcher, progress);
        var detected = matcher.Detected.Count > 0 ? $"; detected mappings: {string.Join(", ", matcher.Detected.Select(m => $"{m.From} → {m.To}"))}" : "";
        Services.Logs.CreateLogger(GetType()).LogInformation("{Integration} sync: {Items} items, {Matched} matched{Detected}", Name, snap.Items.Count, matched, detected);
        return new IntegrationSyncResult(true, $"{snap.Items.Count} items, {matched} found on disk{(snap.Summary is null ? "" : " · " + snap.Summary)}{detected}", snap.Items.Count, matched);
    }

    private int Store(SqliteConnection conn, Snapshot snap, PathMatcher matcher, IProgress<IntegrationProgress> progress)
    {
        var notes = new Dictionary<(long, string), Annotations.Item>();
        void Note(long entryId, string kind, string label, string? detail = null, string? state = null, string? url = null) =>
            notes.TryAdd((entryId, kind), new Annotations.Item(entryId, kind, label, detail, state, url));

        var folderIds = new HashSet<long>();
        foreach (var f in snap.Folders.OrderByDescending(f => f.IsRoot))
        {
            if (matcher.FindDir(f.Path) is { } dirId)
            {
                if (f.IsRoot)
                {
                    folderIds.Add(dirId);
                }

                Note(dirId, f.IsRoot ? RootKind : f.Kind, f.Label, f.Detail ?? (f.IsRoot ? f.Kind : null), f.State, f.Url);
            }
        }

        var matched = 0;
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM IntegrationItems WHERE Source = $s";
            del.Parameters.AddWithValue("$s", Id);
            del.ExecuteNonQuery();
        }

        using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO IntegrationItems (Source, Kind, ExternalId, Title, GroupKey, GroupTitle, SubTitle, Year, ExternalPath, Size, IsDir, State, Detail,
                AddedAt, LastUsedAt, PlayCount, Url, EntryId, MatchedBy)
            VALUES ($src, $kind, $ext, $title, $gk, $gt, $sub, $year, $path, $size, $dir, $state, $detail, $added, $used, $plays, $url, $entry, $by)
            """;
        foreach (var p in new[] { "$src", "$kind", "$ext", "$title", "$gk", "$gt", "$sub", "$year", "$path", "$size", "$dir", "$state", "$detail", "$added", "$used", "$plays", "$url", "$entry", "$by" })
        {
            ins.Parameters.Add(new SqliteParameter(p, null));
        }

        static object V(object? v) => v ?? DBNull.Value;
        var i = 0;
        foreach (var item in snap.Items)
        {
            var (entryId, by, isDir) = item.IsDir switch
            {
                true => Tuple(matcher.Match(item.Path, item.Size, true), true),
                false => Tuple(matcher.Match(item.Path, item.Size, false), false),
                null => matcher.MatchAny(item.Path, item.Size),
            };
            ins.Parameters["$src"].Value = Id;
            ins.Parameters["$kind"].Value = item.Kind;
            ins.Parameters["$ext"].Value = item.ExternalId;
            ins.Parameters["$title"].Value = item.Title;
            ins.Parameters["$gk"].Value = V(item.GroupKey);
            ins.Parameters["$gt"].Value = V(item.GroupTitle);
            ins.Parameters["$sub"].Value = V(item.SubTitle);
            ins.Parameters["$year"].Value = V(item.Year);
            ins.Parameters["$path"].Value = item.Path;
            ins.Parameters["$size"].Value = item.Size;
            ins.Parameters["$dir"].Value = isDir || item.IsDir == true;
            ins.Parameters["$state"].Value = V(item.State);
            ins.Parameters["$detail"].Value = V(item.Detail);
            ins.Parameters["$added"].Value = V(item.AddedAt);
            ins.Parameters["$used"].Value = V(item.LastUsedAt);
            ins.Parameters["$plays"].Value = item.PlayCount;
            ins.Parameters["$url"].Value = V(item.Url);
            ins.Parameters["$entry"].Value = V(entryId);
            ins.Parameters["$by"].Value = V(by);
            ins.ExecuteNonQuery();

            if (entryId is { } id)
            {
                matched++;
                Note(id, item.Kind, item.Label ?? item.Title, item.Detail, item.Badge, item.Url);
                if (item.Parents is { Count: > 0 } parents)
                {
                    var (parent, grand) = matcher.Parents(id);
                    if (parent is { } p && !folderIds.Contains(p))
                    {
                        Note(p, parents[0].Kind, parents[0].Label);
                        if (parents.Count > 1 && grand is { } g && !folderIds.Contains(g))
                        {
                            Note(g, parents[1].Kind, parents[1].Label);
                        }
                    }
                }
            }

            if (++i % 1000 == 0)
            {
                progress.Report(new IntegrationProgress("matching", i, snap.Items.Count));
            }
        }

        tx.Commit();
        Annotations.Replace(conn, Id, notes.Values);
        return matched;

        static (long?, string?, bool) Tuple((long? Id, string? By) m, bool dir) => (m.Id, m.By, dir);
    }

    /// <summary>Proposes path mappings from the app's items without saving anything.</summary>
    internal async Task<IReadOnlyList<PathMapping>> SuggestAsync(IntegrationSettings settings, CancellationToken ct)
    {
        var snap = await FetchAsync(settings, new Progress<IntegrationProgress>(), ct);
        using var conn = Services.Sqlite.Open();
        var matcher = new PathMatcher(conn, Services.Roots, []);
        var sample = snap.Items.Take(300).Select(i => (i.Path, i.Size, i.IsDir ?? false)).ToList();
        matcher.Detect(sample, sample.Count);
        return matcher.Detected;
    }

    public void MapEndpoints(RouteGroupBuilder group)
    {
        ItemEndpoints.Map(group, this);
        MapExtra(group);
    }

    /// <summary>Plugin-specific endpoints besides the shared ones.</summary>
    protected virtual void MapExtra(RouteGroupBuilder group)
    {
    }

    /// <summary>Unix seconds from an ISO date string, or null.</summary>
    protected static long? Unix(string? iso) =>
        DateTimeOffset.TryParse(iso, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1971
            ? d.ToUnixTimeSeconds()
            : null;

    protected static long? Unix(DateTimeOffset? d) => d is { Year: > 1971 } v ? v.ToUnixTimeSeconds() : null;
}
