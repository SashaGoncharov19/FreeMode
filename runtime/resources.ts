// Loading, unloading and hot reload of TypeScript server resources. A resource's entry module exports
// `default function main(gtan: Gtan)`; Bun runs TypeScript directly, so no bundling is needed on the server side.
import { watch, type FSWatcher } from "node:fs";
import { pathToFileURL } from "node:url";
import { join } from "node:path";
import type { Bridge } from "./bridge";
import { Gtan } from "./gtan";

interface Loaded { gtan: Gtan; dir: string; entry: string; settings: Record<string, string>; watcher?: FSWatcher; reloadTimer?: ReturnType<typeof setTimeout>; }

export class Resources {
  private loaded = new Map<string, Loaded>();
  constructor(private bridge: Bridge, private catalogue: any, private log: (level: string, resource: string, text: string) => void) {}

  get(resource: string): Gtan | undefined { return this.loaded.get(resource)?.gtan; }

  async load(resource: string, dir: string, entry: string, settings: Record<string, string>) {
    if (this.loaded.has(resource)) await this.unload(resource);
    const gtan = new Gtan(this.bridge, resource, dir, settings, this.catalogue);
    const item: Loaded = { gtan, dir, entry, settings };
    this.loaded.set(resource, item);
    try {
      const url = pathToFileURL(join(dir, entry)).href + "?v=" + Date.now(); // cache-busting import for hot reload
      const mod = await import(url);
      const main = mod.default ?? mod.main;
      if (typeof main !== "function") throw new Error(entry + " must export default function main(gtan)");
      await main(gtan);
      if (this.loaded.get(resource) !== item) { this.log("info", resource, "unloaded while " + entry + " was loading; not started"); return; }
      await gtan.dispatch("resourceStart", []);
      this.log("info", resource, "started " + entry + " (Bun " + Bun.version + ")");
    } catch (e) {
      this.log("error", resource, "failed to start " + entry + ": " + ((e as Error).stack ?? e));
    }
    try {
      item.watcher = watch(dir, { recursive: true }, (_event, file) => {
        if (!file || !/\.(ts|js|mts|mjs|json)$/.test(String(file))) return;
        clearTimeout(item.reloadTimer);
        item.reloadTimer = setTimeout(() => { this.log("info", resource, "changed (" + file + "): reloading"); void this.load(resource, dir, entry, settings); }, 500);
      });
    } catch (e) {
      this.log("warn", resource, "hot reload unavailable: " + (e as Error).message);
    }
  }

  async unload(resource: string) {
    const item = this.loaded.get(resource);
    if (!item) return;
    this.loaded.delete(resource);
    item.watcher?.close();
    clearTimeout(item.reloadTimer);
    await item.gtan.dispatch("resourceStop", []);
    item.gtan.dispose();
    this.log("info", resource, "stopped");
  }

  async unloadAll() { for (const name of [...this.loaded.keys()]) await this.unload(name); }
}
