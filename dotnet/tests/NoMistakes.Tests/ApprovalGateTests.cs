using NoMistakes.Cli;
using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Pipeline;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports the awaiting-agent marker invariants from Go's
/// TestExecutor_AwaitingAgentMarkerSetOnGateClearedOnRespond: the run-level
/// parked marker is set before the gate becomes observable to pollers, is
/// non-null exactly while the step is parked, and is cleared on both the
/// respond and cancel exit paths. Also covers the gate's respond validation
/// and the parked run object render over a real DB row.
/// </summary>
public class ApprovalGateTests
{
    private static (Run Run, StepResult Step) Setup(Database db)
    {
        var repo = db.InsertRepo("/home/user/project", "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", "abc123", "def456");
        var step = db.InsertStepResult(run.Id, StepName.Review);
        return (run, step);
    }

    [Fact]
    public async Task MarkerIsSetBeforeGateBecomesObservable()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var (run, step) = Setup(db);
        var gate = new ApprovalGate();

        // onParked is the moment the executor flips the step status to the
        // gate state — the first instant a poller can observe the parked
        // step. The run marker (and the gate's readiness to accept a respond)
        // must already be in place here.
        long? sinceAtObserve = null;
        var wait = gate.ParkAsync(db, run.Id, StepName.Review, onParked: () =>
        {
            sinceAtObserve = db.GetRun(run.Id)!.AwaitingAgentSince;
            db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval);
            gate.Respond(StepName.Review, ApprovalAction.Approve);
        }, TestContext.Current.CancellationToken);

        var response = await wait;
        Assert.NotNull(sinceAtObserve);
        Assert.Equal(ApprovalAction.Approve, response.Action);
    }

    [Fact]
    public async Task RespondClearsMarkerAndDeliversResponse()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var (run, step) = Setup(db);
        var gate = new ApprovalGate();

        var wait = gate.ParkAsync(db, run.Id, StepName.Review,
            onParked: () => db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval),
            TestContext.Current.CancellationToken);

        Assert.NotNull(db.GetRun(run.Id)!.AwaitingAgentSince);

        gate.RespondWithOverrides(StepName.Review, ApprovalAction.Fix,
            findingIds: new List<string> { "f1", "f2" },
            instructions: new Dictionary<string, string> { ["f1"] = "note" },
            addedFindings: new List<Finding> { new() { Description = "extra" } });

        var response = await wait;
        Assert.Equal(ApprovalAction.Fix, response.Action);
        Assert.Equal(new[] { "f1", "f2" }, response.FindingIds);
        Assert.Equal("note", response.Instructions!["f1"]);
        Assert.Equal("extra", Assert.Single(response.AddedFindings!).Description);

        // Resumed: the marker is non-null only while actually parked.
        Assert.Null(db.GetRun(run.Id)!.AwaitingAgentSince);
    }

    [Fact]
    public async Task CancelClearsMarkerAndThrows()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var (run, _) = Setup(db);
        var gate = new ApprovalGate();
        using var cts = new CancellationTokenSource();

        var wait = gate.ParkAsync(db, run.Id, StepName.Review, onParked: null, cts.Token);
        Assert.NotNull(db.GetRun(run.Id)!.AwaitingAgentSince);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        // The wait ended without a respond — the marker still clears so a
        // cancelled run is never reported as parked.
        Assert.Null(db.GetRun(run.Id)!.AwaitingAgentSince);

        // The gate is disarmed after cancellation.
        var ex = Assert.Throws<InvalidOperationException>(
            () => gate.Respond(StepName.Review, ApprovalAction.Approve));
        Assert.Equal("no step awaiting approval", ex.Message);
    }

    [Fact]
    public void RespondWithoutParkedStepThrows()
    {
        var gate = new ApprovalGate();
        var ex = Assert.Throws<InvalidOperationException>(
            () => gate.Respond(StepName.Review, ApprovalAction.Approve));
        Assert.Equal("no step awaiting approval", ex.Message);
    }

    [Fact]
    public async Task RespondToWrongStepThrowsAndKeepsGateParked()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var (run, _) = Setup(db);
        var gate = new ApprovalGate();
        using var cts = new CancellationTokenSource();

        var wait = gate.ParkAsync(db, run.Id, StepName.Review, onParked: null, cts.Token);

        var ex = Assert.Throws<InvalidOperationException>(
            () => gate.Respond(StepName.Test, ApprovalAction.Approve));
        Assert.Equal(
            "step mismatch: responding to \"test\" but \"review\" is awaiting approval",
            ex.Message);

        // A rejected respond leaves the run parked and the gate still armed.
        Assert.NotNull(db.GetRun(run.Id)!.AwaitingAgentSince);
        gate.Respond(StepName.Review, ApprovalAction.Skip);
        Assert.Equal(ApprovalAction.Skip, (await wait).Action);
        Assert.Null(db.GetRun(run.Id)!.AwaitingAgentSince);
    }

    [Fact]
    public async Task ParkedRunRendersAwaitingAgentOnlyWhileParked()
    {
        using var dir = new TempDir();
        using var db = DataTestSupport.OpenTestDb(dir);
        var (run, step) = Setup(db);
        db.UpdateRunStatus(run.Id, RunStatus.Running);
        var gate = new ApprovalGate();

        var wait = gate.ParkAsync(db, run.Id, StepName.Review,
            onParked: () => db.UpdateStepStatus(step.Id, StepStatus.AwaitingApproval),
            TestContext.Current.CancellationToken);

        // While parked and non-terminal the run object carries the signal.
        var parkedDoc = AxiRender.Doc(AxiRender.RunObjectField(
            RunView.FromDb(db.GetRun(run.Id)!, db.GetStepsByRun(run.Id))));
        Assert.Contains("awaiting_agent: parked ", parkedDoc);

        gate.Respond(StepName.Review, ApprovalAction.Approve);
        await wait;

        // After the respond the signal is gone from the same read path.
        var resumedDoc = AxiRender.Doc(AxiRender.RunObjectField(
            RunView.FromDb(db.GetRun(run.Id)!, db.GetStepsByRun(run.Id))));
        Assert.DoesNotContain("awaiting_agent", resumedDoc);
    }
}
