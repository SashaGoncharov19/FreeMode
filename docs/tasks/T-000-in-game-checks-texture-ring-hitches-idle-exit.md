# T-000 — In-game checks: texture ring, hitch diagnostics, idle exit of the browser host

Status: needs owner
Epic: E-02 Agent framework (closing the browser work of 4 Sept)
Size: S
Branch: claude/modernize-deps-4d8uyn (code already committed: dd2857e, dd263e9)
Depends on: none
PR: no

## Goal

The owner confirms in game that (1) typing in the CEF login form reacts at once with the shared-texture ring,
(2) hitch lines and the system monitor appear and can be read together, (3) the browser host stops a minute after
the last browser closes and comes back for the next page.

## Why

Three changes shipped to `~/GTANetwork` on 4 Sept without an in-game run: the texture-ring fix for laggy inputs,
the `[HITCH]` diagnostics + `hitch-monitor.log`, and `<CefIdleExitSeconds>` (default 60).

## Scope

* In: the owner's run and the log reading below; fixing what the run shows.
* Out: new features.

## Files

* Read: `Client/GUI/CEFManager.cs` (idle exit :617–:648, restart on host death), `Client/GUI/DirectXHook/Hook/DXHookD3D11.cs` (`RecordPresentCost`), `Launcher/HitchMonitor.cs`, `Subprocess/GTANetwork.CefHost/TextureRelay.cs`.

## Acceptance criteria

- [ ] `CEF.log` has `Texture ring 420x480: 0x…` and no `shared textures unavailable`; typing has no visible delay.
- [ ] `CEF.log` has `No browser for 60 s: stopping the browser host` about a minute after login, and a later page
      (e.g. rejoin, or a resource that opens a browser) logs `Starting the browser host` again and the page shows.
- [ ] `Runtime.log` `[HITCH]` lines (if any) can be matched with `hitch-monitor.log` lines; the verdict per hitch is written
      into this task's Log (machine / GPU / game / ours).

## Test plan (owner)

1. `~/GTANetwork/play.sh --debug`; join the local server; log in through the form; play 5 minutes; rejoin once.
2. ```bash
   grep -n "Texture ring\|unavailable\|No browser for\|Starting the browser host\|creating again" ~/GTANetwork/logs/CEF.log | tail -20
   grep -n "HITCH" ~/GTANetwork/logs/Runtime.log | tail -20
   grep -n "held the game thread" ~/GTANetwork/logs/ScriptHookVDotNet-$(date +%F).log | tail -20
   tail -n 40 ~/GTANetwork/logs/hitch-monitor.log
   ```
3. For every `[HITCH] HH:MM:SS.fff` line, read the monitor line of that second: `swap in` > 0 or `mem stall` > 0 → the
   machine; GPU MHz drop → thermals; nothing moved and no SHVDN line at that second → the game.

## Log

* 2026-09-04 22:10 agent — created; code synced to the install, launcher republished.

## Result

(pending the owner's run)
