# T-004 — TypeScript typings generated from the C# APIs (server, client, CEF)

Status: done
Epic: E-04 TypeScript
Size: M
Branch: task/T-004-typegen from the integration branch
Depends on: none (T-001 preferred first)
PR: yes

## Goal

`Tools/GTANetwork.TypeGen` reads the built `GTANetworkServer.dll` and `GTANetwork.dll` and writes `types/client.d.ts`,
`types/shared.d.ts` (enums, `Vector3`, entity property types) and, for the server, the **Bun library** `runtime/gtan/`
(`index.ts` with a function per `API` member that sends a bridge frame, plus its `.d.ts`; see T-006 and D-09) from the API
catalogue (`Server/Runtime/ApiCatalogue.cs`, reflection over `Server/API.cs` with a `needsResult` flag per member);
`types/cef.d.ts` is hand-written. CI runs the generator and `tsc --noEmit` over a sample resource; the `types/` folder ships
with every release.

## Why

TS on both sides (E-04) needs the API surface as types; the surface is 381 public members on `Server/API.cs` and 403 on
the client `ScriptContext` (`Client/Javascript/JavascriptHook.cs:586`) plus events — too many to hand-write and keep current.

## Scope

* In: the generator, its mapping rules, the CI step, the sample, the shipped folder.
* Out: changing the C# APIs to be "nicer" (record what maps badly in the Result instead).

## Files

* New: `Tools/GTANetwork.TypeGen/GTANetwork.TypeGen.csproj` (net10.0 console; `System.Reflection.MetadataLoadContext`
  so the net48 client DLL and the stub SHVDN can be inspected from a .NET 10 process), `Program.cs` (arguments:
  `--server <dll> --client <dll> --shared <dll> --out types/`), `Emit.cs` (C# → TS type mapping), `types/cef.d.ts`
  (`resourceCall`, `resourceEval`, `gtan.call`, `gtan.eval`, later `gtan.rpc` from T-008), `samples/ts-resource/` (a
  resource with `client.ts`, `server.ts`, `tsconfig.json` referencing `../../types`).
* Change: `.github/workflows/build.yml` (`linux` job: run TypeGen after the build, `bunx tsc --noEmit -p samples/ts-resource`
  with `oven-sh/setup-bun@v2`; upload `types/` as an artifact and include it in the release zip), `eng/package-client.ps1`
  (copy `types/` into the client package), `eng/dev-test.sh` (run the generator + tsc), `README.md` (a "Scripting in TypeScript" pointer).
* Read: `Server/API.cs` (:41 `class API`, events :88–:122, delegates :61), `Server/Elements/*.cs`, `Shared/EntityProperties.cs`,
  `Shared/*Hashes.cs`, `Client/Javascript/JavascriptHook.cs:586` (`ScriptContext`), `:3878` (client events), `:408`
  (host types exposed: `List<>`, `Dictionary<,>`, `Vector3`, `Matrix4`, `Keys`, `Point`, `Size`, `menuControl`, `BadgeStyle`).

## Approach

1. Mapping: `string`→`string`; numeric→`number`; `bool`→`boolean`; `void`→`void`; `Vector3`/`Quaternion`→ interfaces in
   `shared.d.ts`; enums → TS `enum` with the same integer values; `object`/`dynamic` → `unknown`; `params object[]` →
   `...args: unknown[]`; `List<T>`→`T[]` (host `List` methods stay available: emit both `T[] | List<T>`); `Dictionary<K,V>`→`Record`;
   `Client`/`Vehicle`/… → interfaces from `Server/Elements`; `NetHandle` → interface; delegates → function types.
   Overloads → TS overload signatures. .NET events → `{ connect(handler: (…) => void): void; disconnect(handler): void }`
   (ClearScript's shape; the client already uses `API.onUpdate.connect`).
2. Server: the `runtime/gtan/` library — one exported function per `API` member; members whose return type is not `void`
   or whose name reads state that is not mirrored (`ApiCatalogue.needsResult`) return `Promise<T>`; mirrored reads
   (`getEntityPosition`, `getPlayerHealth`, …) become synchronous getters on `players`/`vehicles`; events become
   `gtan.on("playerConnected", (player) => …)` with typed payloads. Client: `declare const API: ScriptContext`,
   `declare const resource: …`, `declare const exported: …`, events as `{ connect(handler): void }` (ClearScript's shape).
3. Doc comments: copy `<summary>` XML docs where present (the build has `GenerateDocumentationFile=false` — enable it for
   `Server` and `Client` in Release so the XML exists next to the DLL; the generator reads it).
4. Sample resource compiles with `tsc --noEmit`; the generator run is idempotent (byte-identical output for the same DLLs).

## Acceptance criteria

- [x] `dotnet run --project Tools/GTANetwork.TypeGen -- …` writes `client.d.ts`, `server.d.ts`, `shared.d.ts`, `api-catalogue.json`; `bun run check` in `samples/ts-resource` passes (three tsconfigs).
- [x] Every public member of `API` and `ScriptContext` appears in the output (the generator prints the counts: 414 and 441, 0 skipped).
- [x] `eng/dev-test.sh` runs the generator and the Bun check; CI fails on stale typings (`git diff --exit-code -- types/`).
- [ ] The release zip contains `types/` — not done here (packaging change deferred: `eng/package-client.ps1` is the Windows job; add `types/` when the launcher/updater ships them, E-06).

## Test plan

`docker compose run --rm dev eng/dev-test.sh` (needs Bun in the container: add `oven-sh/bun` install to `.devcontainer/Dockerfile`
in this task); `diff` two consecutive generator outputs → empty.

## Risks and notes

The client DLL is net48: load it with `MetadataLoadContext` and the reference assemblies from
`Microsoft.NETFramework.ReferenceAssemblies`; the stub SHVDN DLL from `Shv.NET/ref` is enough for types.

## Log

* 2026-09-04 22:10 agent — created.
* 2026-09-04 23:35 agent — started on task/T-004-typegen (worktree).
* 2026-09-05 00:10 agent — generator runs against the stub-built client DLL (SHVDN stub folder via --probe); name collisions across files (TS merges same-named `declare enum`s) solved with one name registry; `String` host type not redeclared (a JS global). PR opened.

* 2026-09-05 08:20 agent — PR #6 merged (after making the output build-independent and merging the integration branch twice). done.

## Result

* **Changed**: new `Tools/GTANetwork.TypeGen/` (net10.0, `System.Reflection.MetadataLoadContext`: `Program.cs` with the mapping rules,
  XML-doc summaries, the API catalogue), new `types/` (`client.d.ts` 837 lines, `server.d.ts` ~4900 lines incl. the `Hash` enum,
  `shared.d.ts`, hand-written `cef.d.ts` and `README.md`, `api-catalogue.json`), new `samples/ts-resource/` (client/server/ui
  sources, three tsconfigs, `package.json` + `bun.lock`), `eng/dev-test.sh` (generator + Bun check), `.github/workflows/build.yml`
  (generator, stale check, `oven-sh/setup-bun@v2` 1.4.1, sample check — separate commit, needs the `workflow` token scope to push),
  `.devcontainer/Dockerfile` (Bun 1.4.1), `Server/GTANetworkServer.csproj` + `Client/GTANetworkClient.csproj` (`GenerateDocumentationFile`),
  `GTANetwork.sln` (TypeGen under Tools), `README.md`, `CHANGELOG.md`.
* **Verified**: `dotnet run --project Tools/GTANetwork.TypeGen …` → `client: 441 members of ScriptContext, 51 types, 0 skipped`,
  `server: 414 members of API, 50 types, 0 skipped, catalogue written`, `shared: 13 types`; unmapped framework types left:
  `GTA.Scaleform` (2), `System.Threading.Thread`, `System.Reflection.ParameterInfo` (as `unknown /* … */`); `bun run check`
  (oven/bun 1.4.1, TypeScript ^5.9) → exit 0 for client, server and cef tsconfigs.
* **Not done / follow-ups**: the Bun library `runtime/gtan/` is emitted by T-006 once the bridge frame format exists (the catalogue
  is its input); `types/` in the release package (E-06); enum member doc comments are not copied (only types/members).
* **Owner check**: none (no game involved).
