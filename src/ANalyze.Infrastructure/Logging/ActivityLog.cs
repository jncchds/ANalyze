using ANalyze.Domain;

namespace ANalyze.Infrastructure.Logging;

/// <summary>One line of the activity log.</summary>
/// <param name="Source">Area: scan, integration:&lt;id&gt;, actions, ai, system.</param>
/// <param name="Detail">Exception text, if any.</param>
public sealed record LogLine(long Id, DateTimeOffset At, LogSeverity Level, string Source, string Message, string? Detail);

/// <summary>
/// The activity log shown in the UI: the most recent lines, kept in memory (not in the database) and announced through
/// <see cref="Written"/> so the UI can follow live. The rolling files in <c>&lt;DataDir&gt;/logs</c> keep the full history.
/// Lines arrive from <c>ILogger</c> through the Serilog sink in ANalyze.Web; <see cref="Write"/> can also be called directly.
/// </summary>
public sealed class ActivityLog(TimeProvider time)
{
    public const int Capacity = 20_000;

    private readonly LogLine?[] _ring = new LogLine?[Capacity];
    private readonly Lock _gate = new();
    private long _next = 1;

    /// <summary>A line was just added.</summary>
    public event Action<LogLine>? Written;

    public void Write(LogSeverity level, string source, string message, string? detail = null)
    {
        LogLine line;
        lock (_gate)
        {
            line = new LogLine(_next, time.GetUtcNow(), level, source, message, detail);
            _ring[_next % Capacity] = line;
            _next++;
        }

        Written?.Invoke(line);
    }

    /// <summary>
    /// Newest lines first that match the filter: <paramref name="before"/> pages back, <paramref name="minLevel"/>, a source prefix
    /// ("integration" matches every integration) and text in the message.
    /// </summary>
    public IReadOnlyList<LogLine> Query(long? before, LogSeverity minLevel, string? source, string? text, int limit)
    {
        var list = new List<LogLine>(Math.Min(limit, 1000));
        lock (_gate)
        {
            var newest = _next - 1;
            var oldest = Math.Max(1, _next - Capacity);
            for (var id = Math.Min(newest, (before ?? long.MaxValue) - 1); id >= oldest && list.Count < limit; id--)
            {
                var line = _ring[id % Capacity];
                if (line is null || line.Level < minLevel)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(source) && !line.Source.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(text) && !line.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(line);
            }
        }

        return list;
    }

    /// <summary>Sources present in the buffer, for the filter list.</summary>
    public IReadOnlyList<string> Sources()
    {
        lock (_gate)
        {
            return _ring.Where(l => l is not null).Select(l => l!.Source).Distinct().Order().ToList();
        }
    }
}
