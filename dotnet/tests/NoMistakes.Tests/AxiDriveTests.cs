using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Ipc;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Pure-logic tests for the axi drive layer (slice 8c.1): outcome mapping,
/// --yes gate resolution, active-run head matching, and the drive-result and
/// abort renders. Ports the decision logic asserted around Go's
/// internal/cli/axi_drive.go.
/// </summary>
public sealed class AxiDriveTests
{
    [Theory]
    [InlineData(RunStatus.Completed, "passed")]
    [InlineData(RunStatus.Failed, "failed")]
    [InlineData(RunStatus.Cancelled, "cancelled")]
    [InlineData(RunStatus.Running, "running")]
    public void OutcomeForMapsTerminalStatuses(string status, string want)
    {
        Assert.Equal(want, AxiDrive.OutcomeFor(status));
    }

    [Fact]
    public void GateResolutionFixesActionableFindingsWithIds()
    {
        var gate = new StepView
        {
            Name = StepName.Review,
            Status = StepStatus.AwaitingApproval,
            FindingsJson = """{"findings":[{"id":"f1","description":"a","action":"auto-fix"},{"id":"f2","description":"b","action":"ask-user"}]}""",
        };
        var (action, ids) = AxiDrive.GateResolution(gate, alreadyFixed: false);
        Assert.Equal(ApprovalAction.Fix, action);
        Assert.Equal(new[] { "f1", "f2" }, ids);
    }

    [Fact]
    public void GateResolutionApprovesWhenAlreadyFixedOnce()
    {
        var gate = new StepView
        {
            Name = StepName.Review,
            Status = StepStatus.AwaitingApproval,
            FindingsJson = """{"findings":[{"id":"f1","description":"a","action":"auto-fix"}]}""",
        };
        var (action, ids) = AxiDrive.GateResolution(gate, alreadyFixed: true);
        Assert.Equal(ApprovalAction.Approve, action);
        Assert.Empty(ids);
    }

    [Fact]
    public void GateResolutionApprovesFixReviewGate()
    {
        var gate = new StepView
        {
            Name = StepName.Review,
            Status = StepStatus.FixReview,
            FindingsJson = """{"findings":[{"id":"f1","description":"a","action":"auto-fix"}]}""",
        };
        var (action, _) = AxiDrive.GateResolution(gate, alreadyFixed: false);
        Assert.Equal(ApprovalAction.Approve, action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("""{"findings":[]}""")]
    [InlineData("""{"findings":[{"id":"f1","description":"informational","action":"no-op"}]}""")]
    [InlineData("not json at all")]
    public void GateResolutionApprovesNonActionableGates(string findingsJson)
    {
        var gate = new StepView
        {
            Name = StepName.Review,
            Status = StepStatus.AwaitingApproval,
            FindingsJson = findingsJson,
        };
        var (action, ids) = AxiDrive.GateResolution(gate, alreadyFixed: false);
        Assert.Equal(ApprovalAction.Approve, action);
        Assert.Empty(ids);
    }

    [Fact]
    public void GateResolutionApprovesActionableFindingsWithoutIds()
    {
        // A fix with zero selected findings would resolve nothing, so the
        // gate is approved instead.
        var gate = new StepView
        {
            Name = StepName.Review,
            Status = StepStatus.AwaitingApproval,
            FindingsJson = """{"findings":[{"description":"no id","action":"auto-fix"}]}""",
        };
        var (action, ids) = AxiDrive.GateResolution(gate, alreadyFixed: false);
        Assert.Equal(ApprovalAction.Approve, action);
        Assert.Empty(ids);
    }

    [Fact]
    public void ActiveRunInfoForHeadFiltersTerminalAndForeignHeads()
    {
        Assert.Null(AxiDrive.ActiveRunInfoForHead(null, "head1"));
        Assert.Null(AxiDrive.ActiveRunInfoForHead(
            new RunInfo { Id = "r1", Status = RunStatus.Completed, HeadSha = "head1" }, "head1"));
        Assert.Null(AxiDrive.ActiveRunInfoForHead(
            new RunInfo { Id = "r1", Status = RunStatus.Running, HeadSha = "other" }, "head1"));
        var match = new RunInfo { Id = "r1", Status = RunStatus.Running, HeadSha = "head1" };
        Assert.Same(match, AxiDrive.ActiveRunInfoForHead(match, "head1"));
    }

    [Fact]
    public void RenderDriveResultCompletedRunIsPassedWithReportHelp()
    {
        var run = new RunInfo
        {
            Id = "run-1",
            Branch = "feature",
            Status = RunStatus.Completed,
            HeadSha = "abcdef1234567890",
            PrUrl = "https://github.com/u/r/pull/7",
        };
        var output = AxiDrive.RenderDriveResult(run, ciReady: false);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains("outcome: passed", output.Doc);
        Assert.Contains("Open the PR: https://github.com/u/r/pull/7", output.Doc);
        Assert.Contains("Summarize this pipeline run for the user", output.Doc);
        Assert.DoesNotContain("fixes", output.Doc);
    }

    [Fact]
    public void RenderDriveResultCompletedRunWithFixesListsThem()
    {
        var run = new RunInfo
        {
            Id = "run-1",
            Branch = "feature",
            Status = RunStatus.Completed,
            HeadSha = "abcdef1234567890",
            Steps = new List<StepResultInfo>
            {
                new()
                {
                    StepName = StepName.Review,
                    Status = StepStatus.Completed,
                    FixSummaries = new List<string> { "tightened the retry loop" },
                },
            },
        };
        var output = AxiDrive.RenderDriveResult(run, ciReady: false);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains("fixes[1]{step,summary}:", output.Doc);
        Assert.Contains("tightened the retry loop", output.Doc);
        Assert.Contains("acknowledge the misses", output.Doc);
    }

    [Fact]
    public void RenderDriveResultFailedRunExitsOneWithError()
    {
        var run = new RunInfo
        {
            Id = "run-1",
            Branch = "feature",
            Status = RunStatus.Failed,
            HeadSha = "abcdef1234567890",
            Error = "test step exited 1",
        };
        var output = AxiDrive.RenderDriveResult(run, ciReady: false);
        Assert.Equal(1, output.ExitCode);
        Assert.Contains("outcome: failed", output.Doc);
        Assert.Contains("test step exited 1", output.Doc);
    }

    [Fact]
    public void RenderDriveResultGateRendersGateFields()
    {
        var run = new RunInfo
        {
            Id = "run-1",
            Branch = "feature",
            Status = RunStatus.Running,
            HeadSha = "abcdef1234567890",
            Steps = new List<StepResultInfo>
            {
                new()
                {
                    StepName = StepName.Review,
                    Status = StepStatus.AwaitingApproval,
                    FindingsJson = """{"findings":[{"id":"f1","severity":"major","file":"a.go","description":"bug","action":"ask-user"}],"summary":"one bug"}""",
                },
            },
        };
        var output = AxiDrive.RenderDriveResult(run, ciReady: false);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains("step: review", output.Doc);
        Assert.Contains("status: awaiting_approval", output.Doc);
        Assert.Contains("summary: one bug", output.Doc);
        // No top-level outcome field: a gate is a decision point, not an end
        // state (the gate help text mentions `outcome:` inline).
        Assert.DoesNotContain("\noutcome:", output.Doc);
    }

    [Fact]
    public void RenderDriveResultChecksPassedIsDistinctSuccessfulOutcome()
    {
        var run = new RunInfo
        {
            Id = "run-1",
            Branch = "feature",
            Status = RunStatus.Running,
            HeadSha = "abcdef1234567890",
            PrUrl = "https://github.com/u/r/pull/7",
        };
        var output = AxiDrive.RenderDriveResult(run, ciReady: true);
        Assert.Equal(0, output.ExitCode);
        Assert.Contains("outcome: checks-passed", output.Doc);
        Assert.Contains("Ask the user to review and merge it: https://github.com/u/r/pull/7", output.Doc);
        Assert.Contains("CI monitor rebases onto the base", output.Doc);
    }

    [Fact]
    public void RenderAbortByIdOutcomeIncludesDetailOnlyOnNoOp()
    {
        var aborted = AxiDrive.RenderAbortByIdOutcome(new AxiAbortOutcome(true, "run-1", null));
        Assert.Equal(0, aborted.ExitCode);
        Assert.Contains("aborted: true", aborted.Doc);
        Assert.Contains("run: run-1", aborted.Doc);
        Assert.DoesNotContain("detail:", aborted.Doc);

        var noop = AxiDrive.RenderAbortByIdOutcome(
            new AxiAbortOutcome(false, "run-1", "no active run with that id (no-op)"));
        Assert.Equal(0, noop.ExitCode);
        Assert.Contains("aborted: false", noop.Doc);
        Assert.Contains("no active run with that id (no-op)", noop.Doc);
    }
}
