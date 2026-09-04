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
| .NET SDK / runtime (server, launcher, bot, tools) | 8.0 (`global.json` 8.0.100, container SDK 8.0.424) | **10.0 LTS** (supported to Nov 2028) | .NET 8 support ends 10 Nov 2026. E-01. |
| In-game client runtime | .NET Framework 4.8 in `GTA5.exe` via the SHVDN C++/CLI shell | stays for the MVP (D-04); .NET 10 in-process hosting later (E-13) | |
| C# compiler for server resources (Roslyn) | Microsoft.CodeAnalysis 4.14.0 | 5.9.0 | With E-01. |
| Browser | CefSharp.OffScreen 151.3.240 (Chromium 151) in `cef/GTANetwork.CefHost.exe` | keep current; bump per CefSharp release | `docs/CEF-UPGRADE.md`. |
| Client JS engine | ClearScript 7.5.1 (V8 12) | 7.5.1.1; also the **server** TS runtime (Q-01) | |
| TypeScript toolchain | none | Bun (latest stable) for `bun build`/bundling, `typescript` for `.d.ts` checks | E-04. Bun is tooling, not the gameplay runtime (Q-01/Q-02). |
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

### E-04 TypeScript on both sides, with typings

**Goal**: a gamemode is written in TS for the server and the client against `@gtanetwork/types` (generated from the C#
API), built with Bun, hot-reloaded on the server. **Exists**: server resources in C#/VB compiled by Roslyn at start
(`Server/Resources.cs`, `Server/API.cs`: 381 public members); client JS resources on ClearScript V8
(`Client/Javascript/JavascriptHook.cs`, `ScriptContext`: 403 public members); resource `meta.xml` and file download
(`Shared/ResourceFiles.cs`). **Approach**: (1) `Tools/GTANetwork.TypeGen`: reflection over `Server/API.cs` and
`ScriptContext` + the shared entity/enum types → `types/server.d.ts`, `types/client.d.ts`, `types/cef.d.ts`, published
with each release and checked in CI (`tsc --noEmit` against the freeroam resource); (2) client: TS files in a resource
are bundled by the server at resource load (`bun build --target=browser`-like ES2020 output for V8 12) and served as
today's JS; (3) server: ClearScript V8 hosted in the server process (`Microsoft.ClearScript.V8` +
`Microsoft.ClearScript.V8.Native.linux-x64`), the `API` object exposed as `API`, events routed like C# handlers, file
watcher for hot reload; (4) a `gtanetwork create <name>` template (server + client + CEF page in TS). **Decisions**:
Q-01, Q-02, Q-03. **Tasks**: T-004 (typings), T-005 (client TS bundling), T-006 (server TS runtime), T-007 template.
**Risks**: the C# API has overloads and `dynamic`/`object` parameters that do not map to TS cleanly — the generator
emits unions and marks the rest `unknown`; Bun on the *server* host is a build-time dependency that the Linux and
Windows installers must ship or download.

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
and the server checks sizes/rates. **Decisions**: crypto library on net48 (BouncyCastle 2.x works on both; .NET
`AesGcm` is not in .NET Framework) — decided in T-008. **Tasks**: T-008. **Risks**: latency budget — one RPC must be one
round trip; the client-side dispatch must not block the script thread.

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

**Goal**: a server declares DLC packs (vehicles, clothes, MLOs); the launcher downloads and installs them before the
game starts; the client refuses to join if a required pack is missing. **Exists**: nothing. **Approach (Q-04 default)**:
server manifest (`dlcpacks` in `settings.xml`: name, URL, SHA256, size); launcher fetches into
`~/GTANetwork/dlcpacks/<server>/`, builds a `mods`-style overlay and a `dlclist.xml` with the packs, starts the game
with the overlay active, restores after. The overlay mechanism (own `fiDevice` redirect ASI vs. an existing ASI) is
decided in the design task. **Tasks**: T-011 (design + manifest + launcher download), then the loader task. **Risks**:
Rockstar launcher integrity checks; per-server restarts; pack size.

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

## 4. Order

| Milestone | Epics / tasks | Exit criterion |
| --- | --- | --- |
| M0 (now) | E-02 merged; the owner's in-game checks of the texture ring, hitch tooling, idle exit (`docs/tasks/T-000`) | one task executed by an agent from the docs alone |
| M1 foundation | E-01 (T-001); E-03 T-002 baseline numbers; E-04 T-004 typings; E-12 T-016 loader; E-07 T-010 master list; E-06 T-009 launcher skeleton | .NET 10 everywhere; numbers for 100/300/1000 bots; `types/*.d.ts` published; loader visible on connect |
| M2 platform | E-04 T-005/T-006/T-007; E-05 T-008; E-12 T-017 menu; E-06 updater + server list; E-03 T-003 interest management | a TS gamemode runs on both sides; RPC used by the freeroam resource; NativeUI server browser gone; 1000 bots within the tick budget |
| M3 gameplay | E-11 T-015 then sync tasks; E-09 T-012/T-013; E-08 T-011 + loader; E-10 T-014 | sync targets met with 2+ real players; voice works; a DLC vehicle spawns; cheat hooks fire |
| M4 | remaining E-03 numbers at 1000 with real packet mix; E-12 T-018 3D browsers; E-13 starts | MVP release |

The order inside a milestone is by dependency; tasks without dependencies run in parallel by different agents when their
Files do not overlap (`docs/agents/workflow.md`).

## 5. What is deliberately not in the MVP

GTA V Enhanced, a kernel-level anti-cheat, server-side NPC population sync, a web admin panel, macOS. They are listed so
that nobody drifts into them.
