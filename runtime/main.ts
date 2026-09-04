// The Bun runtime of GTA Network: hosts the TypeScript server resources and talks to the engine (GTANetworkServer,
// Server/Runtime/RuntimeBridge.cs) over the bridge. Started by the engine: bun run main.ts --socket unix:<path>|tcp:<port>
// with GTAN_RUNTIME_TOKEN and GTAN_SERVER_PID in the environment. Exits when the engine goes away.
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { Bridge, FrameType, type Frame } from "./bridge";
import { Resources } from "./resources";
import { applyState } from "./state";

const argv = process.argv.slice(2);
const socketArg = argv[argv.indexOf("--socket") + 1] ?? "unix:/tmp/gtan-runtime.sock";
const token = process.env.GTAN_RUNTIME_TOKEN ?? "";
const serverPid = Number(process.env.GTAN_SERVER_PID ?? 0);

function readCatalogue(): any {
  for (const candidate of [join(import.meta.dir, "gtan", "catalogue.json"), join(import.meta.dir, "..", "types", "api-catalogue.json")]) {
    if (existsSync(candidate)) { try { return JSON.parse(readFileSync(candidate, "utf8")); } catch { /* next */ } }
  }
  return null;
}

const bridge = new Bridge();
const catalogue = readCatalogue();
const resources = new Resources(bridge, catalogue, (level, resource, text) => bridge.log(resource, level, text));

bridge.onFrame = (f: Frame) => {
  switch (f.type) {
    case FrameType.Load: {
      const p = f.payload as { resource: string; dir: string; entry: string; settings: Record<string, string> };
      void resources.load(p.resource, p.dir, p.entry, p.settings ?? {});
      break;
    }
    case FrameType.Unload:
      void resources.unload(String(f.name));
      break;
    case FrameType.Event: {
      const p = f.payload as { r: string; a: unknown[] };
      const gtan = resources.get(p.r);
      if (!gtan) { if (f.id != null) bridge.result(f.id, { cancel: false }); break; }
      void gtan.dispatch(String(f.name), p.a ?? []).then((r) => { if (f.id != null) bridge.result(f.id, r); });
      break;
    }
    case FrameType.State:
      applyState(f.payload);
      break;
    default:
      break;
  }
};
bridge.onClose = () => { console.error("runtime: the engine closed the connection; exiting"); process.exit(0); };

await bridge.connect(socketArg);
const hello = (await bridge.call("hello", [token], true)) as { ok?: boolean; tickHz?: number } | null;
if (!hello?.ok) { console.error("runtime: handshake failed"); process.exit(1); }
console.log("runtime: connected to the engine over " + socketArg + " (Bun " + Bun.version + ", " + (catalogue?.functions?.length ?? 0) + " API members known)");

if (serverPid > 0) {
  setInterval(() => {
    try { process.kill(serverPid, 0); } catch { console.error("runtime: the engine process is gone; exiting"); process.exit(0); }
  }, 2000);
}
process.on("SIGTERM", () => { void resources.unloadAll().then(() => process.exit(0)); });
