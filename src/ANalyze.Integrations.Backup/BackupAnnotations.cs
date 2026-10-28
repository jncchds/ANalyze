using Microsoft.Data.Sqlite;

namespace ANalyze.Integrations.Backup;

/// <summary>
/// Browse labels from <c>BackupFiles</c>: every local folder with backed-up content gets a summary ("Backed up · verified",
/// "Backup: 3 missing · 1 corrupt"), the backup target folders say whose backup they are, and files that need attention
/// (corrupt, outdated, only here) get their own label. Files that are fine are not labelled one by one (there can be millions);
/// their folder says so.
/// </summary>
internal static class BackupAnnotations
{
    public const string Kind = "backup";
    public const string RootKind = "backup-root";
    private const int MaxFileLabels = 50_000;

    private sealed class Counts
    {
        public readonly long[] Files = new long[BackupStates.All.Length];
        public readonly long[] Bytes = new long[BackupStates.All.Length];

        public long this[string state] => Files[Array.IndexOf(BackupStates.All, state)];

        public long Total => Files.Sum() - this[BackupStates.Extra];
    }

    public static int Write(SqliteConnection conn, string source, string parentName, IReadOnlyList<BackupTarget> targets)
    {
        var notes = new Dictionary<(long, string), Annotations.Item>();
        var stops = targets.Where(t => t.RootId is not null).Select(t => (t.RootId!.Value, t.RelPath!)).ToHashSet();
        var dirs = new Dictionary<(int, string), Counts>();
        var fileLabels = 0;

        using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT LocalRootId, LocalRelPath, State, Size, EntryId, Side, MTime, LocalMTime, SourcePath FROM BackupFiles WHERE LocalRootId IS NOT NULL";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var root = r.GetInt32(0);
                var rel = r.GetString(1);
                var state = r.GetString(2);
                var i = Array.IndexOf(BackupStates.All, state);
                var size = r.GetInt64(3);
                var p = rel;
                do
                {
                    p = Parent(p);
                    if (!dirs.TryGetValue((root, p), out var c))
                    {
                        dirs[(root, p)] = c = new Counts();
                    }

                    c.Files[i]++;
                    c.Bytes[i] += size;
                }
                while (p.Length > 0 && !stops.Contains((root, p)));

                if (!r.IsDBNull(4) && fileLabels < MaxFileLabels && FileLabel(state, r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetInt64(7)) is { } label)
                {
                    var id = r.GetInt64(4);
                    var detail = state == BackupStates.Extra ? $"not on {parentName}" : $"{parentName}: {(r.IsDBNull(8) ? "" : r.GetString(8))} · {Format.Bytes(size)} · {Format.Date(r.GetInt64(6))}";
                    notes[(id, Kind)] = new Annotations.Item(id, Kind, label, detail, state);
                    fileLabels++;
                }
            }
        }

        using var find = conn.CreateCommand();
        find.CommandText = "SELECT Id FROM Entries WHERE RootId = $r AND RelPath = $p AND IsDir = 1";
        find.Parameters.Add(new SqliteParameter("$r", null));
        find.Parameters.Add(new SqliteParameter("$p", null));
        long? Dir(int root, string rel)
        {
            find.Parameters["$r"].Value = root;
            find.Parameters["$p"].Value = rel;
            return find.ExecuteScalar() as long?;
        }

        foreach (var ((root, rel), c) in dirs)
        {
            if (Dir(root, rel) is { } id)
            {
                var (label, state) = FolderLabel(c);
                notes[(id, Kind)] = new Annotations.Item(id, Kind, label, Detail(c, parentName), state);
            }
        }

        foreach (var t in targets.Where(t => t.EntryId is not null))
        {
            var id = t.EntryId!.Value;
            var detail = dirs.TryGetValue((t.RootId!.Value, t.RelPath!), out var c) ? Detail(c, parentName) : null;
            notes[(id, RootKind)] = new Annotations.Item(id, RootKind, $"Backup of {parentName}:{t.ParentPath}", detail);
        }

        Annotations.Replace(conn, source, notes.Values);
        return notes.Count;
    }

    private static string? FileLabel(string state, string? side, long mtime, long? localMTime) => state switch
    {
        BackupStates.Corrupt => side switch
        {
            BackupStates.SideSource => "Corrupt on the parent — this copy is the good one",
            BackupStates.SideBackup => "Corrupt backup copy",
            _ => "Content differs from the parent (same size and date)",
        },
        BackupStates.Outdated => localMTime < mtime ? "Older than the parent's copy" : localMTime > mtime ? "Newer than the parent's copy" : "Differs from the parent's copy",
        BackupStates.Extra => "Only in the backup",
        _ => null,
    };

    private static (string Label, string State) FolderLabel(Counts c)
    {
        var parts = new List<string>();
        void Part(string state, string word)
        {
            if (c[state] > 0)
            {
                parts.Add($"{c[state]:N0} {word}");
            }
        }

        Part(BackupStates.Missing, "missing");
        Part(BackupStates.Corrupt, "corrupt");
        Part(BackupStates.Outdated, "outdated");
        Part(BackupStates.Extra, "only here");
        if (parts.Count == 0)
        {
            return (c[BackupStates.Verified] == c.Total ? "Backed up · verified" : "Backed up", "ok");
        }

        var state = c[BackupStates.Corrupt] > 0 ? BackupStates.Corrupt
            : c[BackupStates.Missing] + c[BackupStates.Outdated] > 0 ? BackupStates.Missing
            : BackupStates.Extra;
        return ("Backup: " + string.Join(" · ", parts), state);
    }

    private static string Detail(Counts c, string parentName)
    {
        var ok = c[BackupStates.Verified] + c[BackupStates.Matched];
        return $"{ok:N0} of {c.Total:N0} files of {parentName} backed up ({c[BackupStates.Verified]:N0} verified by content)";
    }

    private static string Parent(string rel) => rel.LastIndexOf('/') is var i and > 0 ? rel[..i] : "";
}

internal static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long n)
    {
        double v = n;
        var u = 0;
        while (v >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{n} B" : $"{v.ToString(v < 100 ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture)} {Units[u]}";
    }

    public static string Date(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
