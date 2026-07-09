using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMistakes.Core;

/// <summary>
/// A single review, test, lint, or PR comment finding. Mirrors Go's
/// types.Finding. Only the subset needed by the run database (stats
/// aggregation) is modelled here; richer behavior lands with later slices.
/// The JsonPropertyName tags mirror Go's json tags so the type serializes
/// directly on wire surfaces (e.g. the IPC RespondParams.AddedFindings).
/// </summary>
public sealed class Finding
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("line")]
    public int Line { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("user_instructions")]
    public string UserInstructions { get; set; } = string.Empty;
}

/// <summary>
/// Structured findings payload. Mirrors the subset of Go's types.Findings the
/// run database consumes.
/// </summary>
public sealed class Findings
{
    public List<Finding> Items { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = string.Empty;
}

/// <summary>Parses findings JSON, tolerating the legacy shapes Go accepts.</summary>
public static class FindingsParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Decodes findings JSON. Accepts the current "findings" key plus the legacy
    /// "items" key, and the legacy per-finding "requires_human_review" boolean
    /// that predates the "action" field. Mirrors Go's ParseFindingsJSON.
    /// </summary>
    public static Findings Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new Findings();
        }

        var wire = JsonSerializer.Deserialize<FindingsWire>(raw, Options)
                   ?? new FindingsWire();
        var items = wire.Items;
        if ((items == null || items.Count == 0) && wire.Legacy is { Count: > 0 })
        {
            items = wire.Legacy;
        }

        var result = new Findings
        {
            Summary = wire.Summary ?? string.Empty,
            RiskLevel = wire.RiskLevel ?? string.Empty,
        };
        if (items != null)
        {
            foreach (var w in items)
            {
                result.Items.Add(FromWire(w));
            }
        }
        return result;
    }

    private static Finding FromWire(FindingWire w)
    {
        var action = w.Action ?? string.Empty;
        if (string.IsNullOrEmpty(action) && w.RequiresHumanReview.HasValue)
        {
            action = w.RequiresHumanReview.Value ? FindingActions.AskUser : FindingActions.AutoFix;
        }
        return new Finding
        {
            Id = w.Id ?? string.Empty,
            Severity = w.Severity ?? string.Empty,
            File = w.File ?? string.Empty,
            Line = w.Line,
            Description = w.Description ?? string.Empty,
            Action = action,
            Source = w.Source ?? string.Empty,
            UserInstructions = w.UserInstructions ?? string.Empty,
        };
    }

    private sealed class FindingsWire
    {
        [JsonPropertyName("findings")]
        public List<FindingWire>? Items { get; set; }

        [JsonPropertyName("items")]
        public List<FindingWire>? Legacy { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("risk_level")]
        public string? RiskLevel { get; set; }
    }

    private sealed class FindingWire
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("severity")]
        public string? Severity { get; set; }

        [JsonPropertyName("file")]
        public string? File { get; set; }

        [JsonPropertyName("line")]
        public int Line { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("user_instructions")]
        public string? UserInstructions { get; set; }

        [JsonPropertyName("requires_human_review")]
        public bool? RequiresHumanReview { get; set; }
    }
}

/// <summary>Finding action constants. Mirrors Go's types.Action* constants.</summary>
public static class FindingActions
{
    public const string NoOp = "no-op";
    public const string AutoFix = "auto-fix";
    public const string AskUser = "ask-user";
}
