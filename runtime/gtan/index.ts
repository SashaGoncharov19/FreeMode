// What a TypeScript server resource gets: `export default function main(gtan: Gtan) { ... }` receives one Gtan per resource.
// gtan.api.<member>(...) is the server API (Server/API.cs) over the bridge — members that return a value resolve a Promise,
// the rest are fire-and-forget; gtan.players is the 10 Hz state mirror; gtan.on(event, handler) subscribes to the engine's
// events (the names of Server/API.cs without the "on" prefix); gtan.commands.register(name, handler) handles "/name ...".
import type { Bridge } from "../bridge";
import { players, type PlayerState } from "../state";
import type { ServerApi } from "./api.generated";

export type EventHandler = (...args: any[]) => unknown | Promise<unknown>;
export type CommandHandler = (player: number, ...args: string[]) => unknown | Promise<unknown>;

interface Catalogue { functions: { name: string; needsResult: boolean }[]; }

export class Gtan {
  /** The server API (Server/API.cs) over the bridge; typed by api.generated.d.ts, every member returns a Promise. */
  readonly api: ServerApi;
  readonly players: ReadonlyMap<number, PlayerState> = players;
  readonly commands: { register(name: string, handler: CommandHandler, options?: { aliases?: string[] }): void; unregister(name: string): void };
  private handlers = new Map<string, Set<EventHandler>>();
  private commandTable = new Map<string, CommandHandler>();
  private needsResult: Map<string, boolean>;

  constructor(private bridge: Bridge, readonly resource: string, readonly dir: string, readonly settings: Record<string, string>, catalogue: Catalogue | null) {
    this.needsResult = new Map((catalogue?.functions ?? []).map((f) => [f.name, f.needsResult]));
    const self = this;
    this.api = new Proxy({} as ServerApi, {
      get(_t, prop) {
        if (typeof prop !== "string") return undefined;
        return (...args: unknown[]) => self.bridge.call(self.resource + "/" + prop, args, self.needsResult.get(prop) ?? true);
      },
    });
    this.commands = {
      register: (name, handler, options) => { this.commandTable.set(name.toLowerCase(), handler); for (const a of options?.aliases ?? []) this.commandTable.set(a.toLowerCase(), handler); },
      unregister: (name) => { this.commandTable.delete(name.toLowerCase()); },
    };
  }

  /** The mirrored state of a player (by handle), or undefined when the engine has not sent it yet. */
  player(handle: number): PlayerState | undefined { return players.get(handle); }

  on(event: string, handler: EventHandler) { let set = this.handlers.get(event); if (!set) { set = new Set(); this.handlers.set(event, set); } set.add(handler); }
  off(event: string, handler: EventHandler) { this.handlers.get(event)?.delete(handler); }

  log(...parts: unknown[]) { this.bridge.log(this.resource, "info", parts.map(String).join(" ")); }
  warn(...parts: unknown[]) { this.bridge.log(this.resource, "warn", parts.map(String).join(" ")); }

  /** Called by the runtime for every event of this resource; returns { cancel } for cancelable events. */
  async dispatch(event: string, args: unknown[]): Promise<{ cancel: boolean }> {
    let cancel = false;
    if (event === "chatCommand") {
      const [player, command] = args as [number, string];
      const parts = command.trim().split(/\s+/);
      const name = parts[0].replace(/^\//, "").toLowerCase();
      const handler = this.commandTable.get(name);
      if (handler) {
        try { await handler(player, ...parts.slice(1)); }
        catch (e) { this.warn("command /" + name + " failed: " + ((e as Error).stack ?? e)); }
        cancel = true;
      }
    }
    const set = this.handlers.get(event);
    if (set) {
      for (const h of set) {
        try {
          const r = await h(...args);
          if (r && typeof r === "object" && (r as any).cancel === true) cancel = true;
        } catch (e) {
          this.warn("handler for " + event + " failed: " + ((e as Error).stack ?? e));
        }
      }
    }
    return { cancel };
  }

  /** Unload: forget handlers and commands (the module instance is dropped by the loader). */
  dispose() { this.handlers.clear(); this.commandTable.clear(); }
}
