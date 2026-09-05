# TypeScript typings of the scripting APIs

| File | Content | Source |
| --- | --- | --- |
| `shared.d.ts` | Enums (`VehicleHash`, `WeaponHash`, `PedHash`, `Keys`, …), `Vector3`, `NetHandle`, entity property types, the `HostEvent` shape | generated from `GTANetworkShared.dll` and the referenced framework types |
| `client.d.ts` | The in-game API: `ScriptContext` (what a client script sees as `API`), its events, host types (`Vector3`, `Keys`, `menuControl`, …) | generated from `Client/bin/Release/net48/GTANetwork.dll` |
| `server.d.ts` | The server API (`API`, `Script`, `Client`, `Vehicle`, … as C# scripts see them; the Bun runtime library of T-006 exposes the same shape) | generated from `Server/bin/Release/net10.0/GTANetworkServer.dll` |
| `cef.d.ts` | The bridge every CEF page gets: `resourceCall`, `resourceEval`, `gtan` | hand-written |
| `api-catalogue.json` | Every server API member with parameter and return types, for the Bun bridge (T-006) | generated |
| `runtime/gtan/api.generated.d.ts` | `gtan.api` of TypeScript server resources: every API member as a Promise-returning method, players/entities as handles | generated (`--runtime-lib`) |

Events are .NET events as ClearScript exposes them: `API.onUpdate.connect(() => …)` / `disconnect`. Overloads appear as
overloaded signatures; C# `object`/`dynamic` is `unknown`; `List<T>` is `T[]`; `Dictionary<string, T>` is `Record<string, T>`;
a type the generator does not know is `unknown /* Full.Name */`.

## Using them in a resource

`samples/ts-resource/` is a complete example (one `tsconfig` per side, because `API` has a different type on the client and
on the server): `tsconfig.client.json` includes `types/shared.d.ts` + `types/client.d.ts`, `tsconfig.server.json` includes
`types/shared.d.ts` + `types/server.d.ts`, `tsconfig.cef.json` includes `types/cef.d.ts`. `bun run check` type-checks all three.

## Regenerating

The files are generated from the built assemblies and committed; CI fails when they are stale.

```bash
dotnet build GTANetwork.sln -c Release
dotnet run --project Tools/GTANetwork.TypeGen -c Release -- \
  --client Client/bin/Release/net48/GTANetwork.dll \
  --server Server/bin/Release/net10.0/GTANetworkServer.dll \
  --net48-refs ~/.nuget/packages/microsoft.netframework.referenceassemblies.net48/1.0.3/build/.NETFramework/v4.8 \
  --probe Shv.NET/ref/bin/Release/net48 \
  --out types --runtime-lib runtime/gtan
git diff --exit-code -- types/   # stale typings fail here
```

Doc comments come from the XML documentation files the Server and Client builds write next to their DLLs.
