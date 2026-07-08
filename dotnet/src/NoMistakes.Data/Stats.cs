using NoMistakes.Core;

namespace NoMistakes.Data;

/// <summary>Summarizes historical no-mistakes usage across all repositories. Mirrors Go's db.Stats.</summary>
public sealed class Stats
{
    public int TotalRepos { get; set; }
    public int TotalRuns { get; set; }
    public int PullRequests { get; set; }
    public int RescueRuns { get; set; }
    public int ReportedFindings { get; set; }
    public int FixedFindings { get; set; }
    public List<StepStats> StepStats { get; set; } = new();
    public List<RepoStats> RepoStats { get; set; } = new();
}

/// <summary>Reported and fixed findings for one pipeline step. Mirrors Go's db.StepStats.</summary>
public sealed class StepStats
{
    public string StepName { get; set; } = string.Empty;
    public int ReportedFindings { get; set; }
    public int FixedFindings { get; set; }
}

/// <summary>Historical usage for one repository. Mirrors Go's db.RepoStats.</summary>
public sealed class RepoStats
{
    public string RepoId { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;
    public int Runs { get; set; }
    public int RescueRuns { get; set; }
    public int ReportedFindings { get; set; }
    public int FixedFindings { get; set; }

    /// <summary>Returns a compact repository name for terminal reports.</summary>
    public string DisplayName()
    {
        var name = Path.GetFileName(WorkingPath);
        if (name is "." or "" || name == Path.DirectorySeparatorChar.ToString())
        {
            return WorkingPath;
        }
        return name;
    }
}

public sealed partial class Database
{
    /// <summary>Aggregates historical usage across all repositories.</summary>
    public Stats GetStats()
    {
        var repos = GetReposOrdered();
        var stats = new Stats { TotalRepos = repos.Count };
        var stepStats = new Dictionary<string, StepStats>();

        foreach (var repo in repos)
        {
            var repoStats = new RepoStats { RepoId = repo.Id, WorkingPath = repo.WorkingPath };
            var runs = GetRunsByRepo(repo.Id);
            repoStats.Runs = runs.Count;
            stats.TotalRuns += runs.Count;

            foreach (var run in runs)
            {
                if (!string.IsNullOrEmpty(run.PrUrl))
                {
                    stats.PullRequests++;
                }

                var (runReported, runFixed) = AggregateRunStats(run.Id, stepStats);
                stats.ReportedFindings += runReported;
                stats.FixedFindings += runFixed;
                repoStats.ReportedFindings += runReported;
                repoStats.FixedFindings += runFixed;
                if (runReported > 0 && runFixed > 0)
                {
                    stats.RescueRuns++;
                    repoStats.RescueRuns++;
                }
            }

            stats.RepoStats.Add(repoStats);
        }

        foreach (var step in stepStats.Values)
        {
            if (step.ReportedFindings == 0 && step.FixedFindings == 0)
            {
                continue;
            }
            stats.StepStats.Add(step);
        }
        SortStepStats(stats.StepStats);
        SortRepoStats(stats.RepoStats);

        return stats;
    }

    /// <summary>Returns reported and fixed finding counts for a single step.</summary>
    public StepStats StepFindingStats(StepResult step)
    {
        var rounds = GetRoundsByStep(step.Id);
        return ComputeStepFindingStats(step, rounds);
    }

    /// <summary>Returns how many findings were resolved for a single step.</summary>
    public int FixedFindingsByStep(StepResult step) => StepFindingStats(step).FixedFindings;

    private (int Reported, int Fixed) AggregateRunStats(string runId, Dictionary<string, StepStats> stepStats)
    {
        var runReported = 0;
        var runFixed = 0;
        foreach (var step in GetStepsByRun(runId))
        {
            var rounds = GetRoundsByStep(step.Id);
            var findingStats = ComputeStepFindingStats(step, rounds);
            runReported += findingStats.ReportedFindings;
            runFixed += findingStats.FixedFindings;

            if (!stepStats.TryGetValue(step.StepName, out var stat))
            {
                stat = new StepStats { StepName = step.StepName };
                stepStats[step.StepName] = stat;
            }
            stat.ReportedFindings += findingStats.ReportedFindings;
            stat.FixedFindings += findingStats.FixedFindings;
        }
        return (runReported, runFixed);
    }

    private static StepStats ComputeStepFindingStats(StepResult step, List<StepRound> rounds)
    {
        var stats = new StepStats { StepName = step.StepName };
        if (rounds.Count == 0)
        {
            stats.ReportedFindings = FindingItems(step.FindingsJson).Count;
            return stats;
        }

        var reported = new HashSet<(string Severity, string File, int Line, string Description)>();
        List<Finding> current = new();
        foreach (var round in rounds)
        {
            var items = FindingItems(round.FindingsJson);
            foreach (var item in items)
            {
                reported.Add(FindingStatsKey(item));
            }
            current = items;
        }

        stats.ReportedFindings = reported.Count;
        var fixedFindings = stats.ReportedFindings - current.Count;
        if (fixedFindings < 0)
        {
            fixedFindings = 0;
        }
        if (fixedFindings > stats.ReportedFindings)
        {
            fixedFindings = stats.ReportedFindings;
        }
        stats.FixedFindings = fixedFindings;
        return stats;
    }

    private static List<Finding> FindingItems(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new List<Finding>();
        }
        try
        {
            return FindingsParser.Parse(raw).Items;
        }
        catch
        {
            return new List<Finding>();
        }
    }

    private static (string, string, int, string) FindingStatsKey(Finding item) =>
        (item.Severity, item.File, item.Line, item.Description);

    private static void SortStepStats(List<StepStats> stats) =>
        stats.Sort((a, b) =>
        {
            if (a.FixedFindings != b.FixedFindings)
            {
                return b.FixedFindings - a.FixedFindings;
            }
            if (a.ReportedFindings != b.ReportedFindings)
            {
                return b.ReportedFindings - a.ReportedFindings;
            }
            return StepName.Order(a.StepName) - StepName.Order(b.StepName);
        });

    private static void SortRepoStats(List<RepoStats> stats) =>
        stats.Sort((a, b) =>
        {
            if (a.RescueRuns != b.RescueRuns)
            {
                return b.RescueRuns - a.RescueRuns;
            }
            if (a.FixedFindings != b.FixedFindings)
            {
                return b.FixedFindings - a.FixedFindings;
            }
            if (a.Runs != b.Runs)
            {
                return b.Runs - a.Runs;
            }
            return string.CompareOrdinal(a.WorkingPath, b.WorkingPath);
        });
}
