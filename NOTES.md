# Porting notes for later steps

## Slice 6a.1 (branch `claude/vertical-slice-six-scm`)

- Slice 6 branch is cut from `origin/main` (slice-4 merge `9226807`), NOT from
  the slice-5 branch. Slice 5 (`claude/vertical-slice-five-shell`, PR #7) is
  still open, so this branch has no `NoMistakes.Processes` project and the test
  csproj has no Processes reference. If PR #7 merges while slice 6 is in
  flight, expect a trivial sln/test-csproj merge conflict (both add project
  entries/references at the same anchors).
- Shared safeurl surface lives at `dotnet/src/NoMistakes.Scm/SafeUrl/Redactor.cs`,
  namespace `NoMistakes.Scm.SafeUrl`, static class `Redactor`
  (`RedactText`, `Redact`). Mirrors Go `internal/safeurl`. The slice-4 local
  copy in `NoMistakes.Git` was deleted; `GitClient` now references
  `NoMistakes.Scm` (`NoMistakes.Git.csproj` has the ProjectReference).
  Later SCM code (slices 6a.2+) goes in the same project — provider parsing
  probably under namespace `NoMistakes.Scm` proper, keeping `SafeUrl` as the
  sub-namespace analogous to Go's separate `safeurl` package.
- `NoMistakes.Scm.csproj` follows the repo convention: bare SDK project,
  `InternalsVisibleTo NoMistakes.Tests`, no explicit TFM (comes from
  `dotnet/Directory.Build.props`: net10.0, nullable, warnings-as-errors).
- Redactor tests moved to `dotnet/tests/NoMistakes.Tests/SafeUrlRedactorTests.cs`
  (class name `RedactorTests` kept); removed from `GitHookAndEnvTests.cs`.
- Solution edited by hand (no local dotnet SDK — Docker only). `NoMistakes.Scm`
  GUID: `{4F8D2B6A-1C3E-4E7B-9A5D-8E2F0C1B3D4E}`, nested under the `src`
  solution folder `{827E0CD3-B72D-47B6-A68D-7590B98EB39B}`. New projects need:
  Project entry, 12 config lines (Debug/Release × Any CPU/x64/x86, all mapping
  to Any CPU), and a NestedProjects line. The sln starts with a UTF-8 BOM —
  preserve it when scripting edits (`encoding='utf-8-sig'`).
- Verification: `docker build -f Dockerfile.test.dotnet .` — 185 tests passed
  after the move (baseline on main plus relocated Redactor tests).
- `Redactor.Redact` keeps the `redacted:@` → `redacted@` normalization hack to
  match the Go oracle (`url.User("redacted")` renders without the colon).

## Slice 6a.2

- Provider detection lives at `dotnet/src/NoMistakes.Scm/ProviderDetector.cs`:
  `ProviderDetector.Detect(url)` returning enum `Provider` (`Provider.cs`:
  Unknown/GitHub/GitLab/Bitbucket/AzureDevOps — enum, not Go's strings; add a
  string mapping when the DB layer needs it).
- Enterprise-host fallback (self-hosted GitLab via glab `config.yml` hosts +
  `api_host`, GHE via gh `hosts.yml`) is ported, env-driven like Go:
  `GLAB_CONFIG_DIR`/`GH_CONFIG_DIR`, then `XDG_CONFIG_HOME`, then
  `~/.config/...`. Fails closed to `Provider.Unknown` on missing/malformed
  config. YAML read via YamlDotNet 13.7.1 (same version as `NoMistakes.Config`
  — pinned; keep versions aligned if bumping).
- `RemoteHost.ExtractHost` (`RemoteHost.cs`) is ported but **internal** —
  detection needs it. Slice 6a.3 promotes it to the public helper surface
  (rename/move as it sees fit) and adds its dedicated tests; `InternalsVisibleTo
  NoMistakes.Tests` already lets tests reach it meanwhile.
- Azure DevOps parsing: `NoMistakes.Scm.AzureDevOps.RemoteUrl.TryParseRemote`
  (out `AzureDevOpsRemote(OrgUrl, Project, Repo)` record struct) plus internal
  `WebPRUrl` (Go's private `webPRURL`, ported now because it is pure URL logic
  with tests; the az command wrapper in 6b.2/12 consumes it).
- Detection tests mutate `GLAB_CONFIG_DIR`/`GH_CONFIG_DIR` process-globally with
  save/restore (`WithConfigDirs` helper in `ProviderDetectorTests.cs`). Safe
  because xunit runs tests within one class serially and no other test class
  reads those vars — keep any future test touching them inside that class (or
  give it a shared collection).
- Go's `TestDetectProvider_SelfHostedGitLabViaGlabConfig` hosts
  (`gitlab.example.com`) actually match the `"gitlab."` substring branch before
  the config fallback; only the `api_host`/`git.example.com` cases exercise the
  glab-config path. Ported faithfully anyway.
- Docker verification: 197 tests passed (185 baseline + 12 new).

## Slice 6a.3

- `RemoteHost.ExtractHost` promoted to `public` in place
  (`dotnet/src/NoMistakes.Scm/RemoteHost.cs`) — no rename/move; this IS the
  public helper surface for Go's `scm.ExtractHost`.
- Provider helpers follow the AzureDevOps convention (static `RemoteUrl` class
  per provider sub-namespace):
  - `NoMistakes.Scm.GitHub.RemoteUrl.RepoSlug` (Go `github.RepoSlug`) at
    `dotnet/src/NoMistakes.Scm/GitHub/RemoteUrl.cs`.
  - `NoMistakes.Scm.GitLab.RemoteUrl.ProjectPath` (Go `gitlab.ProjectPath`,
    subgroups + Windows-drive-path exclusion) at
    `dotnet/src/NoMistakes.Scm/GitLab/RemoteUrl.cs`.
  Three classes named `RemoteUrl` now exist (AzureDevOps/GitHub/GitLab);
  consumers referencing more than one need using-aliases (see
  `ScmUrlHelperTests.cs`).
- GitLab `ProjectPath` URL branch uses `Uri.TryCreate` +
  `Uri.UnescapeDataString(AbsolutePath)` to mirror Go's decoded `url.Parse`
  path; malformed URLs yield "" like Go's parse-error branch.
- Go's `github.HostPrefixedSlug` (GHE `host/owner/name` for gh `--repo`) NOT
  ported yet — deferred to slice 6b.1, which builds the gh command wrapper
  that consumes it.
- Tests: `dotnet/tests/NoMistakes.Tests/ScmUrlHelperTests.cs` (xunit Theory
  ports of Go `TestExtractHost`/`TestRepoSlug`/`TestProjectPath`).
- Docker verification: 235 tests passed (197 baseline + 38 new).

## Slice 6b.1

- Command wrappers live in `NoMistakes.Scm` as `NoMistakes.Scm.GitHub.Host` and
  `NoMistakes.Scm.GitLab.Host` (same per-provider-namespace convention as
  `RemoteUrl`; consumers referencing both need using-aliases). Both take a
  positional ctor `(CommandRunner run, Func<bool>? cliAvailable, string host,
  string repo|projectPath)` mirroring Go's `New`; `"", ""` reproduces the
  legacy unscoped behavior. 6b.2 should follow this exact shape.
- CLI execution is abstracted as delegate `NoMistakes.Scm.CommandRunner`
  (`CommandRunning.cs`): `(name, args, stdin, ct) -> Task<CommandResult>`
  (Go's per-package `CmdFactory`). **No real process-spawning implementation
  exists yet** — deliberately deferred so it can be built on slice-5
  `NoMistakes.Processes`/`ShellCommand` once PR #7 merges (the wrapper needs
  the process-tree/reaping semantics from CLAUDE.md). Tests use
  `ScmCommandFakes.Runner` (tests/`ScmCommandFakes.cs`), keyed by the exact
  "name arg1 arg2 ..." string like Go's helper-process factories — an
  unexpected command line fails the call, so argument construction is what
  the fixtures assert. `CommandResult.CombinedOutput` = Stdout+Stderr stands
  in for Go `CombinedOutput()`; wrappers that Go reads via `Output()` read
  `.Stdout` only.
- Shared host surface (`HostTypes.cs`, `IHost.cs`): `PullRequest`,
  `PullRequestContent`, enums `PullRequestState`/`MergeableState`/`CheckBucket`
  (Go's raw-string passthrough on unrecognized values maps to
  `Unknown`/`None` — matches no terminal state, callers keep polling),
  `Check` (`CompletedAt` is `DateTimeOffset?`, null = Go zero time),
  `Capabilities`, and `PullRequestUrl.TryExtractNumber` (Go
  `scm.ExtractPRNumber`). `IHost` deliberately has NO `FindPR` yet — slice 6c
  adds it (lookup + fork routing); `NewWithFork`/fork-owner head filtering
  also deferred to 6c. Errors are `ScmCommandException` with Go-shaped
  messages ("glab mr list: <out>: exit status N"); `CheckAvailabilityAsync`
  returns `string?` (null = ready) instead of throwing, mirroring Go's
  `Available() error`.
- GitLab specifics preserved from Go: host-scoped `glab auth status
  --hostname <host>` with unscoped fallback when host unknown; NO `--state`
  flag on `mr list` (removed in glab v1.5x — relevant for 6c); job reads via
  `glab api --paginate projects/<enc>/pipelines/<id>/jobs` when projectPath
  set (detached-HEAD-safe), `glab ci get` fallback otherwise; concatenated
  per-page JSON arrays decoded via repeated `JsonDocument.TryParseValue` over
  a `Utf8JsonReader`, corrupt page surfaces an error even when earlier jobs
  parsed. `finished_at`/`completedAt` parsed with `DateTimeOffset.TryParse`
  (looser than Go's strict RFC3339 — accepted).
- `GitHub.RemoteUrl.HostPrefixedSlug` (GHE `host/owner/name` for gh `--repo`)
  landed here as planned in the 6a.3 note.
- Go's `gh pr create`/`glab mr create` URL comes from combined output; gh PR
  body streams via stdin `--body-file -` (fake asserts `WantStdin`).
- Docker verification: 290 tests passed (235 baseline + 55 new).

## Slice 6b.2

- Azure DevOps wrapper: `NoMistakes.Scm.AzureDevOps.Host`, positional ctor
  `(CommandRunner run, Func<bool>? cliAvailable, string org, string project,
  string repo)` mirroring Go's five-arg `New` (org URL for `--organization`;
  show/update/policy-list get org-only scoping, create gets full
  org/project/repo — az rejects `--project`/`--repository` on id-addressed
  commands). Reads **`.Stdout` only** (Go `outputJSON`): az prints
  preview-notice chatter to stderr that must not reach the JSON decode; on
  failure the trimmed stderr goes into the `ScmCommandException` message
  ("az repos pr create: <stderr>: exit status N"). `FetchFailedCheckLogsAsync`
  throws `NotSupportedException` (Go `ErrUnsupported`; Capabilities
  FailedCheckLogs=false). Az `repos pr list` (Go `FindPR`) NOT ported —
  deferred to 6c with the rest of PR lookup.
- PR-body clamp ported as `NoMistakes.Scm.PRBody` (`Length`/`MaxChars`/`Clamp`,
  Go prbody.go). In .NET `Length` is just `string.Length` (UTF-16 units are
  native); `Clamp` guards against splitting a surrogate pair at the cut.
  Azure create/update descriptions are clamped to 4000; other providers
  unlimited (0).
- Bitbucket has **no CLI**: transport is HTTP
  (`NoMistakes.Scm.Bitbucket.Client`), basic auth email+token. The "auth
  check" is `Client.FromEnv(IReadOnlyList<string>? env, HttpClient?)` —
  KEY=VALUE entries preferred over ambient process env, throws
  `InvalidOperationException` "missing NO_MISTAKES_BITBUCKET_EMAIL/…_API_TOKEN";
  `Host.CheckAvailabilityAsync` only reports "bitbucket client is not
  configured" when client is null (mirrors Go). Internal ctor
  `(baseUrl, email, token, HttpClient)` + `InternalsVisibleTo` lets tests
  inject a fake `HttpMessageHandler` (no httptest server needed).
  `Client.ParseRepoRef` throws `ArgumentException` with Go's exact messages
  (lookalike-host rejection kept).
- `BitbucketPullRequest` (int Id) is the client-level DTO — prefixed name
  because `Scm.PullRequest` owns the bare name. Client-level
  `FindOpenPRBySourceBranchAsync` IS ported (query construction tested);
  only the IHost-level FindPR surface waits for 6c.
- Bitbucket pagination follows `next` links with the same-origin guard
  (`ValidatePaginationUrl`, ScmCommandException "reject cross-origin…");
  `GetStepLogAsync` streams and keeps only the last 32 KiB (`ReadTailAsync`).
  `Host.FetchFailedCheckLogsAsync` swallows every `ScmCommandException` and
  degrades to "" like Go.
- `Bitbucket.Host.PipelineUuidFromStatusUrl` is a manual parse (fragment
  consulted before path, query stripped by the `?` split) because .NET `Uri`
  is laxer than Go's `url.Parse`; it explicitly rejects invalid
  percent-escapes to mirror Go returning "" on parse failure.
- Both Go raw-string passthroughs (bitbucket normalizePRState unknown states,
  bucket "") map to enum `Unknown`/`None` per the 6b.1 convention.
- Docker verification: 416 tests passed (290 baseline + 126 new).

## Slice 6c

- `IHost.FindPRAsync(branch, baseBranch, ct) -> Task<PullRequest?>` (null = no
  open PR, mirroring Go's `(nil, nil)`) implemented on all four hosts. Go's
  unmarshal-error-means-nil behavior is kept on GitHub/GitLab (unparseable
  list output returns null, NOT an exception); Azure throws
  `ScmCommandException "az repos pr list: parse response: ..."` like Go.
- GitHub fork support: `GitHub.Host` gained a 5th ctor param `forkRepo`
  ("owner/name" slug; only the owner is kept) - the 5-arg primary ctor is
  Go's `NewWithFork`, the 4-arg ctor chains with `""`. `CreatePRAsync` now
  passes `--head <forkOwner>:<branch>` when a fork is configured. FindPR
  lists by the BARE branch (gh pr list --head rejects `<owner>:<branch>`) and
  filters returned `headRefName`/`headRepositoryOwner.login` (case-insensitive
  owner compare); the fork test poisons the owner-prefixed fixture key so a
  regression fails loudly. json fields requested widen to
  `number,url,headRefName,headRepositoryOwner` only when a fork is set.
- Fork routing fail-closed guard is a pure function
  `NoMistakes.Scm.ForkRouting.SkipReason(Provider, forkUrl)` (new
  `ForkRouting.cs`): null = proceed (blank fork URL, or GitHub); otherwise
  Go buildHost's exact skip strings ("fork PR routing for GitLab is not
  implemented", ... Bitbucket ..., ... "Azure DevOps" ...). Unknown provider
  with a fork URL also fails closed. Slice 12's buildHost port must call this
  before constructing GitLab/Bitbucket/AzureDevOps hosts and skip PR creation
  with the returned reason (Go: internal/pipeline/steps/host.go).
- GitLab FindPR keeps the no-`--state`-flag invariant (v1.5x) - the test
  fixture key omits it - and routes through `TrimToJson` first. New
  `GitLab.Host.ToPR(MrPayload)` (internal) mirrors Go's `mrPayload.toPR`.
- Bitbucket host-level FindPR delegates to the already-ported
  `Client.FindOpenPRBySourceBranchAsync`; a null client throws
  `ScmCommandException "bitbucket client is not configured"` (RequireClient),
  matching create/update.
- Docker verification: 437 tests passed (416 baseline + 21 new). Slice 6
  marked Done in VERTICAL_SLICES.md.

## Slice 7a (branch `claude/vertical-slice-seven-ipc`)

- Slice 7 branch is cut from `origin/main` (slice-4 merge `9226807`) like
  slice 6 was; slices 5 (PR #7) and 6 are still open, so `STEPS.md`/`NOTES.md`
  were carried over from the slice-6 branch via `git checkout <branch> -- ...`
  (they are not on main yet). Expect the usual sln/test-csproj merge-conflict
  anchors when the open PRs merge.
- `NoMistakes.Ipc` (`dotnet/src/NoMistakes.Ipc/`) ports Go
  `internal/ipc/protocol.go`. Sln GUID
  `{5B7C3D9E-4A2F-4C6B-8D1E-2A0F3B5C7D9E}`, nested under `src`, usual 12
  config lines; sln BOM preserved.
- Shape: statics `Methods` (`push_received` = what `daemon notify-push`
  sends, `cancel_run`, etc.) and `ErrorCodes`; classes `Request`/`Response`/
  `RpcError` with `JsonElement?` standing in for Go `json.RawMessage`;
  factories on static `Protocol` (`NewRequest` auto-increments a shared id
  via `Interlocked.Increment`, mirroring Go's atomic counter). Params/results
  in `Messages.cs`, `RunInfo`/`StepResultInfo`/`IpcEvent`(+`EventTypes`) in
  `WireTypes.cs`. Go's `Event` is named `IpcEvent` (bare `Event` is
  unpleasant in C#).
- JSON: explicit `[JsonPropertyName]` snake_case everywhere (repo
  convention); shared `IpcJson.Options` = `WhenWritingNull` +
  `PropertyNameCaseInsensitive`. Go pointer-omitempty fields are nullable;
  value-typed omitempty (`RunInfo.AwaitingAgent`,
  `StepResultInfo.{Reported,Fixed}Findings`) use
  `[JsonIgnore(WhenWritingDefault)]`. Empty-STRING omitempty parity is NOT
  preserved ("" serializes as "") — both sides parse tolerantly, accepted.
- Step-name normalization ("babysit" → "ci", Go `StepName.UnmarshalJSON`)
  is per-property converters `StepNameJsonConverter`/`StepNameListJsonConverter`
  (in `IpcJson.cs`) on `RespondParams.Step`, `SkipSteps` lists,
  `StepResultInfo.StepName`, `IpcEvent.StepName` — NOT a global string
  converter. New step-name fields must remember the attribute.
- Framing: `JsonLineStream` (one JSON doc per `\n`-terminated line) mirrors
  Go's `json.Encoder`+`bufio.Scanner` pair including the 1 MiB cap
  (`MaxMessageBytes`; a message of exactly 1 MiB is accepted, +1 byte throws
  `IOException`). Clean EOF at a boundary reads as `null`; EOF mid-line
  throws. Not concurrency-safe — 7b's client/server must serialize access
  (Go ipc.Client mutex). 7b should also add the 30s read deadline
  (Go client.Call `SetReadDeadline`) at the client layer, not in
  `JsonLineStream`.
- `Core.Finding` gained `[JsonPropertyName]` tags (Go json tags) so
  `RespondParams.AddedFindings` serializes correctly; `FindingsParser` is
  unaffected (it uses its own wire classes).
- Tests: `IpcProtocolTests.cs` (protocol_test.go port + babysit
  normalization) and `IpcSocketRoundTripTests.cs` (unix-domain socket pair
  in temp dir — the daemon transport; covers cancel_run and notify-push
  round trips, error response, pipelined framing, subscribe event stream,
  oversized-message rejection, mid-message EOF). Docker verification:
  212 tests passed (185 main baseline + 27 new).

## Slice 7b

- IPC server lives in `NoMistakes.Ipc` (`Server.cs`, class `IpcServer`),
  porting Go `internal/ipc/server.go` + the unix `listen()` transport:
  stale socket file deleted before bind, socket file chmod 0700 after bind
  (Go's umask 0o077). Windows named-pipe transport NOT ported (unix domain
  sockets only; slice 17e Windows parity). Surface: delegates
  `IpcHandler`/`IpcStreamHandler` (`JsonElement?` params, like the 7a
  protocol), `Handle`/`HandleStream`, `ServeAsync(socketPath)` (blocks until
  `Close()`, drains in-flight connections), `CloseListener()` (Go parity),
  and `Task Listening` — completes once bound/accepting, faults on bind
  failure. Later slices (7c+) register handlers before `RunAsync`.
- Dispatch semantics kept from Go: unknown method →
  `method not found: <m>` (`ErrorCodes.MethodNotFound`); handler exception →
  `ErrorCodes.Internal` with `ex.Message`; malformed JSON line →
  ParseError response with id 0 and the connection keeps reading; stream
  handlers get an initial `{ok:true}` response then own the connection,
  which closes when they return. Responses are deliberately written WITHOUT
  the shutdown token so the shutdown method's own OK reaches the client
  before teardown (Go's goroutine trick, made deterministic).
- `NoMistakes.Daemon` project (`dotnet/src/NoMistakes.Daemon/`), sln GUID
  `{8C2F4E6B-1D3A-4B5C-9E7F-3A1B2C4D5E6F}`, nested under `src`, refs Core +
  Ipc. Class is `DaemonHost` (NOT `Daemon` — a type named like its own
  namespace makes C# resolve the bare name to the namespace and breaks
  every unqualified use). `RunAsync()` = Go `RunWithOptions` reduced to
  lifecycle: EnsureDirs, registers health+shutdown handlers, writes PID
  file, serves on `Paths.Socket`, exposes `ServerPidsDir` from Paths (Go's
  `agent.SetServerPIDsDir` hook-up waits for the agent slice 14).
  `Task Ready` completes when accepting + PID file written; `Shutdown()`
  idempotent.
- PID file: `DaemonPidRecord` (`pid`/`started_at` JSON; `started_at`
  omitted when null). `Parse` accepts the legacy plain-integer file and
  rejects non-positive PIDs (`FormatException`), mirroring
  `readDaemonPIDFileData`. `Write` is atomic (same-dir temp + rename,
  0644); internal static `Rename` hook is swappable for the
  rename-failure test — tests that touch it share xunit collection
  "daemon" with the lifecycle tests to avoid cross-class parallel races.
  Cleanup invariant kept from Go: on exit the PID file (and socket) are
  removed only if the exiting daemon still owns the PID file
  (`SameOwner`: pid + started_at instant equality).
- Gotcha: .NET unlinks a bound unix-domain socket file on listener dispose
  (same as Go `net.UnixListener` unlink-on-close), so don't write tests
  asserting the socket FILE survives a foreign-owner shutdown — only PID
  ownership is asserted (`StoppingDaemonKeepsPidFileOwnedByReplacementDaemon`).
- Not ported here (later slices): signal handling, config/log-level load,
  telemetry, `prepareDaemonEnvironment`, recovery (`RecoverStaleRuns` 7d,
  `reapOrphanedServers`/`migrateGateConfigs` later), run manager (7c),
  and all run-related IPC handlers.
- Tests: `DaemonLifecycleTests.cs` (start serves health over the socket +
  PID written, shutdown via IPC and via method removing artifacts, stale
  socket replaced, foreign PID file preserved, method-not-found, bind
  failure faults `Ready` and leaves no PID file) and
  `DaemonPidRecordTests.cs`. Docker verification: 232 tests passed
  (212 baseline + 20 new).

## Slice 7c

- `RunManager` (`dotnet/src/NoMistakes.Daemon/RunManager.cs`) ports
  internal/daemon/manager.go reduced to run lifecycle. The pipeline executor
  is a delegate seam: `PipelineRunner(Run, Repo, CancellationToken)`, required
  ctor param — slice 9's executor plugs in here and OWNS run status
  transitions (running/completed/failed). What the manager writes today,
  guarded so the future executor is never overwritten:
  - cancellation (runner throws OperationCanceledException while its CTS is
    cancelled): writes `cancelled` + the recorded cancel reason, but ONLY if
    the run is still pending/running in the DB (mirrors Go's executor writing
    context.Cause). Slice 9 can move this into the executor without breaking
    the guard.
  - runner exception: unconditional `failed` + "internal panic: <msg>"
    (Go's panic recovery).
  - clean completion: manager touches nothing — a runner that never sets a
    terminal status leaves the run pending (same as Go).
- Cancel reasons live in `NoMistakes.Core.RunCancelReason`
  (AbortedByUser/Superseded = Go types constants, byte-for-byte; plus
  DaemonShutdown = Go's ad-hoc "daemon shutting down" cause). They become the
  run's error message.
- `StartRunAsync(repo, branch, headSha, baseSha)` serializes per repo+branch
  (ConcurrentDictionary of SemaphoreSlim = Go branchLocks), cancels active
  same-branch runs with the Superseded reason and waits for them (bounded by
  internal `CancelWaitTimeout`, default 30s like Go), then InsertRun + spawn.
  Not ported here (7e.2/later): HandlePushReceived/HandleRerun, worktree
  setup, trusted-config load, agent creation, telemetry, Subscribe/broadcast,
  HandleRespond.
- `HandleCancel` throws InvalidOperationException "no active run <id>" for
  unknown/finished runs — Go parity. The idempotent `aborted: false` no-op
  belongs to the CLI layer (slice 7e.1), do NOT add it to the manager.
- Go's context.WithCancelCause is emulated with a per-run `CancelReason`
  field set by the FIRST cancel request (`??=`); CTSes are deliberately never
  disposed (avoids Cancel-vs-Dispose race on the shutdown/supersede paths).
- `RunInfoMapper.RunToInfo/StepToInfo` (RunInfoMapper.cs) port
  runToInfo/stepToInfo: AwaitingAgent == (AwaitingAgentSince != null);
  `Steps`/`FixSummaries` stay null (not empty lists) when empty to match Go
  omitempty on the wire; stats/fix-summary enrichment is try/catch
  best-effort like Go's ignored errors.
- `RunIpcHandlers.Register(server, manager, db)` registers
  get_run/get_runs/get_active_run/cancel_run only. Error strings Go-shaped:
  "run not found: <id>", "no active run <id>", "invalid params: ..."; they
  surface as ErrorCodes.Internal via the 7b server. Nothing is wired into
  DaemonHost.RunAsync yet — 7e.2 (notify-push) should construct
  Database+RunManager in the daemon and call Register there, adding
  push_received; subscribe needs the broadcast machinery (with executor
  events, slice 9).
- `NoMistakes.Daemon.csproj` now references `NoMistakes.Data`.
- Tests: `RunManagerTests.cs` (create/cancel/supersede/branch-isolation/
  shutdown/panic/clean-completion), `RunInfoMapperTests.cs` (runinfo_test.go
  port + awaiting-agent derivation), `RunIpcHandlerTests.cs` (handlers over a
  real unix socket, incl. cancel_run driving a live manager). Docker
  verification: 253 tests passed (232 baseline + 21 new).

## Slice 7d

- `Database.RecoverStaleRuns` was ALREADY ported (with the slice-3 data layer,
  `dotnet/src/NoMistakes.Data/Run.cs`) including the single transaction and
  awaiting-agent clear; 7d added the daemon wiring plus the missing test
  coverage rather than re-porting the DB method.
- Daemon wiring: `DaemonHost` ctor gained an optional `Database? db` param;
  `RunAsync` calls private `RecoverOnStartup()` right after `EnsureDirs`,
  BEFORE the PID file is written and the socket serves (Go recoverOnStartup
  order). Recovery is best-effort (exception swallowed, daemon keeps
  starting, like Go's slog.Error path). With `db == null` (all pre-7d
  callers/tests) it is a no-op — 7e.2 should pass the real Database when it
  constructs Database+RunManager in the daemon.
- Error message constant: `DaemonHost.CrashRecoveryError` =
  `"daemon crashed during execution"` (public — tests and later slices
  reference it instead of re-typing the literal).
- Go recoverOnStartup's other duties (reapOrphanedServers,
  migrateGateConfigs, orphaned-worktree cleanup) still NOT ported —
  `RecoverOnStartup` doc comment marks them for later slices.
- New tests: `RunTests.RecoverStaleRunsFailsRunAndStepsInOneTransaction`
  (run + gate-parked step failed together, same error, step completed_at
  stamped, marker cleared), `RunInfoMapperTests.RecoveredRunIsNeverReportedParked`
  (wire-level: recovered run RunToInfo has AwaitingAgent=false/Since=null),
  and `DaemonLifecycleTests.StartupRecoversStaleRunsBeforeServing` (port of
  Go TestRecoverStaleRunsOnStartup; asserts recovery completed by the time
  `Ready` resolves). Existing 7b lifecycle tests construct `DaemonHost(paths)`
  without a db and are unaffected.
- Docker verification: 256 tests passed (253 baseline + 3 new).

## Slice 7e.1

- IPC client landed: `NoMistakes.Ipc.IpcClient` (`Client.cs`) — `DialAsync(socketPath)`
  (throws `IOException` "dial ipc: ..." when nothing listening) +
  `CallAsync<TResult>(method, params, ct)`. Server-side JSON-RPC errors surface
  as `IpcRpcException` (carries `Code`; `Message` is the raw server error text,
  matching Go `*RPCError.Error()` — callers substring-match on it like Go).
  Per-call 30s response timeout (`CallTimeout`, internal-settable) mirrors Go's
  read deadline. Calls on one connection serialized via `SemaphoreSlim`.
  `Subscribe` NOT ported — arrives with event streaming.
- Daemon liveness: `NoMistakes.Daemon.DaemonStatus.IsRunningAsync(Paths)` —
  dial + health "ok"; any dial/call failure (incl. stale socket file left by a
  crash) reads as not running. Ports Go `daemon.IsRunning`/`daemonIsRunningViaIPC`.
- Abort-by-id op: `NoMistakes.Cli.AxiAbort.AbortByRunIdAsync(Paths, runId)`
  returning record `AxiAbortOutcome(Aborted, RunId, Detail)`. `Detail` non-null
  exactly on the no-op paths, carrying Go's literal detail strings
  ("daemon not running, so no active run to cancel (no-op)" /
  "no active run with that id (no-op)"). No TOON render here — slice 8a/8c.1
  wires the command surface + rendering over this op (8c.1's "abort-by-id
  already landed in slice 7e" refers to this). Other IPC failures propagate as
  exceptions for the future emitError layer.
- `NoMistakes.Cli.csproj` now references `NoMistakes.Daemon` + `NoMistakes.Ipc`
  (first non-Core refs on the Cli project; slice 8 builds on these).
- Unknown/inactive-run distinction lives in the RPC error string: daemon-side
  `RunManager.HandleCancel` throws "no active run <id>" (per the slice-7c
  design note); the CLI layer maps it to the idempotent no-op. Keep that
  message stable or the substring match breaks.
- Tests: `AxiAbortByRunIdTests.cs`, `[Collection("daemon")]` (serialized with
  the other daemon-socket tests). Harness starts a real `DaemonHost` with
  `RunIpcHandlers.Register` — order matters: start the daemon BEFORE
  `StartRunAsync`, else startup `RecoverStaleRuns` fails the fresh run.
- Docker verification: 261 tests passed (256 baseline + 5 new).

## Slice 7e.2

- `RunManager.HandlePushReceivedAsync(PushReceivedParams)` ports Go
  HandlePushReceived: zero-SHA ref-deletion guard, `RepoIdFromGatePath`
  (internal static; Go's basename+`.git` check), `BranchFromRef`, GetRepo,
  then the existing 7c `StartRunAsync`. **SkipSteps and Intent are accepted
  on the wire but NOT stamped onto the run** — 7c's StartRunAsync has no
  trigger/skip/intent params; slice 9's executor-era startRun port must add
  them (Go stamps skip_steps/intent at run creation). Error strings are
  Go-shaped and tests substring-match them: "ref deletion push, no pipeline
  to run", "invalid gate path: <p>", "unknown repo for gate <gate>".
- `RunIpcHandlers.Register` now also registers `push_received`; still no
  rerun/respond/subscribe. Daemon wiring stays EXTERNAL (tests construct
  Database+RunManager and call Register) — the 7c/7d note about "constructing
  Database+RunManager in the daemon" was NOT done here because RunManager
  requires the PipelineRunner seam that only slice 9 fills; the real daemon
  bootstrap (`daemon run` command) should do that construction when it lands.
- CLI: `NoMistakes.Cli.DaemonNotifyPush.NotifyPushAsync(Paths, gate, ref,
  old, new, pushOptions)` is the op (dial failure rethrown as IOException
  "connect to daemon: ..." for Go message parity); push-option helpers
  (`ParseSkipPushOptions`/`ParseIntentPushOptions`/`FormatSkipPushOptions`/
  `FormatIntentPushOption`, internal) live on the same class, incl. the
  base64 `no-mistakes.intent=` transport and unknown-step rejection
  (`unknown step "x"`, validated against `StepName.All` — "babysit" is
  rejected like Go). `CliApp` dispatches `daemon notify-push` with manual
  flag parsing (required --gate/--ref/--old/--new, repeatable
  --push-option); hidden — not in help text, errors print `Error: <msg>` to
  stderr, exit 1 (cobra-ish). Slice 8's command framework should absorb this
  dispatch.
- `NoMistakes.Cli.csproj` gained `InternalsVisibleTo NoMistakes.Tests`
  (first internal CLI surface under test).
- Hook e2e test (`DaemonNotifyPushTests.PostReceiveHookInvocationReachesDaemon`)
  runs the REAL slice-4 post-receive script against the REAL CLI binary:
  test project's output contains `no-mistakes.dll` (Cli is a project ref), a
  shell shim exports NM_HOME and execs `dotnet no-mistakes.dll "$@"`, hook
  script generated via `PostReceiveHook.ScriptFor(shim)`, run with cwd = a
  real `git init --bare` gate dir and GIT_PUSH_OPTION_* env; asserts the run
  lands in the daemon's RunManager with head/base/branch mapped
  (New→HeadSha, Old→BaseSha). Pattern reusable for future CLI-as-subprocess
  tests. `[Collection("daemon")]` — it also mutates nothing global except
  the two CLI tests that set/restore NM_HOME process-wide (keep NM_HOME
  mutation inside this collection).
- Docker verification: 279 tests passed (261 baseline + 18 new). Slice 7
  marked Done in VERTICAL_SLICES.md (table + details Status line).

## Slice 8a

- TOON encoder is a hand port, NOT a NuGet dep (none exists that matches
  toon-go's exact output): `dotnet/src/NoMistakes.Cli/Toon.cs` — `ToonField`,
  `ToonObject` (ordered), static `Toon.MarshalString`, `ToonEncodingException`.
  Ports toon-go's encoder with the Core Profile defaults (2-space indent,
  comma delimiter, no length markers) and the full quoting ruleset
  (empty/whitespace, true/false/null, numeric-looking, leading `-`, any of
  `:\"[]{}`, \n\r\t, comma; control chars < 0x20 throw). Supported value
  subset: null, bool, string, integers (2^53 safe-int guard), nested
  ToonObject, all-primitive sequences (inline `key[N]: a,b`), and uniform
  primitive-field object rows (tabular `key[N]{cols}:`). Mixed/nested list
  shapes the axi layer never emits THROW — port toon-go's list-item encoding
  when a later slice needs it. Note: URLs contain `:` so `pr:` values render
  quoted — same as Go, don't "fix" it.
- Render layer: `dotnet/src/NoMistakes.Cli/AxiRender.cs` — `RunView`/`StepView`
  (FromIpc/FromDb, AwaitingStep, FindingsTally, FixRows, FindingCount) and
  static `AxiRender` (RunObjectField/WithKey, GateFields, Doc, Truncate,
  ShortSha, FormatParkedFor, TerminalStatus, MaxFindingDesc=600). Field order
  matches Go exactly: id, branch, status, [awaiting_agent], head, [pr],
  findings, steps table; gate: step, status, [summary], [risk], [note],
  findings table, then top-level help array. Gate help strings and the
  review-gate auto-fix note are byte-for-byte Go copies — keep in sync with
  the Go source when 8e syncs guidance surfaces.
- `AxiRender.NowUnix` is an internal settable static Func<long> (Go's nowUnix
  package var). Tests pinning it stay inside `AxiRenderTests` (xunit
  serializes within a class) and restore in finally.
- NO `significance` column: the Go findings table has no such field (the
  step's "if merged" condition is false). findingRow stays
  id,severity,file,action,description.
- Go's render layer ignores findings-JSON parse errors; .NET
  `FindingsParser.Parse` throws on malformed JSON, so the render layer wraps
  it in `AxiRender.ParseFindingsOrEmpty` (catch → empty Findings). Reuse that
  helper, don't call Parse directly from render code.
- `NoMistakes.Core.Findings` gained `RiskLevel` (wire key `risk_level`) for
  the gate `risk:` field. Go's other Findings fields (Tested, TestingSummary,
  Artifacts, RiskRationale) still unported.
- `NoMistakes.Cli.csproj` now references `NoMistakes.Data` (explicit, for
  `RunView.FromDb`).
- emitDoc/emitError (cobra plumbing) NOT ported — `AxiRender.Doc` returns the
  document string; 8b's command layer owns writing to stdout and the
  exit-code error shape.
- Truncate counts Unicode code points (rune parity with Go), not UTF-16 units.
- Tests: `ToonTests.cs` (encoder shape + quoting matrix), `AxiRenderTests.cs`
  (ports of TestWriteRunObjectShape — exact-document field-order assert —
  TestRunObjectRendersAwaitingAgent, TestFormatParkedFor, TestWriteGateShape,
  TestGateNote_ReviewOnly, TestFindingsTally, TestTruncateDisclosesTotal,
  plus risk render, pr placement, FixRows, malformed-JSON degradation).
- Docker verification: 319 tests passed (279 baseline + 40 new).

## Slice 8b

- Read-only axi commands are split in two layers: `AxiQuery` (pure document
  builders over Database+Paths, fully deterministic under test — Home/Status/
  Logs return `AxiOutput(Doc, ExitCode)`) and `CliApp.RunAxi` dispatch +
  `AxiEnv` (environment: `Paths.New()`+EnsureDirs, `Database.Open`, cwd repo
  lookup via `GitClient.FindGitRootAsync` with main-worktree fallback —
  Go's openAxiEnv/findRepo read-only half). The ensure-daemon half of
  openAxiEnv is NOT here — 8c.1's mutating commands add it.
- `AxiOutput.Error(code, msg, help...)` is the emitError port: structured
  TOON error on stdout + non-zero exit. Exit codes follow Go: 2 = usage/flag
  validation (unknown step, missing --step, unknown flag), 1 = environment/
  lookup failures (uninitialized repo, unknown --run id, unreadable log).
  `AxiQuery.RepoInitHelp` appends the `no-mistakes init` hint only when the
  error message contains "not initialized" — keep the `AxiEnv.FindRepoAsync`
  message ("repo not initialized (run 'no-mistakes init' first)") in sync.
- `axi logs` validates `--step` BEFORE opening any environment
  (`ValidateLogsStep`, Go order); test
  `AxiLogsValidatesStepBeforeTouchingAnyEnvironment` runs with no NM_HOME/repo
  to pin that. "babysit" is rejected (StepName.All has no alias).
- Flag parsing is `CliApp.ParseAxiFlags` (value flags + bool flags, cobra-ish
  errors "unknown flag:"/"flag needs an argument:"). 8c should reuse/extend
  it rather than add another loop.
- Home constants: `RecentRunsHomeLimit` = 10, `LogTailLines` = 40,
  `SkillDescription` (byte-for-byte skill description; slice 16b's generated
  skill should absorb it). Home works daemon-down by design (reads only the
  DB; daemon state comes from `DaemonStatus.IsRunningAsync`).
- CLI e2e tests (`AxiQueryCliTests`) mutate NM_HOME AND process cwd
  (`Directory.SetCurrentDirectory`) with save/restore — kept inside
  `[Collection("daemon")]` per the 7e.2 rule. They register the repo under
  the symlink-RESOLVED root from `FindGitRootAsync` (macOS /var vs
  /private/var).
- Log-line rows render via single-column tabular `log[N]{line}:` —
  `LogRows` wraps each line in a one-field ToonObject because the 8a encoder
  has no list-item encoding for bare strings containing arbitrary text; Go
  emits the same shape.
- Docker verification: 347 tests passed (319 baseline + 28 new).

## Slice 8c.1

- `AxiDrive` (`dotnet/src/NoMistakes.Cli/AxiDrive.cs`) ports axi_drive.go's
  runAxiRun/triggerRun/driveRun/gateResolution/renderDriveResult/runAxiAbort
  (worktree/branch-scoped half; by-id render is `RenderAbortByIdOutcome` over
  the 7e `AxiAbort` op). `AxiEnv.OpenAsync(ensureDaemonConn: true)` is the
  ensure-daemon half of openAxiEnv, populating `AxiEnv.Client` — but it does
  NOT spawn the daemon (no .NET daemon bootstrap command yet); a stopped
  daemon is an error ("start daemon: daemon is not running"). The daemon
  slice must add the on-demand spawn there.
- Go's ciLogReader/ciReadyToMerge (cimonitor.ChecksPassed over the CI step
  log) is NOT ported: `AxiDrive.RunAsync` takes a
  `Func<string,bool>? ciChecksPassed` callback (null today = no early
  checks-passed return). The CI monitor slice must supply the log-parsing
  half and wire it in CliApp.RunAxiRun. The checks-passed RENDER path
  (outcome: checks-passed + StaleMonitorGuidance) IS ported and tested.
- Daemon-side `rerun` and `respond` IPC handlers are still unregistered
  (RunIpcHandlers has push_received/get_run/get_runs/get_active_run/
  cancel_run only). `AxiDrive.TriggerRunAsync`'s rerun fallback and
  `SendRespondAsync` compile against Methods.Rerun/Methods.Respond and will
  get "unknown method" IpcRpcException until the executor-era slices register
  them — 8c.1 tests only exercise the push-triggers-run path and
  autoApprove=false (no respond sent). 8c.2 (respond) needs the daemon
  handler registered.
- New `NoMistakes.Git.Gate` holds only `RemoteName = "no-mistakes"` (working
  repo remote pointing at the gate); gate setup lands with the init slice.
  New `NoMistakes.Core.ApprovalAction` (approve/fix/skip wire strings).
  `Findings.HasActionable()` ports types.HasActionableFindings (blank action
  defaults to auto-fix; only all-no-op or empty is non-actionable).
- TOON quoting gotcha: run ids are ULIDs starting "01", so
  `HasLeadingZeroDecimal` quotes them — `run: "01KX..."` in abort/run docs
  (toon-go does the same; Go tests dodge it with ids like "some-run-id").
  Assert quoted. Same for messages containing quotes: TOON escapes them, so
  assert `\\\"deploy\\\"` not `"deploy"`.
- `AxiDriveCliTests` submission test reuses the 7e.2 CLI-shim + real
  post-receive hook pattern, with a RunManager runner that parks the run at
  a review gate (SetRunAwaitingAgent BEFORE the status flip, per the executor
  invariant) and holds on `Task.Delay(Timeout.Infinite, token)` until cancel.
  Poll interval knobs (`DrivePollInterval`, `TriggerWaitTimeout`) are
  internal settable statics; tests currently run at defaults.
- Docker verification: 370 tests passed (347 baseline + 23 new).

## Slice 8c.2a

- Daemon-side respond routing: `RunManager` gained a responder registry —
  delegate `ApprovalResponder(step, action, findingIds, instructions,
  addedFindings)`, `RegisterResponder(runId, responder)`, and
  `HandleRespond(...)` throwing Go's exact "no active executor for run <id>"
  when nothing is registered. This is Go's `m.executors` map reduced to a
  seam: slice 9's executor must call `RegisterResponder` (its
  RespondWithOverrides side) when a run starts and OWNS the "no step awaiting
  approval" / step-mismatch validation — the manager only routes. Entries are
  auto-removed in ExecuteRunAsync's finally, so registration inside the
  pipeline runner is safe.
- `RunIpcHandlers.Register` now registers `Methods.Respond`
  (RespondParams → HandleRespond → `RespondResult { Ok = true }`). Rerun and
  subscribe remain unregistered.
- CLI: `AxiDrive.ValidateRespondAction` (exit-2 usage errors, runs BEFORE any
  env open like Go and 8b's ValidateLogsStep — pinned by a no-NM_HOME test),
  `AxiDrive.RespondAsync(env, progress, action, ciChecksPassed, ct)` (ports
  runAxiRespond minus flags), `GateStatusFor`, and CliApp `axi respond`
  dispatch with only `--action`.
- **8c.2b/8c.2c hooks:** `--action fix` currently ALWAYS returns the exit-2
  "--action fix requires --findings <id,...> or --add-finding <json>" error
  (Go's empty-selection check; the flags don't exist yet) — 8c.2b replaces
  that block with real findingIds/instructions/addFinding plumbing (Go builds
  per-finding instructions from one --instructions note; parseAddFinding is
  the payload parser). 8c.2c adds `--step` (replaces the AwaitingStep-only
  lookup — note Go trims and validates step AFTER the no-gate check only when
  step is empty) and threads `autoYes` into the DriveRunAsync call
  (currently hardcoded `autoApprove: false`).
- Fix verb's gate-resolution semantics are tested over the IPC wire via
  `AxiDrive.SendRespondAsync` (finding IDs reach the parked step) since the
  CLI flags are absent; approve/skip are tested end-to-end through CliApp
  with a park-until-respond runner (`ParkedGateManager` in
  `AxiRespondTests.cs` — reusable for 8c.2b/c; it maps skip → step skipped,
  else completed, then completes the run).
- Docker verification: 379 tests passed (370 baseline + 9 new). NOTE: the
  build initially failed with containerd I/O errors — the HOST disk was 100%
  full (Docker.raw could not grow). Freed regenerable caches
  (~/Library/Caches/{go-build,Homebrew,*.ShipIt}), restarted Docker Desktop,
  then the build ran clean. If Docker errors look like corruption, check
  `df -h` first.

## Slice 8c.2b

- `axi respond` finding flags live in `AxiDrive.RespondAsync(env, progress,
  action, findings, instructions, addFinding, ciChecksPassed, ct)` — the
  three flag values are optional string params (default ""), so 8c.2a
  call sites still compile. CliApp wires `--findings`/`--instructions`/
  `--add-finding` as plain value flags via ParseAxiFlags.
- Go-order parity preserved: finding IDs are split (`AxiDrive.SplitCsv`,
  ports splitCSV — trims, drops empties) for EVERY action and passed to
  SendRespondAsync even for approve/skip; the fix-only block (empty-selection
  exit-2 error, instructions fan-out, add-finding parse) runs AFTER the
  gate lookup, so selection errors still require an active parked run.
- `--instructions` is ONE note fanned out per selected finding ID
  (map id→note); it is silently dropped when there are no --findings IDs
  (Go behavior) — pinned by the add-finding CLI test passing an orphan note.
- `AxiDrive.ParseAddFinding` ports parseAddFinding (lives in AxiDrive, not
  AxiQuery, since only respond uses it): `Deserialize<Finding>` with
  PropertyNameCaseInsensitive (encoding/json parity), JsonException wrapped
  in FormatException; null literal / blank description throw
  "description is required". CLI maps FormatException to exit 2
  `invalid --add-finding: <msg>` + the Go help string
  `Expected a JSON object, e.g. {"description":"...","action":"auto-fix"}`.
  Error text after the prefix differs from Go (System.Text.Json wording) —
  tests assert the prefix only.
- `RespondSeen` in AxiRespondTests now captures Instructions and Added from
  the responder; 8c.2c can reuse it for --step/--yes coverage.
- Docker verification: 391 tests passed (379 baseline + 12 new).
