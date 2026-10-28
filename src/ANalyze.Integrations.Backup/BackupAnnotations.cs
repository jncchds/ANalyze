using Microsoft.Data.Sqlite;

namespace ANalyze.Integrations.Backup;

/// <summary>
/// Browse labels from <c>BackupFiles</c>: every folder says whether it is synchronized (with the share of its direct content that is),
/// the backup target folders say where they are backed up, and corrupt or missing files get their own label. Synchronized and
/// not-synchronized files are not labelled one by one (there can be millions); their folder says so.
/// </summary>
internal static class BackupAnnotations
{
    public const string Kind = "backup";
    public const string RootKind = "backup-root";
    private const int MaxFileLabels = 50_000;

    public static int Write(SqliteConnection conn, string source, string parentName, IReadOnlyList<BackupTarget> targets)
    {
        var notes = new List<Annotations.Item>();
        var files = 0;
        using (var q = conn.CreateCommand())
        {
            q.CommandText = $"""
                SELECT EntryId, IsDir, State, Children, SyncedChildren, Size, ParentSize FROM BackupFiles
                WHERE IsDir = 1 OR State IN ('{BackupStates.Corrupt}', '{BackupStates.Missing}')
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var state = r.GetString(2);
                if (r.GetBoolean(1))
                {
                    var (children, synced) = (r.GetInt32(3), r.GetInt32(4));
                    var share = children == 0 ? "empty" : $"{synced:N0} of {children:N0} items";
                    notes.Add(state == BackupStates.Synced
                        ? new Annotations.Item(id, Kind, "Synced", $"on {parentName} · {share}", "ok")
                        : new Annotations.Item(id, Kind, children == 0 ? "Not synced" : $"Not synced · {Percent(synced, children)}", $"on {parentName} · {share}", BackupStates.Unsynced));
                }
                else if (files++ < MaxFileLabels)
                {
                    notes.Add(state == BackupStates.Corrupt
                        ? new Annotations.Item(id, Kind, "Corrupted: differs from the backup", r.IsDBNull(6) ? null : $"{parentName} has {Format.Bytes(r.GetInt64(6))}, here {Format.Bytes(r.GetInt64(5))}", state)
                        : new Annotations.Item(id, Kind, "Missing from the backup", $"not on {parentName}", state));
                }
            }
        }

        foreach (var t in targets.Where(t => t.EntryId is not null))
        {
            notes.Add(new Annotations.Item(t.EntryId!.Value, RootKind, $"Backed up to {parentName}:{t.ParentPath}"));
        }

        Annotations.Replace(conn, source, notes);
        return notes.Count;
    }

    private static string Percent(int part, int whole) => whole == 0 ? "0%" : $"{Math.Floor(part * 100.0 / whole):0}%";
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
}
