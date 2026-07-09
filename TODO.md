# .NET Port TODO

Remaining phases of the Go-to-.NET rewrite tracked in [VERTICAL_SLICES.md](VERTICAL_SLICES.md).
Slices 1–5 are done. Slices 6–17 remain.

The Go implementation stays the compatibility oracle. Every slice follows the same workflow:

- Branch off the default branch, pull latest first.
- Add or port tests **before** implementing behavior (TDD).
- Scaffold the new `.NET` project(s), wire into `dotnet/no-mistakes.sln` and `NoMistakes.Tests.csproj`.
- Keep the solution green: `docker build -f Dockerfile.test.dotnet .` (no local dotnet SDK).
- Mark the slice `Done` in `VERTICAL_SLICES.md` with ported-behavior notes.
- Commit, push, open a PR against the default branch.

---

## 6. SCM URL Parsing and Host Backends → `NoMistakes.Scm`

- [ ] 6.1 Scaffold `NoMistakes.Scm`; promote the slice-4 local `Redactor` into the shared `safeurl` surface here.
- [ ] 6.2 Port provider detection + URL parsing: GitHub, GitLab, Bitbucket, Azure DevOps (HTTPS, SSH, enterprise hosts, subgroups).
- [ ] 6.3 Port `ExtractHost`, `RepoSlug` (GitHub), `ProjectPath` (GitLab, subgroups allowed).
- [ ] 6.4 Port provider command wrappers + auth checks; preserve GitLab **host-scoped** auth (`--hostname <host>`, unscoped fallback when host unknown).
- [ ] 6.5 Port existing PR/MR lookup: GitHub fork lookup lists by **bare branch** and filters returned head-owner fields.
- [ ] 6.6 GitLab/Bitbucket fork routing skips unsupported self-PR cases (fail closed).
- [ ] 6.7 Tests: HTTPS/SSH/enterprise/subgroup/malformed URLs; GitLab host-scoped auth; GitHub fork owner filtering.
- [ ] 6.8 Mark slice 6 Done; build/test in Docker; PR.

## 7. Daemon IPC and Run Lifecycle → `NoMistakes.Daemon`, `NoMistakes.Ipc`

- [ ] 7.1 Scaffold `NoMistakes.Ipc` + `NoMistakes.Daemon`.
- [ ] 7.2 Port daemon startup/shutdown (socket, PID file, server-PID location from `Paths`).
- [ ] 7.3 Port the IPC protocol (request/response methods, incl. `CancelRun`, `notify-push`).
- [ ] 7.4 Port the run manager: run creation, `HandleCancel`, run status, `runToInfo` (incl. awaiting-agent fields).
- [ ] 7.5 Port stale-run recovery (`RecoverStaleRuns` clears awaiting-agent, fails stale runs+steps in one tx).
- [ ] 7.6 Port `axi abort --run <id>` working outside a worktree; unknown/inactive target is an idempotent no-op.
- [ ] 7.7 Wire in the slice-4 `notify-push` hook command the post-receive hook invokes.
- [ ] 7.8 Tests: daemon start/stop, request/response IPC, run create/cancel/stale-recovery, abort-by-id no-op.
- [ ] 7.9 Mark slice 7 Done; build/test in Docker; PR.

## 8. AXI Command Surface and Gates → `NoMistakes.Cli`, `NoMistakes.Pipeline`

- [ ] 8.1 Port the `axi` command surface (home, run, respond, status, logs, abort).
- [ ] 8.2 Port TOON rendering with **stable field order**; run object, gate object, findings table (incl. `significance` column if merged).
- [ ] 8.3 Port gate responses (approve/fix/skip, `--findings`, `--add-finding`, `--step`, `--yes`).
- [ ] 8.4 Port the parked awaiting-agent signal: set on gate entry **before** pollers observe the parked step; clear on respond/cancel.
- [ ] 8.5 Keep skill body, live `axi` help strings, and `docs/.../agents.md` synchronized.
- [ ] 8.6 Tests: TOON output shape/field order; gate entry sets awaiting-agent before observability; respond/cancel clears it.
- [ ] 8.7 Mark slice 8 Done; build/test in Docker; PR.

## 9. Pipeline Executor and Step Contracts → `NoMistakes.Pipeline`

- [ ] 9.1 Port the `Step` contract + `StepContext`/`StepOutcome`.
- [ ] 9.2 Port run orchestration + step state transitions (pending→running→completed/failed/awaiting/fix-review).
- [ ] 9.3 Port the auto-fix loop contract + per-step auto-fix limits (review disabled by default).
- [ ] 9.4 Port run logging + step results persistence + fix summaries.
- [ ] 9.5 Port cancellation propagation to every subprocess/long-running op (via slice-5 `ShellCommand`).
- [ ] 9.6 Tests: success, failure, skipped steps, approval gates, auto-fix limits, cancellation; terminal states match Go.
- [ ] 9.7 Mark slice 9 Done; build/test in Docker; PR.

## 10. Review, Test, Lint, and Format Steps → `NoMistakes.Pipeline.Steps`

- [ ] 10.1 Port local command execution (`commands.{test,lint,format}` via `ShellCommand`).
- [ ] 10.2 Enforce trusted default-branch command loading (`EffectiveRepoConfig`); pushed branch cannot self-enable repo commands.
- [ ] 10.3 Port the review step: prompt, findings schema (severity + significance + action), risk assessment; auto-fix disabled by default.
- [ ] 10.4 Port the test step incl. agent-driven test fallback + evidence artifacts.
- [ ] 10.5 Port lint + format steps.
- [ ] 10.6 Tests: trusted-command enforcement, pushed-branch cannot self-enable, review auto-fix off by default, info findings do not park/auto-fix.
- [ ] 10.7 Mark slice 10 Done; build/test in Docker; PR.

## 11. Rebase, Push, and Force-Push Safety → `NoMistakes.Pipeline.Steps`, `NoMistakes.Git`

- [ ] 11.1 Port the rebase step: fetch authoritative remote refs, rebase onto remote-tracking refs (never local default).
- [ ] 11.2 Port `detectBundledLocalDefaultCommits` (unpushed local-default commits bundled into a gated branch require approval, non-auto-fixable).
- [ ] 11.3 Port `resolveForcePushDecision` (`forcepush.go`): lease-guard every force push; patch-id incorporation check (`--cherry-pick --right-only`, `^baseSHA` exclusion).
- [ ] 11.4 Keep `lastSeenSHA` the last **observed** head (rebase refreshes `origin/<branch>` only on normal push, never on force push).
- [ ] 11.5 Port push routing through `Repo.PushURL()` (fork targets).
- [ ] 11.6 Tests (port the Go regressions): rebase uses fetched refs; bundled-local-default requires approval; force-push refuses to clobber unseen upstream; fast-path clobber refused.
- [ ] 11.7 Mark slice 11 Done; build/test in Docker; PR.

## 12. PR and MR Creation → `NoMistakes.Pipeline.Steps`, `NoMistakes.Scm`

- [ ] 12.1 Port PR/MR creation + update for supported providers.
- [ ] 12.2 GitHub PR: keep `--repo` at the parent; fork PR uses `<fork_owner>:<branch>` head.
- [ ] 12.3 Existing-PR lookup handles fork-owner filtering (list by bare branch).
- [ ] 12.4 Unsupported fork MR/PR routing fails closed (no self-PR).
- [ ] 12.5 Tests: parent `--repo`, fork head format, existing-PR owner filtering, fail-closed fork routing.
- [ ] 12.6 Mark slice 12 Done; build/test in Docker; PR.

## 13. CI Monitor and Auto-Fix Loop → `NoMistakes.Pipeline.Steps`, `NoMistakes.Daemon`

- [ ] 13.1 Port CI polling + check status reading (GitHub; GitLab REST pipeline jobs when project path known).
- [ ] 13.2 Port `ci_timeout` as an **idle** timeout: re-arm `timeoutAnchor` when the base-branch tip advances; `started` fixed for pacing.
- [ ] 13.3 Port `ci_timeout` keyword parsing (`unlimited`/`none`/`off`/`never`/non-positive → unlimited sentinel); keep default (7d) in sync with default config.
- [ ] 13.4 Port auto-fix on failing checks + merge-conflict rebase (`autoFixCI`); CI auto-fix pushes use force-push safety (slice 11).
- [ ] 13.5 Port monitor cancellation + `checks-passed` outcome (return when green, keep monitoring in background).
- [ ] 13.6 Tests: idle re-arm, unlimited keyword mapping, GitLab REST jobs, force-push-safe auto-fix.
- [ ] 13.7 Mark slice 13 Done; build/test in Docker; PR.

## 14. Native Agent Integrations → `NoMistakes.Agent`

- [ ] 14.1 Scaffold `NoMistakes.Agent`; port the agent interface + run options.
- [ ] 14.2 Port native agents: Claude, Codex, Pi, Copilot, Droid; and ACP/acpx target selection.
- [ ] 14.3 Start every agent command through the slice-5 process lifecycle wrapper; cancellation terminates the full process tree.
- [ ] 14.4 Port `ResolveAgent` PATH resolution (deferred from slice 2).
- [ ] 14.5 Port fake-agent test harness + recorded fixtures (or fakes) for command construction + output parsing.
- [ ] 14.6 Tests: agents started via lifecycle wrapper, clean-exit leaked-descendant reaping per runner, cancellation kills the tree.
- [ ] 14.7 Mark slice 14 Done; build/test in Docker; PR.

## 15. Terminal UI → `NoMistakes.Tui`

- [ ] 15.1 Scaffold `NoMistakes.Tui`; pick the .NET TUI approach (document the lipgloss/bubbletea substitute).
- [ ] 15.2 Port model/update behavior with focused unit tests.
- [ ] 15.3 Render run status, steps, findings, gates, and logs.
- [ ] 15.4 Degrade cleanly in non-interactive terminals.
- [ ] 15.5 Tests: model/update units; non-interactive fallback.
- [ ] 15.6 Mark slice 15 Done; build/test in Docker; PR.

## 16. Init, Skill, Wizard, Update, and Telemetry → `NoMistakes.Cli`, `NoMistakes.Core`

- [ ] 16.1 Port `init` (gate bare repo, remotes, hook install/refresh, skill install); preserve fork URL on idempotent refresh.
- [ ] 16.2 Port the generated agent skill from one source of truth (equivalent of `genskill` + drift check).
- [ ] 16.3 Port the setup wizard.
- [ ] 16.4 Port update checks + background update behavior (disable/redirect in tests).
- [ ] 16.5 Port telemetry configuration following build-info metadata.
- [ ] 16.6 Tests: init fork-URL preservation, skill single-source generation, update check disable/redirect, telemetry metadata.
- [ ] 16.7 Mark slice 16 Done; build/test in Docker; PR.

## 17. End-to-End Parity and Release Packaging

- [ ] 17.1 Port the end-to-end harness (init, daemon, gates, process cleanup, config trust, push safety, fork routing, CI monitoring).
- [ ] 17.2 Ensure `Dockerfile.dotnet` (and build/test Dockerfiles) restore, build, and test the full solution.
- [ ] 17.3 Port release metadata injection (version, commit, date, telemetry into published binaries).
- [ ] 17.4 Cross-platform publishing: Linux, macOS, Windows targets + archives + install scripts.
- [ ] 17.5 Wire into the CI workflow.
- [ ] 17.6 Resolve deferred Windows parity gaps (process-group/job-object from slice 5) or document them as release-blocking/known.
- [ ] 17.7 Tests/E2E green across targets; mark slice 17 Done; final PR.
