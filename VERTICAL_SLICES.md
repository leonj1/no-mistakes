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
| 2. Paths, environment, and config loading | Planned | `internal/paths`, `internal/config` | `NoMistakes.Core`, `NoMistakes.Config` |
| 3. SQLite run database | Planned | `internal/db`, migrations | `NoMistakes.Data` |
| 4. Git command wrapper and repository model | Planned | `internal/git`, `internal/types` | `NoMistakes.Git`, `NoMistakes.Core` |
| 5. Shell process lifecycle | Planned | `internal/shellenv` | `NoMistakes.Processes` |
| 6. SCM URL parsing and host backends | Planned | `internal/scm`, `internal/bitbucket` | `NoMistakes.Scm` |
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

Port `NM_HOME`, app directory layout, default config rendering, repo config parsing, YAML handling, and config precedence.

Acceptance checks:

- Unit tests cover default paths, `NM_HOME`, config defaults, repo config parsing, and effective config merging.
- Security tests prove code-executing config fields stay separated from untrusted pushed-branch config.
- Compatibility tests compare representative Go and .NET config outcomes.

### 3. SQLite Run Database

Port schema creation, migration behavior, run records, step records, awaiting-agent fields, and recovery updates.

Acceptance checks:

- Database tests use `t.TempDir()` equivalent temp roots and isolated SQLite files.
- Set, clear, and recovery behavior for `awaiting_agent_since` matches Go.
- Migration tests can open an old schema fixture and upgrade it.

### 4. Git Command Wrapper and Repository Model

Port repository discovery, command execution, remote parsing, worktree helpers, SHA/ref helpers, and bare gate repo handling.

Acceptance checks:

- Tests create real git repos.
- Bare repo commands work under `safe.bareRepository=explicit`.
- Worktree add/remove behavior matches Go.
- All gate-repo git calls go through the .NET git wrapper.

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
