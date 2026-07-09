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
