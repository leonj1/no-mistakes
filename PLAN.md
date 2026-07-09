# PLAN: .NET Port — Slices 6–17

## Overview

Continue the incremental Go-to-.NET rewrite of `no-mistakes` under `dotnet/`, tracked in `VERTICAL_SLICES.md`. Slices 1–5 (CLI bootstrap, paths/config, SQLite DB, git wrapper, shell process lifecycle) are done. This plan covers the remaining twelve slices: SCM backends, daemon/IPC, AXI surface, pipeline executor, the step implementations (review/test/lint/format, rebase/push safety, PR creation, CI monitor), native agents, TUI, init/skill/wizard/update/telemetry, and final e2e parity plus release packaging.

The Go implementation stays the compatibility oracle: ported behavior must match Go semantics, and Go regression tests are ported alongside the code they protect.

## Goals

- Reach feature parity of the .NET port with the Go implementation, one independently shippable slice at a time.
- Preserve every safety invariant documented in `CLAUDE.md` — these are the product:
  - Repo config trust boundary: `commands.{test,lint,format}` and `agent` load only from the trusted default branch at a pinned SHA; fail closed on fetch failure; pushed branch cannot self-enable `allow_repo_commands`.
  - Force-push safety: every force push routes through the ported `resolveForcePushDecision` lease guard with the patch-id incorporation check and `^baseSHA` exclusion; `lastSeenSHA` stays the last *observed* head (rebase never refreshes `origin/<branch>` on a force push); fail closed, never degrade to bare `--force`.
  - Rebase base always comes from freshly fetched remote-tracking refs, never local state; bundled unpushed local-default commits require human approval (non-auto-fixable).
  - Process-tree lifecycle: every subprocess spawned for a cancellable step/agent goes through the slice-5 `ShellCommand` wrapper; clean-exit descendant reaping, cancellation kills the whole tree, pipe-wedge backstop.
  - Parked awaiting-agent invariant: set on gate entry before pollers can observe the gate, cleared on respond/cancel and on stale-run recovery.
  - GitLab specifics: host-scoped auth (`--hostname`, unscoped fallback), no `--state opened`, REST pipeline-jobs endpoint for detached-HEAD worktrees.
  - Fork routing: push via `Repo.PushURL()`; GitHub PR `--repo` stays on the parent with `<fork_owner>:<branch>` head; existing-PR lookup lists by bare branch and filters head owner; GitLab/Bitbucket fork MR/PR fails closed (skip, no self-PR).
  - Review auto-fix disabled by default; info-level findings neither park nor auto-fix.
  - CI `ci_timeout` is an idle timeout (re-arms on base-tip advance), with the `unlimited`/`none`/`off`/`never` keyword sentinel and 7-day default kept in sync with the default config.

## Constraints

- **TDD**: port or write tests before implementing each behavior.
- **No local dotnet SDK**: build and test via `docker build -f Dockerfile.test.dotnet .`.
- Each slice: branch off the default branch (pull latest first), keep `dotnet/no-mistakes.sln` and `NoMistakes.Tests.csproj` wired and green, mark the slice `Done` in `VERTICAL_SLICES.md` with ported-behavior notes, commit, push, PR against `main`.
- Prefer real temp dirs, real git repos, and real subprocess execution where the Go tests do; preserve safety behavior before convenience behavior.
- New dependencies (e.g. a TUI library for slice 15) get documented and discussed; the lipgloss/bubbletea substitute choice is written down in slice 15.
- Windows process-group/job-object parity gaps deferred from slice 5 are resolved or explicitly documented as known/release-blocking in slice 17.

## Architecture

New/extended .NET projects under `dotnet/src`, mirroring the Go package boundaries:

| Slice | .NET project(s) | Go source |
| --- | --- | --- |
| 6 | `NoMistakes.Scm` (absorbs slice-4 `Redactor` as shared `safeurl`) | `internal/scm`, `internal/bitbucket` |
| 7 | `NoMistakes.Daemon`, `NoMistakes.Ipc` | `internal/daemon`, `internal/ipc` |
| 8 | `NoMistakes.Cli` (axi), `NoMistakes.Pipeline` (gates) | `internal/cli/axi*.go`, `internal/gate` |
| 9 | `NoMistakes.Pipeline` (executor, step contracts) | `internal/pipeline` |
| 10 | `NoMistakes.Pipeline.Steps` (review/test/lint/format) | `internal/pipeline/steps` |
| 11 | `NoMistakes.Pipeline.Steps`, `NoMistakes.Git` (rebase/push/force-push) | `rebase.go`, `push.go`, `forcepush.go` |
| 12 | `NoMistakes.Pipeline.Steps`, `NoMistakes.Scm` (PR/MR creation) | `pr.go`, SCM backends |
| 13 | `NoMistakes.Pipeline.Steps`, `NoMistakes.Daemon` (CI monitor) | `ci*.go` |
| 14 | `NoMistakes.Agent` | `internal/agent` |
| 15 | `NoMistakes.Tui` | `internal/tui` |
| 16 | `NoMistakes.Cli`, `NoMistakes.Core` (init/skill/wizard/update/telemetry) | `internal/skill`, `internal/wizard`, `internal/update`, `internal/telemetry` |
| 17 | `dotnet/tests` e2e harness, Dockerfiles, release workflow | `internal/e2e`, `Makefile`, CI workflows |

Dependency order is the slice order: 6 (SCM) feeds 12/13; 7 (daemon/IPC) feeds 8/13; 9 (executor) feeds 10–13; 14 (agents) feeds 10's agent-driven test fallback in full fidelity. Slices land sequentially.

## Verification

Per slice: `docker build -f Dockerfile.test.dotnet .` green (runs the full solution test suite in Docker). Slice 17 adds the ported e2e harness covering init, daemon, gates, process cleanup, config trust, push safety, fork routing, and CI monitoring, plus cross-platform publish checks via `Dockerfile.dotnet`/`Dockerfile.build.dotnet`.
