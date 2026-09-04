# Dev container: build and test locally, skip the CI wait

The slow part of iterating on the client used to be the round trip **edit → push → wait for CI →
download release → install → test**. The managed client (`GTANetwork.dll`) compiles on Linux, so a
container with the .NET 8 SDK rebuilds it in seconds and drops it straight into your existing
`~/GTANetwork` install. Only the real C++/CLI `ScriptHookVDotNet.dll` needs Windows + MSVC, and that
almost never changes — you already have it in `~/GTANetwork/bin` from an installed release.

## What builds here

| Part | Builds in the container? |
| --- | --- |
| Server, launcher, bot (net8.0) | yes |
| In-game client `GTANetwork.dll` (net48) + the CEF/ClearScript runtime | yes (against the managed SHVDN reference stub) |
| `ScriptHookVDotNet.dll` (C++/CLI) | no — Windows + MSVC only (comes from a release; the CI Windows job builds it) |

## Open it

**VS Code / Cursor / JetBrains:** install Docker, open the repo, and choose **Reopen in Container**
(VS Code: the *Dev Containers* extension; the "Reopen in Container" prompt appears automatically). The
first build restores NuGet packages into a cached volume; later starts are instant. Then use the
integrated terminal.

**CLI, no editor** (uses `docker-compose.yml`):

```bash
export GTAN_INSTALL=$HOME/GTANetwork          # your installed game (for --sync)
docker compose build
docker compose run --rm dev bash              # a shell in the container
```

## The fast loop

Edit client C#, then, in a container shell:

```bash
# build GTANetwork.dll and copy it into the install (managed DLLs only — seconds)
eng/dev-build-client.sh --sync
```

Then on the host, launch as usual:

```bash
~/GTANetwork/play.sh --debug
```

`--sync` writes into `$GTAN_INSTALL` (set in the container to `/gtanetwork`) or, if you didn't mount
an install, into `~/GTANetwork`. Because the repo is bind-mounted, the build output also appears on
the host under `Client/bin/Release/net48/`, so you can instead sync from the host:

```bash
eng/dev-sync-client.sh ~/GTANetwork        # host-side copy, no container needed
```

### Mounting your install into the container (optional)

So `--sync` lands the client without leaving the container, mount the install. With Compose it is
already wired to `$GTAN_INSTALL`. For VS Code, add to `.devcontainer/devcontainer.json`:

```jsonc
"mounts": [
  "source=gtan-nuget,target=/home/dev/.nuget/packages,type=volume",
  "source=${localEnv:HOME}/GTANetwork,target=/gtanetwork,type=bind"
]
```

## Other commands

```bash
eng/dev-build-client.sh              # just build (no sync)
eng/dev-build-client.sh -c Debug --sync   # Debug build turns on verbose logging in game
eng/dev-build-client.sh --sync --cef # also refresh cef/ (only when the CefSharp version changed)
eng/dev-test.sh                      # the Linux CI checks: build the solution, run the server + bot tests
dotnet build GTANetwork.sln -c Release    # plain full build
```

## When you still need CI

* A **release** players install (the full package + Windows launchers + the real SHVDN) — that is the
  `build.yml` workflow with a `release_tag`.
* Any change to `ScriptHookVDotNet.dll` itself (memory patterns, the C++/CLI host).

Everything else you can now see in game without waiting for GitHub.
