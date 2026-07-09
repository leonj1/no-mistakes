using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Ipc;

namespace NoMistakes.Cli;

/// <summary>
/// A render-ready view of a single pipeline step, decoupled from whether it
/// came from the daemon (ipc) or the local database. Mirrors Go's cli.stepView.
/// </summary>
public sealed class StepView
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long DurationMs { get; set; }
    public string FindingsJson { get; set; } = string.Empty;
    public IReadOnlyList<string>? FixSummaries { get; set; }

    /// <summary>The number of findings recorded for this step.</summary>
    public int FindingCount()
    {
        if (FindingsJson.Length == 0)
        {
            return 0;
        }
        return AxiRender.ParseFindingsOrEmpty(FindingsJson).Items.Count;
    }
}

/// <summary>A render-ready view of a pipeline run. Mirrors Go's cli.runView.</summary>
public sealed class RunView
{
    public string Id { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string HeadSha { get; set; } = string.Empty;
    public string PrUrl { get; set; } = string.Empty;

    /// <summary>
    /// The unix-seconds time the run parked at a gate awaiting the driving
    /// agent, or null when the run is not parked. It powers the top-level
    /// parked signal in the run object.
    /// </summary>
    public long? AwaitingAgentSince { get; set; }

    public List<StepView> Steps { get; set; } = new();

    public static RunView FromIpc(RunInfo r)
    {
        var rv = new RunView
        {
            Id = r.Id,
            Branch = r.Branch,
            Status = r.Status,
            HeadSha = r.HeadSha,
            PrUrl = r.PrUrl ?? string.Empty,
            AwaitingAgentSince = r.AwaitingAgentSince,
        };
        foreach (var s in r.Steps ?? [])
        {
            rv.Steps.Add(new StepView
            {
                Name = s.StepName,
                Status = s.Status,
                DurationMs = s.DurationMs ?? 0,
                FindingsJson = s.FindingsJson ?? string.Empty,
                FixSummaries = s.FixSummaries,
            });
        }
        return rv;
    }

    public static RunView FromDb(Run r, IReadOnlyList<StepResult> steps)
    {
        var rv = new RunView
        {
            Id = r.Id,
            Branch = r.Branch,
            Status = r.Status,
            HeadSha = r.HeadSha,
            PrUrl = r.PrUrl ?? string.Empty,
            AwaitingAgentSince = r.AwaitingAgentSince,
        };
        foreach (var s in steps)
        {
            rv.Steps.Add(new StepView
            {
                Name = s.StepName,
                Status = s.Status,
                DurationMs = s.DurationMs ?? 0,
                FindingsJson = s.FindingsJson ?? string.Empty,
            });
        }
        return rv;
    }

    /// <summary>
    /// Returns the step currently blocking on a human decision, if any. At most
    /// one step awaits at a time, so the first match is the active gate.
    /// </summary>
    public StepView? AwaitingStep()
    {
        foreach (var s in Steps)
        {
            if (s.Status is StepStatus.AwaitingApproval or StepStatus.FixReview)
            {
                return s;
            }
        }
        return null;
    }

    /// <summary>
    /// Summarizes the run's findings across all steps by action, so an agent
    /// sees the shape of outstanding work without a follow-up call.
    /// </summary>
    public string FindingsTally()
    {
        int awaiting = 0, autofix = 0, info = 0;
        foreach (var s in Steps)
        {
            if (s.FindingsJson.Length == 0)
            {
                continue;
            }
            foreach (var f in AxiRender.ParseFindingsOrEmpty(s.FindingsJson).Items)
            {
                switch (f.Action)
                {
                    case FindingActions.AskUser:
                        awaiting++;
                        break;
                    case FindingActions.AutoFix:
                        autofix++;
                        break;
                    default:
                        info++;
                        break;
                }
            }
        }
        var parts = new List<string>(3);
        if (awaiting > 0)
        {
            parts.Add($"{awaiting} awaiting");
        }
        if (autofix > 0)
        {
            parts.Add($"{autofix} auto-fix");
        }
        if (info > 0)
        {
            parts.Add($"{info} info");
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    /// <summary>
    /// Flattens the fixes the pipeline applied across all steps into renderable
    /// rows, in step then round order. A fix round that recorded no summary
    /// still produced a fix commit, so it gets an explicit placeholder rather
    /// than being dropped.
    /// </summary>
    public List<ToonObject> FixRows()
    {
        var rows = new List<ToonObject>();
        foreach (var s in Steps)
        {
            foreach (var summary in s.FixSummaries ?? [])
            {
                var text = summary.Length == 0 ? "fix applied (no summary recorded)" : summary;
                rows.Add(new ToonObject(
                    new ToonField("step", s.Name),
                    new ToonField("summary", text)));
            }
        }
        return rows;
    }
}

/// <summary>
/// TOON rendering for the axi command surface: the run object, the gate
/// object, and the findings table, with stable field order. Ports Go's
/// internal/cli/axi_render.go.
/// </summary>
public static class AxiRender
{
    /// <summary>
    /// Caps a finding description rendered inline. Findings are the decision
    /// content at a gate, so the limit is generous; only pathological
    /// descriptions get truncated, with the full length disclosed.
    /// </summary>
    public const int MaxFindingDesc = 600;

    /// <summary>
    /// Current time in unix seconds. Settable so tests can pin the clock when
    /// asserting how long a run has been parked (Go's nowUnix package var).
    /// </summary>
    internal static Func<long> NowUnix { get; set; } = () => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Reports whether a run has reached a final state.</summary>
    public static bool TerminalStatus(string status) =>
        status is RunStatus.Completed or RunStatus.Failed or RunStatus.Cancelled;

    /// <summary>
    /// Renders how long a run has been parked awaiting the agent, given the
    /// unix-seconds time it parked. The phrasing reports the elapsed duration
    /// so a supervisor can tell a fresh park ("parked 4s") from a stalled one
    /// ("parked 18m20s") in a single `axi status` read.
    /// </summary>
    public static string FormatParkedFor(long sinceUnix)
    {
        var secs = NowUnix() - sinceUnix;
        if (secs < 0)
        {
            secs = 0;
        }
        return secs switch
        {
            < 60 => $"parked {secs}s",
            < 3600 => $"parked {secs / 60}m{secs % 60}s",
            < 86400 => $"parked {secs / 3600}h{secs / 60 % 60}m",
            _ => $"parked {secs / 86400}d{secs / 3600 % 24}h",
        };
    }

    /// <summary>Trims a commit SHA for display.</summary>
    public static string ShortSha(string sha) => sha.Length > 8 ? sha[..8] : sha;

    /// <summary>
    /// Shortens s to limit code points, appending a disclosure of the full size
    /// when it actually trims, per the AXI content-truncation convention.
    /// (Go counts runes, so the .NET port counts code points, not UTF-16 units.)
    /// </summary>
    public static string Truncate(string s, int limit)
    {
        var total = 0;
        var cutIndex = -1;
        var i = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (total == limit)
            {
                cutIndex = i;
            }
            i += r.Utf16SequenceLength;
            total++;
        }
        if (total <= limit)
        {
            return s;
        }
        return s[..cutIndex] + $"… (truncated, {total} chars total)";
    }

    /// <summary>
    /// Parses findings JSON, degrading to an empty payload on malformed input,
    /// matching Go's ignored ParseFindingsJSON errors in the render layer.
    /// </summary>
    internal static Findings ParseFindingsOrEmpty(string raw)
    {
        try
        {
            return FindingsParser.Parse(raw);
        }
        catch (Exception)
        {
            return new Findings();
        }
    }

    /// <summary>Renders a run as a TOON "run:" object with a steps table.</summary>
    public static ToonField RunObjectField(RunView rv) => RunObjectFieldWithKey("run", rv);

    public static ToonField RunObjectFieldWithKey(string key, RunView rv)
    {
        var fields = new List<ToonField>
        {
            new("id", rv.Id),
            new("branch", rv.Branch),
            new("status", rv.Status),
        };
        // Surface the parked-awaiting-agent signal right after status so one
        // read distinguishes a run waiting for the agent to drive a gate from
        // one that is actively running/fixing/ci. The value reports how long it
        // has been parked, which separates a fresh park from a stalled one.
        // Present only while genuinely parked (non-null marker on a
        // non-terminal run).
        if (rv.AwaitingAgentSince is { } since && !TerminalStatus(rv.Status))
        {
            fields.Add(new ToonField("awaiting_agent", FormatParkedFor(since)));
        }
        fields.Add(new ToonField("head", ShortSha(rv.HeadSha)));
        if (rv.PrUrl.Length > 0)
        {
            fields.Add(new ToonField("pr", rv.PrUrl));
        }
        fields.Add(new ToonField("findings", rv.FindingsTally()));

        var rows = new List<ToonObject>(rv.Steps.Count);
        foreach (var s in rv.Steps)
        {
            rows.Add(new ToonObject(
                new ToonField("step", s.Name),
                new ToonField("status", s.Status),
                new ToonField("findings", s.FindingCount()),
                new ToonField("duration_ms", s.DurationMs)));
        }
        fields.Add(new ToonField("steps", rows));
        return new ToonField(key, new ToonObject(fields));
    }

    /// <summary>
    /// Renders the active approval gate: the awaiting step, its findings table,
    /// and the next-step commands an agent can run to clear it.
    /// </summary>
    public static ToonField[] GateFields(StepView gate)
    {
        var parsed = ParseFindingsOrEmpty(gate.FindingsJson);
        var gfields = new List<ToonField>
        {
            new("step", gate.Name),
            new("status", gate.Status),
        };
        if (parsed.Summary.Length > 0)
        {
            gfields.Add(new ToonField("summary", parsed.Summary));
        }
        if (parsed.RiskLevel.Length > 0)
        {
            gfields.Add(new ToonField("risk", parsed.RiskLevel));
        }
        // Point-of-use reminder at the review gate: review auto-fix defaults to
        // disabled, so agents should expect blocking and ask-user findings to
        // park unless config explicitly opts back in.
        if (gate.Name == StepName.Review)
        {
            gfields.Add(new ToonField("note", "Review auto-fix is disabled by default (`auto_fix.review: 0`; a repo or global `auto_fix.review > 0` override re-enables it), so blocking and ask-user review findings park for your decision rather than being silently self-fixed."));
        }
        var rows = new List<ToonObject>(parsed.Items.Count);
        foreach (var f in parsed.Items)
        {
            rows.Add(new ToonObject(
                new ToonField("id", f.Id),
                new ToonField("severity", f.Severity),
                new ToonField("file", f.File),
                new ToonField("action", f.Action),
                new ToonField("description", Truncate(f.Description, MaxFindingDesc))));
        }
        gfields.Add(new ToonField("findings", rows));

        return
        [
            new ToonField("gate", new ToonObject(gfields)),
            new ToonField("help", new List<string>
            {
                "Run `no-mistakes axi respond --action approve` to accept this step and continue",
                "Run `no-mistakes axi respond --action fix --findings <ids>` to have the pipeline fix the selected findings (do not edit files yourself)",
                "Run `no-mistakes axi respond --action skip` to skip this step",
                $"Run `no-mistakes axi logs --step {gate.Name} --full` to read the full step log",
                "A long-running call is working, not stalled - background it if your harness needs to, but the run never advances past a gate on its own. Read every return; on a `gate:`, respond; loop until an `outcome:`.",
            }),
        ];
    }

    /// <summary>
    /// Marshals an ordered set of TOON fields into a document with a trailing
    /// newline. Encoding errors are impossible for the value shapes built here,
    /// so a failure degrades to an empty document rather than propagating.
    /// </summary>
    public static string Doc(params ToonField[] fields)
    {
        try
        {
            return Toon.MarshalString(new ToonObject(fields)) + "\n";
        }
        catch (ToonEncodingException)
        {
            return "";
        }
    }
}
