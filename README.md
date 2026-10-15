# my-hook-tool - ZZZ

## Status

`ZZZ` is the long-lived Zenless Zone Zero development branch. It currently
contains the ZZZ 3.0 profile scaffold and the initial session protocol, not a
validated ZZZ runtime bridge. Do not treat an empty `.hook` session as Shader,
Material or TypeTree recovery. HSR implementations and offsets are not copied
into this branch.

## Branch And Version Policy

- `main` preserves the initial baseline.
- `HSR` and `ZZZ` are separate long-lived game development branches.
- Client updates require version-specific profiles, binary hashes, ABI checks
  and evidence; a branch name does not establish compatibility.
- Former versioned branch tips are preserved as
  `archive/branches/<former-name>` tags. Historical profile IDs, evidence and
  existing release tags retain their original versions.

## Build

```powershell
dotnet build .\src\MyHookTool\MyHookTool.csproj -c Release
```

Use .NET 8. The profile scaffold is `profiles/ZZZ-3.0-MUMU.json`.
There is currently no validated one-click ZZZ collection workflow in this
branch. Runtime records require a real matching guest bridge; host session
creation alone does not establish object identity or recover game data.

See [`docs/runtime-bridge.md`](docs/runtime-bridge.md) for the session protocol
and evidence boundaries.
