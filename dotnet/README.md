# no-mistakes .NET port

This directory contains the incremental .NET rewrite of `no-mistakes`.

The current milestone is intentionally small:

- `NoMistakes.Core` contains shared logic that can be ported and tested without process or filesystem side effects.
- `NoMistakes.Cli` contains the first CLI compatibility surface: help, version output, and usage errors.
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
