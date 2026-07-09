# .NET Port Plan

Summary of `VERTICAL_SLICES.md`, restated as a plan of intent.

## Intent

Rewrite the Go CLI `no-mistakes` in .NET under `dotnet/`, delivered as independently shippable vertical behavior slices rather than a package-by-package translation. The Go implementation stays the compatibility oracle until the .NET port reaches feature parity. Every slice leaves the solution buildable, testable, and publishable.

## Working Rules

1. Test-first: add or port tests before implementing behavior.
2. Keep the solution green: `dotnet test dotnet/no-mistakes.sln --no-restore` after every slice.
3. Keep self-contained single-file publishing working for `NoMistakes.Cli`.
4. Prefer real temp dirs, real git repos, and real subprocesses where Go tests already cross process/I-O boundaries — no heavy mocking.
5. Port safety behavior (trust boundaries, force-push leases, process reaping) before convenience behavior.
6. Update `VERTICAL_SLICES.md` whenever a slice completes, splits, or reorders.

## Plan by Phase

### Phase A — Foundations (Done)

- **Slice 1 — Bootstrap CLI and build artifact.** Root help, `--version`, unknown-command exit codes, single-file publish. (`NoMistakes.Cli`, `NoMistakes.Core`)
- **Slice 2 — Paths, environment, config loading.** `NM_HOME` layout, strict global config parsing, lenient repo config, Go-duration `ci_timeout` compatibility, and the `EffectiveRepoConfig` trust boundary (code-executing fields only from trusted default branch unless `allow_repo_commands`). Deferred: `ResolveAgent` PATH resolution to slice 14. (`NoMistakes.Core`, `NoMistakes.Config`)
- **Slice 3 — SQLite run database.** Same schema as Go, idempotent additive migrations, ULID keys, awaiting-agent set/clear, single-transaction stale-run recovery, stats rollups. (`NoMistakes.Data`)
- **Slice 4 — Git wrapper and repository model.** `GitClient` subprocess runner with bare-gate-repo `--git-dir` handling (`safe.bareRepository=explicit`), non-interactive env, worktree/remote/ref helpers, post-receive hook install, URL redaction. Deferred: full `safeurl`/SCM surface to slice 6, `notify-push` daemon command to slice 7. (`NoMistakes.Git`)
- **Slice 7 — Daemon IPC and run lifecycle.** Daemon start/stop, request/response IPC, run manager, cancellation, stale recovery, `axi abort --run <id>` outside a worktree as idempotent no-op on unknown targets. (`NoMistakes.Daemon`, `NoMistakes.Ipc`)

### Phase B — Command Surface and Execution Core (Current)

- **Slice 8 — AXI command surface and gates** *(in progress: `axi respond` verb dispatch, finding flags, `--step`/`--yes` already ported)*. Agent-driving commands, TOON gate rendering with stable field order, gate responses, parked awaiting-agent signal set before pollers observe the gate and cleared on respond/cancel. Keep skill, live AXI help, and docs in sync.
- **Slice 5 — Shell process lifecycle.** Cancellable execution, process-tree isolation, clean-exit descendant reaping, pipe-hang backstops, timeout behavior; explicit Windows tests or documented parity gaps. (`NoMistakes.Processes`)
- **Slice 9 — Pipeline executor and step contracts.** Orchestration, step state transitions, auto-fix loop contracts, run logging, cancellation reaching every subprocess, terminal states matching Go. (`NoMistakes.Pipeline`)

### Phase C — Pipeline Steps and Safety

- **Slice 6 — SCM URL parsing and host backends.** Provider detection; GitHub/GitLab/Bitbucket/Azure DevOps URL parsing (HTTPS, SSH, enterprise hosts, subgroups); GitLab host-scoped auth; GitHub fork PR lookup by bare branch with owner filtering; fail-closed fork routing for GitLab/Bitbucket. (`NoMistakes.Scm`)
- **Slice 10 — Review, test, lint, format steps.** Trusted default-branch command loading enforced; pushed branch cannot self-enable repo commands; review auto-fix disabled by default; info-level findings neither park nor auto-fix.
- **Slice 11 — Rebase, push, force-push safety.** Rebase onto freshly fetched remote refs only; bundled unpushed local-default commits require approval; force-push lease refuses to clobber unseen upstream commits; `lastSeenSHA` stays the last *observed* head, never a live read; fork pushes use `PushURL()` behavior.
- **Slice 12 — PR and MR creation.** GitHub `--repo` stays on parent, fork head as `<fork_owner>:<branch>`, existing-PR owner filtering, unsupported fork routing fails closed.
- **Slice 13 — CI monitor and auto-fix loop.** Idle `ci_timeout` re-armed on default-branch tip advance, `unlimited` keyword sentinel, GitLab branch-independent REST job lookup, CI auto-fix pushes routed through force-push safety.

### Phase D — Agents, UI, and Product Surface

- **Slice 14 — Native agent integrations.** Claude, Codex, Pi, Copilot, Droid, ACP/acpx runners through the process lifecycle wrapper; clean-exit descendant-leak tests; fake agents/fixtures for command construction; full-tree cancellation. (`NoMistakes.Agent`)
- **Slice 15 — Terminal UI.** After non-interactive CLI and daemon stabilize: run status, steps, findings, gates, logs; clean degradation in non-interactive terminals. (`NoMistakes.Tui`)
- **Slice 16 — Init, skill, wizard, update, telemetry.** `init` preserves fork URL on refresh, skill generated from one source of truth, disableable update checks, telemetry from build-info.

### Phase E — Parity and Release

- **Slice 17 — End-to-end parity and release packaging.** E2E harness (init, daemon, gates, process cleanup, config trust, push safety, fork routing, CI monitoring), `Dockerfile.dotnet` build/test path, Linux/macOS/Windows artifacts with embedded version/commit/date/telemetry metadata.

## Status Snapshot

| Phase | Slices | Status |
| --- | --- | --- |
| A Foundations | 1, 2, 3, 4, 7 | Done |
| B Command surface + execution core | 8, 5, 9 | Slice 8 in progress |
| C Steps + safety | 6, 10, 11, 12, 13 | Planned |
| D Agents, UI, product surface | 14, 15, 16 | Planned |
| E Parity + release | 17 | Planned |
