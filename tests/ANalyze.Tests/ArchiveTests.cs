using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using ANalyze.Infrastructure.Data;
using ANalyze.Scanner;
using Microsoft.Extensions.DependencyInjection;

namespace ANalyze.Tests;

public class ArchiveTests
{
    private static byte[] Bytes(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void Zip(string path, params (string Name, byte[] Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(content);
        }
    }

    private static void TarGz(string path, params (string Name, byte[] Content)[] files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var tar = new TarWriter(gz, TarEntryFormat.Pax);
        foreach (var (name, content) in files)
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) });
        }
    }

    private static async Task<JsonElement> Extractions(HttpClient http, string query = "") =>
        await http.GetFromJsonAsync<JsonElement>("/api/v1/archives/extractions?limit=100" + query);

    private static JsonElement Group(JsonElement list, string archiveRelPath) =>
        list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("archive").GetProperty("relPath").GetString() == archiveRelPath);

    /// <summary>The only extraction of the archive.</summary>
    private static JsonElement ByArchive(JsonElement list, string archiveRelPath) =>
        Group(list, archiveRelPath).GetProperty("extractions").EnumerateArray().Single();

    private static IEnumerable<JsonElement> AllExtractions(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().SelectMany(g => g.GetProperty("extractions").EnumerateArray());

    [Fact]
    public void Archive_paths_are_normalized_and_junk_is_dropped()
    {
        Assert.Equal("a/b.txt", ArchiveIndexer.Normalize(@".\a\\b.txt"));
        Assert.Null(ArchiveIndexer.Normalize("__MACOSX/a/._b.txt"));
        Assert.Null(ArchiveIndexer.Normalize("a/.DS_Store"));
        Assert.Null(ArchiveIndexer.Normalize("../evil.txt"));
    }

    [Fact]
    public async Task Finds_complete_partial_and_unwrapped_extractions_and_checks_them_against_the_archive()
    {
        await using var app = new TestApp();
        var lib = app.Library;
        var (a, b, c) = (Bytes(50_000, 1), Bytes(20_000, 2), Bytes(300, 3));
        Zip(Path.Combine(lib, "Downloads", "pack.zip"), ("pack/a.bin", a), ("pack/sub/b.bin", b), ("pack/c.txt", c));
        Write(Path.Combine(lib, "Extracted", "pack", "a.bin"), a);
        Write(Path.Combine(lib, "Extracted", "pack", "sub", "b.bin"), b);
        Write(Path.Combine(lib, "Extracted", "pack", "c.txt"), c);
        Write(Path.Combine(lib, "Extracted", "pack", "my-notes.txt"), Bytes(100, 4)); // extra files do not matter

        // Interrupted: x complete, y cut short, z never written.
        var (x, y, z) = (Bytes(1_500_000, 5), Bytes(200_000, 6), Bytes(90_000, 7));
        Zip(Path.Combine(lib, "Downloads", "two.zip"), ("x.bin", x), ("y.bin", y), ("z.bin", z));
        Write(Path.Combine(lib, "Two", "x.bin"), x);
        Write(Path.Combine(lib, "Two", "y.bin"), y[..50_000]);

        // Extracted without the archive's top folder.
        var (one, two) = (Bytes(40_000, 8), Bytes(30_000, 9));
        Zip(Path.Combine(lib, "Downloads", "wrapped.zip"), ("wrapped/one.bin", one), ("wrapped/two.bin", two));
        Write(Path.Combine(lib, "W", "one.bin"), one);
        Write(Path.Combine(lib, "W", "two.bin"), two);

        // An archive nobody extracted.
        Zip(Path.Combine(lib, "Downloads", "lonely.zip"), ("nothing.bin", Bytes(10_000, 10)));

        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var list = await Extractions(http);

        var pack = ByArchive(list, "Downloads/pack.zip");
        Assert.Equal("complete", pack.GetProperty("state").GetString());
        Assert.Equal("Extracted/pack", pack.GetProperty("folder").GetProperty("relPath").GetString());
        Assert.Equal(3, pack.GetProperty("matched").GetInt32());

        var partial = ByArchive(list, "Downloads/two.zip");
        Assert.Equal("partial", partial.GetProperty("state").GetString());
        Assert.Equal(1, partial.GetProperty("matched").GetInt32());
        Assert.Equal(1, partial.GetProperty("smaller").GetInt32());
        Assert.Equal(1, partial.GetProperty("missing").GetInt32());

        var wrapped = ByArchive(list, "Downloads/wrapped.zip");
        Assert.Equal("complete", wrapped.GetProperty("state").GetString());
        Assert.Equal("W", wrapped.GetProperty("baseRelPath").GetString());
        Assert.Equal("wrapped/", wrapped.GetProperty("strip").GetString());

        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("archive").GetProperty("relPath").GetString() == "Downloads/lonely.zip");

        // The per-file view names what is wrong with the partial extraction.
        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/v1/archives/extractions/{partial.GetProperty("id").GetInt64()}?problems=true");
        var states = detail.GetProperty("files").EnumerateArray().ToDictionary(f => f.GetProperty("path").GetString()!, f => f.GetProperty("state").GetString());
        Assert.Equal(new Dictionary<string, string?> { ["y.bin"] = "smaller", ["z.bin"] = "missing" }, states);

        // The comparison page also lists the files on disk, in the archive's folders, that the archive does not have.
        var compare = await http.GetFromJsonAsync<JsonElement>($"/api/v1/archives/extractions/{pack.GetProperty("id").GetInt64()}?extras=true&limit=100000");
        var extra = compare.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("state").GetString() == "extra");
        Assert.Equal("pack/my-notes.txt", extra.GetProperty("path").GetString());
        Assert.Equal("Extracted/pack/my-notes.txt", extra.GetProperty("target").GetString());
        Assert.True(extra.GetProperty("entryId").GetInt64() > 0);
        Assert.Equal(4, compare.GetProperty("total").GetInt32());
        Assert.All(compare.GetProperty("files").EnumerateArray().Where(f => f.GetProperty("state").GetString() == "matched"), f => Assert.True(f.GetProperty("diskMTime").GetInt64() > 0));

        // Damage one extracted file without changing its size or date, then check against the archive's CRC32s.
        var damaged = Path.Combine(lib, "Extracted", "pack", "sub", "b.bin");
        var when = File.GetLastWriteTimeUtc(damaged);
        var bad = (byte[])b.Clone();
        bad[100] ^= 0xFF;
        File.WriteAllBytes(damaged, bad);
        File.SetLastWriteTimeUtc(damaged, when);
        var ids = new[] { pack.GetProperty("id").GetInt64(), wrapped.GetProperty("id").GetInt64() };
        (await http.PostAsJsonAsync("/api/v1/archives/extractions/verify", new { ids })).EnsureSuccessStatusCode();
        await WaitAsync(async () => AllExtractions(await Extractions(http, "&state=complete")).All(i => i.TryGetProperty("verifiedAt", out var v) && v.ValueKind == JsonValueKind.Number));

        list = await Extractions(http);
        pack = ByArchive(list, "Downloads/pack.zip");
        Assert.Equal(1, pack.GetProperty("verifiedBad").GetInt32());
        Assert.Equal(2, pack.GetProperty("verifiedOk").GetInt32());
        wrapped = ByArchive(list, "Downloads/wrapped.zip");
        Assert.Equal(2, wrapped.GetProperty("verifiedOk").GetInt32());
        detail = await http.GetFromJsonAsync<JsonElement>($"/api/v1/archives/extractions/{pack.GetProperty("id").GetInt64()}?problems=true");
        Assert.Equal("corrupt", detail.GetProperty("files").EnumerateArray().Single().GetProperty("state").GetString());

        // A rescan keeps the result while the files stay the same; the insight lists only the archive whose copy checked out.
        await app.ScanAsync(http);
        Assert.Equal(1, ByArchive(await Extractions(http), "Downloads/pack.zip").GetProperty("verifiedBad").GetInt32());
        var insights = await http.GetFromJsonAsync<JsonElement>("/api/v1/insights");
        var verified = insights.EnumerateArray().Single(i => i.GetProperty("key").GetString() == "extracted-verified");
        Assert.Equal(1, verified.GetProperty("count").GetInt64());

        // Plan to remove the archive that is fully extracted.
        var plan = await http.PostAsJsonAsync("/api/v1/archives/extractions/plan", new { ids = new[] { wrapped.GetProperty("id").GetInt64() }, remove = "archive", op = "trash" });
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        Assert.Equal(1, (await plan.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetInt32());
        // A partial extraction's archive is kept.
        var refused = await http.PostAsJsonAsync("/api/v1/archives/extractions/plan", new { ids = new[] { partial.GetProperty("id").GetInt64() }, remove = "archive", op = "trash" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task An_archive_extracted_to_several_folders_is_listed_once()
    {
        await using var app = new TestApp();
        var (a, b) = (Bytes(40_000, 21), Bytes(30_000, 22));
        Zip(Path.Combine(app.Library, "Downloads", "twice.zip"), ("twice/a.bin", a), ("twice/b.bin", b));
        Write(Path.Combine(app.Library, "One", "twice", "a.bin"), a);
        Write(Path.Combine(app.Library, "One", "twice", "b.bin"), b);
        Write(Path.Combine(app.Library, "Two", "twice", "a.bin"), a);
        Write(Path.Combine(app.Library, "Two", "twice", "b.bin"), b);

        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var list = await Extractions(http);

        Assert.Equal(1, list.GetProperty("total").GetInt64());
        Assert.Equal(1, list.GetProperty("counts").GetProperty("complete").GetInt64());
        var folders = Group(list, "Downloads/twice.zip").GetProperty("extractions").EnumerateArray()
            .Select(x => x.GetProperty("folder").GetProperty("relPath").GetString()).Order().ToList();
        Assert.Equal(["One/twice", "Two/twice"], folders);
    }

    [Fact]
    public async Task Identical_copies_of_an_archive_are_one_group_and_removing_it_removes_every_copy()
    {
        await using var app = new TestApp();
        var (a, b) = (Bytes(40_000, 23), Bytes(30_000, 24));
        Zip(Path.Combine(app.Library, "Downloads", "same.zip"), ("same/a.bin", a), ("same/b.bin", b));
        Directory.CreateDirectory(Path.Combine(app.Library, "Mirror"));
        File.Copy(Path.Combine(app.Library, "Downloads", "same.zip"), Path.Combine(app.Library, "Mirror", "same.zip"));
        foreach (var dir in new[] { "One", "Two" })
        {
            Write(Path.Combine(app.Library, dir, "same", "a.bin"), a);
            Write(Path.Combine(app.Library, dir, "same", "b.bin"), b);
        }

        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var list = await Extractions(http);

        Assert.Equal(1, list.GetProperty("total").GetInt64());
        var group = list.GetProperty("items")[0];
        Assert.Equal(["Downloads/same.zip", "Mirror/same.zip"], group.GetProperty("copies").EnumerateArray().Select(p => p.GetProperty("relPath").GetString()).ToList());
        var rows = group.GetProperty("extractions").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count); // one row per folder, not per archive copy
        Assert.All(rows, r => Assert.Equal(2, r.GetProperty("ids").GetArrayLength()));

        var ids = rows.SelectMany(r => r.GetProperty("ids").EnumerateArray().Select(i => i.GetInt64())).ToArray();
        var res = await http.PostAsJsonAsync("/api/v1/archives/extractions/plan", new { ids, remove = "archive", op = "trash" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var plan = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, plan.GetProperty("items").GetInt32());

        // Then the extracted copy in One goes into the same plan.
        res = await http.PostAsJsonAsync("/api/v1/archives/extractions/plan", new { ids = new[] { ids[0] }, remove = "extracted", op = "trash", planId = plan.GetProperty("id").GetInt32() });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Equal(4, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").GetInt32());
    }

    [Fact]
    public async Task Tar_archives_are_listed_only_when_enabled()
    {
        await using var app = new TestApp();
        var (a, b) = (Bytes(60_000, 11), Bytes(70_000, 12));
        TarGz(Path.Combine(app.Library, "Downloads", "bundle.tar.gz"), ("bundle/a.bin", a), ("bundle/b.bin", b));
        Write(Path.Combine(app.Library, "Out", "bundle", "a.bin"), a);
        Write(Path.Combine(app.Library, "Out", "bundle", "b.bin"), b);

        using var http = app.CreateClient();
        await app.ScanAsync(http);
        Assert.Equal(0, (await Extractions(http)).GetProperty("total").GetInt64());

        var settings = await http.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings.GetRawText())!;
        body["listStreamedArchives"] = JsonSerializer.SerializeToElement(true);
        (await http.PutAsJsonAsync("/api/v1/settings", body)).EnsureSuccessStatusCode();
        await app.ScanAsync(http);

        var x = ByArchive(await Extractions(http), "Downloads/bundle.tar.gz");
        Assert.Equal("complete", x.GetProperty("state").GetString());
        Assert.Equal("Out/bundle", x.GetProperty("folder").GetProperty("relPath").GetString());

        // CRC32s computed while listing make the check work without reading the archive again.
        (await http.PostAsJsonAsync("/api/v1/archives/extractions/verify", new { ids = new[] { x.GetProperty("id").GetInt64() } })).EnsureSuccessStatusCode();
        await WaitAsync(async () => ByArchive(await Extractions(http), "Downloads/bundle.tar.gz").GetProperty("verifiedOk").GetInt32() == 2);
    }

    [Fact]
    public async Task Ignoring_a_folder_removes_it_from_results_at_once()
    {
        await using var app = new TestApp();
        var content = Bytes(5000, 13);
        Write(Path.Combine(app.Library, "Keep", "photo.jpg"), content);
        Write(Path.Combine(app.Library, "Snapshots", "old", "photo.jpg"), content);
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        Assert.Contains("Snapshots/old/photo.jpg", await DuplicatePaths(http));

        (await http.PostAsJsonAsync("/api/v1/ignore", new { rootId = 1, relPath = "Snapshots" })).EnsureSuccessStatusCode();
        await WaitAsync(async () => !(await DuplicatePaths(http)).Contains("Snapshots/old/photo.jpg"));

        using var c = app.Services.GetRequiredService<DbSetup>().Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM Entries WHERE RelPath = 'Snapshots' OR RelPath LIKE 'Snapshots/%'";
        Assert.Equal(0L, (long)q.ExecuteScalar()!);
        q.CommandText = "SELECT TotalSize FROM Entries WHERE RelPath = ''";
        var rootSize = (long)q.ExecuteScalar()!;
        q.CommandText = "SELECT COALESCE(SUM(TotalSize), 0) FROM Entries WHERE Depth = 1";
        Assert.Equal(Convert.ToInt64(q.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture), rootSize);
    }

    private static async Task<List<string>> DuplicatePaths(HttpClient http)
    {
        var d = await http.GetFromJsonAsync<JsonElement>("/api/v1/duplicates?limit=500");
        return [.. d.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("members").EnumerateArray()).Select(m => m.GetProperty("relPath").GetString()!)];
    }

    [Fact]
    public async Task Repair_extracts_damaged_and_missing_files_again_and_undo_puts_everything_back()
    {
        await using var app = new TestApp();
        var lib = app.Library;
        // x fine, y cut short, w damaged in place (same size and date), z never written (nor its folder).
        var (x, y, w, z) = (Bytes(1_500_000, 21), Bytes(200_000, 22), Bytes(100_000, 23), Bytes(90_000, 24));
        Zip(Path.Combine(lib, "Downloads", "fix.zip"), ("x.bin", x), ("y.bin", y), ("w.bin", w), ("deep/z.bin", z));
        Write(Path.Combine(lib, "Fix", "x.bin"), x);
        Write(Path.Combine(lib, "Fix", "y.bin"), y[..50_000]);
        var badW = (byte[])w.Clone();
        badW[10] ^= 0xFF;
        Write(Path.Combine(lib, "Fix", "w.bin"), badW);
        using var http = app.CreateClient();
        await app.ScanAsync(http);
        var id = ByArchive(await Extractions(http), "Downloads/fix.zip").GetProperty("id").GetInt64();
        (await http.PostAsJsonAsync("/api/v1/archives/extractions/verify", new { ids = new[] { id } })).EnsureSuccessStatusCode();
        await WaitAsync(async () => ByArchive(await Extractions(http), "Downloads/fix.zip").GetProperty("verifiedBad").GetInt32() == 1);

        // Nothing chosen is refused; the generic plan endpoint does not extract.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v1/archives/extractions/repair", new { ids = new[] { id }, damaged = false, missing = false })).StatusCode);
        var xId = (await http.GetFromJsonAsync<JsonElement>("/api/v1/search?q=Fix/x.bin")).GetProperty("items")[0].GetProperty("id").GetInt64();
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/api/v1/plans", new { op = "extract", entryIds = new[] { xId } }, TestApp.Json)).StatusCode);

        // w is kept next to itself as ~w.bin; y goes to the trash.
        var res = await http.PostAsJsonAsync("/api/v1/archives/extractions/repair", new { ids = new[] { id }, paths = new[] { "w.bin" }, keep = "{relDir}/~{name}" });
        res.EnsureSuccessStatusCode();
        var planId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        res = await http.PostAsJsonAsync("/api/v1/archives/extractions/repair", new { ids = new[] { id }, planId });
        res.EnsureSuccessStatusCode();
        var plan = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{planId}");
        var items = plan.GetProperty("page").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("sourcePath").GetString()!);
        Assert.Equal(["Fix/deep/z.bin", "Fix/w.bin", "Fix/y.bin"], items.Keys.Order());
        Assert.All(items.Values, i => Assert.Equal("extract", i.GetProperty("op").GetString()));
        Assert.All(items.Values, i => Assert.True(i.GetProperty("included").GetBoolean()));
        Assert.Equal("deep/z.bin", items["Fix/deep/z.bin"].GetProperty("targetPath").GetString());
        // Adding the files again replaced w's item: it lost its kept path, like any item planned again.
        Assert.False(items["Fix/w.bin"].TryGetProperty("keepPath", out var kp) && kp.ValueKind == JsonValueKind.String);

        // Plan w's keep again on its own, so one damaged file is kept and one trashed.
        (await http.PostAsJsonAsync("/api/v1/archives/extractions/repair", new { ids = new[] { id }, paths = new[] { "w.bin" }, keep = "{relDir}/~{name}", planId })).EnsureSuccessStatusCode();

        (await http.PostAsync($"/api/v1/plans/{planId}/apply", null)).EnsureSuccessStatusCode();
        plan = await app.WaitPlanAsync(http, planId, "done", "failed");
        Assert.Equal(3, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.Equal(y, File.ReadAllBytes(Path.Combine(lib, "Fix", "y.bin")));
        Assert.Equal(w, File.ReadAllBytes(Path.Combine(lib, "Fix", "w.bin")));
        Assert.Equal(z, File.ReadAllBytes(Path.Combine(lib, "Fix", "deep", "z.bin")));
        Assert.Equal(badW, File.ReadAllBytes(Path.Combine(lib, "Fix", "~w.bin")));
        Assert.Equal(50_000, new FileInfo(Path.Combine(lib, ".analyze-trash", planId.ToString(), "Fix", "y.bin")).Length);
        Assert.False(Directory.Exists(Path.Combine(lib, ".analyze-trash", planId.ToString(), ".extracting")));
        Assert.Equal(1, plan.GetProperty("summary").GetProperty("trashItems").GetInt32());
        Assert.Equal("complete", ByArchive(await Extractions(http), "Downloads/fix.zip").GetProperty("state").GetString());

        (await http.PostAsync($"/api/v1/plans/{planId}/undo", null)).EnsureSuccessStatusCode();
        await app.WaitPlanAsync(http, planId, "undone");
        Assert.Equal(50_000, new FileInfo(Path.Combine(lib, "Fix", "y.bin")).Length);
        Assert.Equal(badW, File.ReadAllBytes(Path.Combine(lib, "Fix", "w.bin")));
        Assert.False(File.Exists(Path.Combine(lib, "Fix", "~w.bin")));
        Assert.False(Directory.Exists(Path.Combine(lib, "Fix", "deep")));
        Assert.True(File.Exists(Path.Combine(lib, "Fix", "x.bin")));
    }

    [Fact]
    public async Task Repair_reads_a_tar_once_for_all_its_missing_files()
    {
        await using var app = new TestApp();
        var (a, b, c, d) = (Bytes(60_000, 31), Bytes(70_000, 32), Bytes(80_000, 33), Bytes(50_000, 34));
        TarGz(Path.Combine(app.Library, "Downloads", "set.tar.gz"), ("set/a.bin", a), ("set/d.bin", d), ("set/b.bin", b), ("set/more/c.bin", c));
        Write(Path.Combine(app.Library, "Out", "set", "c.bin"), c[..1]); // another file: the archive's c.bin is in more/
        Write(Path.Combine(app.Library, "Out", "set", "a.bin"), a);
        Write(Path.Combine(app.Library, "Out", "set", "d.bin"), d);
        using var http = app.CreateClient();
        var settings = await http.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settings.GetRawText())!;
        body["listStreamedArchives"] = JsonSerializer.SerializeToElement(true);
        (await http.PutAsJsonAsync("/api/v1/settings", body)).EnsureSuccessStatusCode();
        await app.ScanAsync(http);
        var x = ByArchive(await Extractions(http), "Downloads/set.tar.gz");
        Assert.Equal("partial", x.GetProperty("state").GetString());

        var res = await http.PostAsJsonAsync("/api/v1/archives/extractions/repair", new { ids = new[] { x.GetProperty("id").GetInt64() }, damaged = false });
        res.EnsureSuccessStatusCode();
        var planId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        (await http.PostAsync($"/api/v1/plans/{planId}/apply", null)).EnsureSuccessStatusCode();
        var plan = await app.WaitPlanAsync(http, planId, "done", "failed");
        Assert.Equal(2, plan.GetProperty("summary").GetProperty("done").GetInt32());
        Assert.Equal(b, File.ReadAllBytes(Path.Combine(app.Library, "Out", "set", "b.bin")));
        Assert.Equal(c, File.ReadAllBytes(Path.Combine(app.Library, "Out", "set", "more", "c.bin")));
        Assert.Equal(1, new FileInfo(Path.Combine(app.Library, "Out", "set", "c.bin")).Length);
        Assert.Equal("complete", ByArchive(await Extractions(http), "Downloads/set.tar.gz").GetProperty("state").GetString());
    }

    private static async Task WaitAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await done())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition not reached");
    }
}
