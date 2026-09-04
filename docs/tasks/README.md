# Tasks

One file per task, `T-NNN-<slug>.md`, numbered in the order they were written (not priority). Status and
dependencies are in the header of each file; the procedure for taking a task is `docs/agents/workflow.md`.
The plan that groups tasks into epics is `docs/PLAN.md`; decisions they depend on are in `docs/DECISIONS.md`.

## Index

The index is generated from the headers; regenerate it with the command below and paste the table here.

```bash
for f in docs/tasks/T-*.md; do
  printf '| %s | %s | %s | %s | %s |\n' "$(basename "$f" .md)" "$(sed -n '1s/^# T-[0-9]* — //p' "$f")" \
    "$(grep -m1 '^Status:' "$f" | sed 's/Status: *//')" "$(grep -m1 '^Epic:' "$f" | sed 's/Epic: *//')" "$(grep -m1 '^Depends on:' "$f" | sed 's/Depends on: *//')"
done
```

| Task | Title | Status | Epic | Depends on |
| --- | --- | --- | --- | --- |
| [T-000-in-game-checks-texture-ring-hitches-idle-exit](T-000-in-game-checks-texture-ring-hitches-idle-exit.md) | In-game checks: texture ring, hitch diagnostics, idle exit of the browser host | needs owner | E-02 Agent framework (closing the browser work of 4 Sept) | none |
| [T-001-dotnet-10-for-server-launcher-bot-tools](T-001-dotnet-10-for-server-launcher-bot-tools.md) | .NET 10 for server, launcher, bot, Map2Resource and the dev container | in progress | E-01 Platform upgrade | none |
| [T-002-bot-load-harness-and-baseline](T-002-bot-load-harness-and-baseline.md) | Bot load harness: N simulated players, server metrics, baseline numbers at 100/300/1000 | ready | E-03 Scale | none (T-001 preferred first) |
| [T-003-server-interest-management](T-003-server-interest-management.md) | Server-side interest management: grid cells, per-type ranges, tiered rates, per-player budget | ready | E-03 Scale | T-002 |
| [T-004-typescript-typings-generator](T-004-typescript-typings-generator.md) | TypeScript typings generated from the C# APIs (server, client, CEF) | ready | E-04 TypeScript | none (T-001 preferred first) |
| [T-005-client-typescript-resources](T-005-client-typescript-resources.md) | Client resources in TypeScript: `lang="typescript"` bundled by the server with Bun | ready | E-04 TypeScript | T-004 |
| [T-006-server-runtime-on-bun-bridge](T-006-server-runtime-on-bun-bridge.md) | Server gamemode runtime on Bun: bridge spike with numbers, then protocol, state mirror, resource loader, hot reload | in progress | E-04 TypeScript | T-001, T-004 |
| [T-007-gamemode-template-and-freeroam-in-typescript](T-007-gamemode-template-and-freeroam-in-typescript.md) | Gamemode template (`gtanetwork create`) and freeroam fully in TypeScript | ready | E-04 TypeScript | T-005, T-006 |
| [T-008-typed-rpc-server-client-cef](T-008-typed-rpc-server-client-cef.md) | Typed RPC: server ⇄ client ⇄ CEF with request ids, timeouts, permissions, rate limits | ready | E-05 RPC and protocol security | T-004 |
| [T-009-session-encryption-and-authentication](T-009-session-encryption-and-authentication.md) | Encrypted, authenticated session between client and server | ready | E-05 RPC and protocol security | T-008 |
| [T-010-launcher-gui-avalonia-skeleton](T-010-launcher-gui-avalonia-skeleton.md) | Launcher GUI (Avalonia 12): Play, settings, log viewer; the CLI becomes a thin front end | ready | E-06 Launcher | T-001 |
| [T-011-master-list-service-and-server-browser](T-011-master-list-service-and-server-browser.md) | Master list service, server announce, server list in the client menu | ready | E-07 Master list | T-001 |
| [T-012-cef-loader-from-connect-to-spawn](T-012-cef-loader-from-connect-to-spawn.md) | CEF loading screen from "connect" until spawn | needs owner | E-12 CEF UI | none |
| [T-013-cef-main-menu-server-browser](T-013-cef-main-menu-server-browser.md) | In-game CEF main menu: server list, favourites, direct connect, settings (replaces the NativeUI server browser) | ready | E-12 CEF UI | T-011, T-012 |
| [T-014-dlc-packs-manifest-and-launcher-install](T-014-dlc-packs-manifest-and-launcher-install.md) | Custom DLC packs: server manifest, launcher download and install-time overlay (design + first implementation) | ready | E-08 DLC packs | T-010 (launcher core) |
| [T-015-voice-protocol-and-bot-test](T-015-voice-protocol-and-bot-test.md) | Voice chat protocol: Opus frames over Lidgren, server relay by range, bot test | ready | E-09 Voice | T-003 (range sets) preferred; none required |
| [T-016-voice-client-capture-playback](T-016-voice-client-capture-playback.md) | Voice chat in the client: capture, encode, positional playback, push-to-talk, indicator | ready | E-09 Voice | T-015 |
| [T-017-anti-cheat-baseline](T-017-anti-cheat-baseline.md) | Anti-cheat baseline: server-side validation, cheat events, client integrity report, signed manifest | ready | E-10 Anti-cheat | T-002 (to tune thresholds under load) |
| [T-018-sync-instrumentation](T-018-sync-instrumentation.md) | Sync instrumentation: per-entity error overlay, packet-age stats, bot route replay | ready | E-11 Sync quality | none |
| [T-019-3d-browsers](T-019-3d-browsers.md) | 3D browsers: pages placed in the world, depth-tested | draft | E-12 CEF UI | T-012 |
| [T-020-remove-dead-code-and-unused-binaries](T-020-remove-dead-code-and-unused-binaries.md) | Remove dead code and unused binaries | ready | E-02 Agent framework (hygiene) | none |
| [T-021-dlc-runtime-mounting-spike](T-021-dlc-runtime-mounting-spike.md) | Spike: mounting DLC packs at runtime (no game restart) | draft | E-08 DLC packs | T-014 |
| [T-022-dlc-in-game-download-and-restart-to-apply](T-022-dlc-in-game-download-and-restart-to-apply.md) | DLC packs in game: download for the next server, restart-to-apply through the launcher, auto-join | ready | E-08 DLC packs | T-014, T-012 |

## Rules

* A task is `ready` only when everything it needs is decided (`docs/DECISIONS.md`) and every acceptance criterion
  can be checked by a command or an owner step.
* Tasks are small: one session, under ~15 files. Larger work is a chain of tasks with `Depends on`.
* The task file is the record: log lines while working, the Result when finished. It is never deleted; `done`
  tasks stay for the history.
* Numbers are assigned once and never reused.
