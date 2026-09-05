# __NAME__

A GTA Network resource in TypeScript, created by `gtanetwork create`.

| Path | What it is |
| --- | --- |
| `meta.xml` | What the server loads: the server script, the client script, the files clients download. `type="script"` runs next to the gamemode; `type="gamemode"` replaces the running one (the server keeps a single gamemode). |
| `server/index.ts` | Runs in the Bun runtime next to the server. `export default function main(gtan)`; `gtan.api` is the server API, `gtan.on` its events, `gtan.commands` the chat commands, `gtan.rpc` request/response calls, `gtan.players` the mirrored players. Reloads while the server runs. |
| `client/index.ts` | Bundled by the server at resource start, runs in the game. `API` is the client API; `API.rpc` answers the page and calls the server. |
| `ui/` | A CEF page (`/panel` opens it) talking to the scripts through `gtan.rpc.call`. |
| `types/` | The typings the checks use (`client.d.ts`, `shared.d.ts`, `cef.d.ts`, the server API as `api.generated.d.ts`, `enums.generated.ts`, `gtan.d.ts`), copied by the CLI so this folder is self-contained. |

```bash
bun install && bun run check      # type-check both sides (Bun: https://bun.sh)
```

Run it: move this folder into `<server>/resources/`, add `<resource src="__NAME__" />` to the server's `settings.xml`, start the
server (it needs Bun: `GTAN_BUN=<path>`, a copy in `<server>/runtime/bun/`, or `bun` on `PATH`), join with the game or the
headless bot. The server log shows `[__NAME__] started server/index.ts` and `bundled client/index.ts -> client/index.js`.
