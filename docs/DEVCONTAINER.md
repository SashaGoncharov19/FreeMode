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
| In-game client `GTANetwork.dll` (net48) + the ClearScript runtime | yes — **against the real `ScriptHookVDotNet.dll`** (`eng/dev-build-client.sh` copies it from your install into `Shv.NET/bin/`, git-ignored). The managed stub in `Shv.NET/ref` only proves the code compiles: it is not binary-compatible (other `InputArgument` conversions → `MissingMethodException`, the client never initialises in game) |
| Browser host `GTANetwork.CefHost.exe` (net48) + the Chromium runtime | yes (Windows binaries from NuGet) |
| CEF harness `CefHarness.exe` (net48) | yes; it runs under Proton on the host machine (`eng/cef-harness.sh`) |
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
# build GTANetwork.dll + GTANetwork.CefHost.exe and copy them into the install (managed files only — seconds)
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
eng/dev-build-client.sh --sync --cef # also refresh cef/ (only when the CefSharp version changed); the PE files in
                                     # cef/ are page-aligned by eng/pe-realign.py (Wine copies 512-byte-aligned DLLs per process)
eng/dev-test.sh                      # the Linux CI checks: build the solution, run the server + bot tests
dotnet build GTANetwork.sln -c Release    # plain full build
```

## The CEF harness: the browser without the game

`Tools/CefHarness` is the acceptance test of the browser host. `eng/cef-harness.sh` runs it **on the host
machine** (not in the container) under Proton, in the game's Wine prefix, with the same environment
`play.sh` uses:

```bash
eng/cef-harness.sh --build          # build host + harness in the container, then run
eng/cef-harness.sh                  # start cef/GTANetwork.CefHost.exe from the build output, create a browser,
                                    # serve https://harness/ui/index.html, read the pixels, wait for resourceCall
eng/cef-harness.sh --install-cef    # the same against the host of your install (~/GTANetwork/cef)
eng/cef-harness.sh --in-process     # Chromium inside the harness process (how the client did it before)
eng/cef-harness.sh --alone --appdomain   # ... in a second AppDomain like ScriptHookVDotNet: reproduces the crash
```

Exit code 0 means Chromium started, painted, and the page reached the game side. Logs land next to the harness
(`Tools/CefHarness/bin/Release/net48/`: `harness.log`, `harness-host.log`, `harness-chromium.log`,
`harness-wine.log`). The script refuses to run while GTA V is running (same prefix).

## When you still need CI

* A **release** players install (the full package + Windows launchers + the real SHVDN) — that is the
  `build.yml` workflow with a `release_tag`.
* Any change to `ScriptHookVDotNet.dll` itself (memory patterns, the C++/CLI host).

Everything else you can now see in game without waiting for GitHub.
