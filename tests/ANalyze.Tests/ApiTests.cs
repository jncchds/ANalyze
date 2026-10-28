using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
