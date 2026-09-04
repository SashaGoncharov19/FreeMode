# Roadmap: from "it runs again" to a RAGE Multiplayer 0.3.7-class platform

This is the plan for the revived GTA Network. The bar is RAGE Multiplayer 0.3.7: a multiplayer mod that
people actually run servers on. That means smooth sync for dozens of players, a scripting API that is
complete enough to write a gamemode without patching the client, a server browser and auto-updates, custom
client content and voice chat. Everything here is ordered by what unblocks the next thing, not by how
impressive it sounds.

## Where we are (September 2026)

Verified by hand on GTA V Legacy 1.0.3889 under Proton (Debian 13): install, connect to a local server,
sync, chat, commands, vehicles, client-side JavaScript. The rest of the state:

| Area | Now | 0.3.7-class target |
| --- | --- | --- |
| Game builds | Legacy 1.0.3889 through a ScriptHookVDotNet fork with pattern fallbacks; two patterns still missing | Every current Legacy build within days of a game update; Enhanced edition evaluated |
| Sync | 2016-2018 code: 100 ms pure sync, 1.5 s light sync, ped/vehicle interpolation, ~64 players tested by the original team | Stable with 100+ players per server, no visible warping for vehicles and peds, weapons/aim/animations/ragdoll |
| Streaming | Client-side streamer thread, fixed ranges, server keeps 250 "near" players | Configurable per entity type, server-side interest management, dimensions used for load |
| Scripting (server) | C#/VB compiled with Roslyn at startup, `API` with ~700 members, events, commands, entities, colshapes, dimensions | Same plus a first-class JavaScript/TypeScript server runtime, typings, documented and versioned API |
| Scripting (client) | JavaScript on V8 12 (ClearScript 7.5), Chromium 151 browsers through CefSharp (multi-process, off-screen), `API` with ~600 members | Typings, hot reload, client packages with assets, GPU-shared browser textures |
| Master server / browser | Gone (`master.gtanet.work`), address configurable, empty by default | Own master server ([issue #1](https://github.com/SashaGoncharov19/FreeMode/issues/1)), browser in the launcher and in game |
| Updates | Linux installer updates from GitHub releases; Windows: NSIS installer, no updater | One updater for all platforms, delta downloads, channels (stable/beta) |
| Custom content | None | Client packages (scripts, CEF assets, sounds), custom DLC packs (vehicles, clothes, interiors) |
| Voice | None | Positional voice chat (Opus), server-controlled channels |
| Tooling | CI builds everything, server smoke test, bot integration test, slow-tick profiler | Same plus perf regression tests, crash reports, docs site, gamemode templates |

## Principles

1. **Measure, then change.** Every sync or performance change ships with a number (ms per tick, bytes per
   second per player, players per server) captured with the bot or the profiler. The slow-tick log and the
   headless bot exist for that.
2. **Test without the game.** The protocol, server and scripting API are covered by the bot and the
   integration test in CI. Only the hook and rendering need a real GTA V.
3. **Never crash the game on a game update.** Missing patterns disable a feature and are logged; the
   updater ships the fix.
4. **Linux is a first-class target** (server native, client through Proton); Windows must keep working.
5. **Small releases, changelog-driven.** Every release has a `CHANGELOG.md` section and can be reproduced
   from the tag.

## Phase 0 - close the alpha (now)

Goal: a `v0.1.0` release that a stranger can install and play on with friends.

* [x] Confirmed in game (alpha.24): client-side JS chain works (`API` members visible, probe in
      `Runtime.log`), no stutter, pattern fallbacks matched on 1.0.3889, 60 fps.
* [ ] Two real players on one server (not only bots): position, vehicles, chat, weapons.
* [ ] Windows path verified once (installer + classic launcher or the cross-platform launcher).
* [x] CEF browser UI verified in game (0.1.1): the `auth` login form appears, an account was registered through
      it. Took three fixes: resource files were never downloaded in HTTP file server mode, the overlay draw switch
      was never turned on, and the overlay released the game's swap chain (crash under DXVK).
* [ ] Missing patterns on 1.0.3889: "force offline" patch (decide whether it is still needed with
      `-scOfflineOnly`) and the euphoria functions (port the upstream NaturalMotion message implementation or
      drop the API).
* [x] Release process: merge to `master`, `build.yml` → `release_tag=v0.1.0`, changelog section becomes the
      release body.

## Next updates (after 0.1.1)

Decided in September 2026, in this order; each is its own branch and pull request, each ships as an alpha first.

1. **Dependency modernisation** (branch `claude/modernize-deps-4d8uyn`, done in code, in-game verification
   pending): CEF 3.2987 (Chromium 57, single-process) replaced by CefSharp.OffScreen 151 (Chromium 151, browser
   subprocess, GPU process optional), ClearScript 5.4.9 by 7.5 (V8 12). Findings and the remaining steps
   (shared-texture rendering, .NET 10 for server/launcher/bot): `docs/CEF-UPGRADE.md`.
   * **Client on modern .NET** (separate step, 1-3 weeks): the in-game client is .NET Framework 4.8 because
     ScriptHookVDotNet is a C++/CLI shell that hosts the desktop CLR. The route is recompiling that shell with
     `/clr:netcore` (.NET 8/10 + `ijwhost`), AssemblyLoadContext instead of the script AppDomain, and the .NET
     Desktop Runtime in the Proton prefix instead of .NET Framework (which also removes the most fragile install
     step on Linux). CefSharp and ClearScript both support .NET Core, so the browser and script work above is a
     prerequisite, not a throw-away.
2. **Debug mode**: one switch (settings.xml `<debug>`, launcher `--debug`, Debug builds default to on) that keeps
   all diagnostics in the code (API probe, download summaries, overlay frames, profiler lines) and turns them on
   or off per build instead of adding and removing log lines by hand.
3. **Linux GUI launcher**: a graphical shell over `GTANetwork.Launcher` (Avalonia, one binary for Linux and
   Windows): server list with favourites, settings, update/install progress, log viewer, "play" button.
4. **CEF connect and loading screen**: the server list, connect and loading flow (from the main menu until the
   server is joined) as a CEF page drawn by the overlay, styled like a modern launcher. NativeUI stays for the
   settings and the other in-game menus. Needs the CEF upgrade first.

## Phase 1 - platform: master server, updates, crash reports (weeks)

* Master server per issue #1: `/servers`, `/addserver` announce, `/verified`, `/stats`, welcome screen,
  update feed. ASP.NET Core minimal API, SQLite, Docker image, deployed with one command. The client and the
  launcher point at it through `MasterServerAddress`.
* Server browser: in the launcher (both platforms) and in the in-game menu; favourites and recent servers stay.
* One updater: the launcher checks the release feed on every start (channels: stable, beta), downloads only
  changed files (manifest with SHA256 per file), verifies signatures. The Linux `update.sh` becomes a thin
  wrapper.
* Crash reports: `Error.log` + SHVDN log + game build + mod version bundled into one zip, opt-in upload to
  the master server; the in-game message tells the player where the report is.
* Windows: replace the 2016 NSIS/WPF launchers with the cross-platform launcher (already runs on Windows).

## Phase 2 - sync and performance (months)

This is what separates a demo from a platform. Work items, each with a measurement:

* **Packet processing off the game thread.** Today `MessagePump` decodes and applies every packet in a
  script tick; move decoding and entity bookkeeping to a worker and apply only the game-thread part per
  frame. Metric: `MessagePump` never above 5 ms in the slow-tick log with 32 bots.
* **Interpolation/extrapolation rewrite** for peds and vehicles: fixed-rate snapshots (20 Hz pure sync),
  velocity-based extrapolation, dead-reckoning for vehicles, latency-aware buffers. Metric: no warping at
  150 ms RTT with packet loss 2 % (simulated with `tc netem` on the bot host).
* **Server-side interest management**: per-player streaming distance per entity type, dimension-aware,
  budgeted updates (nearest first). Metric: bytes/s per player flat when players go from 32 to 128.
* **Weapons, aim, animations, ragdoll**: aim vector and weapon state in pure sync, animation dictionary
  sync, ragdoll flag; euphoria through the upstream SHVDN implementation.
* **Vehicle details**: doors, tyres, damage, trailers, landing gear, sirens, mod kits and colours already
  exist as sync events; audit each for correctness on the current build and cover with bot tests.
* **Load tests in CI**: 32 bots against one server for five minutes, assert CPU per player and packet loss.

## Phase 3 - scripting API parity (in parallel with phase 2)

* **Freeze and document the API**: generate reference docs from the XML comments of `Server/API.cs` and
  `Client/Javascript/JavascriptHook.cs`; publish with the release (docs site from `docs/`).
* **TypeScript typings** for the client API (generated from `ScriptContext`), a `gtanetwork-types` package.
* **Server-side JavaScript**: run gamemodes written in JS/TS on the server. Options in order of effort:
  ClearScript V8 on .NET 8 (keeps one process, same API surface as C#), or a Node.js sidecar over IPC. Start
  with ClearScript because the C# `API` object can be exposed directly.
* **Resource system**: dependencies, shared scripts, config per resource, hot reload on the server (already
  compiled with Roslyn; add file watching), `client_packages`-style asset folders streamed to the client.
* **Modern runtimes**: done for the browser (CefSharp 151) and the script engine (ClearScript 7.5); next is the
  client itself on .NET 8/10 (see "Next updates").
* **Gamemode templates**: `dotnet new`-style templates for C# and TS gamemodes, the freeroam resource as the
  reference implementation with tests.

## Phase 4 - content and voice

* **Client packages**: scripts, CEF assets, sounds and fonts downloaded from the server (already there for
  scripts and maps; add hashing, caching and size limits).
* **Custom DLC packs**: vehicles, clothes, interiors (MLOs) through the game's DLC list. Requires writing
  `dlclist.xml` entries into the mods folder per server and restarting the game with the packs; design the
  UX (opt-in download, per-server cache) before touching the loader.
* **Voice chat**: Opus over the existing Lidgren connection or a separate UDP channel, positional audio
  through the game's audio API or NAudio, server-controlled channels and mute. Prototype with the bot first
  (send/receive frames) before the client UI.

## Phase 5 - beyond 0.3.7

* Server-side NPC/ped sync (traffic and population owned by the server).
* Anti-cheat basics: server-authoritative health/armour/weapons, sanity checks on speed and teleport,
  signed client builds.
* GTA V Enhanced: depends on a ScriptHookV for Enhanced and re-done memory patterns; track upstream SHVDN.
* Console-less server tooling: web admin panel talking to the master server and to servers.

## Risks

* **Game updates** break memory patterns and native hashes. Mitigation: pattern fallbacks, upstream SHVDN
  tracking, CI job that scans a dumped executable for the patterns.
* **ScriptHookV** is closed source and not redistributable; every player downloads it. Long term the loader
  could be replaced by an own ASI loader plus native invoker, which is a large reverse-engineering effort.
* **Proton/.NET Framework**: the in-game client is .NET Framework 4.8 inside wine. Moving the client to
  .NET 8 (in-process hosting from the C++/CLI shell) would remove the protontricks step entirely; it is a
  big refactor of the hook and every P/Invoke.

## How to contribute a step

Pick an item, open an issue with the metric you will report, keep the change small, add a bot test where the
protocol is involved, and add a changelog line under *Unreleased*. `eng/integration-test.sh` and
`dotnet build GTANetwork.sln` must stay green on Linux.
