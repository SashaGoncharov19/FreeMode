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
| [T-001-dotnet-10-for-server-launcher-bot-tools](T-001-dotnet-10-for-server-launcher-bot-tools.md) | .NET 10 for server, launcher, bot, Map2Resource and the dev container | done | E-01 Platform upgrade | none |
| [T-002-bot-load-harness-and-baseline](T-002-bot-load-harness-and-baseline.md) | Bot load harness: N simulated players, server metrics, baseline numbers at 100/300/1000 | done | E-03 Scale | none (T-001 preferred first) |
| [T-003-server-interest-management](T-003-server-interest-management.md) | Server-side interest management: grid cells, per-type ranges, tiered rates, per-player budget | needs owner (implemented and measured; the two-player check remains) | E-03 Scale | T-002 |
| [T-004-typescript-typings-generator](T-004-typescript-typings-generator.md) | TypeScript typings generated from the C# APIs (server, client, CEF) | done | E-04 TypeScript | none (T-001 preferred first) |
| [T-005-client-typescript-resources](T-005-client-typescript-resources.md) | Client resources in TypeScript: `lang="typescript"` bundled by the server with Bun | done | E-04 TypeScript | T-004 |
| [T-006-server-runtime-on-bun-bridge](T-006-server-runtime-on-bun-bridge.md) | Server gamemode runtime on Bun: bridge spike with numbers, then protocol, state mirror, resource loader, hot reload | done (follow-ups listed under Result, stage 2 → Not done) | E-04 TypeScript | T-001, T-004 |
| [T-007-gamemode-template-and-freeroam-in-typescript](T-007-gamemode-template-and-freeroam-in-typescript.md) | Gamemode template (`gtanetwork create`) and freeroam fully in TypeScript | done | E-04 TypeScript | T-005, T-006 |
| [T-008-typed-rpc-server-client-cef](T-008-typed-rpc-server-client-cef.md) | Typed RPC: server ⇄ client ⇄ CEF with request ids, timeouts, permissions, rate limits | done | E-05 RPC and protocol security | T-004 |
| [T-009-session-encryption-and-authentication](T-009-session-encryption-and-authentication.md) | Encrypted, authenticated session between client and server | done | E-05 RPC and protocol security | T-008 |
| [T-010-launcher-gui-avalonia-skeleton](T-010-launcher-gui-avalonia-skeleton.md) | Launcher GUI (Avalonia 12): Play, settings, log viewer; the CLI becomes a thin front end | needs owner (implemented; the window itself must be seen on the owner's Debian) | E-06 Launcher | T-001 |
| [T-011-master-list-service-and-server-browser](T-011-master-list-service-and-server-browser.md) | Master list service, server announce, server list in the client menu | needs owner (implemented; Q-07 domain + host, then the in-game list check) | E-07 Master list | T-001 |
| [T-012-cef-loader-from-connect-to-spawn](T-012-cef-loader-from-connect-to-spawn.md) | CEF loading screen from "connect" until spawn | needs owner | E-12 CEF UI | none |
| [T-013-cef-main-menu-server-browser](T-013-cef-main-menu-server-browser.md) | In-game CEF main menu: server list, favourites, direct connect, settings (replaces the NativeUI server browser) | needs owner (implemented; in-game check pending) | E-12 CEF UI | T-012 (T-011 supplies the master list later; the menu already reads `MasterServerAddress` the way the NativeUI browser does) |
| [T-014-dlc-packs-manifest-and-launcher-install](T-014-dlc-packs-manifest-and-launcher-install.md) | Custom DLC packs: server manifest, launcher download and install-time overlay (design + first implementation) | needs owner (manifest, download, protocol and refusal done; the update.rpf overlay waits for Q-15) | E-08 DLC packs | T-010 (launcher core) |
| [T-015-voice-protocol-and-bot-test](T-015-voice-protocol-and-bot-test.md) | Voice chat protocol: Opus frames over Lidgren, server relay by range, bot test | done | E-09 Voice | T-003 (range sets) preferred; none required |
| [T-016-voice-client-capture-playback](T-016-voice-client-capture-playback.md) | Voice chat in the client: capture, encode, positional playback, push-to-talk, indicator | needs owner (implemented; the microphone and the positional playback must be checked in game under Proton) | E-09 Voice | T-015 |
| [T-017-anti-cheat-baseline](T-017-anti-cheat-baseline.md) | Anti-cheat baseline: server-side validation, cheat events, client integrity report, signed manifest | done (the manifest signing is a follow-up that needs a repository secret from the owner) | E-10 Anti-cheat | T-002 (to tune thresholds under load) |
| [T-018-sync-instrumentation](T-018-sync-instrumentation.md) | Sync instrumentation: per-entity error overlay, packet-age stats, bot route replay | ready | E-11 Sync quality | none |
| [T-019-3d-browsers](T-019-3d-browsers.md) | 3D browsers: pages placed in the world, depth-tested | draft | E-12 CEF UI | T-012 |
| [T-020-remove-dead-code-and-unused-binaries](T-020-remove-dead-code-and-unused-binaries.md) | Remove dead code and unused binaries | done | E-02 Agent framework (hygiene) | none |
| [T-021-dlc-runtime-mounting-spike](T-021-dlc-runtime-mounting-spike.md) | Spike: mounting DLC packs at runtime (no game restart) | draft | E-08 DLC packs | T-014 |
| [T-022-dlc-in-game-download-and-restart-to-apply](T-022-dlc-in-game-download-and-restart-to-apply.md) | DLC packs in game: download for the next server, restart-to-apply through the launcher, auto-join | ready | E-08 DLC packs | T-014, T-012 |
| [T-023-encrypted-relay-cost](T-023-encrypted-relay-cost.md) | Encrypted relay cost: pooled buffers, and a per-server relay key (Q-14) | done | E-03 Scale | T-002 |
| [T-024-launcher-server-list-and-connect](T-024-launcher-server-list-and-connect.md) | Launcher window: server list from the master, favourites and recent, connect in one click | in progress | E-06 Launcher with a GUI (Linux and Windows) and an updater | T-010, T-011 |
| [T-025-manifest-signing](T-025-manifest-signing.md) | Signed client manifest: the release job signs manifest.json, the server verifies the signature | ready | E-10 Anti-cheat | T-017 |
| [T-026-entity-broadcast-interest](T-026-entity-broadcast-interest.md) | Entity create/update/delete and unoccupied-vehicle sync under interest management | ready | E-03 Scale | T-003 |
| [T-027-voice-devices-and-activation](T-027-voice-devices-and-activation.md) | Voice: input device selection, voice activation, a local talking indicator | draft | E-09 Voice | T-016 (the owner's in-game check first) |
