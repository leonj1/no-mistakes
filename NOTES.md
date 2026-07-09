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
