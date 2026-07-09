# STEPS: .NET Port — Slices 6–17

Each slice: branch off `main` (pull latest first), tests before implementation, keep the solution green in Docker, mark the slice `Done` in `VERTICAL_SLICES.md`, commit, push, open a PR against `main`. Slices split into sub-steps below share one branch/PR; the slice's final sub-step marks it Done and opens the PR.

- [x] **Slice 6a.1 — Scaffold `NoMistakes.Scm`; promote `Redactor` to shared `safeurl` surface.** Scaffold the `NoMistakes.Scm` project and promote the slice-4 local `Redactor` into the shared `safeurl` surface, updating the slice-4 call sites to use the shared location. Tests move with the code and keep passing.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the relocated `Redactor` tests passing from the shared `safeurl` surface and all pre-existing tests still green.

- [x] **Slice 6a.2 — Provider detection and URL parsing.** Port provider detection and URL parsing for GitHub, GitLab, Bitbucket, and Azure DevOps, covering HTTPS, SSH, enterprise hosts, and subgroups. Tests cover HTTPS/SSH/enterprise/subgroup/malformed URLs for all four providers.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the four-provider URL-parsing tests passing.

- [x] **Slice 6a.3 — URL helpers: `ExtractHost`, `RepoSlug`, `ProjectPath`.** Port `ExtractHost`, `RepoSlug` (GitHub), and `ProjectPath` (GitLab, subgroups allowed) on top of the slice-6a.2 parsing. Tests cover each helper across provider and subgroup variants.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the `ExtractHost`/`RepoSlug`/`ProjectPath` tests passing.

- [x] **Slice 6b.1 — GitHub and GitLab command wrappers and auth checks.** Port the GitHub (`gh`) and GitLab (`glab`) CLI command wrappers and auth checks in `NoMistakes.Scm`, preserving GitLab host-scoped auth (`glab auth status --hostname <host>`, falling back to the unscoped check when the host is unknown). Tests cover both providers' command/argument construction, the host-scoped auth arguments, and the unscoped fallback.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the GitHub/GitLab command-wrapper and GitLab host-scoped-auth tests passing.

- [x] **Slice 6b.2 — Bitbucket and Azure DevOps command wrappers and auth checks.** Port the Bitbucket and Azure DevOps CLI command wrappers and auth checks in `NoMistakes.Scm`, following the slice-6b.1 wrapper pattern. Tests cover both providers' command/argument construction and auth-check paths.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the Bitbucket/Azure DevOps command-wrapper and auth-check tests passing.

- [x] **Slice 6c — Existing PR/MR lookup and fork routing.** Port existing PR/MR lookup: GitHub fork lookup lists by bare branch and filters returned head-owner fields (never passes `<owner>:<branch>` to the list command); GitLab/Bitbucket fork routing fails closed on unsupported self-PR cases. Tests cover GitHub fork-owner filtering and the fail-closed GitLab/Bitbucket paths. Mark slice 6 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the PR/MR-lookup tests passing, slice 6 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [x] **Slice 7a — Scaffold `NoMistakes.Ipc`; IPC protocol.** Scaffold the `NoMistakes.Ipc` project and port the IPC request/response protocol, including the `CancelRun` and `notify-push` message types, with serialization round-trip tests over a socket pair.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with IPC request/response round-trip tests passing.

- [x] **Slice 7b — Scaffold `NoMistakes.Daemon`; daemon startup/shutdown.** Scaffold the `NoMistakes.Daemon` project and port daemon startup and shutdown: socket creation, PID file, and server-PID location from `Paths`. Tests cover start, stop, and PID-file lifecycle.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with daemon start/stop tests passing.

- [x] **Slice 7c — Run manager.** Port the run manager: run creation, `HandleCancel`, run status, and `runToInfo` including the awaiting-agent fields. Tests cover run create, cancel, and status/info mapping.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with run-manager create/cancel/status tests passing.

- [x] **Slice 7d — Stale-run recovery.** Port `RecoverStaleRuns`: clears awaiting-agent and fails stale runs plus their steps in one transaction. Tests cover the transactional recovery and that a recovered run is never reported as parked.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the stale-run-recovery tests passing.

- [x] **Slice 7e.1 — Abort-by-id.** Port `axi abort --run <id>` working outside a worktree (needs only `NM_HOME` plus the daemon), with unknown/inactive targets and a stopped daemon as idempotent no-ops (`aborted: false`). Tests cover abort-by-id success and each no-op case (unknown id, inactive run, stopped daemon).
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the abort-by-id success and no-op tests passing.

- [x] **Slice 7e.2 — `notify-push` wiring.** Wire in the slice-4 `notify-push` hook command invoked by the post-receive hook, routed over the slice-7a IPC surface to the daemon. Tests cover the hook invocation reaching the daemon. Mark slice 7 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the notify-push hook-invocation tests passing, slice 7 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [x] **Slice 8a — TOON rendering.** Port TOON rendering with stable field order in `NoMistakes.Cli`: the run object, the gate object, and the findings table (including the `significance` column if merged). Tests assert output shape and field order.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the TOON output-shape/field-order tests passing.

- [x] **Slice 8b — Read-only `axi` commands.** Port the read-only `axi` commands: home, status, and logs, rendering via the slice-8a TOON layer. Tests cover each command's output against the rendered shapes.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the axi home/status/logs tests passing.

- [ ] **Slice 8c.1 — `axi run` and `axi abort`.** Port the `axi run` and `axi abort` commands (worktree/branch-scoped abort; abort-by-id already landed in slice 7e). Tests cover run submission and scoped abort.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the `axi run` and `axi abort` tests passing.

- [ ] **Slice 8c.2a — `axi respond` verb dispatch.** Port the `axi respond` command with the three response verbs (approve/fix/skip) resolving a parked gate, without the finding or targeting flags. Tests cover each verb's gate-resolution semantics.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the approve/fix/skip verb tests passing.

- [ ] **Slice 8c.2b — `axi respond` finding flags.** Port the `--findings` and `--add-finding` flags on `axi respond`, including finding-payload parsing and validation errors for malformed payloads. Tests cover each flag's payload path and the malformed-payload errors.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the `--findings`/`--add-finding` payload and error tests passing.

- [ ] **Slice 8c.2c — `axi respond` `--step` targeting and `--yes` default.** Port the `--step` flag (targeting a specific parked step) and the `--yes` default behavior on `axi respond`. Tests cover step targeting, targeting a non-parked step, and the `--yes` default path.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the `--step` and `--yes` tests passing.

- [ ] **Slice 8d — Parked awaiting-agent signal.** Port the awaiting-agent signal in `NoMistakes.Pipeline`: set on gate entry before pollers can observe the parked step, cleared the moment the gate wait returns (respond or cancel); render `awaiting_agent: parked <duration>` in the run object only while set and the run is non-terminal, with an injectable clock. Tests cover the set-before-observe and clear-on-respond-or-cancel invariants plus the render.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the awaiting-agent invariant tests passing.

- [ ] **Slice 8e — Guidance-surface sync.** Synchronize the three agent-guidance surfaces for the ported `axi` behavior: the skill body, the live `axi` help/note strings, and `docs/.../agents.md`. Mark slice 8 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: the three surfaces describe the same driving guidance (verified by reading each), `docker build -f Dockerfile.test.dotnet .` still succeeds, slice 8 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 9a — Step contract and state machine.** Port the `Step` contract with `StepContext`/`StepOutcome` and the step state transitions (pending → running → completed/failed/awaiting/fix-review) in `NoMistakes.Pipeline`. Tests cover every legal transition and terminal states matching Go.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the step-contract and state-transition tests passing.

- [ ] **Slice 9b.1 — Core executor orchestration.** Port run orchestration over the step contract for the straight-line paths: steps run in order, success advances, failure stops the run, and skipped steps are recorded. Tests cover success, failure, and skipped-step sequencing.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the executor success/failure/skip orchestration tests passing.

- [ ] **Slice 9b.2 — Approval-gate wait machinery.** Port the executor's approval-gate wait (the `waitForApproval` equivalent): a step returning needs-approval parks the run at the gate until a respond or cancel resolves it, then the executor resumes or stops accordingly. Tests cover gate entry, resume on respond, and stop on cancel.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the approval-gate park/resume/cancel tests passing.

- [ ] **Slice 9b.3 — Cancellation propagation.** Port cancellation propagation through the executor: cancelling a run reaches every subprocess and long-running operation via the slice-5 `ShellCommand` wrapper, and the run lands in the cancelled terminal state. Tests cover cancellation mid-step killing the subprocess tree and the resulting run/step states.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the executor cancellation-propagation tests passing.

- [ ] **Slice 9c.1 — Auto-fix loop.** Port the auto-fix loop contract with per-step auto-fix limits and the review-disabled default (`Review: 0`; a repo or global override re-enables it). Tests cover limit enforcement, step re-invocation on fix, and the review-disabled default.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the auto-fix limit and review-default tests passing.

- [ ] **Slice 9c.2 — Run logging.** Port run logging: per-run log files under the app state directory with the same content and layout as the Go implementation. Tests cover log creation and content for a run.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the run-logging tests passing.

- [ ] **Slice 9c.3 — Step-results persistence and fix summaries.** Port step-results persistence and fix summaries to the database. Tests cover persisted step results and fix-summary content. Mark slice 9 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the persistence and fix-summary tests passing, slice 9 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 10a — Trusted command loading (security boundary).** Port trusted default-branch command loading (`EffectiveRepoConfig` and the pinned-SHA read): `commands.{test,lint,format}` and `agent` come only from the trusted default branch, fail closed on fetch/resolve failure, and a pushed branch cannot self-enable `allow_repo_commands`. Tests port the fail-closed-on-fetch-failure, pinned-SHA-reads-fresh-default, and pushed-branch-cannot-self-enable regressions.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the trust-boundary regression tests passing.

- [ ] **Slice 10b.1 — Lint and format steps.** Port local command execution for `commands.{lint,format}` via `ShellCommand` as the lint and format steps, sourcing commands through slice-10a trusted loading. Tests cover each step's success and failure paths.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the lint and format step tests passing.

- [ ] **Slice 10b.2a — Test step: configured command.** Port the test step for the configured-command path: `commands.test` execution via `ShellCommand` through slice-10a trusted loading. Tests cover configured-command success and failure.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the configured-command test-step success/failure tests passing.

- [ ] **Slice 10b.2b — Test step: agent-driven fallback.** Port the test step's agent-driven fallback when no test command is configured: spawn the agent to run the tests and parse its outcome into the step result (agent invocation behind a test seam/fake until `NoMistakes.Agent` lands in slice 14). Tests cover fallback selection when `commands.test` is absent and outcome parsing for pass and fail.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the agent-fallback selection and outcome-parsing tests passing.

- [ ] **Slice 10b.2c — Test step: evidence artifacts.** Port the test step's evidence-artifact production for both the configured-command and agent-fallback paths. Tests cover artifact creation and content on each path.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the evidence-artifact tests passing.

- [ ] **Slice 10c.1 — Review step: findings schema and prompt.** Port the review step's findings schema (severity + significance + action) and the review prompt construction. Tests cover schema round-tripping (parse/serialize of findings with all three fields) and prompt content.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the findings-schema and prompt tests passing.

- [ ] **Slice 10c.2 — Review step: risk assessment.** Port the review step's risk assessment on top of the slice-10c.1 schema. Tests cover risk-assessment derivation from findings.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the risk-assessment tests passing.

- [ ] **Slice 10c.3 — Review step: auto-fix default and parking behavior.** Port the review step's gate/auto-fix interaction: auto-fix disabled by default (`Review: 0`, re-enabled only by a repo or global override), blocking and ask-user findings park for an agent decision, and info-level findings neither park nor auto-fix under the default. Tests cover the off-by-default override path, parking for blocking/ask-user findings, and the info-level neither-parks-nor-fixes case. Mark slice 10 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the review auto-fix-default and parking tests passing, slice 10 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 11a — Rebase step onto fetched remote refs.** Port the rebase step: fetch authoritative remote refs (`origin/<default>` and `origin/<branch>` or the fork tracking ref) and rebase onto those remote-tracking refs, never the local default branch. On a force push the step skips both the rebase-onto and the branch fetch, so the tracking ref stays the last-observed head. Tests port the rebase-uses-fetched-refs regression and assert no branch refresh on the force-push path.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the rebase-step tests (fetched-refs and no-refresh-on-force-push) passing.

- [ ] **Slice 11b — Bundled local-default-commit detection.** Port `detectBundledLocalDefaultCommits`: when the working repo's local default tip is ahead of `origin/<default>` and an ancestor of the branch HEAD, return needs-approval with auto-fix disabled; return nil best-effort when the local default advanced past the branch point or the working repo is unreadable. Tests port the `TestRebaseStep_DetectsUnpushedLocalDefaultBranchCommits` regression (#283).
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the bundled-local-default detection regression passing.

- [ ] **Slice 11c — Force-push lease guard.** Port `resolveForcePushDecision`: allow a force push only when the branch is new, the remote equals the head, the remote equals `lastSeenSHA`, or every remote commit is incorporated by patch-id (`git rev-list --cherry-pick --right-only HEAD...current`) excluding history reachable from `baseSHA` (`^baseSHA`); anything else returns the would-discard error and the caller must not push, and a fetch/ls-remote failure fails closed (never degrade to bare `--force`/`--force-with-lease`). Tests port the `TestResolveForcePushDecision_*` matrix.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the `resolveForcePushDecision` decision-matrix tests passing.

- [ ] **Slice 11d — Push step and CI-fix push through the lease guard.** Route both force-push sites through slice-11c: the push step passes the rebase-synced tracking ref as `lastSeenSHA` and `Run.BaseSHA` for the exclusion; the CI-fix push passes `Run.HeadSHA`. Route pushes through `Repo.PushURL()` for fork targets. Tests port the remaining Go regressions: refuses-to-clobber-unseen-upstream (#281), refuses-to-clobber-advanced-upstream (#305), and fast-path clobber refused. Mark slice 11 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with all ported force-push/rebase regression tests passing, slice 11 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 12a — GitHub PR creation.** Port GitHub PR creation in `NoMistakes.Pipeline.Steps`/`NoMistakes.Scm`: `--repo` stays pointed at the parent repository, and `<fork_owner>:<branch>` is used as the head when a fork is configured. Tests cover parent `--repo` and the fork head format.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the GitHub PR-creation tests (parent `--repo`, fork head format) passing.

- [ ] **Slice 12b — GitHub existing-PR lookup and PR update.** Port GitHub existing-PR lookup (list by bare branch, filter returned head-owner fields — never pass `<owner>:<branch>` to the list command) and PR update for an already-open PR. Tests cover owner filtering on lookup and the update path.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the existing-PR owner-filtering and PR-update tests passing.

- [ ] **Slice 12c.1 — GitLab MR creation and update.** Port MR creation and update for the supported non-fork GitLab path. Tests cover the create and update paths.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the GitLab MR create/update tests passing.

- [ ] **Slice 12c.2 — Bitbucket PR creation and update.** Port PR creation and update for the supported non-fork Bitbucket path. Tests cover the create and update paths.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the Bitbucket PR create/update tests passing.

- [ ] **Slice 12c.3 — Fail-closed GitLab/Bitbucket fork routing.** Make unsupported GitLab/Bitbucket fork MR/PR routing fail closed with no self-PR, including legacy or manually edited rows with `fork_url` set. Tests cover the fail-closed skip for both providers and the legacy `fork_url` row case. Mark slice 12 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the fail-closed fork-routing tests passing, slice 12 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 13a.1 — CI polling loop and GitHub check-status reading.** Port the CI polling loop and GitHub check-status reading, mapping GitHub check states into the `Check` model. Tests cover the polling loop and GitHub status mapping.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the CI-polling and GitHub status-mapping tests passing.

- [ ] **Slice 13a.2 — GitLab pipeline-jobs reading via REST with legacy fallback.** Port GitLab check-status reading through the branch-independent REST pipeline-jobs endpoint (`glab api projects/<url-encoded group%2Fproject>/pipelines/<id>/jobs`, mapping `finished_at` into `Check.CompletedAt`) when the project path is known, keeping the legacy `glab ci get` path only as the fallback when no project path is supplied. Tests cover the URL-encoded REST jobs path, the `finished_at` mapping, and the legacy fallback.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the GitLab REST-jobs and legacy-fallback tests passing.

- [ ] **Slice 13b — Idle-timeout semantics and keyword parsing.** Port `ci_timeout` as an idle timeout: `timeoutAnchor` re-arms when the base-branch tip advances (injectable `baseBranchTip`, re-arm only ever extends) while `started` stays fixed for poll pacing; port keyword parsing (`unlimited`/`none`/`off`/`never`/non-positive map to the unlimited sentinel, `0` means the 7-day default) and keep the default in sync with the default config. Tests cover idle re-arm, keyword mapping, and the default-config sync check.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the idle-timeout and keyword-parsing tests passing.

- [ ] **Slice 13c.1 — CI auto-fix for failing checks.** Port the failing-check half of `autoFixCI`: detect failing checks, invoke the fix, and push the updated head through the slice-11 force-push safety (`Run.HeadSHA` as `lastSeenSHA`). Tests cover the force-push-safe auto-fix of a failing check.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the force-push-safe failing-check auto-fix tests passing.

- [ ] **Slice 13c.2 — Rebase on merge conflict.** Port the merge-conflict half of `autoFixCI`: detect a merge conflict against the base branch and rebase, with the resulting push also going through the slice-11 force-push safety. Tests cover the merge-conflict rebase path.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the merge-conflict rebase tests passing.

- [ ] **Slice 13c.3 — Monitor cancellation and `checks-passed` outcome.** Port monitor cancellation and the `checks-passed` outcome: the step returns when checks are green while the monitor keeps running in the background until merge, close, cancel, or timeout. Tests cover cancellation and the checks-passed return with continued background monitoring. Mark slice 13 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the cancellation and checks-passed tests passing, slice 13 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 14a.1 — Scaffold `NoMistakes.Agent`; agent interface and run options.** Scaffold the `NoMistakes.Agent` project and port the agent interface and run options. Tests cover the interface contract with a trivial in-package fake.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the agent-interface and run-options tests passing.

- [ ] **Slice 14a.2 — `ResolveAgent` PATH resolution.** Port `ResolveAgent` PATH resolution (deferred from slice 2) onto the slice-14a.1 interface. Tests cover found, not-found, and precedence cases against a controlled PATH.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the `ResolveAgent` PATH-resolution tests passing.

- [ ] **Slice 14a.3 — Fake-agent test harness.** Port the fake-agent test harness with recorded fixtures (or fakes) for command construction and output parsing, exercised against the slice-14a.1 interface. Tests drive the harness through command construction and output parsing.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the fake-agent-harness tests passing.

- [ ] **Slice 14b — Claude agent runner.** Port the Claude native agent on the slice-14a interface: the command starts through the slice-5 process lifecycle wrapper so cancellation terminates the full process tree, and leaked descendants are reaped on clean exit. Tests verify command construction, output parsing, lifecycle-wrapper start, clean-exit reaping, and cancellation-kills-tree for this runner.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the Claude runner lifecycle/reaping tests passing.

- [ ] **Slice 14c — Codex and Pi agent runners.** Port the Codex and Pi native agents following the slice-14b pattern, with the same per-runner lifecycle, clean-exit reaping, and cancellation tests.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the Codex and Pi runner lifecycle/reaping tests passing.

- [ ] **Slice 14d — Copilot and Droid agent runners.** Port the Copilot and Droid native agents following the same pattern, with the same per-runner lifecycle, clean-exit reaping, and cancellation tests.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the Copilot and Droid runner lifecycle/reaping tests passing.

- [ ] **Slice 14e — ACP/acpx target selection.** Port ACP/acpx target selection (`acp:` agent values) onto the runner set, started through the same lifecycle wrapper, with selection and lifecycle tests. Mark slice 14 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the ACP/acpx selection and lifecycle tests passing, slice 14 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 15a — TUI library choice and scaffold.** Scaffold `NoMistakes.Tui`, pick the .NET TUI approach (the lipgloss/bubbletea substitute), document the decision in the repo, and port a minimal model/update skeleton proving the event loop under test.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the skeleton model/update test passing and the library-choice document exists in the repo.

- [ ] **Slice 15b — Run, step, and gate rendering.** Port model/update behavior rendering run status, steps, and gates on the slice-15a skeleton, with focused model/update unit tests per surface.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the run/step/gate rendering model tests passing.

- [ ] **Slice 15c — Findings, logs, and non-interactive fallback.** Port findings and log rendering, and degrade cleanly in non-interactive terminals. Tests cover the two surfaces and the non-interactive fallback. Mark slice 15 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the findings/logs and non-interactive-fallback tests passing, slice 15 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 16a.1 — `init`: gate repo and remotes.** Port the repo half of `init` in `NoMistakes.Cli`: gate bare-repo creation and remote wiring, idempotent on re-run, preserving an existing fork URL on refresh. Tests cover first-run init, idempotent refresh, and fork-URL preservation.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the init repo/remote tests (including fork-URL preservation) passing.

- [ ] **Slice 16a.2 — `init`: hook install/refresh.** Port hook installation into the gate repo (the post-receive hook driving `notify-push`), refreshed idempotently on re-run. Tests cover first install and refresh of an existing hook.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the hook install/refresh tests passing.

- [ ] **Slice 16a.3 — `init`: skill install.** Port user-level skill installation/refresh from `init`, idempotent on re-run. Tests cover install and refresh of the installed skill.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the skill install/refresh tests passing.

- [ ] **Slice 16b — Generated skill with drift check.** Port the generated agent skill from one source of truth (equivalent of `genskill` plus a `--check` drift gate wired into the build/lint path); `init` installs this same rendering. Tests cover single-source generation and drift detection.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the skill-generation and drift-check tests passing.

- [ ] **Slice 16c.1 — Setup wizard prompt engine and test harness.** Port the setup wizard's prompt engine in `NoMistakes.Cli`: the ordered prompt sequence as a testable model (scripted answers in, collected answers out) with no terminal interaction required in tests, plus the harness that drives it. Tests drive the full prompt sequence with scripted answers, covering defaults, validation, and back-out/skip paths.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the scripted prompt-flow tests passing.

- [ ] **Slice 16c.2 — Setup wizard configuration output.** Port the wizard's configuration production: map collected answers into the written configuration exactly as the Go wizard does, and wire the wizard command entry point onto the slice-16c.1 engine. Tests cover the answers-to-configuration mapping and that a scripted wizard run writes the expected configuration.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the wizard configuration-output tests passing.

- [ ] **Slice 16d.1 — Update checks.** Port update checks with background update behavior, disabled or redirected in tests so no real network/update runs. Tests cover the version-comparison logic and the disable/redirect plumbing.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the update-check tests passing.

- [ ] **Slice 16d.2 — Telemetry configuration.** Port telemetry configuration following build-info metadata. Tests cover telemetry metadata derivation from build info. Mark slice 16 Done in `VERTICAL_SLICES.md` and open the PR.
Done when: `docker build -f Dockerfile.test.dotnet .` succeeds with the telemetry-metadata tests passing, slice 16 is marked Done in `VERTICAL_SLICES.md`, and a PR is open against `main`.

- [ ] **Slice 17a.1 — E2E harness foundation.** Port the e2e harness foundation: setup options including `AllowRepoCommands`, journey infrastructure running inside `Dockerfile.test.dotnet`, and one minimal smoke journey proving the harness end to end.
Done when: `docker build -f Dockerfile.test.dotnet .` runs the harness smoke journey and it passes.

- [ ] **Slice 17a.2 — E2E: init and daemon lifecycle journeys.** Port the e2e journeys covering init and daemon lifecycle onto the slice-17a.1 harness.
Done when: `docker build -f Dockerfile.test.dotnet .` runs the init and daemon-lifecycle e2e journeys and they pass.

- [ ] **Slice 17a.3 — E2E: gate-driving journeys.** Port the e2e journeys covering gate driving (park, respond, resume) onto the harness.
Done when: `docker build -f Dockerfile.test.dotnet .` runs the gate-driving e2e journeys and they pass.

- [ ] **Slice 17b — E2E: process cleanup and config trust.** Port the e2e journeys for process cleanup (grandchild reaping) and repo-config trust (commands from default branch, pushed branch cannot self-enable).
Done when: `docker build -f Dockerfile.test.dotnet .` runs the process-cleanup and config-trust e2e journeys and they pass.

- [ ] **Slice 17c.1 — E2E: push-safety journeys.** Port the e2e journeys for force-push/rebase safety (the slice-11 behaviors: lease-guarded force push, refused clobbers, bundled-local-default detection) onto the harness.
Done when: `docker build -f Dockerfile.test.dotnet .` runs the push-safety e2e journeys and they pass.

- [ ] **Slice 17c.2 — E2E: fork-routing journeys.** Port the e2e journeys for fork routing (the slice-12 behaviors: fork push target, parent-repo PR base, fail-closed non-GitHub fork paths) onto the harness.
Done when: `docker build -f Dockerfile.test.dotnet .` runs the fork-routing e2e journeys and they pass.

- [ ] **Slice 17c.3 — E2E: CI-monitoring journeys.** Port the e2e journeys for CI monitoring (the slice-13 behaviors: polling, idle timeout, auto-fix, checks-passed) onto the harness.
Done when: `docker build -f Dockerfile.test.dotnet .` runs the CI-monitoring e2e journeys and they pass.

- [ ] **Slice 17d.1 — Dockerfiles build and test the full solution.** Ensure `Dockerfile.dotnet` and the build/test Dockerfiles restore, build, and test the complete solution (every project, all test assemblies).
Done when: `docker build -f Dockerfile.test.dotnet .` and `docker build -f Dockerfile.dotnet .` both succeed against the full solution.

- [ ] **Slice 17d.2 — Release metadata injection.** Port release metadata injection: version, commit, date, and telemetry values baked into published binaries at build time.
Done when: `docker build -f Dockerfile.dotnet .` produces a binary whose version output reports the injected version, commit, and date.

- [ ] **Slice 17d.3a — Cross-platform publish targets and archives.** Port cross-platform publishing: Linux, macOS, and Windows publish targets plus archive production with release metadata.
Done when: `docker build -f Dockerfile.dotnet .` produces publishable archives with release metadata for all three OS targets.

- [ ] **Slice 17d.3b — Install scripts.** Port the install scripts consuming the slice-17d.3a archives.
Done when: the install scripts exist in the repo and a script run against a locally produced archive installs a working binary (version output prints).

- [ ] **Slice 17e.1 — CI wiring.** Wire the .NET build and test into the CI workflow so every push/PR runs them.
Done when: the CI workflow file runs the .NET build/test and a CI run on the branch completes them successfully.

- [ ] **Slice 17e.2 — Windows parity gap assessment.** Enumerate the deferred Windows process-group/job-object parity gaps from slice 5 in a repo document (e.g. `docs/windows-parity.md`): each gap, its impact, and a per-gap decision — resolve now, or document as known/release-blocking with rationale.
Done when: the assessment document exists in the repo listing every deferred slice-5 Windows gap with an explicit resolve-or-document decision for each.

- [ ] **Slice 17e.3a — Windows parity: known-issue documentation.** For each gap the slice-17e.2 assessment marked document, finalize the known-issue entry (impact, workaround, release-blocking status) in the assessment document.
Done when: every gap marked document in the slice-17e.2 assessment has a finalized known-issue entry (impact, workaround, release-blocking status) in the repo document.

- [ ] **Slice 17e.3b — Windows parity: expand resolve-marked gaps into steps.** Insert into `STEPS.md`, directly after this step, one new checkbox step per gap the slice-17e.2 assessment marked resolve; each new step covers exactly one gap's Windows job-object/`taskkill` semantics with its own tests and its own `Done when:` line (`docker build -f Dockerfile.test.dotnet .` succeeds with that gap's tests passing). If no gap is marked resolve, insert nothing. The inserted steps are then implemented one at a time, in order, before slice 17e.4.
Done when: `STEPS.md` contains one checkbox step per resolve-marked gap between this step and slice 17e.4 (or none if no gap is marked resolve), each ending with a `Done when:` line.

- [ ] **Slice 17e.4 — Final slice bookkeeping.** Mark slice 17 Done in `VERTICAL_SLICES.md` and open the final PR against `main`.
Done when: slice 17 is marked Done in `VERTICAL_SLICES.md` and the final PR is open against `main`.
