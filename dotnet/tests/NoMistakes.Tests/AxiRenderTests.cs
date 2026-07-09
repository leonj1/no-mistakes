using System.Text.Json;
using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Data;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports the TOON render-shape tests from Go's internal/cli/axi_test.go: the
/// run object, the gate object, and the findings table, asserting output shape
/// and stable field order. Tests that pin AxiRender.NowUnix mutate process
/// state, so every test touching it stays in this class (xunit runs a class's
/// tests serially) and restores the clock in a finally block.
/// </summary>
public class AxiRenderTests
{
    private static string FindingsJson(string summary, params object[] items) =>
        JsonSerializer.Serialize(new { findings = items, summary });

    [Fact]
    public void RunViewFromDbFindsAwaitingStep()
    {
        var run = new Run { Id = "r1", Branch = "feature/x", HeadSha = "abcdef1234567890", Status = RunStatus.Running };
        var steps = new List<StepResult>
        {
            new() { StepName = StepName.Review, Status = StepStatus.Completed },
            new()
            {
                StepName = StepName.Test,
                Status = StepStatus.AwaitingApproval,
                FindingsJson = """{"findings":[],"summary":"x"}""",
            },
        };
        var rv = RunView.FromDb(run, steps);
        var gate = rv.AwaitingStep();
        Assert.NotNull(gate);
        Assert.Equal(StepName.Test, gate!.Name);
    }

    [Fact]
    public void FindingsTallyGroupsByAction()
    {
        var rv = new RunView
        {
            Steps =
            {
                new StepView
                {
                    FindingsJson = FindingsJson("s",
                        new { id = "a", action = FindingActions.AskUser, description = "x" },
                        new { id = "b", action = FindingActions.AutoFix, description = "y" },
                        new { id = "c", action = FindingActions.NoOp, description = "z" },
                        new { id = "d", action = FindingActions.AskUser, description = "w" }),
                },
            },
        };
        Assert.Equal("2 awaiting, 1 auto-fix, 1 info", rv.FindingsTally());

        var empty = new RunView { Steps = { new StepView() } };
        Assert.Equal("none", empty.FindingsTally());
    }

    [Fact]
    public void TruncateDisclosesTotal()
    {
        Assert.Equal("hello", AxiRender.Truncate("hello", 100));

        var truncated = AxiRender.Truncate(new string('x', 50), 10);
        Assert.Contains("truncated, 50 chars total", truncated);
        Assert.StartsWith(new string('x', 10), truncated);
    }

    [Fact]
    public void RunObjectHasStableShapeAndFieldOrder()
    {
        var rv = new RunView
        {
            Id = "run-1",
            Branch = "feature/x",
            Status = RunStatus.Running,
            HeadSha = "abcdef1234567890",
            Steps =
            {
                new StepView
                {
                    Name = "review",
                    Status = "completed",
                    DurationMs = 1200,
                    FindingsJson = FindingsJson("s",
                        new { id = "r1", action = FindingActions.NoOp, description = "ok" }),
                },
                new StepView { Name = "test", Status = "awaiting_approval" },
            },
        };
        var doc = AxiRender.Doc(AxiRender.RunObjectField(rv));

        // Exact document: asserts both the rendered shape and the stable field
        // order (id, branch, status, head, findings, steps table).
        Assert.Equal(
            "run:\n" +
            "  id: run-1\n" +
            "  branch: feature/x\n" +
            "  status: running\n" +
            "  head: abcdef12\n" +
            "  findings: 1 info\n" +
            "  steps[2]{step,status,findings,duration_ms}:\n" +
            "    review,completed,1,1200\n" +
            "    test,awaiting_approval,0,0\n",
            doc);
    }

    [Fact]
    public void RunObjectIncludesPrUrlBeforeFindingsWhenSet()
    {
        var rv = new RunView
        {
            Id = "run-1",
            Branch = "feature/x",
            Status = RunStatus.Completed,
            HeadSha = "abcdef1234567890",
            PrUrl = "https://github.com/o/r/pull/7",
        };
        var doc = AxiRender.Doc(AxiRender.RunObjectField(rv));
        // The URL carries a colon, so TOON quoting wraps the value.
        Assert.Contains("  head: abcdef12\n  pr: \"https://github.com/o/r/pull/7\"\n  findings: none\n", doc);
    }

    [Fact]
    public void RunObjectRendersAwaitingAgent()
    {
        var restore = AxiRender.NowUnix;
        AxiRender.NowUnix = () => 1_000_000;
        try
        {
            var rv = new RunView
            {
                Id = "run-1",
                Branch = "feature/x",
                Status = RunStatus.Running,
                HeadSha = "abcdef1234567890",
                AwaitingAgentSince = 1_000_000 - 150, // 2m30s ago
                Steps = { new StepView { Name = "review", Status = "awaiting_approval" } },
            };
            var doc = AxiRender.Doc(AxiRender.RunObjectField(rv));
            Assert.Contains("awaiting_agent: parked 2m30s\n", doc);
            // The signal sits right after status so one read distinguishes parked.
            Assert.Contains("status: running\n  awaiting_agent: parked 2m30s\n", doc);

            // A run that is not parked omits the signal entirely.
            rv.AwaitingAgentSince = null;
            Assert.DoesNotContain("awaiting_agent", AxiRender.Doc(AxiRender.RunObjectField(rv)));

            // A terminal run never renders as parked even if a stale marker survives.
            rv.AwaitingAgentSince = 1_000_000 - 150;
            rv.Status = RunStatus.Completed;
            Assert.DoesNotContain("awaiting_agent", AxiRender.Doc(AxiRender.RunObjectField(rv)));
        }
        finally
        {
            AxiRender.NowUnix = restore;
        }
    }

    [Theory]
    [InlineData(4, "parked 4s")]
    [InlineData(150, "parked 2m30s")]
    [InlineData(3 * 3600 + 12 * 60, "parked 3h12m")]
    [InlineData(2 * 86400 + 5 * 3600, "parked 2d5h")]
    [InlineData(-10, "parked 0s")] // clock skew clamps to zero
    public void FormatParkedForBuckets(long agoSecs, string want)
    {
        var restore = AxiRender.NowUnix;
        AxiRender.NowUnix = () => 1_000_000;
        try
        {
            Assert.Equal(want, AxiRender.FormatParkedFor(1_000_000 - agoSecs));
        }
        finally
        {
            AxiRender.NowUnix = restore;
        }
    }

    [Fact]
    public void GateHasStableShapeAndFindingsTable()
    {
        var gate = new StepView
        {
            Name = "review",
            Status = "awaiting_approval",
            FindingsJson = FindingsJson("1 blocking issue",
                new
                {
                    id = "review-1",
                    severity = "warning",
                    file = "main.go",
                    line = 4,
                    action = FindingActions.AskUser,
                    description = "calls os.Exit, leaks fd",
                }),
        };
        var doc = AxiRender.Doc(AxiRender.GateFields(gate));

        foreach (var want in new[]
        {
            "gate:\n",
            "  step: review\n",
            "  status: awaiting_approval\n",
            "  summary: 1 blocking issue\n",
            "  findings[1]{id,severity,file,action,description}:\n",
            "    review-1,warning,main.go,ask-user,\"calls os.Exit, leaks fd\"",
            "no-mistakes axi respond --action approve",
            "to have the pipeline fix the selected findings (do not edit files yourself)",
            // Review gate carries the auto-fix-disabled note and the
            // keep-driving reminder so an agent reads them at the point of use.
            "Review auto-fix is disabled by default",
            "auto_fix.review > 0",
            "the run never advances past a gate on its own",
        })
        {
            Assert.Contains(want, doc);
        }

        // Field order within the gate object is stable: step, status, summary,
        // note, findings table.
        var stepIdx = doc.IndexOf("  step: review", StringComparison.Ordinal);
        var statusIdx = doc.IndexOf("  status: awaiting_approval", StringComparison.Ordinal);
        var summaryIdx = doc.IndexOf("  summary:", StringComparison.Ordinal);
        var findingsIdx = doc.IndexOf("  findings[1]", StringComparison.Ordinal);
        Assert.True(stepIdx < statusIdx && statusIdx < summaryIdx && summaryIdx < findingsIdx,
            $"gate field order wrong in:\n{doc}");
    }

    [Fact]
    public void GateRendersRiskLevelWhenPresent()
    {
        var gate = new StepView
        {
            Name = "test",
            Status = "awaiting_approval",
            FindingsJson = """{"findings":[],"summary":"s","risk_level":"high"}""",
        };
        var doc = AxiRender.Doc(AxiRender.GateFields(gate));
        Assert.Contains("  risk: high\n", doc);
    }

    [Fact]
    public void GateNoteAppearsOnlyAtReviewGate()
    {
        static string Mk(string step) => AxiRender.Doc(AxiRender.GateFields(new StepView
        {
            Name = step,
            Status = "awaiting_approval",
            FindingsJson = JsonSerializer.Serialize(new
            {
                findings = new[]
                {
                    new { id = step + "-1", severity = "warning", file = "main.go", action = FindingActions.AutoFix, description = "x" },
                },
                summary = "summary",
            }),
        }));

        var review = Mk("review");
        Assert.Contains("Review auto-fix is disabled by default", review);
        Assert.Contains("auto_fix.review > 0", review);

        var lint = Mk("lint");
        Assert.DoesNotContain("Review auto-fix is disabled", lint);
        Assert.Contains("the run never advances past a gate on its own", lint);
    }

    [Fact]
    public void GateTruncatesPathologicalDescriptions()
    {
        var gate = new StepView
        {
            Name = "lint",
            Status = "awaiting_approval",
            FindingsJson = FindingsJson("s",
                new { id = "l1", action = FindingActions.AskUser, description = new string('d', 700) }),
        };
        var doc = AxiRender.Doc(AxiRender.GateFields(gate));
        Assert.Contains("truncated, 700 chars total", doc);
    }

    [Fact]
    public void FixRowsFlattenSummariesWithPlaceholder()
    {
        var rv = new RunView
        {
            Steps =
            {
                new StepView { Name = "review", FixSummaries = new[] { "tightened nil check", "" } },
                new StepView { Name = "test", FixSummaries = new[] { "fixed flaky wait" } },
            },
        };
        var doc = Toon.MarshalString(new ToonObject(new ToonField("fixes", rv.FixRows())));
        Assert.Equal(
            "fixes[3]{step,summary}:\n" +
            "  review,tightened nil check\n" +
            "  review,fix applied (no summary recorded)\n" +
            "  test,fixed flaky wait",
            doc);
    }

    [Fact]
    public void MalformedFindingsJsonDegradesToZeroFindings()
    {
        var step = new StepView { Name = "review", Status = "completed", FindingsJson = "not json" };
        Assert.Equal(0, step.FindingCount());

        var rv = new RunView { Steps = { step } };
        Assert.Equal("none", rv.FindingsTally());
    }
}
