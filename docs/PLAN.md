# Plan — the MVP of the revived GTA Network

The owner's MVP list (4 Sept 2026), turned into epics with measurable targets, the current state, the approach,
the decisions each depends on (`docs/DECISIONS.md`) and the tasks (`docs/tasks/`). `docs/HANDOFF.md` says where we
are today; this file says where we are going and in which order. It supersedes `docs/ROADMAP.md`.

## 1. What "MVP" means here

A server owner can run a GTA Network server that holds up to 1000 players, write the gamemode in TypeScript (server
and client) against published typings, ship custom content (`dlc.rpf` packs, CEF pages, sounds) to players, and
players install with one launcher (Linux and Windows), pick a server from a list, see a CEF loading screen until they
spawn, talk to each other in positional voice, and play with synchronisation that does not warp vehicles, peds,
projectiles, props, animations or trailers. Cheating is at least detectable server-side and pluggable. Everything runs
on current frameworks.

Targets that decide "done" (each has a task that measures it):

| Target | Number | Measured with |
| --- | --- | --- |
| Players per server | 1000 connected bots moving; server tick ≤ 16 ms (60 Hz kept), egress ≤ 30 KB/s per player, packet loss 0 on LAN | bot load harness (E-03) |
| Join time | connect → spawned ≤ 10 s on LAN with a 50 MB resource set already cached | client `Runtime.log` timestamps (E-12) |
| Browser UI | loader visible ≤ 1 s after "connect"; eval→frame latency ≤ 10 ms (harness) | `eng/cef-harness.sh` (done: 8.7 ms ring / 3.1 ms software) |
| Sync | no visible warp at 150 ms RTT + 2 % loss for vehicles and peds; projectiles/props/trailers consistent on all clients | bot + `tc netem`, owner runs with 2+ real players (E-11) |
| Voice | end-to-end ≤ 250 ms, 3D positional, 48 kHz Opus 24 kbit/s | bot voice test + owner (E-09) |
| Install | fresh Linux machine: installer → play ≤ 5 min excluding downloads; Windows the same with the same launcher | owner + one Windows tester (E-06) |

## 2. Versions — current and target

| Component | Now | Target | Notes |
| --- | --- | --- | --- |
| .NET SDK / runtime (server, launcher, bot, tools) | **10.0 LTS** (`global.json` 10.0.100, T-001 on 4 Sept 2026) | 10.0 LTS (supported to Nov 2028) | .NET 8 support ends 10 Nov 2026. |
| In-game client runtime | .NET Framework 4.8 in `GTA5.exe` via the SHVDN C++/CLI shell | stays for the MVP (D-04); .NET 10 in-process hosting later (E-13) | |
| C# compiler for server resources (Roslyn) | Microsoft.CodeAnalysis 5.9.0 (T-001) | 5.9.0 | |
| Browser | CefSharp.OffScreen 151.3.240 (Chromium 151) in `cef/GTANetwork.CefHost.exe` | keep current; bump per CefSharp release | `docs/CEF-UPGRADE.md`. |
| Client JS engine | ClearScript 7.5.1 (V8 12) | 7.5.1.1; also the **server** TS runtime (Q-01) | |
| Bun | none | 1.4.1 pinned in `runtime/.bun-version`, shipped with the server package | E-04 (D-09): the server gamemode runtime and the TS bundler; the client keeps V8. |
| Network | Lidgren fork (`libs/Lidgren.Network.dll` 2012.1.7) | keep until E-03 numbers say otherwise (Q-10); LiteNetLib 2.1.4 is the alternative | |
| Serialisation | protobuf-net 2.4.9, Newtonsoft.Json 13.0.4 | protobuf-net 3.4.21 evaluated in E-01 (wire-compatible; API changes), Newtonsoft stays on net48 | |
| DirectX overlay | SharpDX 4.0.1 (unmaintained since 2019), EasyHook 2.7.5870 | stays for the MVP; Vortice.Direct3D11 3.8.3 + MinHook with E-13 | Both work today; a rewrite only pays with the .NET 10 client. |
| Audio | NAudio 1.7.3 (`libs/`) | NAudio 3.0.1 + Concentus 2.2.2 (Opus in C#) | E-09. |
| Launcher GUI | none (CLI `GTANetwork.Launcher`) | Avalonia 12.1.2 | E-06 (Q-08). |
| Master list | none | ASP.NET Core minimal API on .NET 10, SQLite | E-07 (Q-07). |
| Game | GTA V Legacy 1.0.3889 via Proton Experimental (Wine 11.0, DXVK 3.0.2) | same; Enhanced edition out of scope | |

## 3. Epics

Each epic: goal, what exists, approach, decisions, tasks, risks. Tasks are files in `docs/tasks/`.

### E-01 Platform upgrade to .NET 10

**Goal**: server, launcher, bot, Map2Resource and the dev container on .NET 10; CI green; the owner's install gets a
.NET 10 launcher and server. **Exists**: everything on .NET 8; `Shared/` is `net48;netstandard2.0` and stays.
**Approach**: bump `global.json`, the four `net8.0` csproj files, `.devcontainer/Dockerfile` SDK image, `build.yml`
`setup-dotnet` version; Roslyn 5.9; run `eng/dev-test.sh`; publish the launcher. protobuf-net 3 is a separate
commit with the bot proving wire compatibility. **Tasks**: T-001. **Risks**: ClearScript native package for linux-x64
on .NET 10 (works: ClearScript 7.5 supports .NET 10), Lidgren fork compiled for net48/netstandard — fine.

### E-02 Agent framework and documentation (this branch)

**Goal**: any agent can pick a task and finish it without steering. **Exists**: `AGENTS.md`, `CLAUDE.md`, `docs/agents/*`,
`docs/tasks/*`, `docs/DECISIONS.md`, `docs/CODEMAP.md`, this plan, the code graph (`.mcp.json`, `.claude/`).
**Done when**: merged into the integration branch and one task has been executed by an agent that only read the docs.

### E-03 Scale: 1000 players per server

**Goal**: the targets in §1. **Exists**: one server tick thread at 60 Hz (`Server/GameServer.cs`), pure sync at 10 Hz
per player (`Shared/PacketOptimization.cs` packs 47–72-byte ped packets), the server relays to the ~250 nearest players
(`docs/SYNC.md`), client packet handling on a script tick (`Client/Main/Network/ProcessMessages.cs`, "MessagePump").
The original team tested ~64 players. No load test exists; the bot (`Tools/GTANetwork.Bot`) joins one connection.
**Arithmetic**: naive relay is O(N²): 1000 players × 999 × 10 Hz × 60 B = 600 MB/s. With interest management (each
player receives ≤ 128 entities, tiered 10/3/1 Hz by distance) and delta packets the budget is ≤ 30 KB/s per player,
240 Mbit/s at 1000. **Approach**: (1) a load harness: N bot processes with scripted movement, a metrics endpoint on the
server (tick ms p50/p99, packets/s, bytes/s per player, GC), a report script; (2) baseline numbers at 100/300/1000;
(3) server-side interest management (grid cells, per-entity-type ranges, dimension-aware, budgeted updates), (4) packet
processing off the client's game thread, (5) allocation-free packet paths, (6) only then consider the transport (Q-10).
**Tasks**: T-002 (harness + baseline), T-003 (interest management), later tasks from the numbers. **Risks**: the
Lidgren fork's single socket thread; GC pauses at 1000 connections (use `Server GC`, pooled buffers).
**Baseline (T-002, 5 Sept 2026, `docs/SYNC.md` §6)**: 100 players — tick 1.9 ms p50, 51 KB/s per player; 300 players —
everyone within near range, the 250-recipient cap active — 126 KB/s per player in plaintext with a 1.1 ms tick, but with the
encrypted sessions the per-recipient copy + AES-GCM makes the tick 66 ms and the loop 11 Hz (T-023, Q-14); 1000 players —
the server does not hold them: 973–1000 join, the relay (~510 k messages/s) saturates the tick (p50 66 ms, p99 0.8 s), a lagging tick reads an ever larger backlog until one tick takes 81 s and Lidgren times the connections out (969 → 4 players); there is no backpressure and no stale-packet drop today. The per-player egress budget (30 KB/s) is exceeded 4× at 300 before any scaling: T-003 must cut recipients and
rates, not bytes per packet. **After T-003** (`docs/SYNC.md` §6): 1000 players hold on the harness — tick p50 2.1 / p99 10.3 ms, 54 ticks/s, 15.1 KB/s per player (both §1 targets met); 300 players 32.5 KB/s per player (the 30 KB/s budget plus the never-dropped full tier). What remains for E-03: the tiers in game (owner check), entity create/update packets and unoccupied-vehicle sync (still broadcast), the transport (Q-10).

### E-04 TypeScript on both sides: Bun runtime on the server, V8 in the game, typings for both

**Goal**: a gamemode is written in TS against `@gtanetwork/server` (Bun) and `@gtanetwork/client` (V8 in game) with
generated typings; server code may use Bun's built-in APIs (`Bun.sql`, `Bun.redis`, `Bun.s3`, `fetch`, WebSocket)
without third-party modules; resources hot-reload on the server. **Exists**: server resources in C#/VB compiled by
Roslyn at start (`Server/Resources.cs`, `Server/API.cs`: 381 public members); client JS on ClearScript V8
(`Client/Javascript/JavascriptHook.cs`, `ScriptContext`: 403 members); resource `meta.xml` and file download
(`Shared/ResourceFiles.cs`). **Decision D-09**: the .NET server stays the *engine*; gamemode scripts run in a **Bun
process** started by the engine; the client keeps V8 in-process; Bun bundles TS for both.

**Approach**: (1) `Tools/GTANetwork.TypeGen` generates `types/client.d.ts` (reflection over `ScriptContext`) and the
server package `runtime/gtan/` (TS client library + `.d.ts`) from an API catalogue the engine exports (each `API`
function: name, parameters, return, whether it needs an answer). (2) **Bridge** (T-006 spike, then implementation):
engine ⇄ Bun over a Unix domain socket (Linux) or loopback TCP (Windows), length-prefixed MessagePack frames
(`MessagePack-CSharp` / `msgpackr`): `event` (engine → runtime: connections, chat, commands, client events, colshapes,
RPC requests), `call` (runtime → engine; carries an id only when a result is needed), `result`, `state` (engine →
runtime: entity create/delete and 10 Hz deltas of position, rotation, velocity, health, armour, vehicle/seat,
dimension for every player and vehicle, so `player.position` is a local read), `log`. Frames are batched and flushed
every millisecond or at 64 KB. (3) Runtime: `runtime/main.ts` loads every resource's `server/index.ts` as an ES module
in one Bun process (trusted code, as C# resources are today), routes events to handlers, watches files for hot reload,
restarts on crash (the engine keeps players connected; handlers see `onRuntimeRestarted`). (4) Client TS: bundled by the
engine at resource start with `bun build` and delivered as today's JS (T-005, done 5 Sept 2026: `Server/Managers/TypeScriptBundler.cs`,
IIFE bundle, hash cache, optional `tsc`). (5) `gtanetwork create` and freeroam in TypeScript (T-007, done 5 Sept 2026:
`Tools/GTANetwork.Cli`, `templates/resource/`, `gtan.enums` + `parseEnum` in the runtime library). (5) A `gtanetwork create` template and
`freeroam` ported (T-007). **Bun** is pinned in `runtime/.bun-version` (1.4.1 at the time of writing) and shipped with the
server package for Linux and Windows (Bun is MIT-licensed, ~100 MB). **Numbers the spike had to reach** (T-006): one-way
`call` ≤ 5 µs amortised, round trip p50 ≤ 60 µs / p99 ≤ 300 µs on the owner's machine, ≥ 200 000 one-way calls/s,
state mirror at 1000 players × 10 Hz ≤ 3 % of one core on each side. **Measured on 5 Sept 2026** (`eng/bench-bridge.sh`,
dev container, Bun 1.4.1, .NET 10.0.11): 2.0 M one-way calls/s at 0.50 µs; round trip p50 6 µs / p99 13 µs over a Unix
socket (8 / 18 µs over loopback TCP); mirror of 1000 players at 10 Hz: engine 1.8 %, Bun 3.4–3.6 % of a core (the one
number above target, by 0.4 points; stage 2 sends deltas). The full table is in the T-006 task file. Verdict: the bridge
is fast enough; stage 2 goes ahead. ClearScript in-process stays the fallback only if the runtime turns out unreliable.
**Stage 2 (5 Sept)**: the runtime is in — `Server/Runtime/*`, `runtime/*`, `Server/resources/tsdemo`; `eng/integration-test.sh` drives a
TypeScript resource (connect greeting, `/tsping`, a cancelable chat handler) through the bot. **Tasks**: T-004, T-005, T-006, T-007. **Risks**: two
processes to supervise (the engine already does this for the browser host — same watchdog pattern); ordering between
`state` and `event` frames (one connection, one order); operators need Bun (shipped).

### E-05 RPC and protocol security (server ⇄ client ⇄ CEF)

**Goal**: one typed RPC layer: `rpc.call(name, args): Promise<T>` from a client script to the server and back, from a
CEF page to the client script and to the server (through the client), with request ids, timeouts, per-name permissions
set by the server, argument validation from the TS types, rate limits, and an encrypted, authenticated session.
**Exists**: fire-and-forget events (`API.triggerClientEvent`, `onServerEventTrigger`), the one-way CEF bridge
(`resourceCall`/`resourceEval`, `Shared/Cef/CefHostProtocol.cs` JsMessage), Lidgren's optional XTEA encryption (not
used), no session token. **Approach**: (1) a `Shared/Rpc/` message set (request/response/error with id, name, payload as
protobuf or MessagePack bytes) on a reliable-ordered channel; (2) server registry `API.registerRpc(name, handler, {permission})`,
client `rpc.register`; CEF gets `gtan.rpc.call` implemented over `CefSharp.PostMessage` + a reply `eval` path;
(3) handshake: ECDH (X25519) → AES-GCM for the session, server certificate pinned via the master list or a server
key in the connect string; (4) validation: the generated types produce runtime validators for RPC payloads (TS side)
and the server checks sizes/rates. **Done (T-008, 5 Sept 2026)**: (1) and (2) — `Shared/Rpc/` messages (one JSON value per
payload, D-13) on the reliable ordered channel `Rpc`; `Server/Managers/RpcDispatcher.cs` (global names, allow check, 30
requests/s per player, 64 KB, 10 s default timeout, C# handlers on the resource thread, TypeScript handlers through the bridge);
`API.rpc` on the client with promises in a JavaScript helper so continuations stay on the script thread; `gtan.rpc.call` in CEF
pages; `auth` and `freeroam` use it; bot round trips in `eng/integration-test.sh`. **Left for T-009**: (3) the session
handshake and (4) generated validators — crypto library on net48 (BouncyCastle 2.x works on both; .NET `AesGcm` is not in .NET
Framework) is T-009's first decision. **Risks**: latency budget — one RPC must be one round trip; the client-side dispatch
must not block the script thread.

### E-06 Launcher with a GUI (Linux and Windows) and an updater

**Goal**: one Avalonia application over the existing launcher logic: install/update (delta by SHA256 manifest, channels
stable/beta), settings, server list (from E-07) with favourites and direct connect, log viewer, Play. Packaging: AppImage
+ .deb for Linux, MSI or portable zip for Windows. **Exists**: `Launcher/` CLI (`Program.cs`, `Deployment.cs`,
`GamePatcher.cs`, `Steam.cs`, `Paths.cs`, `HitchMonitor.cs`), `eng/setup-linux.sh` installer, `update.sh`, NSIS installer
for Windows (`Setup/`). **Approach**: move the CLI logic into a `GTANetwork.Launcher.Core` library, add
`GTANetwork.Launcher.Gui` (Avalonia 12), keep the CLI as a thin front end; the updater reads a `manifest.json` produced
by the release job (`eng/package-client.ps1`). **Decisions**: Q-08, Q-11. **Tasks**: T-009 (skeleton + Play + settings),
then updater and server list tasks. **Risks**: Avalonia under Wayland/X11 on the owner's Debian; single-file publish size.

### E-07 Master list and server browser

**Goal**: servers announce themselves; the launcher and the in-game menu list them (name, players, gamemode, map,
ping, version, verified flag). **Exists**: `MasterServerAddress` setting (empty; the 2016 master is gone),
`Shared/ServerSettings.cs` announce fields, the old announce code path in the server (inventory in `docs/CODEMAP.md`).
**Approach**: `Tools/GTANetwork.Master` (ASP.NET Core minimal API, .NET 10, SQLite): `POST /servers/announce`
(heartbeat every 60 s, token per server), `GET /servers` (JSON, filters), `GET /verified`, `GET /stats`,
`GET /update-feed`; Docker image; deployed by the owner. Server: announce job; client/launcher: list + favourites +
recent (exist). **Decisions**: Q-07 (hosting, domain). **Tasks**: T-010. **Risks**: abuse (fake servers) — the master
pings the announced port before listing.

### E-08 Custom `dlc.rpf` packs

**Goal**: a server declares DLC packs (vehicles, clothes, MLOs); players get them from the launcher before the game
starts **or** in game when connecting, and packs of the next server can be fetched while playing; a server refuses
players missing a required pack. **Exists**: nothing. **Decision D-10**: download anywhere, apply at game start; when the
mounted set differs, the client offers "restart with packs" and the launcher relaunches with the new overlay and
auto-joins; runtime mounting is a later spike. **Approach**: server manifest (`dlcpacks` in `settings.xml`: name,
URL, SHA256, size; also served as `GET /dlcpacks.json` and announced to the master list); launcher fetches into
`~/GTANetwork/dlcpacks/<name>/`, applies an overlay and a `dlclist.xml` with the packs, starts the game, restores after
exit; in game the CEF loader (E-12) downloads missing packs into the same folder and, if a restart is needed, hands the
launcher (which waits on `GTA5.exe`) a "relaunch with packs X, auto-join server Y" request through the existing
`gtan://` auto-join channel (`Shared/GTANSchemeListener.cs`). The overlay mechanism (own `fiDevice` redirect in the
SHVDN shell vs. an existing ASI) is the design question inside T-014. **Tasks**: T-014 (manifest, launcher download,
overlay), T-022 (in-game download + restart-to-apply), T-021 (runtime mounting spike, draft). **Risks**: Rockstar
launcher/Steam integrity checks with an overlay; pack sizes; a restart per server switch until T-021.

### E-09 Voice chat

**Goal**: positional voice, push-to-talk, server-controlled channels and mute, ≤ 250 ms end to end. **Exists**: nothing;
NAudio 1.7.3 in `libs/`. **Approach (Q-05 default)**: client captures 48 kHz mono (NAudio WASAPI under Wine — verify;
fallback WaveIn), encodes with Concentus (Opus, 20 ms frames, 24 kbit/s), sends on a Lidgren unreliable channel; the
server relays to players within voice range (interest management from E-03); receivers decode and play through NAudio
with per-speaker 3D attenuation from the synced positions. Bot first: two bots exchange voice frames through the server
and the test asserts arrival and jitter. **Tasks**: T-012 (protocol + bot test), T-013 (client capture/playback + UI).
**Risks**: audio capture under Wine/Proton (PulseAudio/PipeWire through winepulse) — measured in T-013 before the UI.

### E-10 Anti-cheat baseline

**Goal**: the server validates what clients claim and exposes hooks; the client reports integrity at connect.
**Exists**: server-side handlers trust client packets (`Server/ProcessMessages.cs`). **Approach (Q-06 default)**:
(1) validators in the server's packet handlers: speed/teleport (distance per tick vs. vehicle max), health/armour caps,
weapon/ammo ownership, model whitelist, event rate limits; (2) `API.onCheatDetected(player, kind, evidence)` for
gamemodes; (3) client integrity report: SHA256 of `bin/scripts/*.dll`, `cef/GTANetwork.CefHost.exe`, resource files,
sent at connect and compared against the release manifest; (4) signed client builds (the release job signs the manifest).
**Tasks**: T-014. **Risks**: false positives on lag — thresholds are measured with the bot under `tc netem`.

### E-11 Synchronisation quality

**Goal**: the sync targets in §1 for players, vehicles, projectiles, props (objects with physics), animations (native and
custom `.ycd` from DLC packs), trailers and attachments. **Exists**: `docs/SYNC.md` (the review of the pipeline, open items
listed there), `Client/Sync/*`, `Client/Streamer/*`, `Server/ProcessMessages.cs`, sync events for doors/tyres/trailers.
**Approach**: (1) instrumentation first: a client sync debug overlay (per-entity position error vs. last packet, packet
age, rate) and a bot that replays a recorded route so two clients can be compared; (2) fixed-rate snapshots with
interpolation buffers and velocity extrapolation for peds and vehicles; (3) projectile sync (fire event with origin,
direction, speed; owner authority for impact); (4) object ownership and physics sync for props; (5) animation dictionary
sync incl. custom dictionaries; (6) trailer/attachment sync audited against the current game build. Each item ships
with a before/after measurement from (1). **Tasks**: T-015 (instrumentation), then one task per item from the numbers.
**Risks**: everything here is verified in game only; the bot cannot render.

### E-12 CEF UI: loader, main menu, 3D browsers

**Goal**: from "connect" until spawn the player sees a CEF loading screen with progress; the server list, direct
connect and settings are CEF pages instead of NativeUI; browsers can be placed in the world. **Exists**: the browser
host starts on connect (`Client/Main/Network/ProcessMessages.cs`, `InitiatedConnect`), 8 ms frame latency (ring),
NativeUI menus in `Client/Main.cs`, design notes for 3D in `docs/CEF-UPGRADE.md`. **Approach**: (1) `ui/` pages shipped
with the client (`images/` today), a `loader.html` shown by the client from connect to spawn with events (download %,
handshake state); (2) `menu.html` (server list from E-07, favourites, settings bound to `PlayerSettings`), the host started
at game start (`CefPreload` default true when the menu is CEF); (3) 3D: a quad in the D3D11 hook with the game camera
matrix and depth test (see `docs/CEF-UPGRADE.md`, "3D browsers"). **Decisions**: Q-11. **Tasks**: T-016 (loader),
T-017 (menu), T-018 (3D). **Risks**: `CefPreload` costs ~0.9 GB from game start — mitigated by `CefIdleExitSeconds`.

### E-13 Client on modern .NET (after the MVP)

**Goal**: the in-game client on .NET 10 (in-process hosting from the C++/CLI shell), removing .NET Framework from the
Proton prefix. **Exists**: the plan in `docs/ROADMAP.md`'s "Client on modern .NET" note (`/clr:netcore`, `ijwhost`,
AssemblyLoadContext). Not before the MVP (D-04); it unblocks Vortice/MinHook and modern C#.

## 4. Order (D-12: platform first, then UI; scale M2; gameplay M3)

| Milestone | Epics / tasks | Exit criterion |
| --- | --- | --- |
| M0 (now) | E-02 merged; the owner's in-game checks of the texture ring, hitch tooling, idle exit (T-000) | one task executed by an agent from the docs alone, delivered as a PR |
| M1 platform + UI | E-01 T-001; E-04 T-004, T-006 (spike first, then the bridge), T-005, T-007; E-05 T-008, T-009; E-12 T-012, T-013; E-06 T-010; E-07 T-011 | .NET 10 everywhere; a TS gamemode runs in Bun against the engine within the spike's numbers; RPC used by `auth`; the loader shows on connect; the CEF menu lists servers from the master; the GUI launcher plays |
| M2 scale + content | E-03 T-002 (baseline), T-003; E-08 T-014, T-022; E-06 updater + server list in the GUI | 1000 bots within the tick and bandwidth budget; a DLC vehicle spawns after a launcher- or in-game download |
| M3 gameplay | E-11 T-018 then sync tasks; E-09 T-015, T-016; E-10 T-017 | sync targets met with 2+ real players; voice works; cheat hooks fire |
| M4 | remaining E-03 numbers with the real packet mix; E-12 T-019 3D browsers; E-08 T-021 spike; E-13 starts | MVP release |

Inside a milestone the order is by dependency; tasks without dependencies run in parallel by different agents when
their Files do not overlap (`docs/agents/workflow.md`). Every task ends in a pull request (D-11).

## 5. What is deliberately not in the MVP

GTA V Enhanced, a kernel-level anti-cheat, server-side NPC population sync, a web admin panel, macOS. They are listed so
that nobody drifts into them.
