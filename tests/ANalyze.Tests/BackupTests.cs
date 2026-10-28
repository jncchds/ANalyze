using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using ANalyze.Integrations;
using ANalyze.Integrations.Backup;
using ANalyze.Integrations.Items;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Tests;

public class BackupClassifyTests
{
    private static LocalFile L(long size, long mtime, string? hash = null, long? corrupt = null) => new(1, size, mtime, hash, corrupt);

    private static (string, string?) C(ExportFile p, LocalFile? l, PreviousRow? prev = null) => BackupSync.Classify(p, l, 2, true, () => prev);

    [Fact]
    public void States_from_size_date_and_hash()
    {
        Assert.Equal((BackupStates.Missing, null), C(new("a", 10, 100), null));
        Assert.Equal((BackupStates.Verified, null), C(new("a", 10, 100, "h"), L(10, 500, "h")));
        Assert.Equal((BackupStates.Matched, null), C(new("a", 10, 100), L(10, 101, "h")));
        Assert.Equal((BackupStates.Outdated, null), C(new("a", 10, 100), L(11, 100)));
        Assert.Equal((BackupStates.Outdated, null), C(new("a", 10, 200, "h"), L(10, 100, "x")));
        Assert.Equal((BackupStates.Verified, null), C(new("a", 0, 100), L(0, 900)));
        Assert.Equal(BackupStates.Corrupt, C(new("a", 10, 100, "h"), L(10, 100, "x")).Item1);
    }

    [Fact]
    public void Corrupt_side_from_own_verification_or_history()
    {
        // The parent's own verification flagged its copy.
        Assert.Equal(BackupStates.SideSource, C(new("a", 10, 100, "new", 5), L(10, 100, "old")).Item2);
        Assert.Equal(BackupStates.SideBackup, C(new("a", 10, 100, "old"), L(10, 100, "new", 5)).Item2);

        // Both agreed on "old" last time: whoever no longer has it changed.
        var agreed = new PreviousRow(BackupStates.Verified, "old", "old", null);
        Assert.Equal(BackupStates.SideBackup, C(new("a", 10, 100, "old"), L(10, 100, "new"), agreed).Item2);
        Assert.Equal(BackupStates.SideSource, C(new("a", 10, 100, "new"), L(10, 100, "old"), agreed).Item2);

        // Still the same pair as last time: the side found then is kept.
        Assert.Equal(BackupStates.SideSource, C(new("a", 10, 100, "new"), L(10, 100, "old"), new PreviousRow(BackupStates.Corrupt, "new", "old", BackupStates.SideSource)).Item2);
        Assert.Null(C(new("a", 10, 100, "new"), L(10, 100, "old")).Item2);
    }

    [Fact]
    public void Parent_url_is_normalized()
    {
        Assert.Equal("http://nas:8080/api/v1", ParentClient.Normalize("http://nas:8080/"));
        Assert.Equal("http://nas:8080/api/v1", ParentClient.Normalize("http://nas:8080/api/v1/"));
    }
}

public class BackupSyncTests
{
    private static void Write(string root, string rel, string content, DateTime mtime)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        File.SetLastWriteTimeUtc(p, mtime);
    }

    [Fact]
    public async Task Export_compare_annotate_and_verify()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        var settings = app.Services.GetRequiredService<SettingsStore>();
        await settings.SaveAsync(settings.Current with { HashAllFiles = true });

        // "src" plays the parent's files, "bak" their backup here.
        var t = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Write(app.Library, "src/docs/a.txt", "same content", t);
        Write(app.Library, "bak/docs/a.txt", "same content", t);
        Write(app.Library, "src/docs/b.txt", "good bytes!", t);
        Write(app.Library, "bak/docs/b.txt", "BAD bytes!!", t);
        Write(app.Library, "src/c.txt", "version two, longer", t.AddDays(2));
        Write(app.Library, "bak/c.txt", "version one", t);
        Write(app.Library, "src/new/d.txt", "not copied yet", t);
        Write(app.Library, "bak/e.txt", "deleted on the parent", t);
        await app.ScanAsync(http);

        // Every file has a hash now, but only real copies count as duplicates (IMG_0002 only has a hard link).
        var dupSearch = await http.GetFromJsonAsync<JsonElement>("/api/v1/search?duplicates=true&q=IMG_0002");
        Assert.Equal(0, dupSearch.GetProperty("total").GetInt64());
        var dups = await http.GetFromJsonAsync<JsonElement>("/api/v1/duplicates");
        Assert.Equal(2, dups.GetProperty("total").GetInt64());

        var info = await http.GetFromJsonAsync<ExportInfo>("/api/v1/backup/info", TestApp.Json);
        Assert.NotNull(info);
        Assert.True(info.HashAllFiles);
        var root = Assert.Single(info.Roots);

        // Small pages to exercise paging; the parent's view is the "src" folder under another path.
        var all = new List<ExportFile>();
        string? after = null;
        do
        {
            var page = await http.GetFromJsonAsync<ExportPage>($"/api/v1/backup/files?rootId={root.Id}&limit=3{(after is null ? "" : "&after=" + Uri.EscapeDataString(after))}", TestApp.Json);
            all.AddRange(page!.Items);
            after = page.Next;
        }
        while (after is not null);

        Assert.Contains(all, f => f.P == "src/docs/a.txt" && f.H is not null);
        var parentFiles = all.Where(f => f.P.StartsWith("src/", StringComparison.Ordinal)).Select(f => f with { P = f.P[4..] }).ToList();
        var parent = info with { Roots = [root with { Path = "/nas1/data", Files = parentFiles.Count }] };
        Task<ExportPage> Fetch(int id, string? from, CancellationToken ct) => Task.FromResult(new ExportPage(parentFiles, null));

        var sqlite = app.Services.GetRequiredService<SqliteSetup>();
        var roots = app.Services.GetRequiredService<RootRegistry>();
        BackupSyncResult result;
        using (var conn = sqlite.Open())
        {
            result = await new BackupSync(conn, roots, 2).RunAsync(parent, [new PathMapping("/nas1/data", app.Library + "/bak")], Fetch, new Progress<IntegrationProgress>(), CancellationToken.None);
            BackupAnnotations.Write(conn, "parent", "nas1", result.Targets);
        }

        Assert.Equal(1, result.Counts[BackupStates.Verified]);
        Assert.Equal(1, result.Counts[BackupStates.Corrupt]);
        Assert.Equal(1, result.Counts[BackupStates.Outdated]);
        Assert.Equal(1, result.Counts[BackupStates.Missing]);
        Assert.Equal(1, result.Counts[BackupStates.Extra]);
        var target = Assert.Single(result.Targets);
        Assert.Equal("bak", target.RelPath);

        using (var conn = sqlite.Open())
        {
            var missing = Db.Query(conn, "SELECT LocalRelPath, SourcePath FROM BackupFiles WHERE State = 'missing'", r => (r.GetString(0), r.GetString(1))).Single();
            Assert.Equal(("bak/new/d.txt", "/nas1/data/new/d.txt"), missing);
            var labels = Db.Query(conn, "SELECT e.RelPath, a.Kind, a.Label FROM Annotations a JOIN Entries e ON e.Id = a.EntryId WHERE a.Source = 'parent'",
                r => (Path: r.GetString(0), Kind: r.GetString(1), Label: r.GetString(2)));
            Assert.Contains(labels, l => l.Path == "bak" && l.Kind == BackupAnnotations.RootKind && l.Label == "Backup of nas1:/nas1/data");
            Assert.Contains(labels, l => l.Path == "bak" && l.Kind == BackupAnnotations.Kind && l.Label.Contains("1 missing") && l.Label.Contains("1 corrupt"));
            Assert.Contains(labels, l => l.Path == "bak/docs/b.txt" && l.Label.StartsWith("Content differs", StringComparison.Ordinal));
            Assert.Contains(labels, l => l.Path == "bak/e.txt" && l.Label == "Only in the backup");
            Assert.DoesNotContain(labels, l => l.Path == "bak/docs/a.txt");
        }

        // Plugin views.
        var folders = await http.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/parent/folders?rootId={target.RootId}&path=bak");
        Assert.Contains(folders.GetProperty("folders").EnumerateArray(), f => f.GetProperty("name").GetString() == "docs");
        var corrupt = await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations/parent/files?state=corrupt");
        Assert.Equal(1, corrupt.GetProperty("total").GetInt64());
        var entryId = corrupt.GetProperty("items")[0].GetProperty("entryId").GetInt64();
        var details = await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{entryId}");
        Assert.Equal("corrupt", details.GetProperty("backup").GetProperty("state").GetString());

        // Bit rot in the backup copy of a.txt: same size and mtime, other bytes. Verification finds it.
        var aPath = Path.Combine(app.Library, "bak/docs/a.txt");
        File.WriteAllText(aPath, "SAME content");
        File.SetLastWriteTimeUtc(aPath, t);
        await settings.SaveAsync(settings.Current with { VerifyAfterDays = 1 });
        using (var conn = sqlite.Open())
        {
            Db.Query(conn, "UPDATE Entries SET HashedAt = 1 WHERE IsDir = 0 RETURNING Id", r => r.GetInt64(0));
        }

        await app.ScanAsync(http);
        var flagged = await http.GetFromJsonAsync<JsonElement>("/api/v1/search?corrupt=true");
        Assert.Equal("a.txt", Assert.Single(flagged.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());

        // The next comparison knows it is the backup copy that changed.
        using (var conn = sqlite.Open())
        {
            result = await new BackupSync(conn, roots, 2).RunAsync(parent, [new PathMapping("/nas1/data", app.Library + "/bak")], Fetch, new Progress<IntegrationProgress>(), CancellationToken.None);
            var side = Db.Query(conn, "SELECT Side FROM BackupFiles WHERE LocalRelPath = 'bak/docs/a.txt'", r => r.IsDBNull(0) ? null : r.GetString(0)).Single();
            Assert.Equal(BackupStates.SideBackup, side);
        }

        Assert.Equal(2, result.Counts[BackupStates.Corrupt]);
    }

    [Fact]
    public async Task Mapping_is_detected_from_sample_files()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        var t = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 4; i++)
        {
            Write(app.Library, $"copy/music/song{i}.mp3", new string('x', 100 + i), t);
        }

        await app.ScanAsync(http);
        var files = Enumerable.Range(0, 4).Select(i => new ExportFile($"music/song{i}.mp3", 100 + i, new DateTimeOffset(t).ToUnixTimeSeconds())).ToList();
        var info = new ExportInfo("ANalyze", "test", "nas1", BackupExport.HashAlgorithm, false, 0, [new ExportRoot(1, "/srv/media", "media", 4, 0, 0, null)]);
        using var conn = app.Services.GetRequiredService<SqliteSetup>().Open();
        var result = await new BackupSync(conn, app.Services.GetRequiredService<RootRegistry>(), 2)
            .RunAsync(info, [], (_, _, _) => Task.FromResult(new ExportPage(files, null)), new Progress<IntegrationProgress>(), CancellationToken.None);
        Assert.Equal(4, result.Counts[BackupStates.Matched]);
        Assert.Equal(new PathMapping("/srv/media", app.Library + "/copy"), Assert.Single(result.Detected));
    }
}
