# Environment — machines, install layout, logs, commands

## The owner's machine (where the game runs)

Debian 13, 15 GB RAM, Intel i5-13420H (12 threads), NVIDIA RTX 4050 Laptop (6 GB). GTA V Legacy 1.0.3889 from
Steam, run through Proton Experimental (Wine 11.0) with DXVK 3.0.2. Firefox, Claude Desktop and Steam usually run
alongside and take ~4 GB; the machine swaps during game sessions (see `docs/tasks/` hitch task and
`docs/HANDOFF.md`). Memory is the scarce resource: every hundred MB the mod holds matters.

The game folder: `~/.steam/debian-installation/steamapps/common/Grand Theft Auto V`. The Wine prefix:
`~/.steam/debian-installation/steamapps/compatdata/271590/pfx`. Never edit either by hand; the launcher deploys
and restores the mod files around every game session.

## The install: `~/GTANetwork`

| Path | Content |
| --- | --- |
| `GTANetwork.Launcher`, `play.sh`, `setup.conf` (`AUTO_UPDATE=0` on the owner's machine: dev builds are not replaced by releases), `update.sh`, `setup-linux.sh` | The Linux launcher (self-contained .NET 8 single file) and the scripts the installer wrote. |
| `bin/` | ScriptHookV (`ScriptHookV.dll`, `dinput8.dll` — the player's own download), `ScriptHookVDotNet.dll/.asi/.ini`, native helpers, ClearScript native DLL. |
| `bin/scripts/` | The managed client: `GTANetwork.dll`, `NativeUI.dll`, `GTANetworkShared.dll`, Newtonsoft, protobuf-net, SharpDX, ClearScript, … |
| `cef/` | The browser host `GTANetwork.CefHost.exe` and the whole Chromium runtime (CefSharp, `libcef.dll`, `CefSharp.BrowserSubprocess.exe`, locales, …), page-aligned; `cef/cache` is Chromium's profile. |
| `server/` | A local server (`GTANetworkServer`) with the `auth` and `freeroam` resources — the test server the owner joins. |
| `resources/` | Resource files downloaded from servers (served to the browser host as `https://<resource>/…`). |
| `logs/` | See below. |
| `settings.xml` | `PlayerSettings` (the owner has `CefGpu=true`, `CefSharedTexture=true`, `CefFrameRate=60`, `CefInProcessGpu=true`). |

## Logs (`~/GTANetwork/logs/`)

| File | Written by | Contains |
| --- | --- | --- |
| `Runtime.log` | client | Init lines, `[PROFILE] Present hook overlay …` (debug mode), `[HITCH] …` (always), resource download summaries. |
| `CEF.log` | client | Browser host start, `CEF initialised: Chromium …`, browsers, `Texture ring …`, page console, `[host stderr]`, fallbacks. |
| `CEF-host.log` | browser host | `Cef.Initialize`, switches, browsers, texture relay, shared texture rings, paints (debug). |
| `CEF-chromium.log` | Chromium | Chromium's own log (Warning level; Verbose in debug mode). |
| `ScriptHookVDotNet-<date>.log` | SHVDN fork | Script load, `held the game thread for N ms` (every tick ≥ 20 ms), `[PROFILE]` per 10 s (fps, ms per script). |
| `Error.log` | client | Managed exceptions of the client. |
| `launcher.log` | launcher | Deploy/restore, launch method, debug mode, game start/exit. |
| `hitch-monitor.log` | launcher (`--debug`, Linux) | One line per second: swap in/out, memory stall ms, MemAvailable, GTA5 RSS/swapped/majflt, Chromium RSS, CPU %, MHz, °C, GPU MHz/°C/%/throttle reasons. |
| `~/steam-271590.log` | Proton (`--debug`) | Wine log with `+seh,+loaddll,+mscoree,…` (exceptions, module loads) and DXVK's stderr. |

## The repository checkout and the dev container

Repository: `~/Projects/FreeMode`. Builds run in the dev container (`docker compose run --rm dev <cmd>`; image
`gtanetwork-dev`, .NET SDK 8, pwsh, python3, rsync). `.env` (git-ignored) sets `GTAN_INSTALL=/home/sviatoslav/GTANetwork`,
mounted as `/gtanetwork` in the container. Details: `docs/DEVCONTAINER.md`.

```bash
docker compose run --rm dev eng/dev-build-client.sh --sync     # client + browser host, built against the real SHVDN, synced into ~/GTANetwork (~15 s)
docker compose run --rm dev eng/dev-test.sh                    # the Linux CI checks: server smoke + bot integration
docker compose run --rm dev dotnet build GTANetwork.sln -c Release
docker compose run --rm dev dotnet publish Launcher/GTANetwork.Launcher.csproj -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /gtanetwork   # the owner's launcher
eng/cef-harness.sh [--build] [--shared-texture] [--bench N --size WxH] [--hold S]   # host machine, game closed
python3 eng/pe-realign.py --check ~/GTANetwork/cef                                  # every file in cef/ page-aligned?
```

`eng/dev-build-client.sh` copies the real `ScriptHookVDotNet.dll` from the install into `Shv.NET/bin/` (git-ignored)
and builds against it. A client built against the stub compiles but dies in game with `MissingMethodException`.

## CI (GitHub Actions, `.github/workflows/build.yml`)

Jobs: `linux` (solution build, publish server/launcher/bot/Map2Resource, server smoke test, bot integration test,
artifacts), `windows` (ScriptHookVDotNet C++/CLI, client package `cef\` included and page-aligned, NSIS installer),
`release` (only on `workflow_dispatch` with `release_tag`, or a `v*` tag: zips artifacts, release body from the
matching `CHANGELOG.md` section). No GTA V anywhere in CI. GitHub access of agents is scoped to
`sashagoncharov19/freemode`; pushing tags returns 403 by design.

## Code graph

`pip install --user code-review-graph` once per machine; `code-review-graph build` once per checkout (the SQLite
graph lives in `.code-review-graph/`, git-ignored); `code-review-graph update` after edits. The MCP server is
configured in `.mcp.json` for Claude Code (other tools: `code-review-graph install --platform <name>`).
