# .NET Port Vertical Slices

This document tracks the incremental .NET rewrite under `dotnet/`.

The goal is to port `no-mistakes` as independently shippable behavior slices, not as a package-by-package translation. Each slice should leave the .NET version buildable and testable. The Go implementation remains the compatibility oracle until the .NET port reaches feature parity.

## Rules for Each Slice

- Add or port tests before implementing behavior.
- Keep the .NET solution green with `dotnet test dotnet/no-mistakes.sln --no-restore`.
- Keep compiled artifact publishing working for the CLI project.
- Prefer real temp directories, real git repositories, and subprocess execution where the Go tests already exercise process or I/O boundaries.
- Preserve safety behavior before expanding convenience behavior.
- Update this file when a slice is completed, split, or reordered.

## Slice Status

| Slice | Status | Primary Go Areas | Primary .NET Areas |
| --- | --- | --- | --- |
| 1. Bootstrap CLI and build artifact | Done | `cmd/no-mistakes`, `internal/buildinfo`, `internal/cli` root | `NoMistakes.Cli`, `NoMistakes.Core`, `Dockerfile.dotnet` |
| 2. Paths, environment, and config loading | Done | `internal/paths`, `internal/config` | `NoMistakes.Core`, `NoMistakes.Config` |
| 3. SQLite run database | Done | `internal/db`, migrations | `NoMistakes.Data` |
| 4. Git command wrapper and repository model | Done | `internal/git`, `internal/types` | `NoMistakes.Git`, `NoMistakes.Core` |
| 5. Shell process lifecycle | Planned | `internal/shellenv` | `NoMistakes.Processes` |
| 6. SCM URL parsing and host backends | Done | `internal/scm`, `internal/bitbucket` | `NoMistakes.Scm` |
| 7. Daemon IPC and run lifecycle | Planned | `internal/daemon`, `internal/ipc`, `internal/cimonitor` | `NoMistakes.Daemon`, `NoMistakes.Ipc` |
| 8. AXI command surface and gates | Planned | `internal/cli/axi*.go`, `internal/gate` | `NoMistakes.Cli`, `NoMistakes.Pipeline` |
| 9. Pipeline executor and step contracts | Planned | `internal/pipeline`, `internal/pipeline/steps` shared types | `NoMistakes.Pipeline` |
| 10. Review, test, lint, and format steps | Planned | `internal/pipeline/steps/review.go`, `test.go`, `lint.go`, `format.go` | `NoMistakes.Pipeline.Steps` |
| 11. Rebase, push, and force-push safety | Planned | `internal/pipeline/steps/rebase.go`, `push.go`, `forcepush.go` | `NoMistakes.Pipeline.Steps`, `NoMistakes.Git` |
| 12. PR and MR creation | Planned | `internal/pipeline/steps/pr.go`, SCM backends | `NoMistakes.Pipeline.Steps`, `NoMistakes.Scm` |
| 13. CI monitor and auto-fix loop | Planned | `internal/pipeline/steps/ci*.go` | `NoMistakes.Pipeline.Steps`, `NoMistakes.Daemon` |
| 14. Native agent integrations | Planned | `internal/agent` | `NoMistakes.Agent` |
| 15. Terminal UI | Planned | `internal/tui` | `NoMistakes.Tui` |
| 16. Init, skill, wizard, update, and telemetry | Planned | `internal/skill`, `internal/wizard`, `internal/update`, `internal/telemetry`, CLI commands | `NoMistakes.Cli`, `NoMistakes.Core` |
| 17. End-to-end parity and release packaging | Planned | `internal/e2e`, `Makefile`, `.github/workflows/*`, release scripts | `dotnet/tests`, `Dockerfile.dotnet`, release workflow |

## Slice Details

### 1. Bootstrap CLI and Build Artifact

Status: Done.

User-visible behavior:

- `no-mistakes --help` prints the root help surface.
- `no-mistakes --version` prints build metadata.
- Unknown commands fail with a usage-style exit code.
- The .NET CLI can be published as a self-contained single-file executable.

Acceptance checks:

- `dotnet restore dotnet/no-mistakes.sln`
- `dotnet build dotnet/no-mistakes.sln --no-restore`
- `dotnet test dotnet/no-mistakes.sln --no-restore`
- `dotnet publish dotnet/src/NoMistakes.Cli/NoMistakes.Cli.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true`

### 2. Paths, Environment, and Config Loading

Status: Done.

Port `NM_HOME`, app directory layout, default config rendering, repo config parsing, YAML handling, and config precedence.

Ported behavior (`NoMistakes.Core.Paths`, `NoMistakes.Config`):

- `Paths` resolves `NM_HOME` or `~/.no-mistakes` and derives the DB, socket, PID,
  config, repos, worktrees, logs, and server-PID locations; `EnsureDirs` creates them.
- `ConfigLoader.LoadGlobal` parses `config.yaml` strictly (unknown top-level fields
  are an error, mirroring yaml.v3 `KnownFields(true)`, so `allow_repo_commands` is
  rejected in the global config), honors the scalar-or-list `agent` field, the legacy
  `babysit_timeout`/`auto_fix.babysit` aliases, and validates `agent_args_override`.
- `GoDuration` parses Go's `time.ParseDuration` format so `ci_timeout` values
  ("168h", "2h30m", "-5m", keywords) stay wire-compatible; `ParseCiTimeout` maps
  non-positive/keyword values to the unlimited sentinel.
- `ConfigLoader.LoadRepo`/`LoadRepoFromBytes` parse `.no-mistakes.yaml` leniently,
  and `EffectiveRepoConfig` enforces the trust boundary: code-executing fields
  (`commands`, `agent`) come only from the trusted default-branch copy unless
  `allow_repo_commands` opts in.
- `Merge` layers global + repo config with auto-fix/intent/test defaults, and
  `AutoFixLimit` mirrors the Go per-step limits (review auto-fix disabled by default).

Acceptance checks:

- Unit tests cover default paths, `NM_HOME`, config defaults, repo config parsing, and effective config merging.
- Security tests prove code-executing config fields stay separated from untrusted pushed-branch config.
- Compatibility tests compare representative Go and .NET config outcomes.
- `dotnet test dotnet/no-mistakes.sln --no-restore`

Deferred to later slices: native-agent PATH resolution (`ResolveAgent`) lands with
slice 14 (native agent integrations); it needs the process-launch layer.

### 3. SQLite Run Database

Status: Done.

Port schema creation, migration behavior, run records, step records, awaiting-agent fields, and recovery updates.

Ported behavior (`NoMistakes.Data`, backed by `Microsoft.Data.Sqlite`):

- `Database.Open` creates the file, applies the same `repos`/`runs`/`step_results`/
  `step_rounds`/`intent_cache` schema as Go's `internal/db/schema.go`, and replays
  the additive `ALTER TABLE` migrations idempotently (the "duplicate column name"
  error is tolerated, so no version table is needed). WAL, `foreign_keys`, and a
  5s `busy_timeout` mirror the Go DSN pragmas, so a transient writer lock during
  migration is waited out rather than failing.
- `Repo`, `Run`, `StepResult`, `StepRound`, and `IntentCache` records round-trip
  with the same columns, nullable semantics, cascade deletes, ULID primary keys
  (monotonic, so the `created_at DESC, id DESC` ordering holds within a one-second
  bucket), and the legacy `babysit` -> `ci` step-name normalization.
- The awaiting-agent signal (`SetRunAwaitingAgent` / `ClearRunAwaitingAgent`) and
  `RecoverStaleRuns` behave as in Go: recovery fails stale runs and in-progress
  steps in a single transaction and drops the parked marker.
- `GetStats` aggregates reported/fixed findings, rescue runs, and per-step/per-repo
  rollups using the shared `NoMistakes.Core` findings model (dedup by
  severity/file/line/description, current-round survivors count as unfixed).

Acceptance checks:

- Database tests use isolated temp SQLite files (`TempDir`).
- Set, clear, and recovery behavior for `awaiting_agent_since` matches Go.
- Migration tests open an old schema fixture and upgrade it (repos fork_url and
  step_rounds selection/fix columns).
- `dotnet test dotnet/no-mistakes.sln --no-restore`

### 4. Git Command Wrapper and Repository Model

Status: Done.

Port repository discovery, command execution, remote parsing, worktree helpers, SHA/ref helpers, and bare gate repo handling.

Ported behavior (`NoMistakes.Git`):

- `GitClient` runs `git` as a subprocess and returns trimmed stdout, throwing
  `GitCommandException` with the redacted command and stderr on non-zero exit.
  It mirrors Go's `git.Run`: when the working dir is itself a bare repo it names
  it explicitly via `--git-dir` (`IsBareGitDir` = HEAD file + objects dir, no
  `.git` entry) so gate operations work under `safe.bareRepository=explicit`,
  while working trees and linked worktrees keep normal discovery.
- `NonInteractiveEnv` forces `GIT_EDITOR`/`GIT_SEQUENCE_EDITOR=true` and
  `GIT_TERMINAL_PROMPT=0` and injects an absolute `PWD` on non-Windows so a
  symlinked working directory (e.g. `/tmp` vs `/private/tmp`) is preserved.
- Repository helpers: `InitBare`, remotes (`Add`/`Ensure`/`Remove`/`GetUrl`/
  `GetConfiguredUrl`), `FindGitRoot`/`FindMainRepoRoot` (symlink-resolved),
  `Diff`/`DiffNameOnly`/`DiffHead`/`Log`, `CommitTime` (UTC)/`CommitAuthorEmail`,
  `HeadSha`/`CurrentBranch`/`IsDetachedHead`, `DefaultBranch` (ls-remote symref,
  falls back to `main`), `Fetch*`, `Push`/`PushWithOptions` (force-with-lease
  anchored to an expected SHA), `LsRemote`, `HasUncommittedChanges`,
  `CreateBranch`, `CommitAll`, `CopyLocalUserIdentity` (per-worktree scope with
  `--local` fallback), `WorktreeAdd`/`WorktreeRemove`, `ResolveRef`/`RefExists`/
  `ShowFile`, plus `EmptyTreeSha`/`IsZeroSha`.
- `PostReceiveHook` ports the notify-push hook: script generation (verbatim
  shell, credential path single-quoted), atomic executable install,
  managed-vs-custom refresh, and `IsolateHooksPath` (enables
  `extensions.worktreeConfig`, pins `core.hookspath` per-worktree, relocates
  `core.bare` to per-worktree scope), all best-effort/idempotent.
- `Redactor.RedactText` (minimal local copy of Go's `safeurl`; slice 6 promotes
  the full surface) hides URL userinfo before it reaches logs or error text.

Acceptance checks:

- Tests create real git repos in temp dirs (`GitTestSupport`), never mocking.
- Bare repo config read/write and worktree add/remove work under
  `safe.bareRepository=explicit` (env-injected git config), matching the Go
  issue #362 regression tests.
- Worktree add/remove behavior matches Go.
- All gate-repo git calls go through `GitClient`, which applies the `--git-dir`
  bare-repo rule centrally.
- `docker build -f Dockerfile.test.dotnet .` restores, builds, and runs the
  suite (185 tests) — used because the host has no dotnet SDK.

Deferred to later slices: SCM URL parsing and provider host backends (the full
`safeurl`/`scm` surface) land with slice 6; the daemon `notify-push` command the
hook invokes lands with slice 7.

### 5. Shell Process Lifecycle

Port cancellable process execution, process-tree isolation, clean-exit descendant reaping, output capture, and timeout behavior.

Acceptance checks:

- Tests prove grandchildren are killed on cancellation.
- Tests prove leaked descendants are reaped after clean command exit.
- Output-reading commands cannot hang forever when a descendant holds inherited pipes open.
- Windows behavior has explicit tests or documented parity gaps before release.

### 6. SCM URL Parsing and Host Backends

Port provider detection, GitHub/GitLab/Bitbucket/Azure DevOps URL parsing, auth checks, existing PR/MR lookup, and provider command wrappers.

Acceptance checks:

- URL parsing tests cover HTTPS, SSH, enterprise hosts, subgroups, and malformed URLs.
- GitLab host-scoped auth behavior is preserved.
- GitHub fork PR lookup lists by bare branch and filters owner fields.
- GitLab and Bitbucket fork routing skip unsupported self-PR cases.

### 7. Daemon IPC and Run Lifecycle

Port daemon startup, IPC protocol, run manager, run cancellation, stale run recovery, run status, and run abort by id.

Acceptance checks:

- Tests cover daemon start/stop, request/response IPC, run creation, cancellation, and stale recovery.
- `axi abort --run <id>` works outside a worktree.
- Unknown or inactive abort targets are idempotent no-ops.

### 8. AXI Command Surface and Gates

Port the agent-driving command surface, gate rendering, gate responses, help text, and parked awaiting-agent signal.

Acceptance checks:

- Output shape tests cover TOON fields and stable field order.
- Gate entry sets awaiting-agent state before pollers can observe the parked step.
- Gate response and cancellation clear awaiting-agent state.
- Skill, live AXI help, and docs guidance stay synchronized when text changes.

### 9. Pipeline Executor and Step Contracts

Port run orchestration, step state transitions, auto-fix loop contracts, run logging, step results, and cancellation propagation.

Acceptance checks:

- Executor tests cover success, failure, skipped steps, approval gates, auto-fix limits, and cancellation.
- Context cancellation reaches every subprocess or long-running operation.
- Terminal run states match Go behavior.

### 10. Review, Test, Lint, and Format Steps

Port local command execution, trusted command selection, agent-driven test fallback, review finding handling, and format/lint/test step behavior.

Acceptance checks:

- Trusted default-branch command loading is enforced.
- Pushed-branch config cannot self-enable repo commands.
- Review auto-fix remains disabled by default.
- Info-level review findings do not park or auto-fix under the default config.

### 11. Rebase, Push, and Force-Push Safety

Port authoritative remote-base fetching, bundled local-default detection, force-push lease decisions, patch-id incorporation checks, and push routing.

Acceptance checks:

- Rebase uses freshly fetched remote tracking refs, not local default branch state.
- Unpushed local default-branch commits bundled into a gated branch require approval.
- Force-push refuses to clobber unseen upstream commits.
- `lastSeenSHA` remains the last observed head, not a live ref read immediately before pushing.
- Fork push targets use `Repo.PushURL()` equivalent behavior.

### 12. PR and MR Creation

Port PR/MR creation and update behavior for supported providers.

Acceptance checks:

- GitHub PR creation keeps `--repo` pointed at the parent repo.
- GitHub fork PR creation uses `<fork_owner>:<branch>` for the head.
- Existing PR lookup handles fork owner filtering.
- Unsupported fork MR/PR routing fails closed instead of creating self PRs.

### 13. CI Monitor and Auto-Fix Loop

Port CI polling, timeout behavior, auto-fix on failing checks, merge-conflict rebase handling, and monitor cancellation.

Acceptance checks:

- `ci_timeout` is an idle timeout and re-arms when the default-branch tip advances.
- `ci_timeout: unlimited` and equivalent keywords map to the unlimited sentinel.
- GitLab pipeline jobs use branch-independent REST lookup when project path is known.
- CI auto-fix pushes use force-push safety logic.

### 14. Native Agent Integrations

Port Claude, Codex, Pi, Copilot, Droid, ACP/acpx, fake agent test harnesses, prompts, output handling, and agent process cleanup.

Acceptance checks:

- Each native agent command is started through the process lifecycle wrapper.
- Clean-exit leaked descendant tests cover agent runners.
- Recorded fixtures or fake agents cover command construction and parsing.
- Cancellation terminates the full process tree.

### 15. Terminal UI

Port the interactive terminal UI after the non-interactive CLI and daemon behavior are stable.

Acceptance checks:

- Model/update behavior has focused unit tests.
- UI can render run status, steps, findings, gates, and logs.
- The TUI degrades cleanly in non-interactive terminals.

### 16. Init, Skill, Wizard, Update, and Telemetry

Port repository initialization, generated agent skill installation, setup wizard, update checks, background update behavior, and telemetry configuration.

Acceptance checks:

- `init` preserves fork URL on idempotent refresh.
- Skill output is generated from one source of truth.
- Update checks can be disabled or redirected in tests.
- Telemetry metadata follows build-info configuration.

### 17. End-to-End Parity and Release Packaging

Port the end-to-end harness, Docker build/test path, release metadata injection, cross-platform publishing, archives, install scripts, and CI workflow integration.

Acceptance checks:

- Docker build of `Dockerfile.dotnet` restores, builds, and tests the .NET solution.
- E2E tests cover init, daemon, gates, process cleanup, config trust, push safety, fork routing, and CI monitoring.
- Release artifacts include Linux, macOS, and Windows targets.
- Published binaries embed version, commit, date, and telemetry metadata.
