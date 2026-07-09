using NoMistakes.Core;
using NoMistakes.Pipeline;

namespace NoMistakes.Pipeline.Steps;

/// <summary>
/// Runs linters and asks the agent to fix issues. Ports Go's steps.LintStep.
/// The configured-command path runs the trusted lint command; the agent path
/// (no command configured, or fix mode) drives the agent through <see cref="IAgent"/>.
/// </summary>
public sealed class LintStep : IStep
{
    public string Name => StepName.Lint;

    public async Task<StepOutcome> ExecuteAsync(StepContext sctx)
    {
        var baseSha = await StepHelpers.ResolveBranchBaseShaAsync(
            sctx, sctx.Run.BaseSha, sctx.Repo.DefaultBranch).ConfigureAwait(false);
        var lintCmd = sctx.Config?.Commands.Lint ?? string.Empty;

        if (lintCmd.Length == 0)
        {
            sctx.Log("no lint command configured, asking agent to lint and fix...");
            var prompt = $"""
Detect the linting and formatting tools for this project, run the relevant checks yourself, apply safe fixes, and verify the result.

Context:
- branch: {sctx.Run.Branch}
- base commit: {baseSha}
- target commit: {sctx.Run.HeadSha}

Task:
- Discover the configured linters and formatters for this repository.
- Only lint or format the relevant changed files when possible.
- Apply safe formatter, linter, and static-analysis fixes yourself.
- Re-run the relevant checks after fixing.
- Report only unresolved lint, format, or static-analysis issues as structured findings.
- If everything is clean or fixed, return an empty findings array.

Rules:
- Do not run tests or broader behavioral validation.
- Focus on lint, format, and static-analysis issues only.
- Do not report issues you already fixed.
- The summary must be one concise sentence fragment suitable for a git commit subject.
- Keep the summary under 10 words.
""";
            if (sctx.PreviousFindings.Length > 0)
            {
                prompt += "\n\nPrevious lint findings to address:\n" + sctx.PreviousFindings;
            }

            var result = await sctx.Agent!.RunAsync(new AgentRunOpts
            {
                Prompt = prompt,
                Cwd = sctx.WorkDir,
                JsonSchema = StepSchemas.Findings,
                OnChunk = sctx.LogChunk,
            }, sctx.Ct).ConfigureAwait(false);

            var findings = ParseOrText(sctx, result);
            var summary = TryExtractSummary(sctx, result, "lint");
            await StepHelpers.CommitAgentFixesAsync(sctx, Name, summary, "fix lint issues").ConfigureAwait(false);

            return new StepOutcome
            {
                NeedsApproval = StepHelpers.HasBlockingFindings(findings.Items),
                AutoFixable = false,
                Findings = FindingsOps.Marshal(findings),
                FixSummary = summary,
            };
        }

        var fixSummary = string.Empty;
        if (sctx.Fixing)
        {
            var fixPrompt = $"""
Fix the lint issues in this repository. Run the linter, identify all issues, and fix them.

Context:
- branch: {sctx.Run.Branch}
- base commit: {baseSha}
- target commit: {sctx.Run.HeadSha}

Rules:
- Make the smallest correct root-cause fix.
- Do not refactor beyond what is needed for that root-cause fix.
- Do not run tests or broader behavioral validation.
- Re-run the relevant lint or format commands before finishing.
- Return JSON with a single "summary" field when you are done.
- The summary must be one concise sentence fragment suitable for a git commit subject.
- Keep the summary under 10 words.
""";
            if (sctx.PreviousFindings.Length > 0)
            {
                fixPrompt += "\n\nPrevious lint findings to address:\n" + sctx.PreviousFindings;
            }
            fixSummary = await StepHelpers.ExecuteFixModeAsync(sctx, Name, new StepHelpers.FixExecutionOptions
            {
                LogMessage = "asking agent to fix lint issues...",
                Prompt = fixPrompt,
                ErrorPrefix = "agent fix lint",
                FallbackSummary = "fix lint issues",
            }).ConfigureAwait(false);
        }

        sctx.Log($"running linter: {lintCmd}");
        var (output, exitCode) = await StepHelpers.RunShellCommandAsync(sctx, lintCmd).ConfigureAwait(false);
        sctx.Log(output);

        if (exitCode != 0)
        {
            var findings = new Findings
            {
                Items = { new Finding { Severity = "warning", Description = $"linter found issues (exit code {exitCode})" } },
                Summary = output,
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

        sctx.Log("lint passed");
        return new StepOutcome { FixSummary = fixSummary };
    }

    private static Findings ParseOrText(StepContext sctx, AgentResult result)
    {
        if (result.Output == null)
        {
            return new Findings();
        }
        try
        {
            return FindingsParser.Parse(result.Output);
        }
        catch (System.Text.Json.JsonException)
        {
            sctx.Log("could not parse structured output, using text response");
            return new Findings { Summary = result.Text };
        }
    }

    private static string TryExtractSummary(StepContext sctx, AgentResult result, string label)
    {
        try
        {
            return StepHelpers.ExtractCommitSummary(result);
        }
        catch (System.InvalidOperationException ex)
        {
            sctx.Log($"warning: could not parse {label} summary: {ex.Message}");
            return string.Empty;
        }
    }
}
