using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ANalyze.Actions;
using ANalyze.Ai;
using ANalyze.Infrastructure;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Integrations;
using ANalyze.Integrations.Arr;
using ANalyze.Integrations.Backup;
using ANalyze.Integrations.Docker;
using ANalyze.Integrations.Downloads;
using ANalyze.Integrations.Jellyfin;
using ANalyze.Integrations.Photos;
using ANalyze.Integrations.Plex;
using ANalyze.Preview;
using ANalyze.Scanner;
using ANalyze.Web.Auth;
using ANalyze.Web.Endpoints;
using ANalyze.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

if (args.Contains("--healthcheck"))
{
    // Used by the container HEALTHCHECK (the runtime image has no curl).
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
    try
    {
        var res = await http.GetAsync($"http://127.0.0.1:{port}/health");
        return res.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

var analyze = builder.Configuration.GetSection(AnalyzeOptions.Section).Get<AnalyzeOptions>() ?? new AnalyzeOptions();
analyze.DataDir = Path.GetFullPath(analyze.DataDir, builder.Environment.ContentRootPath);
if (analyze.Roots.Count == 0 && Directory.Exists("/library"))
{
    analyze.Roots.Add("/library");
}

builder.Services.Configure<AnalyzeOptions>(builder.Configuration.GetSection(AnalyzeOptions.Section));
builder.Services.PostConfigure<AnalyzeOptions>(o =>
{
    o.DataDir = analyze.DataDir;
    o.Roots = analyze.Roots;
});
Directory.CreateDirectory(analyze.DataDir);
Directory.CreateDirectory(analyze.ThumbsDir);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(analyze.DataDir, "logs", "analyze-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14));

builder.Services.AddAnalyzeInfrastructure(analyze.DatabasePath);
builder.Services.AddAnalyzeScanner();
builder.Services.AddAnalyzeActions();
builder.Services.AddAnalyzeAi();
// Integration plugins: add new ones here (each is a project implementing IIntegration).
builder.Services.AddIntegrations();
builder.Services.AddPlexIntegration();
builder.Services.AddMediaServerIntegrations();
builder.Services.AddArrIntegrations();
builder.Services.AddDownloadIntegrations();
builder.Services.AddPhotoIntegrations();
builder.Services.AddDockerIntegration();
builder.Services.AddBackupIntegration();
builder.Services.AddSingleton(sp => new PreviewService(analyze.ThumbsDir, sp.GetRequiredService<ILogger<PreviewService>>()));
builder.Services.AddSingleton<IJobObserver, JobHubObserver>();
builder.Services.AddMemoryCache();
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddAnalyzeAuth(analyze);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);
builder.Services.AddDataProtection()
    .SetApplicationName("analyze")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(analyze.DataDir, "keys")));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(analyze.AutoMigrate);
}

if (string.IsNullOrEmpty(analyze.Password))
{
    app.Logger.LogWarning("ANalyze__Password is not set: the UI and API are open to anyone who can reach this port");
}

app.UseForwardedHeaders();
app.UseResponseCompression();
app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
    ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error : Serilog.Events.LogEventLevel.Debug);
app.UseDefaultFiles();
app.MapStaticAssets();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
var secured = api.MapGroup("").RequireAuthorization(AuthSetup.Policy);
secured.MapSystemEndpoints();
secured.MapBrowseEndpoints();
secured.MapFileEndpoints();
secured.MapDuplicateEndpoints();
secured.MapRuleEndpoints();
secured.MapPlanEndpoints();
secured.MapInsightEndpoints();
secured.MapAiEndpoints();
secured.MapIntegrationEndpoints();
// Read by another ANalyze that backs up this one (its "ANalyze parent" integration).
secured.MapBackupExport();
app.MapHub<JobHub>("/hubs/jobs").RequireAuthorization(AuthSetup.Policy);

// Unknown API paths must not fall through to the SPA.
app.Map("/api/{**rest}", () => Results.NotFound());

app.MapFallbackToFile("index.html");

await app.RunAsync();
return 0;

public partial class Program;
