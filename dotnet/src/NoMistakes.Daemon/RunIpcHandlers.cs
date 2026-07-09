using System.Text.Json;
using NoMistakes.Data;
using NoMistakes.Ipc;

namespace NoMistakes.Daemon;

/// <summary>
/// Registers the run-status and run-cancel IPC methods on a server. Ported
/// from the run-related handlers in Go internal/daemon/daemon.go
/// registerHandlers; push_received/rerun/respond/subscribe arrive with the
/// slices that port their machinery.
/// </summary>
public static class RunIpcHandlers
{
    public static void Register(IpcServer server, RunManager manager, Database db)
    {
        server.Handle(Methods.GetRun, (parameters, _) =>
        {
            var p = RequireParams<GetRunParams>(parameters);
            var run = db.GetRun(p.RunId)
                ?? throw new InvalidOperationException($"run not found: {p.RunId}");
            var steps = db.GetStepsByRun(p.RunId);
            return Task.FromResult<object?>(new GetRunResult
            {
                Run = RunInfoMapper.RunToInfo(db, run, steps),
            });
        });

        server.Handle(Methods.GetRuns, (parameters, _) =>
        {
            var p = RequireParams<GetRunsParams>(parameters);
            var infos = new List<RunInfo>();
            foreach (var run in db.GetRunsByRepo(p.RepoId))
            {
                infos.Add(RunInfoMapper.RunToInfo(db, run, db.GetStepsByRun(run.Id)));
            }
            return Task.FromResult<object?>(new GetRunsResult { Runs = infos });
        });

        server.Handle(Methods.GetActiveRun, (parameters, _) =>
        {
            var p = RequireParams<GetActiveRunParams>(parameters);
            var run = db.GetActiveRun(p.RepoId, p.Branch ?? string.Empty);
            if (run == null)
            {
                return Task.FromResult<object?>(new GetActiveRunResult());
            }
            return Task.FromResult<object?>(new GetActiveRunResult
            {
                Run = RunInfoMapper.RunToInfo(db, run, db.GetStepsByRun(run.Id)),
            });
        });

        server.Handle(Methods.CancelRun, (parameters, _) =>
        {
            var p = RequireParams<CancelRunParams>(parameters);
            manager.HandleCancel(p.RunId);
            return Task.FromResult<object?>(new CancelRunResult { Ok = true });
        });
    }

    private static T RequireParams<T>(JsonElement? parameters)
    {
        if (parameters == null)
        {
            throw new ArgumentException("invalid params: missing");
        }
        try
        {
            return IpcJson.Deserialize<T>(parameters.Value)
                ?? throw new ArgumentException("invalid params: null");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"invalid params: {ex.Message}");
        }
    }
}
