using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ANalyze.Ai;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ANalyze.Tests;

/// <summary>Runs the real app against a generated temporary library and data folder.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public string Library { get; } = Path.Combine(Path.GetTempPath(), "analyze-test-" + Guid.NewGuid().ToString("N")[..8], "lib");
    public string Data { get; }
    public bool ReadOnly { get; init; }
    public Func<IList<ChatMessage>, string>? AiReply { get; init; }

    /// <summary>Extra service registrations (e.g. a fake integration).</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public TestApp()
    {
        Data = Path.Combine(Path.GetDirectoryName(Library)!, "data");
        Directory.CreateDirectory(Library);
        Samples.Create(Library);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ANalyze:Roots:0", Library);
        builder.UseSetting("ANalyze:DataDir", Data);
        builder.UseSetting("ANalyze:ReadOnly", ReadOnly ? "true" : "false");
        builder.ConfigureServices(s =>
        {
            if (AiReply is not null)
            {
                s.RemoveAll<IAiClientFactory>();
                s.AddSingleton<IAiClientFactory>(new FakeAiFactory(AiReply));
            }

            ExtraServices?.Invoke(s);
        });
    }

    public async Task ScanAsync(HttpClient http)
    {
        var before = await LastScanId(http);
        (await http.PostAsJsonAsync("/api/v1/scan", new { })).EnsureSuccessStatusCode();
        for (var i = 0; i < 200; i++)
        {
            await Task.Delay(100);
            var scans = await http.GetFromJsonAsync<JsonElement>("/api/v1/scans?limit=1");
            if (scans.GetArrayLength() > 0 && scans[0].GetProperty("id").GetInt64() > before && scans[0].GetProperty("status").GetString() is "completed")
            {
                return;
            }
        }

        throw new TimeoutException("Scan did not finish");
    }

    private static async Task<long> LastScanId(HttpClient http)
    {
        var scans = await http.GetFromJsonAsync<JsonElement>("/api/v1/scans?limit=1");
        return scans.GetArrayLength() > 0 ? scans[0].GetProperty("id").GetInt64() : 0;
    }

    public async Task<JsonElement> WaitPlanAsync(HttpClient http, int id, params string[] states)
    {
        for (var i = 0; i < 200; i++)
        {
            await Task.Delay(100);
            var p = await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
            if (states.Contains(p.GetProperty("plan").GetProperty("status").GetString()))
            {
                // Rescans triggered by the plan run right after; give them a moment.
                await Task.Delay(500);
                return await http.GetFromJsonAsync<JsonElement>($"/api/v1/plans/{id}");
            }
        }

        throw new TimeoutException("Plan did not finish");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            Directory.Delete(Path.GetDirectoryName(Library)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeAiFactory(Func<IList<ChatMessage>, string> reply) : IAiClientFactory
    {
        public IChatClient Create(Infrastructure.Settings.AiSettings settings) => new FakeChat(reply);
    }

    private sealed class FakeChat(Func<IList<ChatMessage>, string> reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(messages.ToList()))));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>A small tree with known duplicates, hard links, junk, empty folders and a broken link.</summary>
public static class Samples
{
    public static void Create(string root)
    {
        void Write(string rel, byte[] content)
        {
            var p = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, content);
        }

        var photo = Bytes(300_000, 1);
        Write("Photos/2021/IMG_0001.jpg", photo);
        Write("Backup/Photos/IMG_0001.jpg", photo);
        Write("Photos/2021/IMG_0001 (1).jpg", photo);
        Write("Photos/2021/IMG_0002.jpg", Bytes(300_000, 2));
        Write("Movies/Heat (1995)/Heat.mkv", Bytes(2_000_000, 3));
        Write("proj/node_modules/lib/index.js", "module.exports = 1\n"u8.ToArray());
        Write("proj/README.md", "# readme\n"u8.ToArray());
        Write("Downloads/file.iso.part", Bytes(10_000, 4));
        Write("Thumbs.db", []);
        Write("mystery.qqq", Bytes(1_000, 5));
        Directory.CreateDirectory(Path.Combine(root, "empty/inner"));
        if (OperatingSystem.IsLinux())
        {
            Infrastructure.Native.CreateHardLink(Path.Combine(root, "Photos/2021/IMG_0002.jpg"), Path.Combine(root, "Photos/link.jpg"));
            File.CreateSymbolicLink(Path.Combine(root, "broken"), "/nonexistent/target");
        }
    }

    private static byte[] Bytes(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }
}
