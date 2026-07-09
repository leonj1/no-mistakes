using NoMistakes.Core;
using NoMistakes.Pipeline;

namespace NoMistakes.Pipeline.Steps;

/// <summary>
/// Runs baseline tests, gathers evidence for user intent, and optionally asks the
/// agent to fix failures. Ports Go's steps.TestStep. The configured test command
/// is loaded from the trusted default-branch config; when absent the agent runs
/// the tests.
///
/// The rich in-repo evidence-directory resolution (Go's resolveTestEvidenceLocation)
/// is simplified here to a per-run temp evidence dir; the full evidence placement
/// lands with the evidence/init slices.
/// </summary>
public sealed class TestStep : IStep
{
    public string Name => StepName.Test;

    public async Task<StepOutcome> ExecuteAsync(StepContext sctx)
    {
        var baseSha = await StepHelpers.ResolveBranchBaseShaAsync(
            sctx, sctx.Run.BaseSha, sctx.Repo.DefaultBranch).ConfigureAwait(false);

        var newTestsFromFix = new List<string>();
        var fixSummary = string.Empty;
        if (sctx.Fixing)
        {
            var fixPrompt = $"""
Fix the failing tests in this repository. Run the tests, identify failures, and fix either the tests or the code to make them pass.

Context:
- branch: {sctx.Run.Branch}
- base commit: {baseSha}
- target commit: {sctx.Run.HeadSha}

Rules:
- Make the smallest correct root-cause fix.
- Do not refactor beyond what is needed for that root-cause fix.
- Do NOT run linters, formatters, or static analysis tools.
- Re-run the relevant tests before finishing.
- Return JSON with a single "summary" field when you are done.
- The summary must be one concise sentence fragment suitable for a git commit subject.
- Keep the summary under 10 words.
""";
            fixSummary = await StepHelpers.ExecuteFixModeAsync(sctx, Name, new StepHelpers.FixExecutionOptions
            {
                LogMessage = "asking agent to fix test failures...",
                Prompt = fixPrompt,
                ErrorPrefix = "agent fix tests",
                FallbackSummary = "fix test failures",
                AfterAgentRun = async _ =>
                {
                    newTestsFromFix = await StepHelpers.DetectNewTestFilesAsync(sctx).ConfigureAwait(false);
                },
            }).ConfigureAwait(false);
        }

        var testCmd = sctx.Config?.Commands.Test ?? string.Empty;
        var tested = new List<string>();
        if (testCmd.Length > 0)
        {
            sctx.Log($"running tests: {testCmd}");
            var (output, exitCode) = await StepHelpers.RunShellCommandAsync(sctx, testCmd).ConfigureAwait(false);
            tested.Add(testCmd);
            sctx.Log(output);

            if (exitCode != 0)
            {
                var findings = new Findings
                {
                    Items = { new Finding { Severity = "error", Description = $"tests failed with exit code {exitCode}" } },
                    Summary = output,
                    Tested = new List<string>(tested),
                };
                return new StepOutcome
                {
                    NeedsApproval = true,
                    AutoFixable = true,
                    Findings = FindingsOps.Marshal(findings),
                    ExitCode = exitCode,
                    FixSummary = fixSummary,
                };
            }
        }

        var useEvidenceAgent = testCmd.Length == 0 || sctx.UserIntent.Trim().Length > 0;
        if (useEvidenceAgent)
        {
            var evidenceDir = Path.Combine(Path.GetTempPath(), "no-mistakes-evidence-" + sctx.Run.Id);
            Directory.CreateDirectory(evidenceDir);
            sctx.Log(testCmd.Length == 0
                ? "no test command configured, asking agent to run tests..."
                : "user intent available, asking agent to gather test evidence...");

            var configuredTestCommand = testCmd.Length > 0
                ? $"\nConfigured test command already ran successfully as baseline: `{testCmd}`\n"
                : string.Empty;
            var prompt = $"""
You are validating a code change by testing it. Examine the repository and run the appropriate tests yourself.

Context:
- branch: {sctx.Run.Branch}
- base commit: {baseSha}
- target commit: {sctx.Run.HeadSha}
{configuredTestCommand}

Task:
- Understand the user intent before testing it.
- Demonstrate the user intent working end-to-end in a way consistent with how an end user would actually experience it.
- Write new evidence files into this evidence directory: {evidenceDir}
- Include a concise "testing_summary" sentence describing what you exercised and the overall result.
- Record the exact tests, manual checks, and evidence-producing steps you ran in a "tested" array.
- Always include an "artifacts" array. Leave it empty when you produced no reviewer-visible evidence artifacts.

Rules:
- Do NOT run linters, formatters, or static analysis tools.
- Only report actionable findings: test failures, unfixable setup issues, flaky tests, or missing evidence.
- If all tests pass and there are no issues, return an empty findings array.
- Set action to "ask-user" for missing-evidence warnings, "auto-fix" for objective test failures, "no-op" for informational notes.
""";

            var result = await sctx.Agent!.RunAsync(new AgentRunOpts
            {
                Prompt = prompt,
                Cwd = sctx.WorkDir,
                JsonSchema = StepSchemas.TestFindings,
                OnChunk = sctx.LogChunk,
            }, sctx.Ct).ConfigureAwait(false);

            Findings findings;
            if (result.Output == null)
            {
                findings = new Findings();
            }
            else
            {
                try
                {
                    findings = FindingsParser.Parse(result.Output);
                }
                catch (System.Text.Json.JsonException)
                {
                    sctx.Log("could not parse structured output, using text response");
                    findings = new Findings { Summary = result.Text };
                }
            }
            if (tested.Count > 0)
            {
                findings.Tested = tested.Concat(findings.Tested).ToList();
            }

            var needsApproval = StepHelpers.HasBlockingFindings(findings.Items);
            var autoFixable = needsApproval;

            var newTests = await StepHelpers.DetectNewTestFilesAsync(sctx).ConfigureAwait(false);
            if (newTests.Count > 0)
            {
                needsApproval = true;
                autoFixable = false;
                foreach (var f in newTests)
                {
                    findings.Items.Add(new Finding
                    {
                        Severity = "info",
                        File = f,
                        Description = $"new test file written by agent: {f}",
                    });
                }
            }

            return new StepOutcome
            {
                NeedsApproval = needsApproval,
                AutoFixable = autoFixable,
                Findings = FindingsOps.Marshal(findings),
                FixSummary = fixSummary,
            };
        }

        if (sctx.Fixing && newTestsFromFix.Count > 0)
        {
            var findings = new Findings
            {
                Summary = "tests passed, but agent wrote new test files",
                Tested = new List<string>(tested),
            };
            foreach (var f in newTestsFromFix)
            {
                findings.Items.Add(new Finding
                {
                    Severity = "info",
                    File = f,
                    Description = $"new test file written by agent: {f}",
                });
            }
            return new StepOutcome
            {
                NeedsApproval = true,
                Findings = FindingsOps.Marshal(findings),
                FixSummary = fixSummary,
            };
        }

        sctx.Log("all tests passed");
        return new StepOutcome
        {
            Findings = FindingsOps.Marshal(new Findings { Tested = new List<string>(tested) }),
            FixSummary = fixSummary,
        };
    }
}
