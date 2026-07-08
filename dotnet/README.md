# no-mistakes .NET port

This directory contains the incremental .NET rewrite of `no-mistakes`.

The current milestone is intentionally small:

- `NoMistakes.Core` contains shared logic that can be ported and tested without heavy process side effects, including the `Paths` filesystem layout (`NM_HOME` and the app directory structure).
- `NoMistakes.Cli` contains the first CLI compatibility surface: help, version output, and usage errors.
- `NoMistakes.Config` ports paths-adjacent configuration loading: global/repo YAML parsing, Go-compatible `ci_timeout` durations, the config merge, and the code-executing-config trust boundary.
- `NoMistakes.Data` ports the SQLite run database (`internal/db`): schema, additive migrations, repo/run/step/round/intent-cache records, the awaiting-agent signal, stale-run recovery, and usage stats.
- `NoMistakes.Tests` covers the behavior already ported.

## Build and test

```sh
dotnet restore no-mistakes.sln
dotnet build no-mistakes.sln --no-restore
dotnet test no-mistakes.sln --no-restore
```

## Publish a compiled artifact

```sh
dotnet publish src/NoMistakes.Cli/NoMistakes.Cli.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o ./artifacts/linux-x64
```

Use the same shape with `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, or `win-arm64` for other targets.
