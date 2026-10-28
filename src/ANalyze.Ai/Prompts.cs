using System.Text;
using System.Text.Json;

namespace ANalyze.Ai;

/// <summary>Every piece of text sent to the model lives here.</summary>
public static class Prompts
{
    public static string ClassifySystem(IEnumerable<(string Name, string? Description)> categories, bool allowNew, string? extra)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You classify files found on a storage drive so the owner can understand what uses disk space and what can be cleaned up.");
        sb.AppendLine("For every file you receive, choose exactly one category from this list:");
        foreach (var (name, description) in categories)
        {
            sb.Append("- ").Append(name);
            if (!string.IsNullOrWhiteSpace(description))
            {
                sb.Append(": ").Append(description);
            }

            sb.AppendLine();
        }

        sb.AppendLine(allowNew
            ? "If none fits well, you may propose a new category as \"new:<Short Name>\" (Title Case, 1-3 words, reusable for many files)."
            : "If none fits, use the closest category.");
        sb.AppendLine("Also give up to 4 short lowercase tags (e.g. \"game-assets\", \"tax-2021\", \"steam\", \"cache\", \"safe-to-delete\") describing what the file is or belongs to.");
        sb.AppendLine("confidence is 0..1: how sure you are about the category. Use < 0.5 when guessing from the name alone.");
        sb.AppendLine("reason: at most 15 words, mention the strongest clue (extension, folder, content).");
        sb.AppendLine("Judge by the whole path: folder names often tell what an application or project a file belongs to.");
        sb.AppendLine("Answer only with JSON: {\"results\":[{\"id\":1,\"category\":\"…\",\"tags\":[\"…\"],\"confidence\":0.9,\"reason\":\"…\"}]} with one result per input id.");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            sb.AppendLine().AppendLine("Owner's notes:").AppendLine(extra.Trim());
        }

        return sb.ToString();
    }

    public static string ClassifyUser(IEnumerable<object> files) =>
        "Classify these files:\n" + JsonSerializer.Serialize(files, Json);

    public static string SuggestSystem(IEnumerable<string> categories, IEnumerable<string> existingRules)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You write categorization rules for a disk usage analyzer. Rules match files by name, path, extension, size or age and assign a category.");
        sb.AppendLine("A rule has: name, category, match (\"all\" = every clause must match, \"any\" = at least one), clauses, tags, rationale.");
        sb.AppendLine("Clause fields: name (file name), path (path relative to the root), extension (no dot, lower case), mime, kind, size (bytes, accepts 10MB/2GB), modifiedDays, accessedDays, pathSegment (any parent folder name), depth.");
        sb.AppendLine("Clause ops: equals, contains, startsWith, endsWith, glob (* within a segment, ** across), regex (.NET syntax, case-insensitive), in (comma separated list), greaterThan, lessThan. negate inverts a clause.");
        sb.AppendLine("Prefer simple, robust rules: 'extension in a,b,c' or 'pathSegment in X' over regexes. Each rule should cover many files.");
        sb.AppendLine("Use an existing category when one fits; otherwise invent a short Title Case category name.");
        sb.AppendLine("Existing categories: " + string.Join(", ", categories));
        sb.AppendLine("Existing rules (do not duplicate): " + string.Join(", ", existingRules));
        sb.AppendLine("Propose at most 10 rules for the uncategorized files described by the user. Skip groups you cannot identify confidently.");
        sb.AppendLine("Answer only with JSON: {\"rules\":[{\"name\":\"…\",\"category\":\"…\",\"match\":\"all\",\"clauses\":[{\"field\":\"extension\",\"op\":\"in\",\"value\":\"a,b\",\"negate\":false}],\"tags\":[],\"rationale\":\"…\"}]}");
        return sb.ToString();
    }

    public static string SuggestUser(object summary) =>
        "Uncategorized files, grouped:\n" + JsonSerializer.Serialize(summary, Json);

    public const string TestPrompt = "Reply with the single word OK.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static readonly JsonElement ClassifySchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "results": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "id": { "type": "integer" },
                  "category": { "type": "string" },
                  "tags": { "type": "array", "items": { "type": "string" } },
                  "confidence": { "type": "number" },
                  "reason": { "type": "string" }
                },
                "required": ["id", "category", "tags", "confidence", "reason"],
                "additionalProperties": false
              }
            }
          },
          "required": ["results"],
          "additionalProperties": false
        }
        """).RootElement;

    public static readonly JsonElement SuggestSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "category": { "type": "string" },
                  "match": { "type": "string", "enum": ["all", "any"] },
                  "clauses": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "field": { "type": "string", "enum": ["name", "path", "extension", "mime", "kind", "size", "modifiedDays", "accessedDays", "pathSegment", "depth"] },
                        "op": { "type": "string", "enum": ["equals", "contains", "startsWith", "endsWith", "glob", "regex", "in", "greaterThan", "lessThan"] },
                        "value": { "type": "string" },
                        "negate": { "type": "boolean" }
                      },
                      "required": ["field", "op", "value", "negate"],
                      "additionalProperties": false
                    }
                  },
                  "tags": { "type": "array", "items": { "type": "string" } },
                  "rationale": { "type": "string" }
                },
                "required": ["name", "category", "match", "clauses", "tags", "rationale"],
                "additionalProperties": false
              }
            }
          },
          "required": ["rules"],
          "additionalProperties": false
        }
        """).RootElement;
}
