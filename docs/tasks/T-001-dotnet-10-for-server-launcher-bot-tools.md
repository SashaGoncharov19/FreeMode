# T-001 — .NET 10 for server, launcher, bot, Map2Resource and the dev container

Status: done
Epic: E-01 Platform upgrade
Size: M
Branch: task/T-001-dotnet-10 from the integration branch
Depends on: none
PR: yes

## Goal

Every `net8.0` project targets `net10.0`; the dev container and CI build with the .NET 10 SDK; Roslyn is 5.9;
`eng/dev-test.sh` passes; the owner's install has a .NET 10 launcher and server.

## Why

.NET 8 support ends 10 Nov 2026 (E-01, Q-09). Server-side TS (E-04) and the master list (E-07) start on .NET 10.

## Scope

* In: TFMs, SDK pins, package bumps that the TFM change requires, the container image, CI, docs.
* Out: protobuf-net 3 (separate commit only if the bot passes with it; otherwise a follow-up task), code modernisation.

## Files

* Change: `global.json` (sdk 10.0.x, `rollForward: latestMajor`), `Server/GTANetworkServer.csproj`,
  `Launcher/GTANetwork.Launcher.csproj`, `Tools/GTANetwork.Bot/GTANetwork.Bot.csproj`, `Map2Resource/Map2Resource.csproj`
  (`net8.0` → `net10.0`), `Directory.Build.props` (`RoslynVersion` 4.14.0 → 5.9.0), `.devcontainer/Dockerfile`
  (`mcr.microsoft.com/dotnet/sdk:10.0`), `.devcontainer/devcontainer.json` if it pins the image, `.github/workflows/build.yml`
  (`actions/setup-dotnet` `dotnet-version: 10.0.x`, the `windows` job too), `docs/agents/conventions.md` and
  `docs/agents/environment.md` (versions), `CHANGELOG.md`, `README.md` layout table (`net8.0` → `net10.0`).
* Read: `Server/Managers/ScriptCompiler.cs` (Roslyn API use; `TRUSTED_PLATFORM_ASSEMBLIES` reference set),
  `eng/setup-linux.sh` (does it install a .NET runtime? the server publish is self-contained, so no change expected).

## Approach

1. Bump TFMs and `global.json`; `docker compose build` with the new SDK image; `dotnet build GTANetwork.sln -c Release`.
2. Fix compile errors from analyzers/breaking changes (expected: none or trivial; `Nullable` is off).
3. Roslyn 5.9: build the `example`, `freeroam` and `auth` resources through the smoke test.
4. Run `eng/dev-test.sh`; publish the launcher and the server into the install (commands in `docs/agents/environment.md`).
5. CI: push the branch only if the owner says so; otherwise note "CI unverified" in Result.

## Acceptance criteria

- [x] `grep -r "net8.0" --include=*.csproj .` returns nothing; `global.json` pins 10.0.
- [x] `eng/dev-test.sh` ends with `All local checks passed.`
- [x] `~/GTANetwork/GTANetwork.Launcher --help` runs; the published server starts (tested on port 4599: resources freeroam, example, auth started on runtime 10.0.11).
- [x] `CHANGELOG.md` line under the version in progress.

## Test plan

`docker compose build && docker compose run --rm dev eng/dev-test.sh` → `All local checks passed.`;
`docker compose run --rm dev dotnet publish Launcher/... -o /gtanetwork` → `~/GTANetwork/GTANetwork.Launcher --help`.

## Risks and notes

`Microsoft.ClearScript.V8` (added in T-006) supports .NET 10; the Lidgren fork is net48/netstandard and needs no change.
If `Microsoft.CodeAnalysis.CSharp 5.9.0` changes `CSharpCompilation` defaults, `ScriptCompiler.cs` may need explicit
`LanguageVersion`.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-04 23:20 agent — started on task/T-001-dotnet-10.
* 2026-09-04 23:30 agent — the sdk:10.0 image is Ubuntu 24.04 with an existing UID 1000 user; the Dockerfile now renames it instead of failing in useradd; the PowerShell repo config follows /etc/os-release.
* 2026-09-04 23:45 agent — tests green in the rebuilt container (SDK 10.0.400); launcher and server published into ~/GTANetwork; PR opened.

* 2026-09-05 08:20 agent — PR #5 merged into claude/modernize-deps-4d8uyn (CI green: Linux + Windows). done.

## Result

* **Changed**: `global.json` (SDK 10.0.100, rollForward latestFeature); `Server/GTANetworkServer.csproj`, `Launcher/GTANetwork.Launcher.csproj`,
  `Tools/GTANetwork.Bot/GTANetwork.Bot.csproj`, `Map2Resource/Map2Resource.csproj` (net10.0); `Directory.Build.props` (Roslyn 5.9.0);
  `.devcontainer/Dockerfile` (sdk:10.0 image, user creation that reuses an existing UID, PowerShell repo from os-release);
  `.github/workflows/build.yml` (setup-dotnet 10.0.x in both jobs); docs and READMEs (versions); `CHANGELOG.md`.
* **Verified**: `docker compose build` → OK; in the container `dotnet --version` → `10.0.400`; `eng/dev-test.sh` → `Build succeeded.`,
  `smoke test passed`, `integration test passed`, `auth integration test passed`, `All local checks passed.`; the published server
  started on port 4599 with the owner's resources: `Resource freeroam started!`, `Resource auth started!`, gamemode banner shows
  runtime 10.0.11; `~/GTANetwork/GTANetwork.Launcher --help` prints the help. Zero new compiler warnings.
* **Not done / follow-ups**: protobuf-net 2.4.9 → 3.x (wire-compatible but API changes; own task if wanted); CI run of the branch
  awaits the PR.
* **Owner check**: restart the local server (`~/GTANetwork/server/start.sh`; the running one still uses the .NET 8 binaries
  from before this sync) and run `~/GTANetwork/play.sh` once — the launcher is the .NET 10 build; nothing in game changes.
