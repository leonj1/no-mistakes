using System.Text.Json;
using NoMistakes.Core;

namespace NoMistakes.Pipeline;

/// <summary>
/// JSON-string findings helpers used by the executor's auto-fix loop and gate
/// selection, ported from Go's internal/pipeline/findings.go. Each tolerates
/// malformed input the way the Go version does (returning the input, an empty
/// string, or the fallback per case).
/// </summary>
internal static class PipelineFindings
{
    /// <summary>Extracts finding IDs as a JSON array string; empty when none.</summary>
    public static string FindingIdsJson(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        Findings findings;
        try
        {
            findings = FindingsParser.Parse(raw);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        var ids = findings.Items.Where(i => i.Id.Length > 0).Select(i => i.Id).ToList();
        return MarshalFindingIds(ids);
    }

    /// <summary>Encodes finding IDs as a JSON array; empty input yields "".</summary>
    public static string MarshalFindingIds(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? string.Empty : JsonSerializer.Serialize(ids);

    public static string NormalizeFindingsJson(string raw, string prefix)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        Findings findings;
        try
        {
            findings = FindingsParser.Parse(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
        var normalized = FindingsOps.Normalize(findings, prefix);
        return FindingsOps.Marshal(normalized);
    }

    public static string AutoFixableFindingsJson(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }
        Findings findings;
        try
        {
            findings = FindingsParser.Parse(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
        var fixable = FindingsOps.AutoFixable(findings);
        return fixable.Items.Count == 0 ? string.Empty : FindingsOps.Marshal(fixable);
    }

    public static bool HasAskUserFindingsJson(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return false;
        }
        try
        {
            return FindingsOps.HasAskUser(FindingsParser.Parse(raw));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string FilterFindingsJson(string raw, IReadOnlyList<string> ids)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }
        Findings findings;
        try
        {
            findings = FindingsParser.Parse(raw);
        }
        catch (JsonException)
        {
            return raw;
        }
        Findings filtered;
        if (ids.Count == 0)
        {
            filtered = new Findings
            {
                Summary = "0 selected findings",
                RiskLevel = findings.RiskLevel,
                RiskRationale = findings.RiskRationale,
            };
        }
        else
        {
            filtered = FindingsOps.Filter(findings, ids);
        }
        return FindingsOps.Marshal(filtered);
    }

    public static string MergeUserOverridesJson(
        string raw,
        IReadOnlyDictionary<string, string>? instructions,
        IReadOnlyList<Finding>? added)
    {
        if ((instructions == null || instructions.Count == 0) && (added == null || added.Count == 0))
        {
            return raw;
        }
        Findings baseFindings;
        try
        {
            baseFindings = FindingsParser.Parse(raw);
        }
        catch (JsonException)
        {
            baseFindings = new Findings();
        }
        var merged = FindingsOps.MergeUserOverrides(baseFindings, instructions, added);
        return FindingsOps.Marshal(merged);
    }

    /// <summary>
    /// The ordered list of finding IDs dispatched to the fix agent: the user's
    /// selected agent-produced IDs plus any user-authored IDs present only in the
    /// merged list. Mirrors Go's combineSelectedFindingIDs.
    /// </summary>
    public static List<string> CombineSelectedFindingIds(IReadOnlyList<string> selected, string mergedFindings)
    {
        var result = new List<string>(selected);
        if (string.IsNullOrEmpty(mergedFindings))
        {
            return result;
        }
        Findings merged;
        try
        {
            merged = FindingsParser.Parse(mergedFindings);
        }
        catch (JsonException)
        {
            return result;
        }
        var seen = new HashSet<string>(selected.Where(id => id.Length > 0));
        foreach (var item in merged.Items)
        {
            if (item.Id.Length == 0 || seen.Contains(item.Id))
            {
                continue;
            }
            result.Add(item.Id);
            seen.Add(item.Id);
        }
        return result;
    }

    /// <summary>Number of findings in a payload; 0 on empty/malformed.</summary>
    public static int Count(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return 0;
        }
        try
        {
            return FindingsParser.Parse(raw).Items.Count;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>Selected count: the id count when non-empty, else the finding count.</summary>
    public static int SelectedFindingCount(string raw, IReadOnlyList<string> ids) =>
        ids.Count > 0 ? ids.Count : Count(raw);
}
