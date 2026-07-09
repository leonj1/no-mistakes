using System.Text.Json;
using NoMistakes.Core;
using NoMistakes.Git;
using NoMistakes.Pipeline;
using NoMistakes.Processes;

namespace NoMistakes.Pipeline.Steps;

/// <summary>
/// Shared helpers for the review/test/lint steps, ported from Go's
/// internal/pipeline/steps common_* files. Only the subset the slice-10 steps
/// use is ported; the rebase/push/PR/CI helpers land with later slices.
/// </summary>
internal static class StepHelpers
{
    private static readonly GitClient Git = new();

    /// <summary>
    /// Runs a shell command (sh -c) in the work dir and returns combined output
    /// plus exit code. A non-zero exit is NOT an error; only a spawn failure
    /// throws. Mirrors Go's runStepShellCommand.
    /// </summary>
    public static async Task<(string Output, int ExitCode)> RunShellCommandAsync(
        StepContext sctx, string cmdStr)
    {
        var spec = new ShellCommandSpec("sh", "-c", cmdStr) { WorkingDirectory = sctx.WorkDir };
        var result = await new ShellCommand(spec).CombinedOutputAsync(sctx.Ct).ConfigureAwait(false);
        return (result.Stdout, result.ExitCode);
    }

    /// <summary>True if any finding has error or warning severity. Mirrors Go's hasBlockingFindings.</summary>
    public static bool HasBlockingFindings(IEnumerable<Finding> items) =>
        items.Any(f => f.Severity is "error" or "warning");

    /// <summary>
    /// The branch base commit relative to the default branch (merge-base), falling
    /// back to the run's base SHA or the empty-tree SHA. Mirrors Go's
    /// resolveBranchBaseSHA + resolveBaseSHA.
    /// </summary>
    public static async Task<string> ResolveBranchBaseShaAsync(
        StepContext sctx, string fallbackBaseSha, string defaultBranch)
    {
        var mb = await MergeBaseWithDefaultBranchAsync(sctx.WorkDir, defaultBranch, sctx.Ct).ConfigureAwait(false);
        if (mb.Length > 0)
        {
            return mb;
        }
        if (!GitClient.IsZeroSha(fallbackBaseSha))
        {
            return fallbackBaseSha;
        }
        var mb2 = await MergeBaseWithDefaultBranchAsync(sctx.WorkDir, defaultBranch, sctx.Ct).ConfigureAwait(false);
        return mb2.Length > 0 ? mb2 : GitClient.EmptyTreeSha;
    }

    private static async Task<string> MergeBaseWithDefaultBranchAsync(
        string workDir, string defaultBranch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            return string.Empty;
        }
        foreach (var reference in new[] { "origin/" + defaultBranch, defaultBranch })
        {
            try
            {
                var mb = await Git.RunAsync(workDir, new[] { "merge-base", "HEAD", reference }, ct)
                    .ConfigureAwait(false);
                if (mb.Trim().Length > 0)
                {
                    return mb.Trim();
                }
            }
            catch (GitCommandException)
            {
                // try the next candidate ref
            }
        }
        return string.Empty;
    }

    /// <summary>
    /// Reports whether a path matches a gitignore-like ignore pattern. Mirrors
    /// Go's matchIgnorePattern.
    /// </summary>
    public static bool MatchIgnorePattern(string path, string pattern)
    {
        if (pattern.EndsWith("/**", StringComparison.Ordinal))
        {
            var prefix = pattern[..^3];
            return path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal);
        }
        if (!pattern.Contains('/'))
        {
            return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
                pattern, Path.GetFileName(path), ignoreCase: false);
        }
        return System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(
            pattern, path, ignoreCase: false);
    }

    /// <summary>Normalizes a branch name to a full ref. Mirrors Go's normalizedBranchRef.</summary>
    public static string NormalizedBranchRef(string reference) =>
        reference.StartsWith("refs/", StringComparison.Ordinal) ? reference : "refs/heads/" + reference;

    /// <summary>
    /// Stages, commits, and updates the branch ref for agent-made changes, then
    /// records the new head on the run. A clean worktree is a no-op. Mirrors Go's
    /// commitAgentFixes.
    /// </summary>
    public static async Task CommitAgentFixesAsync(
        StepContext sctx, string stepName, string summary, string fallbackSummary)
    {
        var status = await GitTry(sctx, "status", "--porcelain").ConfigureAwait(false);
        if (status.Trim().Length == 0)
        {
            sctx.Log("no agent changes to commit");
            return;
        }
        await Git.RunAsync(sctx.WorkDir, new[] { "add", "-A" }, sctx.Ct).ConfigureAwait(false);
        if (summary.Length == 0)
        {
            summary = fallbackSummary;
        }
        var commitMessage = DeterministicFixCommitMessage(stepName, summary);
        await Git.RunAsync(sctx.WorkDir, new[] { "commit", "-m", commitMessage }, sctx.Ct).ConfigureAwait(false);
        var headSha = (await Git.RunAsync(sctx.WorkDir, new[] { "rev-parse", "HEAD" }, sctx.Ct).ConfigureAwait(false)).Trim();
        var reference = NormalizedBranchRef(sctx.Run.Branch);
        await Git.RunAsync(sctx.WorkDir, new[] { "update-ref", reference, headSha }, sctx.Ct).ConfigureAwait(false);
        sctx.Run.HeadSha = headSha;
        sctx.Db.UpdateRunHeadSha(sctx.Run.Id, headSha);
        sctx.Log($"committed agent fixes: {commitMessage}");
    }

    private static string DeterministicFixCommitMessage(string stepName, string summary)
    {
        if (summary.Length == 0)
        {
            summary = "apply fixes";
        }
        return $"no-mistakes({stepName}): {summary}";
    }

    /// <summary>Extracts the one-line commit summary from an agent result. Mirrors Go's extractCommitSummary.</summary>
    public static string ExtractCommitSummary(AgentResult result)
    {
        if (result.Output == null)
        {
            throw new InvalidOperationException("agent returned no structured summary");
        }
        string? raw;
        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            raw = doc.RootElement.TryGetProperty("summary", out var s) ? s.GetString() : null;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"parse commit summary: {ex.Message}");
        }
        var cleaned = string.Join(' ', (raw ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Trim(' ', '\t', '\r', '\n', '"', '\'', '.', ';', ':', ',', '-');
    }

    /// <summary>Options controlling a fix-mode agent invocation. Mirrors Go's fixExecutionOptions.</summary>
    public sealed class FixExecutionOptions
    {
        public bool RequirePreviousFindings { get; init; }
        public string MissingFindingsError { get; init; } = string.Empty;
        public string LogMessage { get; init; } = string.Empty;
        public required string Prompt { get; init; }
        public required string ErrorPrefix { get; init; }
        public required string FallbackSummary { get; init; }
        public Func<AgentResult, Task>? AfterAgentRun { get; init; }
    }

    /// <summary>
    /// Runs the fix agent and commits any resulting changes, returning the agent's
    /// one-line fix summary. Mirrors Go's executeFixMode.
    /// </summary>
    public static async Task<string> ExecuteFixModeAsync(
        StepContext sctx, string stepName, FixExecutionOptions opts)
    {
        if (!sctx.Fixing)
        {
            return string.Empty;
        }
        if (opts.RequirePreviousFindings && sctx.PreviousFindings.Length == 0)
        {
            throw new InvalidOperationException(opts.MissingFindingsError);
        }
        if (opts.LogMessage.Length > 0)
        {
            sctx.Log(opts.LogMessage);
        }
        AgentResult result;
        try
        {
            result = await sctx.Agent!.RunAsync(new AgentRunOpts
            {
                Prompt = opts.Prompt,
                Cwd = sctx.WorkDir,
                JsonSchema = StepSchemas.CommitSummary,
                OnChunk = sctx.LogChunk,
            }, sctx.Ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"{opts.ErrorPrefix}: {ex.Message}");
        }
        if (opts.AfterAgentRun != null)
        {
            await opts.AfterAgentRun(result).ConfigureAwait(false);
        }
        var summary = string.Empty;
        try
        {
            summary = ExtractCommitSummary(result);
        }
        catch (InvalidOperationException ex)
        {
            sctx.Log($"warning: could not parse fix summary: {ex.Message}");
        }
        await CommitAgentFixesAsync(sctx, stepName, summary, opts.FallbackSummary).ConfigureAwait(false);
        return summary;
    }

    /// <summary>New test files (untracked or staged-add) in the worktree. Mirrors Go's detectNewTestFiles.</summary>
    public static async Task<List<string>> DetectNewTestFilesAsync(StepContext sctx)
    {
        var outText = await GitTry(sctx, "status", "--porcelain").ConfigureAwait(false);
        var testFiles = new List<string>();
        if (outText.Length == 0)
        {
            return testFiles;
        }
        foreach (var line in outText.Split('\n'))
        {
            if (line.Length < 4)
            {
                continue;
            }
            var statusCode = line[..2];
            var path = line[3..].Trim();
            if (statusCode == "??" || statusCode[0] == 'A')
            {
                if (IsTestFile(path))
                {
                    testFiles.Add(path);
                }
            }
        }
        return testFiles;
    }

    /// <summary>Language-aware test-file detection. Mirrors Go's isTestFile.</summary>
    public static bool IsTestFile(string path)
    {
        var baseName = Path.GetFileName(path);
        if (baseName.Length == 0)
        {
            return false;
        }
        if (baseName.EndsWith("_test.go", StringComparison.Ordinal)) return true;
        if (baseName.EndsWith("_test.rs", StringComparison.Ordinal)) return true;
        if (baseName.EndsWith(".py", StringComparison.Ordinal))
        {
            var name = baseName[..^3];
            if (name.StartsWith("test_", StringComparison.Ordinal) || name.EndsWith("_test", StringComparison.Ordinal))
            {
                return true;
            }
        }
        if (baseName.EndsWith(".rb", StringComparison.Ordinal) && baseName.StartsWith("test_", StringComparison.Ordinal))
        {
            return true;
        }
        if (baseName.EndsWith("Test.java", StringComparison.Ordinal) || baseName.EndsWith("Tests.java", StringComparison.Ordinal))
        {
            return true;
        }
        foreach (var ext in new[] { ".js", ".ts", ".jsx", ".tsx" })
        {
            if (baseName.EndsWith(".test" + ext, StringComparison.Ordinal) || baseName.EndsWith(".spec" + ext, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // git.Run that swallows errors, returning "" (used where Go ignores the error).
    private static async Task<string> GitTry(StepContext sctx, params string[] args)
    {
        try
        {
            return await Git.RunAsync(sctx.WorkDir, args, sctx.Ct).ConfigureAwait(false);
        }
        catch (GitCommandException)
        {
            return string.Empty;
        }
    }

    // git.Run surfacing failures (used where Go propagates the error).
    public static Task<string> GitRunAsync(StepContext sctx, params string[] args) =>
        Git.RunAsync(sctx.WorkDir, args, sctx.Ct);
}
