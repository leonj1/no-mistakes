using NoMistakes.Core;
using NoMistakes.Pipeline;

namespace NoMistakes.Pipeline.Steps;

/// <summary>
/// Reviews the diff for bugs, security issues, and doc gaps. Ports Go's
/// steps.ReviewStep. Review auto-fix is disabled by default (the executor's
/// AutoFixLimit(review) is 0), so blocking/ask-user findings park for an agent
/// decision rather than being silently self-fixed.
/// </summary>
public sealed class ReviewStep : IStep
{
    public string Name => StepName.Review;

    public async Task<StepOutcome> ExecuteAsync(StepContext sctx)
    {
        var baseSha = await StepHelpers.ResolveBranchBaseShaAsync(
            sctx, sctx.Run.BaseSha, sctx.Repo.DefaultBranch).ConfigureAwait(false);
        var branch = sctx.Run.Branch;
        var ignorePatterns = sctx.Config != null && sctx.Config.IgnorePatterns.Count > 0
            ? string.Join(", ", sctx.Config.IgnorePatterns)
            : "none";

        var reviewScope = sctx.Fixing
            ? $"current worktree and HEAD changes relative to base commit {baseSha} (starting head {sctx.Run.HeadSha})"
            : $"branch changes between {baseSha} and {sctx.Run.HeadSha}";

        var fixSummary = string.Empty;
        if (sctx.Fixing)
        {
            var fixPrompt = $"""
Investigate previous review findings and address legitimate ones.

Examine the relevant code yourself and apply fixes directly.

Context:
- branch: {branch}
- base commit: {baseSha}
- target commit: {sctx.Run.HeadSha}
- review scope: {reviewScope}
- default branch: {sctx.Repo.DefaultBranch}
- ignore patterns: {ignorePatterns}

Rules:
- Always start with double checking whether the findings are legitimate.
- Prefer the smallest correct root-cause fix within the changed area over patching only the reported line.
- Do not add code comments explaining your fixes.
- Verify that the issues are resolved before finishing.
- Return JSON with a single "summary" field when you are done.
- The summary must be one concise sentence fragment suitable for a git commit subject.
- Keep the summary under 10 words.

Previous review findings to address:
{sctx.PreviousFindings}
""";
            fixSummary = await StepHelpers.ExecuteFixModeAsync(sctx, Name, new StepHelpers.FixExecutionOptions
            {
                RequirePreviousFindings = true,
                MissingFindingsError = "review fix requires previous review findings",
                LogMessage = "asking agent to fix identified issues...",
                Prompt = fixPrompt,
                ErrorPrefix = "agent fix",
                FallbackSummary = "address review findings",
            }).ConfigureAwait(false);
        }

        var args = sctx.Fixing
            ? new[] { "diff", "--name-only", baseSha }
            : new[] { "diff", "--name-only", baseSha + ".." + sctx.Run.HeadSha };
        var changedFiles = await StepHelpers.GitRunAsync(sctx, args).ConfigureAwait(false);

        var hasReviewableChanges = false;
        foreach (var raw in changedFiles.Split('\n'))
        {
            var path = raw.Trim();
            if (path.Length == 0)
            {
                continue;
            }
            var ignored = false;
            if (sctx.Config != null)
            {
                foreach (var pattern in sctx.Config.IgnorePatterns)
                {
                    if (StepHelpers.MatchIgnorePattern(path, pattern))
                    {
                        ignored = true;
                        break;
                    }
                }
            }
            if (!ignored)
            {
                hasReviewableChanges = true;
                break;
            }
        }

        if (!hasReviewableChanges)
        {
            sctx.Log("no changes to review");
            var noChange = new Findings { RiskLevel = "low", RiskRationale = "no reviewable changes" };
            return new StepOutcome { Findings = FindingsOps.Marshal(noChange), FixSummary = fixSummary };
        }

        sctx.Log("reviewing changes...");
        var prompt = $"""
Review the code changes and return structured findings with a risk assessment.

Context:
- branch: {branch}
- base commit: {baseSha}
- target commit: {sctx.Run.HeadSha}
- review scope: {reviewScope}
- default branch: {sctx.Repo.DefaultBranch}
- ignore patterns: {ignorePatterns}

Task:
- Read the relevant history and diff yourself.
- Focus findings on risks introduced by changed code.
- Do NOT run tests during review. The pipeline has a dedicated test step after review.
- Analyze for bugs, risks, and code simplification opportunities.

Rules:
- Anchor every finding to a specific file and one-indexed line number in the changed code when possible.
- Use severity "error" for problems that should absolutely not get merged, "warning" for things that are worth addressing but can be done in a follow up, and "info" for things that are nice to have.
- Do NOT report styling, formatting, linting, compilation, or type-checking issues.
- If the change is clean, return an empty findings array.
- For each finding, set the action field to "ask-user", "auto-fix", or "no-op".

Risk assessment (after listing all findings):
- Set risk_level to "low", "medium", or "high".
- Provide a one-sentence risk_rationale explaining why you chose that risk level.
""";

        var result = await sctx.Agent!.RunAsync(new AgentRunOpts
        {
            Prompt = prompt,
            Cwd = sctx.WorkDir,
            JsonSchema = StepSchemas.ReviewFindings,
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

        return new StepOutcome
        {
            NeedsApproval = StepHelpers.HasBlockingFindings(findings.Items),
            AutoFixable = findings.Items.Count > 0,
            Findings = FindingsOps.Marshal(findings),
            FixSummary = fixSummary,
        };
    }
}
