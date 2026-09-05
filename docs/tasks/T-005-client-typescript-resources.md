# T-005 — Client resources in TypeScript: `lang="typescript"` bundled by the server with Bun

Status: done
Epic: E-04 TypeScript
Size: M
Branch: task/T-005-client-typescript from the integration branch
Depends on: T-004
PR: yes

## Goal

A resource declares `<script src="client/index.ts" type="client" lang="typescript"/>`; at resource start the server
bundles it (imports resolved, TS erased) into one JS text delivered to clients exactly like today's `client.js`; the
in-game engine (ClearScript V8 12) is unchanged. `freeroam`'s `client.js` is ported to TS as the proof.

## Why

Q-02 default: the client script runtime stays in-process (natives are called per frame); TS arrives as a build step,
not a new runtime.

## Scope

* In: bundling at resource start, error reporting, caching by content hash, the sample port, docs.
* Out: server-side TS (T-006), hot reload of client scripts (later), source maps in game (the bundle keeps names; `--minify` off).

## Files

* Change: `Server/ResourceInfo.cs` (`ScriptingEngineLanguage.typescript`; meta.xml `lang="typescript"`),
  `Server/Resources.cs:26` `StartResource` (for TS client scripts: run the bundler, put the output into the
  `ClientsideScript` list as `client/index.js` with the bundle's MD5), `Server/Managers/ScriptCompiler.cs` (a
  `TypeScriptBundler` class: finds `bun` on `PATH` or `GTAN_BUN`, runs `bun build <entry> --target=browser --format=iife
  --outfile <tmp>`, captures stderr, 30 s timeout; cache dir `resources/.cache/<resource>/<hash>.js`),
  `Shared/Packets.cs` (`ClientsideScript` unchanged), `Server/resources/freeroam/meta.xml` + `client/index.ts` (ported),
  `docs/CODEMAP.md` §9, `CHANGELOG.md`, `README.md`.
* Read: `Client/Javascript/JavascriptHook.cs:408` (what the client accepts: one script text per file), `Client/Networking/DownloadManager.cs`
  (script delivery), `eng/integration-test.sh` (uses freeroam's chat replies — must still pass).

## Approach

1. `TypeScriptBundler`: if `bun` is missing, `StartResource` fails with one clear line (`resource X: TypeScript needs bun
   (https://bun.sh) on PATH or GTAN_BUN`); the server keeps running the other resources.
2. Bundle target: V8 12 = ES2023; `--target=browser` output with `--format=iife` wraps the module in a function so the
   engine's global `API` object is still reachable; check that top-level `await` is rejected (not supported in a script) by a test.
3. The bundle's file name in the `ClientsideScript` keeps the `.ts` entry's directory so `resource`-relative references work.
4. Port `freeroam/client.js` → `client/index.ts` using `types/client.d.ts`; `tsconfig.json` in the resource.
5. Integration test: `eng/integration-test.sh` exercises freeroam; add a check that the server log shows `bundled client/index.ts`.

## Acceptance criteria

- [x] `eng/integration-test.sh` passes with freeroam's client in TS (the bot receives `client/index.js`).
- [x] A syntax error fails the resource start with Bun's file:line; a *type* error does so when the resource has TypeScript installed
      (`node_modules/typescript` + `tsconfig.json` → `tsc --noEmit` runs first). Bun alone erases types without checking them.
- [x] Second start with unchanged sources uses the cache (log line `cached bundle of client/index.ts`), well under 50 ms.
- [ ] Owner check: the in-game behaviour of freeroam is unchanged (notification on start, shard on `/help`, subtitle in a vehicle).

## Test plan

`docker compose run --rm dev eng/dev-test.sh` (Bun installed in the container by T-004); a deliberate type error → failing
start with a readable message; in-game run by the owner.

## Risks and notes

Bun binaries per platform for server operators: document in `README.md` (Linux/Windows). The Windows server package
does not bundle Bun.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-05 agent — implemented on `task/T-005-client-typescript`; `eng/dev-test.sh` green (smoke test bundles, the integration
  tests read the cache); PR opened. Deviation: Bun has no type checker, so the type-error criterion holds only for resources that
  bring `typescript` in `node_modules` (then `tsc --noEmit` runs before the bundle); syntax errors always fail the start.

## Result

* **Changed**: new `Server/Managers/TypeScriptBundler.cs` (Bun lookup through `RuntimeProcess.FindBun`, `bun build <entry>
  --target=browser --format=iife --outfile <cache>.tmp`, 30 s timeout, stdout+stderr in the exception, cache
  `resources/.cache/<resource>/<md5>.js` keyed by the Bun version, the entry and every `.ts/.js/.json` file of the resource
  (node_modules by its lock file), optional `bun x tsc --noEmit -p tsconfig.json`, week-old bundles pruned), `Server/Resources.cs`
  (client `lang="typescript"` → bundle → `ClientsideScript` named `client/index.js` through the shared `AddClientScript`; no Bun →
  one error line and the resource starts without that script; a bundle error → the resource does not start),
  `Server/GTANetworkServer.csproj` (`.cache` not copied), `.gitignore`, `Server/resources/freeroam` (`client.js` → `client/index.ts`,
  `tsconfig.json`, `meta.xml`), `eng/smoke-test-server.sh` (`bundled client/index.ts -> client/index.js`), `eng/integration-test.sh`
  (bundled or cached + the bot's `client script "client/index.js" from "freeroam"`), `eng/integration-test-auth.sh` (the copied
  server folder must hit the cache), docs (`CHANGELOG.md`, `README.md`, `docs/CODEMAP.md`, `docs/PLAN.md`, `docs/HANDOFF.md`).
* **Verified**: `docker compose run --rm dev eng/dev-test.sh` → `All local checks passed.`; the smoke test's server logs
  `bundled client/index.ts -> client/index.js (… KB, … ms)`, the two integration servers log `cached bundle of …` (a few ms), the
  bot logs `client script "client/index.js" from "freeroam"`. Numbers in the PR.
* **Owner check**: in game, freeroam behaves as before: the green notification on join, `/help` shard, "Enjoy the ride!" in a vehicle.
  The owner's server needs Bun: the deploy put the container's Bun 1.4.1 into `~/GTANetwork/server/runtime/bun/bun`.
* **Not done**: hot reload of client scripts (would need a re-download protocol); shipping Bun in the server packages (T-006
  follow-up; the Windows package has none, so TypeScript resources there need `GTAN_BUN`); source maps in game.
