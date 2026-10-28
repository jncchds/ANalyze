using System.Globalization;
using System.Text.RegularExpressions;

namespace ANalyze.Domain;

public enum MatchMode
{
    All,
    Any,
}

public enum RuleField
{
    /// <summary>File name with extension.</summary>
    Name,
    /// <summary>Path relative to the root.</summary>
    Path,
    /// <summary>Extension without the dot, lower case.</summary>
    Extension,
    Mime,
    Kind,
    /// <summary>Size in bytes; values accept suffixes (10MB, 2GiB).</summary>
    Size,
    /// <summary>Days since last modification.</summary>
    ModifiedDays,
    /// <summary>Days since last access (only meaningful when the filesystem records atime).</summary>
    AccessedDays,
    /// <summary>Any parent directory name (e.g. node_modules).</summary>
    PathSegment,
    /// <summary>Directory depth below the root.</summary>
    Depth,
}

public enum RuleOp
{
    Equals,
    Contains,
    StartsWith,
    EndsWith,
    Glob,
    Regex,
    /// <summary>Value is a comma separated list; matches if equal to any item.</summary>
    In,
    GreaterThan,
    LessThan,
}

public sealed class RuleCondition
{
    public MatchMode Match { get; set; } = MatchMode.All;
    public List<RuleClause> Clauses { get; set; } = [];
}

public sealed class RuleClause
{
    public RuleField Field { get; set; }
    public RuleOp Op { get; set; }
    public string Value { get; set; } = "";
    public bool Negate { get; set; }
}

/// <summary>The facts a rule is evaluated against.</summary>
public readonly record struct EntryFacts(
    string Name,
    string RelPath,
    string? Ext,
    string? Mime,
    FileKind Kind,
    long Size,
    long MTime,
    long ATime,
    int Depth);

public readonly record struct RuleResult(int? CategoryId, int? RuleId, IReadOnlyList<string> Tags);

/// <summary>Compiled, ordered rule set. Rules are evaluated in order; tags accumulate and the first rule that assigns a category wins.</summary>
public sealed class RuleEngine
{
    private readonly List<CompiledRule> _rules;

    public RuleEngine(IEnumerable<Rule> rules)
    {
        _rules = rules.Where(r => r.Enabled).OrderBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r => new CompiledRule(r, Compile(r.Condition))).ToList();
    }

    public int Count => _rules.Count;

    public RuleResult Evaluate(in EntryFacts f, long now)
    {
        int? category = null;
        int? ruleId = null;
        List<string>? tags = null;
        foreach (var r in _rules)
        {
            if (!r.Match(f, now))
            {
                continue;
            }

            if (r.Rule.Tags.Count > 0)
            {
                tags ??= [];
                foreach (var t in r.Rule.Tags)
                {
                    if (!tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(t);
                    }
                }
            }

            if (r.Rule.CategoryId is { } c && category is null)
            {
                category = c;
                ruleId = r.Rule.Id;
                if (r.Rule.StopProcessing)
                {
                    break;
                }
            }
        }

        return new RuleResult(category, ruleId, (IReadOnlyList<string>?)tags ?? []);
    }

    /// <summary>Checks a single condition (used to preview a rule while editing it).</summary>
    public static Func<EntryFacts, long, bool> CompileCondition(RuleCondition condition)
    {
        var c = Compile(condition);
        return (f, now) => c(f, now);
    }

    private sealed record CompiledRule(Rule Rule, Func<EntryFacts, long, bool> Match);

    private static Func<EntryFacts, long, bool> Compile(RuleCondition condition)
    {
        var clauses = condition.Clauses.Select(CompileClause).ToArray();
        if (clauses.Length == 0)
        {
            return static (_, _) => false;
        }

        return condition.Match == MatchMode.All
            ? (f, now) =>
            {
                foreach (var c in clauses)
                {
                    if (!c(f, now))
                    {
                        return false;
                    }
                }

                return true;
            }
            : (f, now) =>
            {
                foreach (var c in clauses)
                {
                    if (c(f, now))
                    {
                        return true;
                    }
                }

                return false;
            };
    }

    private static Func<EntryFacts, long, bool> CompileClause(RuleClause clause)
    {
        var inner = CompileClauseInner(clause);
        return clause.Negate ? (f, now) => !inner(f, now) : inner;
    }

    private static Func<EntryFacts, long, bool> CompileClauseInner(RuleClause clause)
    {
        var value = clause.Value?.Trim() ?? "";
        switch (clause.Field)
        {
            case RuleField.Size:
                return Numeric(clause.Op, ParseSize(value), static (f, _) => f.Size);
            case RuleField.ModifiedDays:
                return Numeric(clause.Op, ParseLong(value), static (f, now) => (now - f.MTime) / 86400);
            case RuleField.AccessedDays:
                return Numeric(clause.Op, ParseLong(value), static (f, now) => (now - f.ATime) / 86400);
            case RuleField.Depth:
                return Numeric(clause.Op, ParseLong(value), static (f, _) => f.Depth);
            case RuleField.Kind:
            {
                var kinds = SplitList(value)
                    .Select(v => Enum.TryParse<FileKind>(v, true, out var k) ? k : (FileKind?)null)
                    .Where(k => k is not null).Select(k => k!.Value).ToHashSet();
                return clause.Op is RuleOp.Equals or RuleOp.In
                    ? (f, _) => kinds.Contains(f.Kind)
                    : StringMatch(clause.Op, value, static f => f.Kind.ToString());
            }
            case RuleField.PathSegment:
            {
                if (clause.Op is RuleOp.Equals or RuleOp.In)
                {
                    var set = SplitList(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    return (f, _) => AnySegment(f.RelPath, set.Contains);
                }

                var single = StringPredicate(clause.Op, value);
                return (f, _) => AnySegment(f.RelPath, single);
            }
            case RuleField.Name:
                return StringMatch(clause.Op, value, static f => f.Name);
            case RuleField.Path:
                return StringMatch(clause.Op, value, static f => f.RelPath);
            case RuleField.Extension:
                return StringMatch(clause.Op, value.TrimStart('.'), static f => f.Ext ?? "");
            case RuleField.Mime:
                return StringMatch(clause.Op, value, static f => f.Mime ?? "");
            default:
                return static (_, _) => false;
        }
    }

    private static Func<EntryFacts, long, bool> Numeric(RuleOp op, long? target, Func<EntryFacts, long, long> get)
    {
        if (target is not { } t)
        {
            return static (_, _) => false;
        }

        return op switch
        {
            RuleOp.GreaterThan => (f, now) => get(f, now) > t,
            RuleOp.LessThan => (f, now) => get(f, now) < t,
            RuleOp.Equals => (f, now) => get(f, now) == t,
            _ => static (_, _) => false,
        };
    }

    private static Func<EntryFacts, long, bool> StringMatch(RuleOp op, string value, Func<EntryFacts, string> get)
    {
        var p = StringPredicate(op, value);
        return (f, _) => p(get(f));
    }

    private static Func<string, bool> StringPredicate(RuleOp op, string value)
    {
        switch (op)
        {
            case RuleOp.Equals:
                return s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase);
            case RuleOp.Contains:
                return s => s.Contains(value, StringComparison.OrdinalIgnoreCase);
            case RuleOp.StartsWith:
                return s => s.StartsWith(value, StringComparison.OrdinalIgnoreCase);
            case RuleOp.EndsWith:
                return s => s.EndsWith(value, StringComparison.OrdinalIgnoreCase);
            case RuleOp.In:
            {
                var set = SplitList(value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return set.Contains;
            }
            case RuleOp.Glob:
            {
                var re = GlobToRegex(value);
                return s => re.IsMatch(s);
            }
            case RuleOp.Regex:
            {
                Regex re;
                try
                {
                    re = new Regex(value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                }
                catch (ArgumentException)
                {
                    return static _ => false;
                }

                return s =>
                {
                    try
                    {
                        return re.IsMatch(s);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return false;
                    }
                };
            }
            default:
                return static _ => false;
        }
    }

    private static bool AnySegment(string relPath, Func<string, bool> predicate)
    {
        // Parent directories only: the last segment is the entry itself.
        var span = relPath.AsSpan();
        var last = span.LastIndexOf('/');
        if (last < 0)
        {
            return false;
        }

        foreach (var range in span[..last].Split('/'))
        {
            if (predicate(span[..last][range].ToString()))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<string> SplitList(string value) =>
        value.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.TrimStart('.'));

    /// <summary>Glob: * matches within a segment, ** across segments, ? one character. Case-insensitive.</summary>
    public static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        // "**/" also matches zero directories.
                        sb.Append("/?");
                        i++;
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static long? ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Parses "1500", "10KB", "1.5 GB", "2GiB" (all units are binary, 1KB = 1024).</summary>
    public static long? ParseSize(string value)
    {
        var m = Regex.Match(value.Trim(), @"^(\d+(?:\.\d+)?)\s*([kmgtp]?)i?b?$", RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            return null;
        }

        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var mul = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "k" => 1L << 10,
            "m" => 1L << 20,
            "g" => 1L << 30,
            "t" => 1L << 40,
            "p" => 1L << 50,
            _ => 1L,
        };
        return (long)(n * mul);
    }
}
