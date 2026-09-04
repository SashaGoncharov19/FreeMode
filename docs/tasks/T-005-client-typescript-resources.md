# T-005 — Client resources in TypeScript: `lang="typescript"` bundled by the server with Bun

Status: ready
Epic: E-04 TypeScript
Size: M
Branch: task/T-005-client-ts from the integration branch
Depends on: T-004
PR: no

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

- [ ] `eng/integration-test.sh` passes with freeroam's client in TS.
- [ ] A TS type error in `client/index.ts` fails the resource start with the file:line from `tsc`/bun in the server log.
- [ ] Second start with unchanged sources uses the cache (log line `cached`), start time ≤ 50 ms for the bundle step.
- [ ] Owner check: the in-game behaviour of freeroam is unchanged (chat, `/veh`, blips).

## Test plan

`docker compose run --rm dev eng/dev-test.sh` (Bun installed in the container by T-004); a deliberate type error → failing
start with a readable message; in-game run by the owner.

## Risks and notes

Bun binaries per platform for server operators: document in `README.md` (Linux/Windows). The Windows server package
does not bundle Bun.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)
