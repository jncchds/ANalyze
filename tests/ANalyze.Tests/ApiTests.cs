using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Tests;

public class ScanAndActionTests
{
    [Fact]
    public async Task Scan_finds_sizes_categories_duplicates_and_insights()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);

        var roots = await http.GetFromJsonAsync<JsonElement>("/api/v1/roots");
        Assert.False(roots[0].GetProperty("readOnly").GetBoolean());
        var rootEntry = roots[0].GetProperty("rootEntryId").GetInt64();
        var root = await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{rootEntry}");
        var expected = 300_000 * 4 + 2_000_000 + 19 + 9 + 10_000 + 1_000 + (OperatingSystem.IsLinux() ? 300_000 : 0);
        Assert.Equal(expected, root.GetProperty("entry").GetProperty("size").GetInt64());

        // Three copies of IMG_0001 → one group, 600 KB wasted. The hard link is not a duplicate.
        var dups = await http.GetFromJsonAsync<JsonElement>("/api/v1/duplicates");
        Assert.Equal(1, dups.GetProperty("total").GetInt64());
        Assert.Equal(600_000, dups.GetProperty("wasted").GetInt64());

        // The folders of two copies, compared file by file: the photo is the same, the rest of Photos/2021 is only in A.
        var group = dups.GetProperty("groups")[0];
        long Copy(string relPath) => group.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("relPath").GetString() == relPath).GetProperty("id").GetInt64();
        var cmp = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/duplicates/compare?a={Copy("Photos/2021/IMG_0001.jpg")}&b={Copy("Backup/Photos/IMG_0001.jpg")}&hash={group.GetProperty("hash").GetString()}");
        Assert.Equal("Photos/2021", cmp.GetProperty("a").GetProperty("relPath").GetString());
        Assert.Equal("Backup/Photos", cmp.GetProperty("b").GetProperty("relPath").GetString());
        var cmpFiles = cmp.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!);
        Assert.Equal("matched", cmpFiles["IMG_0001.jpg"].GetProperty("state").GetString());
        Assert.Equal("match", cmpFiles["IMG_0001.jpg"].GetProperty("crc").GetString());
        Assert.Equal("missing", cmpFiles["IMG_0002.jpg"].GetProperty("state").GetString());
        // The other copy of the photo, renamed: only in A, with the same content at another path in B.
        Assert.Equal("IMG_0001.jpg", cmpFiles["IMG_0001 (1).jpg"].GetProperty("elsewhere").GetString());
        Assert.Equal(3, cmp.GetProperty("copies").GetArrayLength());
        // One level up: Photos against Backup, where the inner pair keeps its relative paths.
        var up = await http.GetFromJsonAsync<JsonElement>(
            $"/api/v1/duplicates/compare?a={cmp.GetProperty("a").GetProperty("parentId").GetInt64()}&b={cmp.GetProperty("b").GetProperty("parentId").GetInt64()}");
        var upFiles = up.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("state").GetString());
        Assert.Equal("missing", upFiles["2021/IMG_0001.jpg"]);
        Assert.Equal("extra", upFiles["Photos/IMG_0001.jpg"]);

        var insights = await http.GetFromJsonAsync<JsonElement>("/api/v1/insights");
        var keys = insights.EnumerateArray().Select(i => i.GetProperty("key").GetString()).ToList();
        Assert.Contains("node_modules", keys);
        Assert.Contains("incomplete", keys);
        Assert.Contains("emptydirs", keys);
        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("brokenlinks", keys);
            Assert.Contains("hardlinked", keys);
        }

        var search = await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=heat&kind=video");
        Assert.Equal(1, search.GetProperty("total").GetInt64());

        // Incremental rescan: one changed file, nothing else rewritten.
        File.AppendAllText(Path.Combine(app.Library, "proj/README.md"), "more\n");
        await app.ScanAsync(http);
        var scans = await http.GetFromJsonAsync<JsonElement>("/api/v1/scans?limit=1");
        Assert.Equal(1, scans[0].GetProperty("changed").GetInt64());
        Assert.Equal(0, scans[0].GetProperty("added").GetInt64());
    }

    [Fact]
    public async Task Dedupe_plan_trashes_copies_and_undo_restores_them()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);

        var res = await http.PostAsJsonAsync("/api/v1/plans/dedupe", new { keep = "preferredFolder", op = "trash", preferredFolders = new[] { "Photos" } }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        Assert.Equal(2, plan.GetProperty("summary").GetProperty("included").GetInt32());
        Assert.All(plan.GetProperty("page").GetProperty("items").EnumerateArray(), i => Assert.Equal("Photos/2021/IMG_0001.jpg", i.GetProperty("keepPath").GetString()));

        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        plan = await app.WaitPlanAsync(http, id, "done", "failed");
        Assert.Equal(2, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.True(File.Exists(Path.Combine(app.Library, "Photos/2021/IMG_0001.jpg")));
        Assert.False(File.Exists(Path.Combine(app.Library, "Backup/Photos/IMG_0001.jpg")));
        Assert.True(File.Exists(Path.Combine(app.Library, ".analyze-trash", id.ToString(), "Backup/Photos/IMG_0001.jpg")));
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonElement>("/api/v1/duplicates")).GetProperty("total").GetInt64());

        (await http.PostAsync($"/api/v1/plans/{id}/undo", null)).EnsureSuccessStatusCode();
        await app.WaitPlanAsync(http, id, "undone");
        Assert.True(File.Exists(Path.Combine(app.Library, "Backup/Photos/IMG_0001.jpg")));
        Assert.False(Directory.Exists(Path.Combine(app.Library, ".analyze-trash")));
        Assert.Equal(1, (await http.GetFromJsonAsync<JsonElement>("/api/v1/duplicates")).GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Emptying_a_plans_trash_runs_in_the_background_and_reports_progress()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);

        var res = await http.PostAsJsonAsync("/api/v1/plans/dedupe", new { keep = "preferredFolder", op = "trash", preferredFolders = new[] { "Photos" } }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        var plan = await app.WaitPlanAsync(http, id, "done", "failed");
        Assert.Equal(2, plan.GetProperty("summary").GetProperty("trashItems").GetInt32());

        Assert.Equal(HttpStatusCode.Accepted, (await http.DeleteAsync($"/api/v1/trash/{id}")).StatusCode);
        var board = app.Services.GetRequiredService<JobBoard>();
        JobProgress? job = null;
        for (var i = 0; i < 100 && job?.State is not JobStates.Completed; i++)
        {
            await Task.Delay(100);
            job = board.Get(JobKinds.Trash);
        }

        Assert.NotNull(job);
        Assert.Equal(JobStates.Completed, job.State);
        Assert.Equal(id, job.RefId);
        Assert.Equal(2, job.Done);
        Assert.Equal(job.TotalBytes, job.Bytes);
        Assert.False(Directory.Exists(Path.Combine(app.Library, ".analyze-trash")));
        plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        Assert.Equal(0, plan.GetProperty("summary").GetProperty("trashItems").GetInt32());
        Assert.False(plan.GetProperty("summary").GetProperty("trashPending").GetBoolean());
        Assert.Equal(0, (await http.GetFromJsonAsync<JsonElement>("/api/v1/trash")).GetArrayLength());
    }

    [Fact]
    public async Task Dedupe_adds_to_a_draft_plan_and_replaces_the_action_of_files_it_has()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var copy = (await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=Backup/Photos/IMG_0001")).GetProperty("items")[0].GetProperty("id").GetInt64();
        var heat = (await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=Heat.mkv")).GetProperty("items")[0].GetProperty("id").GetInt64();
        var res = await http.PostAsJsonAsync("/api/v1/plans", new { entryIds = new[] { copy, heat }, op = "move", destination = "Old" }, TestApp.Json);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        res = await http.PostAsJsonAsync("/api/v1/plans/dedupe", new { keep = "preferredFolder", op = "trash", preferredFolders = new[] { "Photos" }, planId = id }, TestApp.Json);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var added = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, added.GetProperty("id").GetInt32());
        Assert.Equal(1, added.GetProperty("replaced").GetInt32());

        // The copy that was to be moved is trashed now; Heat.mkv keeps its move; nothing appears twice.
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        var items = plan.GetProperty("page").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("trash", items.Single(i => i.GetProperty("entryId").GetInt64() == copy).GetProperty("op").GetString());
        Assert.Equal("move", items.Single(i => i.GetProperty("entryId").GetInt64() == heat).GetProperty("op").GetString());
        Assert.NotNull(plan.GetProperty("plan").GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task A_file_kept_by_a_dedupe_item_is_not_also_trashed_by_the_same_plan()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var res = await http.PostAsJsonAsync("/api/v1/plans/dedupe", new { keep = "preferredFolder", op = "trash", preferredFolders = new[] { "Photos" } }, TestApp.Json);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var kept = (await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=IMG_0001")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("relPath").GetString() == "Photos/2021/IMG_0001.jpg").GetProperty("id").GetInt64();
        // The kept copy is trashed too, added after the dedupe: the review warns and applying keeps it.
        res = await http.PostAsJsonAsync("/api/v1/plans", new { entryIds = new[] { kept }, op = "trash", planId = id }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        Assert.Equal(3, plan.GetProperty("summary").GetProperty("warnings").GetInt32()); // the trash of the kept copy and both duplicates relying on it

        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        plan = await app.WaitPlanAsync(http, id, "done", "failed");
        Assert.Equal(2, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.Equal(1, plan.GetProperty("summary").GetProperty("skipped").GetInt32());
        Assert.True(File.Exists(Path.Combine(app.Library, "Photos/2021/IMG_0001.jpg")));
    }

    [Fact]
    public async Task Move_with_template_updates_the_index()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var heat = (await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=Heat.mkv")).GetProperty("items")[0].GetProperty("id").GetInt64();
        var res = await http.PostAsJsonAsync("/api/v1/plans", new { entryIds = new[] { heat }, op = "move", destination = "Sorted/{category}/{name}" }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        await app.WaitPlanAsync(http, id, "done");
        Assert.True(File.Exists(Path.Combine(app.Library, "Sorted/Video/Heat.mkv")));
        var found = await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=Heat.mkv");
        Assert.Equal("Sorted/Video/Heat.mkv", found.GetProperty("items")[0].GetProperty("relPath").GetString());
    }

    [Fact]
    public async Task Read_only_root_excludes_every_item()
    {
        await using var app = new TestApp { ReadOnly = true };
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var cfg = await http.GetFromJsonAsync<JsonElement>("/api/v1/config");
        Assert.True(cfg.GetProperty("readOnly").GetBoolean());
        var res = await http.PostAsJsonAsync("/api/v1/plans/dedupe", new { keep = "oldest", op = "trash" }, TestApp.Json);
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        Assert.Equal(0, plan.GetProperty("summary").GetProperty("included").GetInt32());
    }

    [Fact]
    public async Task Disk_listing_goes_into_folders_that_are_not_scanned_yet()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        Directory.CreateDirectory(Path.Combine(app.Library, "new/2024/deep"));
        File.WriteAllText(Path.Combine(app.Library, "new/2024/deep/a.txt"), "hello");
        var rootEntry = (await http.GetFromJsonAsync<JsonElement>("/api/v1/roots"))[0].GetProperty("rootEntryId").GetInt64();

        var top = await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{rootEntry}/disk");
        Assert.Contains(top.GetProperty("items").EnumerateArray(), i => i.GetProperty("name").GetString() == "new" && i.GetProperty("isDir").GetBoolean());

        var deep = await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{rootEntry}/disk?sub=new/2024/deep");
        var item = Assert.Single(deep.GetProperty("items").EnumerateArray());
        Assert.Equal("new/2024/deep/a.txt", item.GetProperty("relPath").GetString());
        Assert.Equal(5, item.GetProperty("size").GetInt64());

        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/v1/entries/{rootEntry}/disk?sub=new/../..")).StatusCode);
        Assert.False((await http.GetFromJsonAsync<JsonElement>($"/api/v1/entries/{rootEntry}/disk?sub=missing")).GetProperty("readable").GetBoolean());
    }

    [Fact]
    public async Task Preview_endpoints_never_leave_the_root()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var readme = (await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=README")).GetProperty("items")[0].GetProperty("id").GetInt64();
        var text = await http.GetFromJsonAsync<JsonElement>($"/api/v1/files/{readme}/text");
        Assert.StartsWith("# readme", text.GetProperty("text").GetString());
        var hex = await http.GetFromJsonAsync<JsonElement>($"/api/v1/files/{readme}/hex?length=4");
        Assert.Equal(Convert.ToBase64String("# re"u8.ToArray()), hex.GetProperty("base64").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/files/999999/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/v1/does-not-exist")).StatusCode);
    }

    [Fact]
    public async Task Ai_classification_applies_confident_answers()
    {
        await using var app = new TestApp
        {
            AiReply = _ => """Here you go: {"results":[{"id":1,"category":"new:Mystery Data","tags":["odd"],"confidence":0.9,"reason":"unknown extension"}]}""",
        };
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var settings = await http.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        var obj = System.Text.Json.Nodes.JsonNode.Parse(settings.GetRawText())!.AsObject();
        obj["ai"]!["enabled"] = true;
        (await http.PutAsJsonAsync("/api/v1/settings", obj)).EnsureSuccessStatusCode();

        (await http.PostAsJsonAsync("/api/v1/ai/classify", new { limit = 1 })).EnsureSuccessStatusCode();
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            var status = await http.GetFromJsonAsync<JsonElement>("/api/v1/ai/status");
            if (!status.GetProperty("running").GetBoolean() && status.GetProperty("history").GetArrayLength() > 0)
            {
                break;
            }
        }

        var cats = await http.GetFromJsonAsync<JsonElement>("/api/v1/categories");
        var mystery = cats.EnumerateArray().First(c => c.GetProperty("name").GetString() == "Mystery Data").GetProperty("id").GetInt32();
        var found = await http.GetFromJsonAsync<JsonElement>($"/api/v1/search?categoryId={mystery}");
        Assert.Equal(1, found.GetProperty("total").GetInt64());
        Assert.Equal("ai", found.GetProperty("items")[0].GetProperty("categorySource").GetString());
    }
}

public class JobCancelTests
{
    [Fact]
    public async Task Each_job_stops_on_its_own_and_stop_all_stops_every_one()
    {
        await using var app = new TestApp();
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var board = app.Services.GetRequiredService<JobBoard>();

        // Nothing runs: stopping one kind finds nothing, stopping all stops nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync("/api/v1/jobs/scan/cancel", null)).StatusCode);
        var none = await (await http.PostAsync("/api/v1/jobs/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, none.GetProperty("stopped").GetArrayLength());

        // Applying rules waits while the index is held exclusively; it is its own job ("rules"), not the scan's.
        var held = await app.Services.GetRequiredService<JobLock>().AcquireAsync("test", CancellationToken.None);
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync("/api/v1/rules/apply", null)).StatusCode);
            await WaitStateAsync(board, JobKinds.Rules, JobStates.Queued);
            Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("/api/v1/rules/apply", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync("/api/v1/jobs/rules/cancel", null)).StatusCode);
            await WaitStateAsync(board, JobKinds.Rules, JobStates.Cancelled);
            Assert.NotEqual("Applying rules", board.Get(JobKinds.Scan)?.Title);

            // Stop all: the waiting rules job is stopped and named in the answer.
            await WaitGoneAsync(app.Services.GetRequiredService<JobCancels>(), JobKinds.Rules);
            Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync("/api/v1/rules/apply", null)).StatusCode);
            await WaitStateAsync(board, JobKinds.Rules, JobStates.Queued);
            var all = await (await http.PostAsync("/api/v1/jobs/cancel", null)).Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("rules", all.GetProperty("stopped").EnumerateArray().Select(k => k.GetString()));
            await WaitStateAsync(board, JobKinds.Rules, JobStates.Cancelled);
        }
        finally
        {
            held.Dispose();
        }

        // Once stopped, rules can be applied again and run to the end.
        await WaitGoneAsync(app.Services.GetRequiredService<JobCancels>(), JobKinds.Rules);
        Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync("/api/v1/rules/apply", null)).StatusCode);
        await WaitStateAsync(board, JobKinds.Rules, JobStates.Completed);
    }

    private static async Task WaitStateAsync(JobBoard board, string kind, string state)
    {
        for (var i = 0; i < 400 && board.Get(kind)?.State != state; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(state, board.Get(kind)?.State);
    }

    /// <summary>The cancelled job reports before its registration is disposed; wait for that too.</summary>
    private static async Task WaitGoneAsync(JobCancels cancels, string kind)
    {
        for (var i = 0; i < 400 && cancels.Running.Contains(kind); i++)
        {
            await Task.Delay(25);
        }
    }
}

public class OverwriteKeepTests
{
    [Fact]
    public async Task A_move_that_overwrites_keeps_the_old_file_renamed_and_undo_puts_both_back()
    {
        await using var app = new TestApp();
        var dir = Path.Combine(app.Library, "ow");
        Directory.CreateDirectory(Path.Combine(dir, "dst"));
        File.WriteAllText(Path.Combine(dir, "note.txt"), "new version");
        File.WriteAllText(Path.Combine(dir, "dst", "note.txt"), "old version");
        File.WriteAllText(Path.Combine(dir, "dst", "~note.txt"), "already there");
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var id = (await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=ow/note.txt")).GetProperty("items")[0].GetProperty("id").GetInt64();

        var res = await http.PostAsJsonAsync("/api/v1/plans",
            new { entryIds = new[] { id }, op = "move", destination = "ow/dst", conflict = "overwrite", keepOverwritten = "{relDir}/~{name}" }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var planId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var item = (await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}")).GetProperty("page").GetProperty("items")[0];
        Assert.Equal("ow/dst/note.txt", item.GetProperty("targetPath").GetString());
        // "~note.txt" is taken: the kept copy gets " (2)".
        Assert.Equal("ow/dst/~note (2).txt", item.GetProperty("keepPath").GetString());

        (await http.PostAsync($"/api/v1/plans/{planId}/apply", null)).EnsureSuccessStatusCode();
        var plan = await app.WaitPlanAsync(http, planId, "done", "failed");
        Assert.Equal(1, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.Equal("new version", File.ReadAllText(Path.Combine(dir, "dst", "note.txt")));
        Assert.Equal("old version", File.ReadAllText(Path.Combine(dir, "dst", "~note (2).txt")));
        Assert.False(File.Exists(Path.Combine(dir, "note.txt")));

        (await http.PostAsync($"/api/v1/plans/{planId}/undo", null)).EnsureSuccessStatusCode();
        await app.WaitPlanAsync(http, planId, "undone");
        Assert.Equal("new version", File.ReadAllText(Path.Combine(dir, "note.txt")));
        Assert.Equal("old version", File.ReadAllText(Path.Combine(dir, "dst", "note.txt")));
        Assert.False(File.Exists(Path.Combine(dir, "dst", "~note (2).txt")));
        Assert.Equal("already there", File.ReadAllText(Path.Combine(dir, "dst", "~note.txt")));
    }
}

public class FolderCleanupTests
{
    [Fact]
    public async Task Similar_folders_are_ranked_and_merging_a_into_b_leaves_a_only_what_b_lacks()
    {
        await using var app = new TestApp();
        var (a, b) = (Path.Combine(app.Library, "cmpA"), Path.Combine(app.Library, "cmpB"));
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        byte[] Bytes(int n, byte seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + seed)).ToArray();
        File.WriteAllBytes(Path.Combine(a, "same.bin"), Bytes(5_000, 1));
        File.WriteAllBytes(Path.Combine(b, "same.bin"), Bytes(5_000, 1));
        File.WriteAllBytes(Path.Combine(a, "renamed.bin"), Bytes(3_001, 2));
        File.WriteAllBytes(Path.Combine(b, "other-name.bin"), Bytes(3_001, 2));
        File.WriteAllBytes(Path.Combine(a, "diff.bin"), Bytes(4_003, 3));
        File.WriteAllBytes(Path.Combine(b, "diff.bin"), Bytes(4_003, 4));
        File.WriteAllText(Path.Combine(a, "onlyA.txt"), "only in A");
        using var http = app.CreateClient();
        await app.ScanAsync(http);

        // The pair is listed with how alike the two are: 8,001 shared bytes of A's 12,013 (A is the larger, with onlyA.txt).
        var pairs = await http.GetFromJsonAsync<JsonElement>("/api/v1/duplicates/folders?sort=similar");
        var pair = pairs.EnumerateArray().Single(p => p.GetProperty("a").GetProperty("relPath").GetString() is "cmpA" or "cmpB"
            && p.GetProperty("b").GetProperty("relPath").GetString() is "cmpA" or "cmpB");
        Assert.Equal(8_001, pair.GetProperty("sharedBytes").GetInt64());
        Assert.Equal(2, pair.GetProperty("sharedFiles").GetInt64());
        Assert.Equal(Math.Round(8_001d / 12_013, 4), pair.GetProperty("similarity").GetDouble());
        var none = await http.GetFromJsonAsync<JsonElement>("/api/v1/duplicates/folders?min=0.99");
        Assert.DoesNotContain(none.EnumerateArray(), p => p.GetProperty("a").GetProperty("relPath").GetString() is "cmpA" or "cmpB");

        async Task<long> Id(string q) => (await http.GetFromJsonAsync<JsonElement>($"/api/v1/search?q={q}")).GetProperty("items")[0].GetProperty("id").GetInt64();
        var (idA, idB) = (await Id("cmpA/same.bin"), await Id("cmpB/same.bin"));
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v1/duplicates/compare/plan", new { a = idA, b = idB, mode = "everything" }, TestApp.Json)).StatusCode);

        var res = await http.PostAsJsonAsync("/api/v1/duplicates/compare/plan", new { a = idA, b = idB, mode = "merge", op = "trash" }, TestApp.Json);
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
        var items = plan.GetProperty("page").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("sourcePath").GetString()!);
        // diff.bin has other content at the same path in B: left alone.
        Assert.Equal(["cmpA/onlyA.txt", "cmpA/renamed.bin", "cmpA/same.bin"], items.Keys.Order());
        Assert.Equal("cmpB/same.bin", items["cmpA/same.bin"].GetProperty("keepPath").GetString());
        Assert.Equal("cmpB/other-name.bin", items["cmpA/renamed.bin"].GetProperty("keepPath").GetString());
        Assert.Equal("move", items["cmpA/onlyA.txt"].GetProperty("op").GetString());
        Assert.Equal("cmpB/onlyA.txt", items["cmpA/onlyA.txt"].GetProperty("targetPath").GetString());
        Assert.All(items.Values, i => Assert.True(i.GetProperty("included").GetBoolean()));

        (await http.PostAsync($"/api/v1/plans/{id}/apply", null)).EnsureSuccessStatusCode();
        plan = await app.WaitPlanAsync(http, id, "done", "failed");
        Assert.Equal(3, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.Equal(["diff.bin"], Directory.GetFiles(a).Select(Path.GetFileName));
        Assert.Equal("only in A", File.ReadAllText(Path.Combine(b, "onlyA.txt")));
        Assert.True(File.Exists(Path.Combine(b, "same.bin")));

        // Nothing is left to plan now: A's only file differs from B's.
        var again = await http.PostAsJsonAsync("/api/v1/duplicates/compare/plan", new { a = await Id("cmpA/diff.bin"), b = idB, mode = "copies" }, TestApp.Json);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }
}
