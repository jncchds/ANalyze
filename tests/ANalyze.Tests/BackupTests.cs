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

public class BackupRuleTests
{
    [Fact]
    public void Folder_needs_80_percent_of_direct_content_or_identical()
    {
        Assert.True(BackupSync.FolderSynced(5, 4, false));
        Assert.False(BackupSync.FolderSynced(5, 3, false));
        Assert.True(BackupSync.FolderSynced(10, 10, false));
        Assert.False(BackupSync.FolderSynced(0, 0, false));
        Assert.True(BackupSync.FolderSynced(0, 0, true));
    }

    [Fact]
    public void File_states()
    {
        Assert.Equal(BackupStates.Synced, BackupSync.FileState(onParent: true, differs: false, folderSynced: true));
        Assert.Equal(BackupStates.Corrupt, BackupSync.FileState(onParent: true, differs: true, folderSynced: true));
        Assert.Equal(BackupStates.Missing, BackupSync.FileState(onParent: false, differs: false, folderSynced: true));
        // Outside synchronized folders a file is only synchronized or not.
        Assert.Equal(BackupStates.Synced, BackupSync.FileState(onParent: true, differs: true, folderSynced: false));
        Assert.Equal(BackupStates.Unsynced, BackupSync.FileState(onParent: false, differs: false, folderSynced: false));
    }

    [Fact]
    public void Differs_by_size_or_known_hashes()
    {
        Assert.True(BackupSync.Differs(10, null, 11, null));
        Assert.True(BackupSync.Differs(10, "a", 10, "b"));
        Assert.False(BackupSync.Differs(10, "a", 10, null));
        Assert.False(BackupSync.Differs(10, "a", 10, "a"));
    }

    [Fact]
    public void Paths_map_back_to_the_parent()
    {
        Assert.Equal("/backup/nas1/docs/a.txt", BackupSync.ToParentPath("/library/docs/a.txt", [new PathMapping("/backup/nas1", "/library")]));
        Assert.Null(BackupSync.ToParentPath("/other/a.txt", [new PathMapping("/backup/nas1", "/library")]));
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
    private static void Write(string root, string rel, string content)
    {
        var p = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
    }

    /// <summary>Pages of the export of this instance, limited to one folder that plays the parent's tree.</summary>
    private static async Task<(ExportInfo Info, List<ExportFile> Files)> ParentFromFolder(HttpClient http, string folder, string parentPath)
    {
        var info = await http.GetFromJsonAsync<ExportInfo>("/api/v1/backup/info", TestApp.Json);
        var root = Assert.Single(info!.Roots);
        var all = new List<ExportFile>();
        string? after = null;
        do
        {
            var page = await http.GetFromJsonAsync<ExportPage>($"/api/v1/backup/files?rootId={root.Id}&limit=3{(after is null ? "" : "&after=" + Uri.EscapeDataString(after))}", TestApp.Json);
            all.AddRange(page!.Items);
            after = page.Next;
        }
        while (after is not null);

        var files = all.Where(f => f.P.StartsWith(folder + "/", StringComparison.Ordinal)).Select(f => f with { P = f.P[(folder.Length + 1)..] }).ToList();
        return (info with { Roots = [root with { Path = parentPath, Files = files.Count(f => !f.D) }] }, files);
    }

    [Fact]
    public async Task Folders_by_share_and_files_by_folder_state()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        var settings = app.Services.GetRequiredService<SettingsStore>();
        await settings.SaveAsync(settings.Current with { HashAllFiles = true });

        // "home" is this instance's data, "bk" plays its copy on the backup server (the parent).
        foreach (var n in "abcde")
        {
            Write(app.Library, $"home/docs/{n}.txt", $"doc {n}");
        }

        Write(app.Library, "bk/docs/a.txt", "doc a");
        Write(app.Library, "bk/docs/b.txt", "doc b");
        Write(app.Library, "bk/docs/c.txt", "doc c, longer on the backup");
        Write(app.Library, "bk/docs/d.txt", "DOC D");
        for (var i = 1; i <= 5; i++)
        {
            Write(app.Library, $"home/photos/p{i}.jpg", $"photo {i}");
        }

        Write(app.Library, "bk/photos/p1.jpg", "photo 1");
        Write(app.Library, "home/top.txt", "top");
        Write(app.Library, "bk/top.txt", "top");
        Directory.CreateDirectory(Path.Combine(app.Library, "home/empty"));
        Directory.CreateDirectory(Path.Combine(app.Library, "bk/empty"));
        Directory.CreateDirectory(Path.Combine(app.Library, "home/empty2"));
        await app.ScanAsync(http);

        // Every file has a hash now, but only real copies count as duplicates (IMG_0002 only has a hard link).
        var dupSearch = await http.GetFromJsonAsync<JsonElement>("/api/v1/search?duplicates=true&q=IMG_0002");
        Assert.Equal(0, dupSearch.GetProperty("total").GetInt64());

        var (parent, files) = await ParentFromFolder(http, "bk", "/backup/nas1");
        Assert.Contains(files, f => f.P == "empty" && f.D);
        Assert.Contains(files, f => f.P == "docs/a.txt" && f.H is not null);

        var sqlite = app.Services.GetRequiredService<SqliteSetup>();
        var roots = app.Services.GetRequiredService<RootRegistry>();
        BackupSyncResult result;
        using (var conn = sqlite.Open())
        {
            result = await new BackupSync(conn, roots).RunAsync(parent, [new PathMapping("/backup/nas1", app.Library + "/home")],
                (_, _, _) => Task.FromResult(new ExportPage(files, null)), new Progress<IntegrationProgress>(), CancellationToken.None);
            BackupAnnotations.Write(conn, "parent", "nas1", result.Targets);
        }

        Assert.Equal("home", Assert.Single(result.Targets).RelPath);
        Assert.Equal(4, result.Files[BackupStates.Synced]);   // a, b, p1, top
        Assert.Equal(2, result.Files[BackupStates.Corrupt]);  // c (size), d (hash)
        Assert.Equal(1, result.Files[BackupStates.Missing]);  // e
        Assert.Equal(4, result.Files[BackupStates.Unsynced]); // p2–p5: their folder is not synchronized

        using (var conn = sqlite.Open())
        {
            var states = Db.Query(conn, "SELECT LocalRelPath, State, Children, SyncedChildren FROM BackupFiles WHERE IsDir = 1", r => (r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3)))
                .ToDictionary(x => x.Item1);
            Assert.Equal(("home/docs", "synced", 5, 4), states["home/docs"]);      // 80%
            Assert.Equal(("home/photos", "unsynced", 5, 1), states["home/photos"]);
            Assert.Equal("synced", states["home/empty"].Item2);                    // identical (empty on both)
            Assert.Equal("unsynced", states["home/empty2"].Item2);                 // not on the parent
            Assert.Equal(("home", "unsynced", 5, 3), states["home"]);              // docs, empty, top of 5

            var labels = Db.Query(conn, "SELECT e.RelPath, a.Kind, a.Label FROM Annotations a JOIN Entries e ON e.Id = a.EntryId WHERE a.Source = 'parent'",
                r => (Path: r.GetString(0), Kind: r.GetString(1), Label: r.GetString(2)));
            Assert.Contains(labels, l => l.Path == "home" && l.Kind == BackupAnnotations.RootKind && l.Label == "Backed up to nas1:/backup/nas1");
            Assert.Contains(labels, l => l.Path == "home/docs" && l.Label == "Synced");
            Assert.Contains(labels, l => l.Path == "home/photos" && l.Label == "Not synced · 20%");
            Assert.Contains(labels, l => l.Path == "home/docs/d.txt" && l.Label.StartsWith("Corrupted", StringComparison.Ordinal));
            Assert.Contains(labels, l => l.Path == "home/docs/e.txt" && l.Label == "Missing from the backup");
            Assert.DoesNotContain(labels, l => l.Path == "home/photos/p2.jpg");
        }

        // Plugin views.
        var folder = await http.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/parent/folders?rootId=1&path=home");
        Assert.Equal("unsynced", folder.GetProperty("self").GetProperty("state").GetString());
        Assert.Contains(folder.GetProperty("folders").EnumerateArray(), f => f.GetProperty("name").GetString() == "docs" && f.GetProperty("state").GetString() == "synced");
        var corrupt = await http.GetFromJsonAsync<JsonElement>("/api/v1/integrations/parent/files?state=corrupt");
        Assert.Equal(2, corrupt.GetProperty("total").GetInt64());
        var entryId = corrupt.GetProperty("items")[0].GetProperty("entryId").GetInt64();
        var details = await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{entryId}");
        Assert.Equal("corrupt", details.GetProperty("backup").GetProperty("state").GetString());

        // A label in the browser opens the folder view at the file's folder.
        var located = await http.GetFromJsonAsync<JsonElement>($"/api/v1/integrations/parent/locate?entryId={entryId}");
        Assert.Equal("home/docs", located.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Verification_flags_content_that_changed_silently()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        var settings = app.Services.GetRequiredService<SettingsStore>();
        await settings.SaveAsync(settings.Current with { HashAllFiles = true });
        var path = Path.Combine(app.Library, "docs/a.txt");
        var t = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        Write(app.Library, "docs/a.txt", "same content");
        File.SetLastWriteTimeUtc(path, t);
        await app.ScanAsync(http);

        // Bit rot: same size and mtime, other bytes.
        File.WriteAllText(path, "SAME content");
        File.SetLastWriteTimeUtc(path, t);
        await settings.SaveAsync(settings.Current with { VerifyAfterDays = 1 });
        using (var conn = app.Services.GetRequiredService<SqliteSetup>().Open())
        {
            Db.Query(conn, "UPDATE Entries SET HashedAt = 1 WHERE IsDir = 0 RETURNING Id", r => r.GetInt64(0));
        }

        await app.ScanAsync(http);
        var flagged = await http.GetFromJsonAsync<JsonElement>("/api/v1/search?corrupt=true");
        Assert.Equal("a.txt", Assert.Single(flagged.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Mapping_is_detected_from_sample_files()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        for (var i = 0; i < 4; i++)
        {
            Write(app.Library, $"copy/music/song{i}.mp3", new string('x', 100 + i));
        }

        await app.ScanAsync(http);
        // On the server the copy lives in a sub-folder of its root: /srv/media is this instance's "copy" folder.
        var files = Enumerable.Range(0, 4).Select(i => new ExportFile($"media/music/song{i}.mp3", 100 + i, 0))
            .Prepend(new ExportFile("media/music", 406, 0, D: true)).Prepend(new ExportFile("media", 406, 0, D: true)).ToList();
        var info = new ExportInfo("ANalyze", "test", "nas1", BackupExport.HashAlgorithm, false, 0, [new ExportRoot(1, "/srv", "srv", 4, 0, 0, null)]);
        using var conn = app.Services.GetRequiredService<SqliteSetup>().Open();
        var result = await new BackupSync(conn, app.Services.GetRequiredService<RootRegistry>())
            .RunAsync(info, [], (_, _, _) => Task.FromResult(new ExportPage(files, null)), new Progress<IntegrationProgress>(), CancellationToken.None);
        Assert.Equal(new PathMapping("/srv/media", app.Library + "/copy"), Assert.Single(result.Detected));
        Assert.Equal("/srv/media", Assert.Single(result.Targets).ParentPath);
        Assert.Equal(4, result.Files[BackupStates.Synced]);
        Assert.Equal(2, result.SyncedFolders); // copy and copy/music
    }
}
