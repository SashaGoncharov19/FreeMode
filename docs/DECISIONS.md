# Decisions

Decided things and open questions, in one place. A task may only be `ready` when every decision it depends on
is in the "Decided" table. An agent that meets an undecided question while working adds it to "Open" with a
recommended default, takes the default, and writes a Log line in the task (`docs/agents/workflow.md`).

Format of an entry: what was decided, why, what was rejected, date. Keep entries short; the reasoning that needs
more than a paragraph lives in the area document and is linked.

## Decided

| # | Decision | Why / rejected | Date |
| --- | --- | --- | --- |
| D-01 | The browser runs in a separate process (`cef/GTANetwork.CefHost.exe`, CefSharp.OffScreen 151), never inside `GTA5.exe`. | CefSharp is C++/CLI and only works in the default AppDomain; SHVDN runs the client in a second one (`docs/CEF-UPGRADE.md`). Rejected: CefGlue in-process (old, Chromium 57), CefSharp in-process (crashes). | 2026-09-04 |
| D-02 | Frames from the browser reach the game as shared-memory dirty rectangles (software) or through a host-owned ring of 4 D3D11 shared textures (GPU). Chromium's own paint handles are never cached. | CEF's `OnAcceleratedPaint` handle is valid only inside the callback (`docs/CEF-UPGRADE.md`, item 3). | 2026-09-04 |
| D-03 | Every PE file shipped in `cef/` is page-aligned (`eng/pe-realign.py`) at build, sync, packaging and install. | Wine copies 512-byte-aligned images into every process; Chromium dropped from 1.5 GB to 0.86 GB PSS. | 2026-09-04 |
| D-04 | The in-game client stays on .NET Framework 4.8 for the MVP; moving it to modern .NET is its own epic after the MVP (E-11). | SHVDN's C++/CLI shell hosts the desktop CLR; the port is 1–3 weeks of hook and P/Invoke work with in-game-only verification. Rejected for MVP: doing it first (blocks every other epic). | 2026-09-04 |
| D-05 | Documentation, code and commits are in English; chat with the owner is in Ukrainian. | Existing docs and code are English; the owner writes Ukrainian. | 2026-09-04 |
| D-06 | Work happens on task branches from the integration branch named in `docs/HANDOFF.md`; `master` receives merges the owner makes. No PRs, no releases, no tags by agents unless asked. | GitHub rules of the repository (403 on tags), owner's preference. | 2026-09-04 |
| D-07 | The browser host stops after 60 s without a browser (`<CefIdleExitSeconds>`, 0 = never) and is restarted on demand; a dead host is replaced up to 3 times per session. | Chromium's ~0.9 GB on a 15 GB machine that swaps during play. | 2026-09-04 |
| D-08 | Diagnostics stay in the code behind one switch (`GTAN_DEBUG=1` / `<DebugMode>` / `--debug`), with `[PROFILE]` and `[HITCH]` markers; nothing is added and removed by hand for a session. | `docs/HANDOFF.md`, debug mode. | 2026-09-04 |

## Open (owner's call; the default is what agents use until decided)

| # | Question | Options | Recommended default | Affects |
| --- | --- | --- | --- | --- |
| Q-01 | Runtime for TypeScript resources on the **server**. | (a) In-process V8 via ClearScript 7.5 on .NET (same engine as the client; the C# `API` object exposed directly; per-call cost is a method call). (b) Bun as a sidecar process; the server exposes its API over a local socket; resources are TS with the npm ecosystem; every API call is an IPC round trip. (c) Both: (a) for gameplay scripts, Bun only as build tooling (`bun build`) and for optional out-of-process services. | (c): in-process V8 for gameplay, Bun for tooling. Reason: gameplay scripts make thousands of API calls per tick; IPC per call is 20–50 µs under load, in-process is ~0.1 µs. | E-04, E-05 |
| Q-02 | Runtime for TypeScript on the **client** (inside `GTA5.exe`). | (a) Keep ClearScript V8 in-process, TS compiled to JS by the server/build (`bun build`/esbuild), typings published. (b) A Bun/Node sidecar like the browser host, talking to the game over RPC. | (a). Natives must be called in-process; a sidecar cannot call `GET_ENTITY_COORDS` 500 times per frame over a pipe. | E-04, E-05 |
| Q-03 | Keep C#/VB server resources compiled with Roslyn when TS arrives? | (a) Keep both indefinitely. (b) TS-first; C# stays supported but new API docs/templates target TS. (c) Drop C# resources. | (b). | E-04, E-05 |
| Q-04 | Custom `dlc.rpf` delivery. | (a) Install-time: the launcher downloads the server's DLC packs (manifest with hashes), places them in a `mods/`-style overlay and edits `dlclist.xml`, game restarts with the packs; per-server cache. (b) Runtime mounting through RAGE filesystem hooks (what FiveM does): no restart, but weeks of reverse engineering on build 1.0.3889 and re-work on every game update. | (a) for the MVP; (b) as a later epic if (a) is too limiting. Needs an answer on how the overlay is applied without OpenIV.asi (own `fiDevice` redirect or accept an ASI dependency). | E-08 |
| Q-05 | Voice chat transport. | (a) In-band: Opus (Concentus, pure C#) frames over the existing Lidgren connection on an unreliable channel, server relays to nearby players, 3D positional playback via NAudio. (b) External voice server (Mumble/TeamSpeak-style) controlled by the game server. | (a). One connection, no extra server, positional data already flows; Concentus avoids native Opus under Wine. | E-09 |
| Q-06 | Anti-cheat depth for the MVP. | (a) Server-side validation only (speed/teleport/health/armour/ammo/model sanity, rate limits, server-authoritative events) plus signed client builds and a client-integrity report at connect. (b) (a) plus in-process module scanning and screenshots on demand. | (a); (b) designed as hooks a server can enable (`API.onCheatDetected`) but implemented later. | E-10 |
| Q-07 | Master list hosting. | (a) The owner hosts an ASP.NET Core minimal API (+ SQLite) on a VPS/domain; the launcher and the in-game browser query it; servers announce with a heartbeat. (b) Static JSON in a GitHub repository updated by a bot. | (a). Needs a domain and a host from the owner (`MasterServerAddress` default). | E-07 |
| Q-08 | Launcher GUI framework. | (a) Avalonia UI 11 (one .NET binary for Linux and Windows, MIT). (b) Web UI (CEF/Electron-like) around the CLI launcher. | (a). The CLI launcher already has the logic; Avalonia adds the shell without a browser runtime. | E-06 |
| Q-09 | .NET version for server, launcher, bot, tools. | (a) .NET 10 LTS (supported to Nov 2028). (b) Stay on .NET 8 LTS (supported to Nov 2026). | (a), as the first task of E-01: the .NET 8 support ends in November 2026. | E-01 |
| Q-10 | Network library for the 1000-player target. | (a) Keep the Lidgren fork, fix hot paths, add interest management and batching. (b) Replace with LiteNetLib (maintained, .NET Standard, similar reliability channels). | (a) until the load test (E-03) shows the library itself, not our code, is the limit. | E-03 |
| Q-11 | Server-selection UI: where does the player pick a server? | (a) Launcher GUI (before the game starts) **and** the in-game CEF main menu. (b) In-game CEF only. (c) Launcher only. | (a). The in-game menu is needed for switching servers without restarting the game; the launcher for one-click play. | E-06, E-07 |
| Q-12 | PR per task, or commits straight to the integration branch? | (a) Commit to a task branch, the owner merges. (b) Agents open a PR per task for the owner to review. | (a) unless the owner switches to (b). | all |
| Q-13 | The classic three-stage Windows launcher (`Subprocess/GTANSubprocess`, `PlayGTANetwork`, `PlayGTANetworkUpdater`; updates from the dead master). | (a) Delete once the Avalonia launcher (T-010) runs on Windows and the NSIS installer points at it. (b) Keep building it indefinitely. | (a), after T-010 is verified on Windows by a tester. | E-06, T-020 |
