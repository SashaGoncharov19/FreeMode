# Testing — what runs where, and what to record

## In-game autotest (no person at the keyboard)

`GTAN_AUTOTEST=127.0.0.1:4499 ~/GTANetwork/play.sh --debug` starts the game through the launcher; once the game is ready the
client connects to that server by itself, waits for the client scripts, calls `freeroam:ping` over RPC from a client script and
from a CEF page (`ui/autotest/index.html`), logs every step as `autotest: …` in `~/GTANetwork/logs/Runtime.log` (`RESULT: OK` or
`RESULT: FAILED`) and quits the game, so the launcher returns and the folder is restored. `GTAN_AUTOTEST_QUIT=0` keeps the game
open. Steam must be running (Proton), the local server too; a run takes 2–4 minutes on the owner's machine. Never run it, or
container builds, while the owner is playing (`pgrep -x 'GTA5.ex[e]'`). Implementation: `Client/Util/AutoTest.cs`; the launcher
passes the two variables through (`Launcher.Core/LaunchSession.cs`).

There is no GTA V in CI and no Windows machine. Everything that can be tested without the game is tested
without the game; the rest is verified by the owner from a checklist the agent writes.

## The matrix

| Change in | Run | Where | What it proves |
| --- | --- | --- | --- |
| Anything | `dotnet build GTANetwork.sln -c Release` | dev container | Every managed project compiles (client against the SHVDN stub). |
| `Server/`, `Shared/`, `Tools/GTANetwork.Bot`, resources | `eng/dev-test.sh` | dev container | Server smoke test (Roslyn scripting, HTTP file server, SIGTERM) and the bot integration test (a headless client joins over the real protocol and passes the `auth` resource). Same as the Linux CI job. |
| `Subprocess/GTANetwork.CefHost`, `Shared/Cef`, `Shared/CefLaunch.cs` | `eng/cef-harness.sh` and `eng/cef-harness.sh --shared-texture` | host machine (Proton; the game must be closed) | The browser host starts, a local page renders, the page→game bridge works, eval→frame latency (ms), resize, close; in the second mode the shared-texture ring across processes. Exit code 0 = pass. |
| Browser performance | `eng/cef-harness.sh [--shared-texture] --bench 10 --size 1280x720` | host machine | Frames/s delivered, copy cost, CPU of host and Chromium. |
| `Client/`, `NativeUI/` | `eng/dev-build-client.sh --sync` then the owner's game run | dev container, then the owner | Compiles against the real ScriptHookVDotNet (the stub is not binary compatible) and lands in `~/GTANetwork`. Behaviour is verified in game only. |
| `Launcher/` | `dotnet build Launcher/GTANetwork.Launcher.csproj`; `~/GTANetwork/GTANetwork.Launcher --help`; publish (see environment.md) | dev container / host | The launcher is the owner's `play.sh`; a broken launcher blocks every in-game test. |
| Sync, server performance | `eng/load-test.sh <players> <seconds>` (T-002): one bot process with N connections (`GTANetwork.Bot --bots N --move 1500`) against one server, `GET /metrics.json` sampled every 5 s; `LOAD_NO_ENCRYPTION=1` for plaintext, `LOAD_THREADS` for the bots' pump threads; `LOAD_PLAYERS=N eng/dev-test.sh` adds a run to the local checks | dev container (game closed) | The table (tick p50/p99/max, ticks/s, packets and KB/s per player in and out, GC, near sets, RSS) and `artifacts/load-<N>.json`; the baseline and its reading are `docs/SYNC.md` §6. |
| Memory of Chromium | `eng/cef-harness.sh --shared-texture --hold 15` and, while it holds, `awk '/^Pss:/{print $2}' /proc/<pid>/smaps_rollup` for every `GTANetwork.CefHost.exe` process | host machine | PSS per process (the numbers in `docs/CEF-UPGRADE.md`, "Memory under Wine"). |

The commands run in the dev container as `docker compose run --rm dev <command>` (`docs/DEVCONTAINER.md`); the
harness runs on the host machine because it needs Proton and the game's Wine prefix.

## What to record

In the task file's Result and, when it is a lasting number, in the area document (`docs/SYNC.md`,
`docs/CEF-UPGRADE.md`): the command, the result line copied verbatim, the machine/conditions when they matter
(Proton version, GPU, page size). Example: `eng/cef-harness.sh --shared-texture --bench 8 --size 1280x720` →
`60.0 texture events/s, 60.0 GPU copies/s (0.020 ms per CopyResource), host 5 %, Chromium 10 %`.

## In-game verification (the owner)

When a task needs the game, the agent writes in the task file:

1. What to run (`~/GTANetwork/play.sh` or `play.sh --debug`), what to do in game (join the local server, open the
   page, type, drive…), for how long.
2. The log lines that show success, as `grep` commands over `~/GTANetwork/logs/*.log`:
   `Runtime.log` (client, `[PROFILE]`, `[HITCH]`), `CEF.log` (browser, game side), `CEF-host.log`,
   `CEF-chromium.log`, `ScriptHookVDotNet-<date>.log` (scripts: "held the game thread", `[PROFILE]`),
   `Error.log` (exceptions), `launcher.log`, `hitch-monitor.log` (with `--debug`), and `~/steam-271590.log`
   (Wine log with `--debug`).
3. What "bad" looks like and what to send back (the log excerpt, the time).

The owner answers in chat; the agent copies the outcome into the task Log and closes or reopens the task.

## Adding tests

* Protocol or server behaviour: extend the bot (`Tools/GTANetwork.Bot`) and `eng/integration-test*.sh`; the CI job runs them.
* Browser host behaviour: extend `Tools/CefHarness/HostTest.cs` (the default run must stay under ~10 s).
* Client-only logic that does not need the game (parsers, math, protocol handling): prefer moving it to `Shared/`
  where the bot can exercise it.
* A test that needs GTA V is not a test: it is an owner check, written as above.

Never weaken an assertion or skip a test to get to green. If a test is wrong, fix the test in its own commit and say why.
