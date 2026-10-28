using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Infrastructure.Data;
using ANalyze.Scanner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Tests;

public class FolderSizeTests
{
    /// <summary>Folders whose stored total differs from the sum of their children.</summary>
    private static List<string> Inconsistent(SqliteConnection c)
    {
        using var q = c.CreateCommand();
        q.CommandText = """
            SELECT d.RelPath, d.TotalSize, COALESCE((SELECT SUM(x.TotalSize) FROM Entries x WHERE x.ParentId = d.Id), 0) AS kids
            FROM Entries d WHERE d.IsDir = 1 AND d.TotalSize <> COALESCE((SELECT SUM(x.TotalSize) FROM Entries x WHERE x.ParentId = d.Id), 0)
            """;
        using var r = q.ExecuteReader();
        var list = new List<string>();
        while (r.Read())
        {
            list.Add($"{r.GetString(0)}: {r.GetInt64(1)} vs {r.GetInt64(2)}");
        }

        return list;
    }

    [Fact]
    public async Task Large_scan_with_intermediate_commits_ends_consistent()
    {
        await using var app = new TestApp();
        // More than the walker's commit batch, so running totals are written while folders are still open.
        for (var i = 0; i < 6000; i++)
        {
            var dir = Path.Combine(app.Library, "Bulk", $"d{i / 500}", $"e{i / 50}");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"f{i}.bin"), new byte[(i % 7) + 1]);
        }

        using var http = app.CreateClient();
        await app.ScanAsync(http);
        // Rescan after a change deep inside: totals above it follow.
        File.WriteAllBytes(Path.Combine(app.Library, "Bulk", "d3", "e33", "extra.bin"), new byte[1000]);
        await app.ScanAsync(http);

        using var c = app.Services.GetRequiredService<SqliteSetup>().Open();
        Assert.Empty(Inconsistent(c));
        using var q = c.CreateCommand();
        q.CommandText = "SELECT TotalSize FROM Entries WHERE RelPath = 'Bulk'";
        var expected = Enumerable.Range(0, 6000).Sum(i => (i % 7) + 1) + 1000;
        Assert.Equal((long)expected, (long)q.ExecuteScalar()!);
    }

    [Fact]
    public async Task Rollup_repairs_folders_left_at_zero()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);

        using var c = app.Services.GetRequiredService<SqliteSetup>().Open();
        int rootId;
        using (var broke = c.CreateCommand())
        {
            // What an interrupted first scan leaves behind: finished deep folders, parents still at 0.
            broke.CommandText = "UPDATE Entries SET TotalSize = 0, FileCount = 0 WHERE IsDir = 1 AND Depth <= 1; SELECT MIN(RootId) FROM Entries";
            rootId = Convert.ToInt32(broke.ExecuteScalar());
        }

        Assert.NotEmpty(Inconsistent(c));
        ScanService.RollupDirectories(c, rootId);
        Assert.Empty(Inconsistent(c));
    }

    [Fact]
    public async Task Files_not_yet_detected_get_their_type_from_the_extension()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);

        using var c = app.Services.GetRequiredService<SqliteSetup>().Open();
        long id;
        using (var reset = c.CreateCommand())
        {
            // A file the detection step has not reached yet (first scan still walking, or stopped early).
            reset.CommandText = "UPDATE Entries SET Kind = 0, Mime = NULL, Detected = 0 WHERE Name = 'Heat.mkv'; SELECT Id FROM Entries WHERE Name = 'Heat.mkv'";
            id = (long)reset.ExecuteScalar()!;
        }

        var entry = (await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{id}")).GetProperty("entry");
        Assert.Equal("video", entry.GetProperty("kind").GetString());
    }
}
