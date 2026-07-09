using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NoMistakes.Core;

/// <summary>
/// Structured operations over <see cref="Findings"/> payloads, ported from Go's
/// internal/types findings helpers. These back the pipeline executor's auto-fix
/// filtering, gate selection, and round persistence.
/// </summary>
public static class FindingsOps
{
    private static readonly JsonSerializerOptions MarshalOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The effective action of a finding: a blank action defaults to auto-fix.</summary>
    public static string ActionOrDefault(Finding f) =>
        string.IsNullOrEmpty(f.Action) ? FindingActions.AutoFix : f.Action;

    /// <summary>Assigns deterministic IDs (prefix-N) to findings that lack one.</summary>
    public static Findings Normalize(Findings findings, string prefix)
    {
        for (var i = 0; i < findings.Items.Count; i++)
        {
            if (findings.Items[i].Id.Length == 0)
            {
                findings.Items[i].Id = prefix + "-" + (i + 1);
            }
        }
        return findings;
    }

    /// <summary>Keeps only findings whose IDs are in <paramref name="ids"/>.</summary>
    public static Findings Filter(Findings findings, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return findings;
        }
        var selected = new HashSet<string>(ids);
        var filtered = CarryMeta(findings);
        foreach (var item in findings.Items)
        {
            if (selected.Contains(item.Id))
            {
                filtered.Items.Add(item);
            }
        }
        if (filtered.Items.Count != findings.Items.Count)
        {
            filtered.Summary = SummarizeSelected(filtered.Items.Count);
        }
        return filtered;
    }

    /// <summary>Keeps only findings whose IDs are NOT in <paramref name="ids"/>.</summary>
    public static Findings Exclude(Findings findings, IReadOnlyList<string> ids)
    {
        if (ids.Count == 0)
        {
            return findings;
        }
        var excluded = new HashSet<string>(ids);
        var result = CarryMeta(findings);
        foreach (var item in findings.Items)
        {
            if (!excluded.Contains(item.Id))
            {
                result.Items.Add(item);
            }
        }
        return result;
    }

    /// <summary>Returns only findings whose effective action is "auto-fix".</summary>
    public static Findings AutoFixable(Findings findings)
    {
        var result = CarryMeta(findings);
        foreach (var item in findings.Items)
        {
            if (ActionOrDefault(item) == FindingActions.AutoFix)
            {
                result.Items.Add(item);
            }
        }
        return result;
    }

    /// <summary>True if any finding has the "ask-user" action.</summary>
    public static bool HasAskUser(Findings findings)
    {
        foreach (var item in findings.Items)
        {
            if (item.Action == FindingActions.AskUser)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Applies per-finding user instructions to existing findings and appends
    /// user-authored findings (stamped source=user, defaulted to auto-fix, with
    /// deterministic "user-N" IDs where absent or colliding). The input is not
    /// mutated. Mirrors Go's types.MergeUserOverrides.
    /// </summary>
    public static Findings MergeUserOverrides(
        Findings findings,
        IReadOnlyDictionary<string, string>? instructions,
        IReadOnlyList<Finding>? added)
    {
        var result = CarryMeta(findings);
        foreach (var item in findings.Items)
        {
            result.Items.Add(Clone(item));
        }
        if (instructions != null)
        {
            foreach (var item in result.Items)
            {
                if (instructions.TryGetValue(item.Id, out var note))
                {
                    item.UserInstructions = note;
                }
            }
        }

        var used = new HashSet<string>();
        foreach (var item in result.Items)
        {
            if (item.Id.Length > 0)
            {
                used.Add(item.Id);
            }
        }

        var counter = 0;
        var appended = false;
        if (added != null)
        {
            foreach (var src in added)
            {
                var item = Clone(src);
                item.Source = FindingSources.User;
                if (item.Action.Length == 0)
                {
                    item.Action = FindingActions.AutoFix;
                }
                if (item.Id.Length == 0 || used.Contains(item.Id))
                {
                    item.Id = NextUserFindingId(used, ref counter);
                }
                else
                {
                    used.Add(item.Id);
                }
                result.Items.Add(item);
                appended = true;
            }
        }

        if (appended && IsSelectedSummary(result.Summary))
        {
            result.Summary = SummarizeSelected(result.Items.Count);
        }
        return result;
    }

    /// <summary>Encodes findings using the current wire shape (matches Go json.Marshal).</summary>
    public static string Marshal(Findings findings)
    {
        var wire = new FindingsMarshalWire
        {
            Items = findings.Items.Select(ToWire).ToList(),
            Summary = findings.Summary,
            RiskLevel = findings.RiskLevel,
            RiskRationale = findings.RiskRationale,
        };
        return JsonSerializer.Serialize(wire, MarshalOptions);
    }

    private static Findings CarryMeta(Findings f) => new()
    {
        Summary = f.Summary,
        RiskLevel = f.RiskLevel,
        RiskRationale = f.RiskRationale,
    };

    private static Finding Clone(Finding f) => new()
    {
        Id = f.Id,
        Severity = f.Severity,
        File = f.File,
        Line = f.Line,
        Description = f.Description,
        Action = f.Action,
        Source = f.Source,
        UserInstructions = f.UserInstructions,
    };

    private static string SummarizeSelected(int count) => count switch
    {
        0 => "0 selected findings",
        1 => "1 selected finding",
        _ => count + " selected findings",
    };

    private static string NextUserFindingId(HashSet<string> used, ref int counter)
    {
        while (true)
        {
            counter++;
            var candidate = "user-" + counter;
            if (used.Contains(candidate))
            {
                continue;
            }
            used.Add(candidate);
            return candidate;
        }
    }

    private static bool IsSelectedSummary(string summary)
    {
        if (summary is "0 selected findings" or "1 selected finding")
        {
            return true;
        }
        const string suffix = " selected findings";
        if (!summary.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var count = summary[..^suffix.Length];
        if (count.Length == 0)
        {
            return false;
        }
        foreach (var c in count)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return true;
    }

    private static FindingMarshalWire ToWire(Finding f) => new()
    {
        Id = f.Id.Length == 0 ? null : f.Id,
        Severity = f.Severity,
        File = f.File.Length == 0 ? null : f.File,
        Line = f.Line == 0 ? null : f.Line,
        Description = f.Description,
        Action = f.Action,
        Source = f.Source.Length == 0 ? null : f.Source,
        UserInstructions = f.UserInstructions.Length == 0 ? null : f.UserInstructions,
    };

    // Wire types matching Go's json tags for marshal round-trips. omitempty
    // fields are nullable so they drop when empty; required fields (severity,
    // description, action, summary, risk_level, risk_rationale) always emit.
    private sealed class FindingsMarshalWire
    {
        [JsonPropertyName("findings")]
        public List<FindingMarshalWire> Items { get; set; } = new();

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("risk_level")]
        public string RiskLevel { get; set; } = string.Empty;

        [JsonPropertyName("risk_rationale")]
        public string RiskRationale { get; set; } = string.Empty;
    }

    private sealed class FindingMarshalWire
    {
        [JsonPropertyName("id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        [JsonPropertyName("severity")]
        public string Severity { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? File { get; set; }

        [JsonPropertyName("line")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Line { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Source { get; set; }

        [JsonPropertyName("user_instructions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? UserInstructions { get; set; }
    }
}

/// <summary>Finding source constants. Mirrors Go's types.FindingSource* constants.</summary>
public static class FindingSources
{
    public const string Agent = "agent";
    public const string User = "user";
}
