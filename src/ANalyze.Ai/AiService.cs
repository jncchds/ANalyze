using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ANalyze.Domain;
using ANalyze.Infrastructure.Data;
using ANalyze.Infrastructure.Jobs;
using ANalyze.Infrastructure.Roots;
using ANalyze.Infrastructure.Settings;
using ANalyze.Scanner;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace ANalyze.Ai;

public interface IAiClientFactory
{
    IChatClient Create(AiSettings settings);
}

/// <summary>Any OpenAI-compatible server: OpenAI, Ollama (/v1), LM Studio, vLLM, OpenRouter…</summary>
public sealed class OpenAiClientFactory(SecretProtector secrets) : IAiClientFactory
{
    public IChatClient Create(AiSettings s)
    {
        var key = secrets.Unprotect(s.ApiKey);
        var client = new OpenAIClient(new ApiKeyCredential(string.IsNullOrEmpty(key) ? "none" : key),
            new OpenAIClientOptions { Endpoint = new Uri(s.BaseUrl.TrimEnd('/')), NetworkTimeout = TimeSpan.FromMinutes(5) });
        return client.GetChatClient(s.Model).AsIChatClient();
    }
}

public sealed record AiRunInfo(string Kind, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, long Files, long Applied, long Suggested, long Cached,
    long Failed, long InputTokens, long OutputTokens, string? Error);

public sealed record AiTestResult(bool Ok, string? Reply, long Milliseconds, string? Error);

/// <summary>Classifies uncategorized files and proposes rules through an OpenAI-compatible model.</summary>
public sealed class AiService(
    IAiClientFactory clients,
    IDbContextFactory<AnalyzeDbContext> dbf,
    SqliteSetup sqlite,
    SettingsStore settings,
    RootRegistry roots,
    Categorizer categorizer,
    JobBoard board,
    JobLock jobLock,
    ILogger<AiService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentQueue<AiRunInfo> _history = new();
    private CancellationTokenSource? _running;

    public IReadOnlyList<AiRunInfo> History => _history.Reverse().ToList();

    public bool IsRunning => _running is not null;

    public bool Cancel()
    {
        var c = _running;
        c?.Cancel();
        return c is not null;
    }

    public async Task<AiTestResult> TestAsync(AiSettings? overrides, CancellationToken ct)
    {
        var s = overrides ?? settings.Current.Ai;
        var sw = Stopwatch.StartNew();
        try
        {
            var client = clients.Create(s);
            var res = await client.GetResponseAsync([new ChatMessage(ChatRole.User, Prompts.TestPrompt)], new ChatOptions { MaxOutputTokens = 20 }, ct);
            return new AiTestResult(true, res.Text.Trim(), sw.ElapsedMilliseconds, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new AiTestResult(false, null, sw.ElapsedMilliseconds, e.Message);
        }
    }

    /// <summary>Starts a classification run in the background. Returns false when one is already running.</summary>
    public bool StartClassify(int? limit, int? rootId)
    {
        if (!TryBegin(out var cts))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // The model is asked without holding the job lock; each answer is stored under a short shared hold (StoreAsync).
                await ClassifyAsync(limit, rootId, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                log.LogError(e, "AI classification failed");
            }
            finally
            {
                End();
            }
        });
        return true;
    }

    public bool StartSuggest()
    {
        if (!TryBegin(out var cts))
        {
            return false;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SuggestRulesAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                log.LogError(e, "AI rule suggestion failed");
            }
            finally
            {
                End();
            }
        });
        return true;
    }

    private bool TryBegin(out CancellationTokenSource cts)
    {
        cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _running, cts, null) is not null)
        {
            cts.Dispose();
            return false;
        }

        return true;
    }

    private void End()
    {
        var c = Interlocked.Exchange(ref _running, null);
        c?.Dispose();
    }

    private void Remember(AiRunInfo info)
    {
        _history.Enqueue(info);
        while (_history.Count > 30)
        {
            _history.TryDequeue(out _);
        }
    }

    private sealed record Candidate(long Id, int RootId, string RelPath, string Name, string? Ext, string? Mime, FileKind Kind, long Size, long MTime, string? FullHash)
    {
        public string CacheKey => FullHash is not null ? "h:" + FullHash : $"p:{RootId}:{RelPath}:{Size}:{MTime}";
    }

    private sealed record ClassifyResult(int Id, string Category, List<string>? Tags, double Confidence, string? Reason);

    private sealed record ClassifyResponse(List<ClassifyResult>? Results);

    public async Task<AiRunInfo> ClassifyAsync(int? limit, int? rootId, CancellationToken ct)
    {
        var s = settings.Current.Ai;
        var started = DateTimeOffset.UtcNow;
        long files = 0, applied = 0, suggested = 0, cached = 0, failed = 0, inTok = 0, outTok = 0;
        string? error = null;

        void Report(string state, string? msg = null) => board.Report(new JobProgress
        {
            Kind = JobKinds.Ai,
            State = state,
            Phase = "classify",
            Title = "AI classification",
            Done = files,
            Total = limit ?? s.MaxFilesPerRun,
            Message = msg,
            StartedAt = started,
            FinishedAt = state == JobStates.Running ? null : DateTimeOffset.UtcNow,
            Counters = new Dictionary<string, long> { ["applied"] = applied, ["suggested"] = suggested, ["cached"] = cached, ["failed"] = failed, ["inputTokens"] = inTok, ["outputTokens"] = outTok },
        }, force: state != JobStates.Running);

        Report(JobStates.Running);
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var categories = await db.Categories.AsNoTracking().OrderBy(c => c.SortOrder).ToListAsync(ct);
            var max = Math.Clamp(limit ?? s.MaxFilesPerRun, 1, 100_000);

            // Biggest unexplained files first: they matter most for disk usage.
            var q = db.Entries.AsNoTracking().Where(e => !e.IsDir && !e.IsSymlink && e.CategorySource == CategorySource.None && e.Size > 0);
            if (rootId is { } rid)
            {
                q = q.Where(e => e.RootId == rid);
            }

            var dismissed = db.AiResults.Where(r => r.Dismissed).Select(r => r.EntryId);
            var candidates = await q.Where(e => !dismissed.Contains(e.Id))
                .OrderByDescending(e => e.Size)
                .Take(max)
                .Select(e => new Candidate(e.Id, e.RootId, e.RelPath, e.Name, e.Ext, e.Mime, e.Kind, e.Size, e.MTime, e.FullHash))
                .ToListAsync(ct);

            // Reuse earlier answers for identical content or unchanged files.
            var keys = candidates.Select(c => c.CacheKey).Distinct().ToList();
            var known = new Dictionary<string, AiResult>();
            foreach (var chunk in keys.Chunk(500))
            {
                foreach (var r in await db.AiResults.AsNoTracking().Where(r => chunk.Contains(r.CacheKey) && !r.Dismissed).ToListAsync(ct))
                {
                    known.TryAdd(r.CacheKey, r);
                }
            }

            var toAsk = new List<Candidate>();
            foreach (var c in candidates)
            {
                if (known.TryGetValue(c.CacheKey, out var hit))
                {
                    if (await StoreAsync(c, hit.Category, hit.Tags, hit.Confidence, hit.Reason, hit.Model, categories, s, ct))
                    {
                        applied++;
                    }
                    else
                    {
                        suggested++;
                    }

                    cached++;
                    files++;
                }
                else
                {
                    toAsk.Add(c);
                }
            }

            Report(JobStates.Running);
            var client = clients.Create(s);
            var system = Prompts.ClassifySystem(categories.Select(c => (c.Name, c.Description)), s.AllowNewCategories, s.ExtraInstructions);
            var batches = toAsk.Chunk(Math.Clamp(s.BatchSize, 1, 100)).ToList();
            var gate = new SemaphoreSlim(1, 1);
            await Parallel.ForEachAsync(batches, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(s.MaxConcurrency, 1, 8), CancellationToken = ct }, async (batch, token) =>
            {
                var payload = batch.Select((c, i) => (object)new
                {
                    id = i + 1,
                    path = c.RelPath,
                    size = HumanSize(c.Size),
                    type = c.Mime ?? c.Kind.ToString(),
                    modified = DateTimeOffset.FromUnixTimeSeconds(c.MTime).ToString("yyyy-MM-dd"),
                    snippet = s.Privacy == AiPrivacy.TextSnippet ? Snippet(c) : null,
                }).ToList();
                try
                {
                    var (response, usage) = await AskAsync<ClassifyResponse>(client, s, system, Prompts.ClassifyUser(payload), Prompts.ClassifySchema, "classification", token);
                    await gate.WaitAsync(token);
                    try
                    {
                        inTok += usage?.InputTokenCount ?? 0;
                        outTok += usage?.OutputTokenCount ?? 0;
                        foreach (var r in response?.Results ?? [])
                        {
                            if (r.Id < 1 || r.Id > batch.Length || string.IsNullOrWhiteSpace(r.Category))
                            {
                                continue;
                            }

                            if (await StoreAsync(batch[r.Id - 1], r.Category.Trim(), r.Tags ?? [], Math.Clamp(r.Confidence, 0, 1), r.Reason, s.Model, categories, s, token))
                            {
                                applied++;
                            }
                            else
                            {
                                suggested++;
                            }
                        }

                        files += batch.Length;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Interlocked.Add(ref failed, batch.Length);
                    error = e.Message;
                    log.LogWarning("AI batch failed: {Error}", e.Message);
                }

                Report(JobStates.Running);
            });
        }
        catch (OperationCanceledException)
        {
            error = "Cancelled";
        }
        catch (Exception e)
        {
            error = e.Message;
            log.LogError(e, "AI classification failed");
        }

        // Directory totals per category are computed on the fly, so nothing else needs refreshing.
        var info = new AiRunInfo("classify", started, DateTimeOffset.UtcNow, files, applied, suggested, cached, failed, inTok, outTok, error);
        Remember(info);
        Report(error is null ? JobStates.Completed : error == "Cancelled" ? JobStates.Cancelled : JobStates.Failed,
            error ?? $"{applied} categorized, {suggested} need review");
        return info;
    }

    /// <summary>Saves the answer; applies it when confident enough. Returns true when applied.</summary>
    private async Task<bool> StoreAsync(Candidate c, string category, List<string> tags, double confidence, string? reason, string model,
        List<Category> categories, AiSettings s, CancellationToken ct)
    {
        // Shared hold: a walk or plan cannot remove the entry while the answer is stored; one may have done so while the model was asked.
        using var lease = await jobLock.AcquireSharedAsync("ai", ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        if (!await db.Entries.AnyAsync(e => e.Id == c.Id, ct))
        {
            return false;
        }

        var name = category.StartsWith("new:", StringComparison.OrdinalIgnoreCase) ? category[4..].Trim() : category;
        var existing = await db.AiResults.FirstOrDefaultAsync(r => r.EntryId == c.Id && !r.Applied, ct);
        var row = existing ?? new AiResult { CacheKey = c.CacheKey, EntryId = c.Id, Model = model, Category = name, CreatedAt = DateTimeOffset.UtcNow };
        row.CacheKey = c.CacheKey;
        row.Category = name;
        row.Tags = tags.Take(6).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToList();
        row.Confidence = confidence;
        row.Reason = reason?.Length > 300 ? reason[..300] : reason;
        row.Model = model;
        if (existing is null)
        {
            db.AiResults.Add(row);
        }

        var apply = confidence >= s.MinConfidence;
        if (apply)
        {
            var cat = categories.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (cat is null && s.AllowNewCategories && name.Length is > 0 and <= 40)
            {
                cat = await db.Categories.FirstOrDefaultAsync(x => x.Name == name, ct);
                if (cat is null)
                {
                    cat = new Category { Name = name, Color = ColorFor(name), Description = "Created by AI", SortOrder = categories.Count + 100 };
                    db.Categories.Add(cat);
                    await db.SaveChangesAsync(ct);
                }

                lock (categories)
                {
                    if (categories.All(x => x.Id != cat.Id))
                    {
                        categories.Add(cat);
                    }
                }
            }

            if (cat is null)
            {
                apply = false;
            }
            else
            {
                await db.Entries.Where(e => e.Id == c.Id && e.CategorySource == CategorySource.None)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.CategoryId, cat.Id).SetProperty(e => e.CategorySource, CategorySource.Ai), ct);
                row.Applied = true;
            }
        }

        await db.SaveChangesAsync(ct);
        return apply;
    }

    /// <summary>Accepts a low-confidence answer the user reviewed.</summary>
    public async Task<bool> AcceptResultAsync(long resultId, string? categoryOverride, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var r = await db.AiResults.FirstOrDefaultAsync(x => x.Id == resultId, ct);
        if (r?.EntryId is not { } entryId)
        {
            return false;
        }

        var name = string.IsNullOrWhiteSpace(categoryOverride) ? r.Category : categoryOverride.Trim();
        var cat = await db.Categories.FirstOrDefaultAsync(c => c.Name == name, ct);
        if (cat is null)
        {
            cat = new Category { Name = name, Color = ColorFor(name), Description = "Created by AI" };
            db.Categories.Add(cat);
            await db.SaveChangesAsync(ct);
        }

        await db.Entries.Where(e => e.Id == entryId && e.CategorySource != CategorySource.Manual)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.CategoryId, cat.Id).SetProperty(e => e.CategorySource, CategorySource.Ai), ct);
        r.Applied = true;
        r.Category = name;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DismissResultAsync(long resultId, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        return await db.AiResults.Where(r => r.Id == resultId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Dismissed, true), ct) > 0;
    }

    private sealed record SuggestClause(string Field, string Op, string Value, bool Negate);

    private sealed record SuggestRule(string Name, string Category, string? Match, List<SuggestClause>? Clauses, List<string>? Tags, string? Rationale);

    private sealed record SuggestResponse(List<SuggestRule>? Rules);

    public async Task<AiRunInfo> SuggestRulesAsync(CancellationToken ct)
    {
        var s = settings.Current.Ai;
        var started = DateTimeOffset.UtcNow;
        long inTok = 0, outTok = 0, added = 0;
        string? error = null;
        board.Report(new JobProgress { Kind = JobKinds.Ai, State = JobStates.Running, Phase = "suggest", Title = "AI rule suggestions", StartedAt = started }, force: true);
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var categories = await db.Categories.AsNoTracking().Select(c => c.Name).ToListAsync(ct);
            var rules = await db.Rules.AsNoTracking().Select(r => r.Name).ToListAsync(ct);
            var summary = BuildSummary();
            if (summary is null)
            {
                error = "Everything is categorized already";
            }
            else
            {
                var client = clients.Create(s);
                var (resp, usage) = await AskAsync<SuggestResponse>(client, s, Prompts.SuggestSystem(categories, rules), Prompts.SuggestUser(summary), Prompts.SuggestSchema, "rules", ct);
                inTok = usage?.InputTokenCount ?? 0;
                outTok = usage?.OutputTokenCount ?? 0;
                foreach (var r in resp?.Rules ?? [])
                {
                    var condition = ToCondition(r);
                    if (condition is null || string.IsNullOrWhiteSpace(r.Name) || string.IsNullOrWhiteSpace(r.Category))
                    {
                        continue;
                    }

                    var (count, bytes) = DryRun(condition);
                    if (count == 0)
                    {
                        continue;
                    }

                    db.AiRuleSuggestions.Add(new AiRuleSuggestion
                    {
                        Name = r.Name.Trim(),
                        CategoryName = r.Category.Trim().Replace("new:", "", StringComparison.OrdinalIgnoreCase),
                        Condition = condition,
                        Tags = r.Tags?.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Take(6).ToList() ?? [],
                        Rationale = r.Rationale,
                        MatchCount = count,
                        MatchBytes = bytes,
                        Status = SuggestionStatus.Pending,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
                    added++;
                }

                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            error = "Cancelled";
        }
        catch (Exception e)
        {
            error = e.Message;
            log.LogError(e, "AI rule suggestion failed");
        }

        var info = new AiRunInfo("suggest", started, DateTimeOffset.UtcNow, 0, added, 0, 0, 0, inTok, outTok, error);
        Remember(info);
        board.Report(new JobProgress
        {
            Kind = JobKinds.Ai,
            State = error is null ? JobStates.Completed : JobStates.Failed,
            Phase = "suggest",
            Title = "AI rule suggestions",
            Message = error ?? $"{added} rule(s) suggested",
            StartedAt = started,
            FinishedAt = DateTimeOffset.UtcNow,
        }, force: true);
        return info;
    }

    /// <summary>Creates a rule from a suggestion (and its category if needed), then re-applies rules.</summary>
    public async Task<Rule?> AcceptSuggestionAsync(int id, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var sug = await db.AiRuleSuggestions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (sug is null)
        {
            return null;
        }

        var cat = await db.Categories.FirstOrDefaultAsync(c => c.Name == sug.CategoryName, ct);
        if (cat is null)
        {
            cat = new Category { Name = sug.CategoryName, Color = ColorFor(sug.CategoryName), Description = "Suggested by AI" };
            db.Categories.Add(cat);
            await db.SaveChangesAsync(ct);
        }

        // AI rules are specific, so they go before the generic built-in "by kind" rules.
        var firstCategoryRule = await db.Rules.Where(r => r.CategoryId != null).OrderBy(r => r.Order).Select(r => (int?)r.Order).FirstOrDefaultAsync(ct) ?? 0;
        await db.Rules.Where(r => r.Order >= firstCategoryRule).ExecuteUpdateAsync(u => u.SetProperty(r => r.Order, r => r.Order + 1), ct);
        var rule = new Rule
        {
            Name = sug.Name,
            Order = firstCategoryRule,
            Condition = sug.Condition,
            CategoryId = cat.Id,
            Tags = sug.Tags,
            Source = RuleSource.Ai,
            Description = sug.Rationale,
        };
        db.Rules.Add(rule);
        sug.Status = SuggestionStatus.Accepted;
        await db.SaveChangesAsync(ct);
        await categorizer.RunAsync(Scope.Everything, null, ct);
        return rule;
    }

    private static RuleCondition? ToCondition(SuggestRule r)
    {
        var clauses = new List<RuleClause>();
        foreach (var c in r.Clauses ?? [])
        {
            if (!Enum.TryParse<RuleField>(c.Field, true, out var f) || !Enum.TryParse<RuleOp>(c.Op, true, out var op) || string.IsNullOrWhiteSpace(c.Value))
            {
                return null;
            }

            clauses.Add(new RuleClause { Field = f, Op = op, Value = c.Value.Trim(), Negate = c.Negate });
        }

        return clauses.Count == 0 ? null : new RuleCondition
        {
            Match = string.Equals(r.Match, "any", StringComparison.OrdinalIgnoreCase) ? MatchMode.Any : MatchMode.All,
            Clauses = clauses,
        };
    }

    /// <summary>How many uncategorized files a condition matches.</summary>
    public (int Count, long Bytes) DryRun(RuleCondition condition)
    {
        var match = RuleEngine.CompileCondition(condition);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var conn = sqlite.Open();
        using var q = conn.CreateCommand();
        q.CommandText = "SELECT Name, RelPath, Ext, Mime, Kind, Size, MTime, ATime, Depth FROM Entries WHERE IsDir = 0 AND CategoryId IS NULL";
        using var r = q.ExecuteReader();
        var count = 0;
        long bytes = 0;
        while (r.Read())
        {
            var f = new EntryFacts(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                (FileKind)r.GetInt32(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt32(8));
            if (match(f, now))
            {
                count++;
                bytes += f.Size;
            }
        }

        return (count, bytes);
    }

    private object? BuildSummary()
    {
        using var conn = sqlite.Open();
        long total, bytes;
        using (var c = conn.CreateCommand())
        {
            c.CommandText = "SELECT COUNT(*), COALESCE(SUM(Size),0) FROM Entries WHERE IsDir = 0 AND CategoryId IS NULL";
            using var r = c.ExecuteReader();
            r.Read();
            total = r.GetInt64(0);
            bytes = r.GetInt64(1);
        }

        if (total == 0)
        {
            return null;
        }

        var exts = new List<object>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = """
                SELECT COALESCE(Ext, ''), COUNT(*), SUM(Size) FROM Entries WHERE IsDir = 0 AND CategoryId IS NULL
                GROUP BY Ext ORDER BY SUM(Size) DESC LIMIT 40
                """;
            using var r = c.ExecuteReader();
            var rows = new List<(string Ext, long Count, long Bytes)>();
            while (r.Read())
            {
                rows.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
            }

            foreach (var (ext, count, size) in rows)
            {
                using var s = conn.CreateCommand();
                s.CommandText = ext.Length == 0
                    ? "SELECT RelPath FROM Entries WHERE IsDir = 0 AND CategoryId IS NULL AND Ext IS NULL ORDER BY Size DESC LIMIT 6"
                    : "SELECT RelPath FROM Entries WHERE IsDir = 0 AND CategoryId IS NULL AND Ext = $e ORDER BY Size DESC LIMIT 6";
                s.Parameters.AddWithValue("$e", ext);
                var samples = new List<string>();
                using var sr = s.ExecuteReader();
                while (sr.Read())
                {
                    samples.Add(sr.GetString(0));
                }

                exts.Add(new { extension = ext.Length == 0 ? "(none)" : ext, files = count, size = HumanSize(size), samples });
            }
        }

        var folders = new List<object>();
        using (var c = conn.CreateCommand())
        {
            c.CommandText = """
                SELECT p.Name, COUNT(*), SUM(e.Size) FROM Entries e JOIN Entries p ON p.Id = e.ParentId
                WHERE e.IsDir = 0 AND e.CategoryId IS NULL GROUP BY p.Name ORDER BY SUM(e.Size) DESC LIMIT 25
                """;
            using var r = c.ExecuteReader();
            while (r.Read())
            {
                folders.Add(new { folder = r.GetString(0), files = r.GetInt64(1), size = HumanSize(r.GetInt64(2)) });
            }
        }

        return new { totalFiles = total, totalSize = HumanSize(bytes), byExtension = exts, byParentFolder = folders };
    }

    private async Task<(T? Value, UsageDetails? Usage)> AskAsync<T>(IChatClient client, AiSettings s, string system, string user, JsonElement schema, string schemaName, CancellationToken ct)
    {
        var messages = new List<ChatMessage> { new(ChatRole.System, system), new(ChatRole.User, user) };
        ChatResponse res;
        try
        {
            res = await client.GetResponseAsync(messages, new ChatOptions
            {
                Temperature = 0.1f,
                ResponseFormat = s.UseJsonSchema ? ChatResponseFormat.ForJsonSchema(schema, schemaName) : ChatResponseFormat.Json,
            }, ct);
        }
        catch (ClientResultException e) when (s.UseJsonSchema && e.Status is 400 or 422)
        {
            // Server does not support json_schema: retry with plain JSON mode.
            res = await client.GetResponseAsync(messages, new ChatOptions { Temperature = 0.1f, ResponseFormat = ChatResponseFormat.Json }, ct);
        }

        return (ParseJson<T>(res.Text), res.Usage);
    }

    /// <summary>Parses the first JSON object in a reply (models sometimes wrap it in prose or code fences).</summary>
    internal static T? ParseJson<T>(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text[start..(end + 1)], Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private string? Snippet(Candidate c)
    {
        if (c.Kind is not (FileKind.Text or FileKind.Code) || c.Size == 0)
        {
            return null;
        }

        try
        {
            var path = roots.ResolveForRead(c.RootId, c.RelPath);
            using var fs = File.OpenRead(path);
            var buf = new byte[2048];
            var n = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return System.Text.Encoding.UTF8.GetString(buf, 0, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }

    private static string ColorFor(string name)
    {
        string[] palette = ["#0ea5e9", "#22c55e", "#f59e0b", "#ef4444", "#8b5cf6", "#14b8a6", "#f97316", "#ec4899", "#84cc16", "#6366f1"];
        var h = 0;
        foreach (var ch in name)
        {
            h = (h * 31) + ch;
        }

        return palette[(int)((uint)h % palette.Length)];
    }
}
