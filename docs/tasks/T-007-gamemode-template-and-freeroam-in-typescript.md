# T-007 — Gamemode template (`gtanetwork create`) and freeroam fully in TypeScript

Status: done
Epic: E-04 TypeScript
Size: M
Branch: task/T-007-freeroam-typescript from the integration branch
Depends on: T-005, T-006
PR: yes

## Goal

`Tools/GTANetwork.Cli` (`gtanetwork create <name>`) writes a resource skeleton — `meta.xml`, `server/index.ts`,
`client/index.ts`, `ui/index.html` + `ui/app.ts`, `tsconfig.json` referencing `types/`, `package.json` with scripts
`check` (`tsc --noEmit`) — and `freeroam` is entirely TS (server + client), the reference gamemode.

## Files

* New: `Tools/GTANetwork.Cli/` (net10.0; `create`, later `check`), `templates/resource/**`.
* Change: `Server/resources/freeroam/**` (finish the port started in T-005/T-006; delete `freeroam.cs`), `README.md`
  ("Write a gamemode" section: create → run → connect), `eng/integration-test.sh` if command texts changed, `.github/workflows/build.yml` (publish the CLI).

## Acceptance criteria

- [x] `gtanetwork create demo && cd demo && bun install && bun run check` passes; the server starts `demo` and a bot receives its
      greeting, its `/hello` answer and its RPC answer (`eng/integration-test-template.sh`).
- [x] `eng/dev-test.sh` passes with freeroam in TS only (`freeroam.cs` deleted; the same chat replies as before).

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 agent — implemented on `task/T-007-freeroam-typescript` right after T-005 merged; `eng/dev-test.sh` green; PR opened.

## Result

* **Changed**: new `Tools/GTANetwork.Cli/` (`gtanetwork create <name> [--dir <parent>] [--force]`, `--version`; templates and typings
  ship next to the executable; in a checkout it finds `templates/` and `types/` up the tree), new `templates/resource/**`
  (`meta.xml`, `server/index.ts`, `server/tsconfig.json`, `client/index.ts`, `tsconfig.json`, `ui/{index.html,style.css,app.js}`,
  `package.json`, `README.md`, `types/gtan.d.ts` — the hand-kept surface of `runtime/gtan/index.ts`), `GTANetwork.sln`;
  `Server/resources/freeroam/server/index.ts` replaces `freeroam.cs` (same commands, aliases, messages, RPC handlers; enum arguments
  through `gtan.parseEnum`), `meta.xml`, `server/tsconfig.json`; runtime library: `Tools/GTANetwork.TypeGen` also emits
  `runtime/gtan/enums.generated.ts` (every enum the API mentions as a frozen table), `runtime/gtan/enums.ts` (`parseEnum`,
  `enumName`), `gtan.enums` / `gtan.parseEnum` / `gtan.enumName` in `runtime/gtan/index.ts`, and a guard: a resource unloaded while
  its module was still loading is not started, and `gtan.api` calls after an unload are dropped with one warning (found by the
  template test: a second `type="gamemode"` resource stops the running gamemode — the template is `type="script"`); tests: new
  `eng/integration-test-template.sh` (create → `bun run check` → server start → bot: greeting, `/hello`, `/panel` without "Command
  not found", `rpc demo:time`), `eng/dev-test.sh` and `.github/workflows/build.yml` publish the CLI (linux-x64, win-x64 artifacts),
  run the template test and include `enums.generated.ts` in the stale-typings check; docs (`README.md` "Write a gamemode",
  `CHANGELOG.md`, `docs/CODEMAP.md`, `docs/PLAN.md`, `docs/HANDOFF.md`).
* **Verified**: `docker compose run --rm dev eng/dev-test.sh` → `All local checks passed.` (numbers in the PR): freeroam's bot
  phases unchanged with the TypeScript server (`Welcome to GTA Network freeroam`, `/veh adder` → `Spawned Adder.`, `/players`,
  `/weapon carbinerifle 250`, RPC `freeroam:ping` / `freeroam:secret`), the template resource created, type-checked and driven.
* **Owner check**: in game freeroam behaves as before (join message, `/help`, `/veh adder`, `/weapon`, `/tp`, `/skin`, `/fix`,
  `/shard text`); `gtanetwork create mymode` on the machine, then the folder under `~/GTANetwork/server/resources/`.
* **Not done**: `gtanetwork check` (run the type check without `bun run`), a template for a C# resource, the ping column of the
  CEF menu (T-011). `types/gtan.d.ts` in the template mirrors `runtime/gtan/index.ts` by hand — a generated declaration file
  (tsc `--declaration` over the runtime) would remove the drift risk.
