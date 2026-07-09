using NoMistakes.Config;
using NoMistakes.Core;
using NoMistakes.Data;
using NoMistakes.Pipeline;
using NoMistakes.Pipeline.Steps;
using Xunit;

namespace NoMistakes.Tests;

/// <summary>
/// Ports the review/test/lint step behaviors from Go's internal/pipeline/steps
/// suite that are reachable without a live agent: the trusted configured-command
/// paths, the agent-driven fallback (via a fake agent), review finding severity
/// handling and the no-reviewable-changes path, new-test-file detection, and the
/// action-based park semantics (info findings do not park; review auto-fix stays
/// disabled by default). Uses real git repos and a fake agent, mirroring Go.
/// </summary>
public class StepsTests
{
    // A scripted fake agent: returns a queued output for each Run call and records
    // whether it was invoked. Mirrors Go's fakeAgent.
    private sealed class FakeAgent : IAgent
    {
        private readonly Queue<AgentResult> results;
        public int Calls { get; private set; }
        public List<AgentRunOpts> Received { get; } = new();

        public FakeAgent(params AgentResult[] scripted)
        {
            results = new Queue<AgentResult>(scripted);
        }

        public Task<AgentResult> RunAsync(AgentRunOpts opts, CancellationToken ct)
        {
            Calls++;
            Received.Add(opts);
            var r = results.Count > 0 ? results.Dequeue() : new AgentResult();
            return Task.FromResult(r);
        }
    }

    private sealed record Harness(
        Database Db, Run Run, Repo Repo, string WorkDir, TempDir Dir) : IDisposable
    {
        public void Dispose()
        {
            Db.Dispose();
            Dir.Dispose();
        }
    }

    private static Harness NewHarness()
    {
        var dir = new TempDir();
        var db = DataTestSupport.OpenTestDb(dir);
        var work = dir.File("work");
        Directory.CreateDirectory(work);
        GitTestSupport.InitRepo(work);
        var baseSha = GitTestSupport.WriteAndCommit(work, "a.txt", "one\n", "base");
        // Branch off the base so merge-base(HEAD, main) == baseSha and the feature
        // commit is a reviewable delta (mirrors a real gated branch).
        GitTestSupport.Git(work, "checkout", "-q", "-b", "feature");
        var headSha = GitTestSupport.WriteAndCommit(work, "b.txt", "two\n", "change");
        var repo = db.InsertRepo(work, "git@github.com:user/project.git", "main");
        var run = db.InsertRun(repo.Id, "feature", headSha, baseSha);
        return new Harness(db, run, repo, work, dir);
    }

    private static StepContext Ctx(Harness h, IAgent? agent = null, Config.Config? config = null, bool fixing = false, string previousFindings = "") =>
        new()
        {
            Ct = CancellationToken.None,
            Run = h.Run,
            Repo = h.Repo,
            WorkDir = h.WorkDir,
            Agent = agent,
            Config = config ?? new Config.Config(),
            Db = h.Db,
            StepResultId = h.Db.InsertStepResult(h.Run.Id, StepName.Review).Id,
            Fixing = fixing,
            PreviousFindings = previousFindings,
        };

    private static Config.Config ConfigWith(string? lint = null, string? test = null)
    {
        var cfg = new Config.Config();
        if (lint != null) cfg.Commands.Lint = lint;
        if (test != null) cfg.Commands.Test = test;
        return cfg;
    }

    // --- Lint ---

    [Fact]
    public async Task Lint_ConfiguredCommand_PassesCleanly()
    {
        using var h = NewHarness();
        var step = new LintStep();
        var outcome = await step.ExecuteAsync(Ctx(h, config: ConfigWith(lint: "true")));

        Assert.False(outcome.NeedsApproval);
        Assert.Equal(0, outcome.ExitCode);
    }

    [Fact]
    public async Task Lint_ConfiguredCommand_FailureParksAndIsAutoFixable()
    {
        using var h = NewHarness();
        var step = new LintStep();
        var outcome = await step.ExecuteAsync(Ctx(h, config: ConfigWith(lint: "exit 3")));

        Assert.True(outcome.NeedsApproval);
        Assert.True(outcome.AutoFixable);
        Assert.Equal(3, outcome.ExitCode);
        var findings = FindingsParser.Parse(outcome.Findings);
        Assert.Equal("warning", Assert.Single(findings.Items).Severity);
    }

    [Fact]
    public async Task Lint_NoCommand_DrivesAgentAndCommitsFixes()
    {
        using var h = NewHarness();
        // Agent "fixes" by touching a file, then returns a clean findings payload
        // with a summary. The step commits the working-tree change.
        var agent = new FakeAgent(new AgentResult
        {
            Output = "{\"findings\":[],\"summary\":\"tidy imports\"}",
        });
        // Simulate the agent editing the tree before the step commits.
        File.WriteAllText(Path.Combine(h.WorkDir, "c.txt"), "three\n");

        var step = new LintStep();
        var outcome = await step.ExecuteAsync(Ctx(h, agent: agent));

        Assert.Equal(1, agent.Calls);
        Assert.False(outcome.NeedsApproval);
        Assert.Equal("tidy imports", outcome.FixSummary);
        // The agent change was committed and advanced HEAD.
        var head = GitTestSupport.Git(h.WorkDir, "rev-parse", "HEAD");
        Assert.Equal(head, h.Db.GetRun(h.Run.Id)!.HeadSha);
    }

    // --- Review ---

    [Fact]
    public async Task Review_BlockingFinding_Parks()
    {
        using var h = NewHarness();
        var agent = new FakeAgent(new AgentResult
        {
            Output = "{\"findings\":[{\"severity\":\"error\",\"description\":\"bug\",\"action\":\"auto-fix\"}],\"risk_level\":\"high\",\"risk_rationale\":\"r\"}",
        });
        var step = new ReviewStep();
        var outcome = await step.ExecuteAsync(Ctx(h, agent: agent));

        Assert.True(outcome.NeedsApproval);
        Assert.True(outcome.AutoFixable);
    }

    [Fact]
    public async Task Review_InfoOnlyFinding_DoesNotPark()
    {
        using var h = NewHarness();
        var agent = new FakeAgent(new AgentResult
        {
            Output = "{\"findings\":[{\"severity\":\"info\",\"description\":\"note\",\"action\":\"no-op\"}],\"risk_level\":\"low\",\"risk_rationale\":\"r\"}",
        });
        var step = new ReviewStep();
        var outcome = await step.ExecuteAsync(Ctx(h, agent: agent));

        // Info/no-op does not park: NeedsApproval false and no ask-user finding.
        Assert.False(outcome.NeedsApproval);
    }

    [Fact]
    public async Task Review_NoReviewableChanges_SkipsAgent()
    {
        using var h = NewHarness();
        // Ignore every changed file so nothing is reviewable.
        var cfg = new Config.Config();
        cfg.IgnorePatterns.Add("*.txt");
        var agent = new FakeAgent();
        var step = new ReviewStep();
        var outcome = await step.ExecuteAsync(Ctx(h, agent: agent, config: cfg));

        Assert.Equal(0, agent.Calls);
        Assert.False(outcome.NeedsApproval);
        var findings = FindingsParser.Parse(outcome.Findings);
        Assert.Equal("low", findings.RiskLevel);
    }

    // --- Test ---

    [Fact]
    public async Task Test_ConfiguredCommand_PassesRecordsTested()
    {
        using var h = NewHarness();
        var step = new TestStep();
        var outcome = await step.ExecuteAsync(Ctx(h, config: ConfigWith(test: "true")));

        Assert.False(outcome.NeedsApproval);
        var findings = FindingsParser.Parse(outcome.Findings);
        Assert.Contains("true", findings.Tested);
    }

    [Fact]
    public async Task Test_ConfiguredCommand_FailureParksAndIsAutoFixable()
    {
        using var h = NewHarness();
        var step = new TestStep();
        var outcome = await step.ExecuteAsync(Ctx(h, config: ConfigWith(test: "exit 1")));

        Assert.True(outcome.NeedsApproval);
        Assert.True(outcome.AutoFixable);
        Assert.Equal(1, outcome.ExitCode);
        var findings = FindingsParser.Parse(outcome.Findings);
        Assert.Equal("error", Assert.Single(findings.Items).Severity);
    }

    [Fact]
    public async Task Test_NoCommand_DrivesAgentAndDetectsNewTestFile()
    {
        using var h = NewHarness();
        var agent = new FakeAgent(new AgentResult
        {
            Output = "{\"findings\":[],\"summary\":\"\",\"tested\":[\"go test\"],\"testing_summary\":\"ok\",\"artifacts\":[]}",
        });
        // Agent writes a new test file into the worktree.
        File.WriteAllText(Path.Combine(h.WorkDir, "widget_test.go"), "package x\n");

        var step = new TestStep();
        var outcome = await step.ExecuteAsync(Ctx(h, agent: agent));

        Assert.Equal(1, agent.Calls);
        // New test file forces a park with an info finding, non-auto-fixable.
        Assert.True(outcome.NeedsApproval);
        Assert.False(outcome.AutoFixable);
        var findings = FindingsParser.Parse(outcome.Findings);
        Assert.Contains(findings.Items, f => f.File == "widget_test.go" && f.Severity == "info");
    }

    // --- Helpers ---

    [Fact]
    public void MatchIgnorePattern_MatchesGitignoreSemantics()
    {
        Assert.True(StepHelpers.MatchIgnorePattern("vendor/pkg/foo.go", "vendor/**"));
        Assert.True(StepHelpers.MatchIgnorePattern("pkg/foo.generated.go", "*.generated.go"));
        Assert.True(StepHelpers.MatchIgnorePattern("a/b.txt", "a/b.txt"));
        Assert.False(StepHelpers.MatchIgnorePattern("src/foo.go", "vendor/**"));
    }

    [Theory]
    [InlineData("foo_test.go", true)]
    [InlineData("bar_test.rs", true)]
    [InlineData("test_x.py", true)]
    [InlineData("x_test.py", true)]
    [InlineData("WidgetTest.java", true)]
    [InlineData("a.test.ts", true)]
    [InlineData("a.spec.tsx", true)]
    [InlineData("main.go", false)]
    public void IsTestFile_DetectsAcrossLanguages(string path, bool expected)
    {
        Assert.Equal(expected, StepHelpers.IsTestFile(path));
    }
}
