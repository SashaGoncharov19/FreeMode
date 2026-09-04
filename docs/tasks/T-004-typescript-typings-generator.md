# T-004 — TypeScript typings generated from the C# APIs (server, client, CEF)

Status: ready
Epic: E-04 TypeScript
Size: M
Branch: task/T-004-typegen from the integration branch
Depends on: none (T-001 preferred first)
PR: no

## Goal

`Tools/GTANetwork.TypeGen` reads the built `GTANetworkServer.dll` and `GTANetwork.dll` and writes `types/server.d.ts`,
`types/client.d.ts`, `types/shared.d.ts` (enums, `Vector3`, entity property types); `types/cef.d.ts` is hand-written.
CI runs the generator and `tsc --noEmit` over a sample resource that uses the types; the `types/` folder ships with every release.

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
2. Server: `declare class API { … }`, `declare abstract class Script { API: API }`; client: `declare const API: ScriptContext`,
   `declare const resource: …`, `declare const exported: …`.
3. Doc comments: copy `<summary>` XML docs where present (the build has `GenerateDocumentationFile=false` — enable it for
   `Server` and `Client` in Release so the XML exists next to the DLL; the generator reads it).
4. Sample resource compiles with `tsc --noEmit`; the generator run is idempotent (byte-identical output for the same DLLs).

## Acceptance criteria

- [ ] `dotnet run --project Tools/GTANetwork.TypeGen -- …` writes the four files; `bunx tsc --noEmit -p samples/ts-resource` passes.
- [ ] Every public member of `API` and `ScriptContext` appears in the output (count check printed by the generator).
- [ ] `eng/dev-test.sh` runs the generator and `tsc`; the release zip contains `types/`.

## Test plan

`docker compose run --rm dev eng/dev-test.sh` (needs Bun in the container: add `oven-sh/bun` install to `.devcontainer/Dockerfile`
in this task); `diff` two consecutive generator outputs → empty.

## Risks and notes

The client DLL is net48: load it with `MetadataLoadContext` and the reference assemblies from
`Microsoft.NETFramework.ReferenceAssemblies`; the stub SHVDN DLL from `Shv.NET/ref` is enough for types.

## Log

* 2026-09-04 22:10 agent — created.

## Result

(empty)
