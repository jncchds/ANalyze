using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Scanner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANalyze.Tests;

/// <summary>"Replace with the backup's copy" plans (<c>ActionOp.Restore</c>) against a fake backup.</summary>
public class RestoreTests
{
    /// <summary>A backup holding a good copy of some entries.</summary>
    private sealed class FakeBackup : IBackupSource
    {
        public readonly Dictionary<long, (byte[] Content, bool Corrupt, string? Hash)> Copies = [];

        public Task<IReadOnlyDictionary<long, BackupCopy>> FindAsync(IReadOnlyCollection<long> entryIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<long, BackupCopy>>(entryIds.Where(Copies.ContainsKey).ToDictionary(id => id, id =>
                new BackupCopy($"backup:/{id}", Copies[id].Content.Length, Copies[id].Hash, Copies[id].Corrupt, Identical: false)));

        public Task<IReadOnlyList<long>> DamagedAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<long>>(Copies.Where(c => c.Value.Corrupt).Select(c => c.Key).ToList());

        public Task<Stream> OpenAsync(long entryId, CancellationToken ct) =>
            Copies.TryGetValue(entryId, out var c) ? Task.FromResult<Stream>(new MemoryStream(c.Content)) : throw new IOException("Not on the backup");
    }

    private static string Hash(byte[] b) => Convert.ToHexStringLower(System.IO.Hashing.XxHash128.Hash(b));

    private static async Task<(TestApp App, HttpClient Http, FakeBackup Backup, long Damaged, long Fine, string Path)> SetupAsync()
    {
        var backup = new FakeBackup();
        var app = new TestApp
        {
            ExtraServices = s =>
            {
                s.RemoveAll<IBackupSource>();
                s.AddSingleton<IBackupSource>(backup);
            },
        };
        var path = Path.Combine(app.Library, "docs/restore.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [.. Enumerable.Repeat((byte)0, 5000)]);
        await File.WriteAllTextAsync(Path.Combine(app.Library, "docs/fine.txt"), "fine");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        var http = app.CreateClient();
        await app.ScanAsync(http);
        async Task<long> Id(string q) => (await http.GetFromJsonAsync<JsonElement>($"/api/v1/search?q={q}")).GetProperty("items")[0].GetProperty("id").GetInt64();
        return (app, http, backup, await Id("restore.bin"), await Id("fine.txt"), path);
    }

    [Fact]
    public async Task Damaged_file_is_replaced_by_the_backup_copy_and_undo_puts_it_back()
    {
        var (app, http, backup, damaged, fine, path) = await SetupAsync();
        await using var _ = app;
        using var __ = http;
        var good = Enumerable.Range(0, 6000).Select(i => (byte)i).ToArray();
        backup.Copies[damaged] = (good, true, Hash(good));
        backup.Copies[fine] = ("fine"u8.ToArray(), false, null);

        var res = await http.PostAsJsonAsync("/api/v1/plans/restore-damaged", new { }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        Assert.Equal(1, plan.GetProperty("summary").GetProperty("included").GetInt32());
        var item = plan.GetProperty("page").GetProperty("items")[0];
        Assert.Equal("restore", item.GetProperty("op").GetString());
        Assert.Equal($"backup:/{damaged}", item.GetProperty("targetPath").GetString());

        // Chosen by hand, a file that is not damaged is listed but left out.
        res = await http.PostAsJsonAsync("/api/v1/plans", new { entryIds = new[] { damaged, fine }, op = "restore" }, TestApp.Json);
        var manual = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{(await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32()}");
        Assert.Equal(1, manual.GetProperty("summary").GetProperty("included").GetInt32());
        Assert.Equal(1, manual.GetProperty("summary").GetProperty("warnings").GetInt32());

        var mtime = File.GetLastWriteTimeUtc(path);
        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        plan = await app.WaitPlanAsync(http, id, "done", "failed");
        Assert.Equal(1, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.Equal(good, await File.ReadAllBytesAsync(path));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(5000, new FileInfo(Path.Combine(app.Library, ".analyze-trash", id.ToString(), "docs/restore.bin")).Length);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*analyze-restore*"));
        var entry = (await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{damaged}")).GetProperty("entry");
        Assert.Equal(6000, entry.GetProperty("size").GetInt64());
        Assert.True(entry.TryGetProperty("hashedAt", out var h) && h.ValueKind == JsonValueKind.Number);
        Assert.False(entry.TryGetProperty("corruptAt", out var c) && c.ValueKind != JsonValueKind.Null);

        (await http.PostAsync($"/api/v1/plans/{id}/undo", null)).EnsureSuccessStatusCode();
        await app.WaitPlanAsync(http, id, "undone");
        Assert.Equal(5000, new FileInfo(path).Length);
        Assert.False(Directory.Exists(Path.Combine(app.Library, ".analyze-trash")));
        entry = (await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{damaged}")).GetProperty("entry");
        Assert.Equal(5000, entry.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Restores_through_the_parent_integration_and_the_comparison_follows()
    {
        // One instance plays both sides (as in BackupDiffTests): "bk" is the parent's good copy of "home", which is cut short here.
        TestApp app = null!;
        app = new TestApp
        {
            ExtraServices = s => s.AddHttpClient(ANalyze.Integrations.IntegrationHttp.Name).ConfigurePrimaryHttpMessageHandler(() => app.Server.CreateHandler()),
        };
        await using var _ = app;
        using var http = app.CreateClient();
        var good = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
        Directory.CreateDirectory(Path.Combine(app.Library, "home/docs"));
        Directory.CreateDirectory(Path.Combine(app.Library, "bk/docs"));
        var path = Path.Combine(app.Library, "home/docs/v.bin");
        await File.WriteAllBytesAsync(path, good[..200_000]);
        await File.WriteAllBytesAsync(Path.Combine(app.Library, "bk/docs/v.bin"), good);
        await app.ScanAsync(http);
        (await http.PutAsJsonAsync("/api/v1/integrations/parent", new { enabled = true, syncAfterScan = false, values = new { url = "http://localhost" } })).EnsureSuccessStatusCode();

        var sqlite = app.Services.GetRequiredService<ANalyze.Infrastructure.Data.SqliteSetup>();
        long entryId;
        using (var conn = sqlite.Open())
        {
            entryId = ANalyze.Integrations.Items.Db.Query(conn, "SELECT Id FROM Entries WHERE RelPath = 'home/docs/v.bin'", r => r.GetInt64(0)).Single();
            ANalyze.Integrations.Items.Db.Query(conn, """
                INSERT INTO BackupFiles (State, IsDir, EntryId, LocalRootId, LocalRelPath, Size, ParentSize, ParentHash, Verified, Children, SyncedChildren)
                VALUES ('corrupt', 0, $e, 1, 'home/docs/v.bin', 200000, 300000, $h, 0, 0, 0) RETURNING Id
                """, r => r.GetInt64(0), ("$e", entryId), ("$h", Hash(good)));
        }

        var info = new ANalyze.Integrations.Backup.ExportInfo("ANalyze", "test", "nas1", ANalyze.Integrations.Backup.BackupExport.HashAlgorithm, true, 0,
            [new ANalyze.Integrations.Backup.ExportRoot(1, "/srv", "srv", 1, 0, null, null)]);
        var snap = new ANalyze.Integrations.Backup.BackupSnapshot(DateTimeOffset.UtcNow, "nas1", "http://localhost", info,
            [new ANalyze.Integrations.Backup.BackupTarget(1, "/srv/bk", app.Library + "/home", 1, "home", null)],
            [new ANalyze.Integrations.PathMapping("/srv/bk", app.Library + "/home")], [], 0, true);
        var dbf = app.Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ANalyze.Infrastructure.Data.AnalyzeDbContext>>();
        await using (var db = await dbf.CreateDbContextAsync())
        {
            db.Settings.Add(new ANalyze.Domain.Setting { Key = ANalyze.Integrations.Backup.ParentIntegration.SnapshotKey, Value = JsonSerializer.Serialize(snap, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            await db.SaveChangesAsync();
        }

        var res = await http.PostAsJsonAsync("/api/v1/plans/restore-damaged", new { }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        Assert.Equal("nas1:/srv/bk/docs/v.bin", plan.GetProperty("page").GetProperty("items")[0].GetProperty("targetPath").GetString());

        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        plan = await app.WaitPlanAsync(http, id, "done", "failed");
        Assert.Equal(1, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.Equal(good, await File.ReadAllBytesAsync(path));
        using (var conn = sqlite.Open())
        {
            var row = ANalyze.Integrations.Items.Db.Query(conn, "SELECT State, Verified FROM BackupFiles WHERE EntryId = $e", r => (r.GetString(0), r.GetBoolean(1)), ("$e", entryId)).Single();
            Assert.Equal(("synced", true), row);
        }
    }

    [Fact]
    public async Task Restore_can_keep_the_local_file_next_to_the_backup_copy()
    {
        var (app, http, backup, damaged, _, path) = await SetupAsync();
        await using var _a = app;
        using var _h = http;
        var good = Enumerable.Range(0, 6000).Select(i => (byte)i).ToArray();
        backup.Copies[damaged] = (good, true, Hash(good));
        var kept = Path.Combine(app.Library, "docs/~restore.bin");
        // A clash with an existing file gets a suffix at planning time.
        await File.WriteAllTextAsync(kept, "older");

        var res = await http.PostAsJsonAsync("/api/v1/plans/restore-damaged", new { destination = "{relDir}/~{name}" }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var item = (await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}")).GetProperty("page").GetProperty("items")[0];
        Assert.Equal("docs/~restore (2).bin", item.GetProperty("keepPath").GetString());

        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        var plan = await app.WaitPlanAsync(http, id, "done", "failed");
        Assert.Equal(1, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.Equal(good, await File.ReadAllBytesAsync(path));
        var aside = Path.Combine(app.Library, "docs/~restore (2).bin");
        Assert.Equal(5000, new FileInfo(aside).Length);
        Assert.Equal("older", await File.ReadAllTextAsync(kept));
        Assert.False(Directory.Exists(Path.Combine(app.Library, ".analyze-trash")));
        var trash = await http.GetFromJsonAsync<JsonElement>("/api/v1/trash");
        Assert.Equal(0, trash.GetArrayLength());

        (await http.PostAsync($"/api/v1/plans/{id}/undo", null)).EnsureSuccessStatusCode();
        await app.WaitPlanAsync(http, id, "undone");
        Assert.Equal(5000, new FileInfo(path).Length);
        Assert.False(File.Exists(aside));
    }

    [Fact]
    public async Task Files_are_added_to_a_draft_plan_and_their_action_can_be_changed()
    {
        var (app, http, backup, damaged, fine, _) = await SetupAsync();
        await using var _a = app;
        using var _h = http;
        var good = Enumerable.Range(0, 6000).Select(i => (byte)i).ToArray();
        backup.Copies[damaged] = (good, true, Hash(good));

        var res = await http.PostAsJsonAsync("/api/v1/plans", new { entryIds = new[] { damaged }, op = "restore" }, TestApp.Json);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        res = await http.PostAsJsonAsync("/api/v1/plans", new { entryIds = new[] { fine }, op = "trash", planId = id }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var added = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, added.GetProperty("id").GetInt32());
        Assert.Equal(2, added.GetProperty("items").GetInt32());

        // The entry panel says which draft plan has the file.
        var planned = (await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{fine}")).GetProperty("planned");
        Assert.Equal("trash", planned[0].GetProperty("op").GetString());

        // Adding the same file again replaces its item: that is how its action changes.
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        var restoreItem = plan.GetProperty("page").GetProperty("items").EnumerateArray().First(i => i.GetProperty("op").GetString() == "restore");
        Assert.True(plan.GetProperty("parents").TryGetProperty(damaged.ToString(), out _));
        res = await http.PostAsJsonAsync($"/api/v1/plans/{id}/items/change", new { ids = new[] { restoreItem.GetProperty("id").GetInt64() }, op = "move", destination = "Old" }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        var items = plan.GetProperty("page").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var moved = items.Single(i => i.GetProperty("op").GetString() == "move");
        Assert.Equal("Old/restore.bin", moved.GetProperty("targetPath").GetString());

        // Applied plans cannot take more.
        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        await app.WaitPlanAsync(http, id, "done", "failed");
        res = await http.PostAsJsonAsync("/api/v1/plans", new { entryIds = new[] { damaged }, op = "trash", planId = id }, TestApp.Json);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_backup_hash_changes_nothing()
    {
        var (app, http, backup, damaged, _, path) = await SetupAsync();
        await using var _a = app;
        using var _h = http;
        var good = Enumerable.Range(0, 6000).Select(i => (byte)i).ToArray();
        backup.Copies[damaged] = (good, true, Hash([1, 2, 3]));

        var res = await http.PostAsJsonAsync("/api/v1/plans/restore-damaged", new { }, TestApp.Json);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        var plan = await app.WaitPlanAsync(http, id, "done", "failed");
        Assert.Equal(1, plan.GetProperty("summary").GetProperty("skipped").GetInt32());
        Assert.Equal(5000, new FileInfo(path).Length);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*analyze-restore*"));
        Assert.False(File.Exists(Path.Combine(app.Library, ".analyze-trash", id.ToString(), "docs/restore.bin")));
    }
}
