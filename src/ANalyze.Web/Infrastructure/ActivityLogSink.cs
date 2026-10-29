using System.Text;
using ANalyze.Domain;
using ANalyze.Infrastructure.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace ANalyze.Web.Infrastructure;

/// <summary>
/// Copies ANalyze's own log lines (and warnings/errors from anything) into the in-memory <see cref="ActivityLog"/> behind the Log page.
/// The area comes from a "Source" log property (a logging scope) or else from the logger's namespace.
/// </summary>
public sealed class ActivityLogSink(ActivityLog activity) : ILogEventSink
{
    public const string SourceProperty = "Source";

    public void Emit(LogEvent e)
    {
        var context = e.Properties.TryGetValue("SourceContext", out var sc) && sc is ScalarValue { Value: string s } ? s : "";
        var ours = context.StartsWith("ANalyze", StringComparison.Ordinal);
        if (!ours && e.Level < LogEventLevel.Warning)
        {
            return;
        }

        var source = e.Properties.TryGetValue(SourceProperty, out var sp) && sp is ScalarValue { Value: string explicitSource }
            ? explicitSource
            : SourceOf(context);
        activity.Write(Level(e.Level), source, Render(e), e.Exception?.ToString());
    }

    public static string SourceOf(string context) => context switch
    {
        _ when context.StartsWith("ANalyze.Scanner", StringComparison.Ordinal) => "scan",
        _ when context.StartsWith("ANalyze.Integrations", StringComparison.Ordinal) => "integrations",
        _ when context.StartsWith("ANalyze.Actions", StringComparison.Ordinal) => "actions",
        _ when context.StartsWith("ANalyze.Ai", StringComparison.Ordinal) => "ai",
        _ when context.StartsWith("ANalyze.Preview", StringComparison.Ordinal) => "preview",
        _ => "system",
    };

    private static LogSeverity Level(LogEventLevel l) => l switch
    {
        LogEventLevel.Verbose or LogEventLevel.Debug => LogSeverity.Debug,
        LogEventLevel.Information => LogSeverity.Information,
        LogEventLevel.Warning => LogSeverity.Warning,
        _ => LogSeverity.Error,
    };

    /// <summary>Like <c>RenderMessage</c>, but strings without quotes and numbers with thousands separators.</summary>
    private static string Render(LogEvent e)
    {
        var sb = new StringBuilder();
        foreach (var token in e.MessageTemplate.Tokens)
        {
            if (token is TextToken t)
            {
                sb.Append(t.Text);
            }
            else if (token is PropertyToken p)
            {
                if (!e.Properties.TryGetValue(p.PropertyName, out var v))
                {
                    sb.Append(p.ToString());
                }
                else if (v is ScalarValue { Value: string str })
                {
                    sb.Append(str);
                }
                else if (v is ScalarValue { Value: int or long } num && p.Format is null)
                {
                    sb.Append(Convert.ToInt64(num.Value, System.Globalization.CultureInfo.InvariantCulture).ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (v is ScalarValue sv)
                {
                    sb.Append(sv.Value is IFormattable f ? f.ToString(p.Format, System.Globalization.CultureInfo.InvariantCulture) : sv.Value?.ToString());
                }
                else
                {
                    sb.Append(v.ToString());
                }
            }
        }

        return sb.ToString();
    }
}
