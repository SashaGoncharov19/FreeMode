// What a TypeScript server resource gets: `export default function main(gtan: Gtan) { ... }` receives one Gtan per resource.
// gtan.api.<member>(...) is the server API (Server/API.cs) over the bridge — members that return a value resolve a Promise,
// the rest are fire-and-forget; gtan.players is the 10 Hz state mirror; gtan.on(event, handler) subscribes to the engine's
// events (the names of Server/API.cs without the "on" prefix); gtan.commands.register(name, handler) handles "/name ...";
// gtan.rpc.register(name, handler) answers API.rpc.call(name, args) from client scripts (T-008).
import type { Bridge } from "../bridge";
import { players, type PlayerState } from "../state";
import type { ServerApi } from "./api.generated";
import * as Enums from "./enums.generated";
import { enumName, parseEnum } from "./enums";

export { parseEnum, enumName };
export type { EnumTable } from "./enums.generated";

export type EventHandler = (...args: any[]) => unknown | Promise<unknown>;
export type CommandHandler = (player: number, ...args: string[]) => unknown | Promise<unknown>;
/** An RPC handler: the caller's handle and the arguments as sent (any JSON value); return a JSON-serialisable value or a Promise of one;
 * throw to fail the call (an Error with a `code` chooses the code the caller sees, default "handler"). */
export type RpcHandler = (player: number, args: any) => unknown | Promise<unknown>;
export interface RpcOptions {
  /** Called before the handler: false answers the caller with the code "denied" without running it. */
  allow?: (player: number) => boolean | Promise<boolean>;
}
/** A failed gtan.rpc.callClient: `code` is timeout | unknown | handler | size | invalid | disconnected. */
export class RpcError extends Error {
  constructor(readonly code: string, message: string) { super(message); this.name = "RpcError"; }
}

interface Catalogue { functions: { name: string; needsResult: boolean }[]; }

export class Gtan {
  /** The server API (Server/API.cs) over the bridge; typed by api.generated.d.ts, every member returns a Promise. */
  readonly api: ServerApi;
  readonly players: ReadonlyMap<number, PlayerState> = players;
  readonly commands: { register(name: string, handler: CommandHandler, options?: { aliases?: string[] }): void; unregister(name: string): void };
  /** The enums the server API uses, as runtime tables: gtan.enums.VehicleHash.Adder, gtan.enums.WeaponHash.CarbineRifle, ... */
  readonly enums = Enums;
  /** Request/response calls (T-008). Names are global across resources: prefix them with the resource name ("shop:buy"). */
  readonly rpc: {
    /** Answers API.rpc.call(name, args) from client scripts and gtan.rpc.call from their CEF pages. Registering a name again replaces the handler. */
    register(name: string, handler: RpcHandler, options?: RpcOptions): void;
    unregister(name: string): void;
    /** Calls a handler the player's client script registered with API.rpc.register(name, handler); rejects with an RpcError. Default timeout 10 s, at most 60 s. */
    callClient<T = unknown>(player: number, name: string, args?: unknown, timeoutMs?: number): Promise<T>;
  };
  private handlers = new Map<string, Set<EventHandler>>();
  private commandTable = new Map<string, CommandHandler>();
  private rpcTable = new Map<string, { handler: RpcHandler; options?: RpcOptions }>();
  private needsResult: Map<string, boolean>;
  private disposed = false;
  private disposedCallsLogged = 0;

  constructor(private bridge: Bridge, readonly resource: string, readonly dir: string, readonly settings: Record<string, string>, catalogue: Catalogue | null) {
    this.needsResult = new Map((catalogue?.functions ?? []).map((f) => [f.name, f.needsResult]));
    const self = this;
    this.api = new Proxy({} as ServerApi, {
      get(_t, prop) {
        if (typeof prop !== "string") return undefined;
        return (...args: unknown[]) => {
          if (self.disposed) {
            // the engine unloaded this resource (stop, or a gamemode replaced it) while code was still running: drop the call
            if (self.disposedCallsLogged++ < 3) self.bridge.log(self.resource, "warn", "api." + prop + " called after the resource was unloaded; ignored");
            return Promise.resolve(undefined);
          }
          return self.bridge.call(self.resource + "/" + prop, args, self.needsResult.get(prop) ?? true);
        };
      },
    });
    this.commands = {
      register: (name, handler, options) => { this.commandTable.set(name.toLowerCase(), handler); for (const a of options?.aliases ?? []) this.commandTable.set(a.toLowerCase(), handler); },
      unregister: (name) => { this.commandTable.delete(name.toLowerCase()); },
    };
    this.rpc = {
      register: (name, handler, options) => {
        if (typeof handler !== "function") throw new Error("rpc.register: the handler must be a function");
        this.rpcTable.set(name, { handler, options });
        void this.bridge.call(this.resource + "/registerRpc", [name], false); // the engine routes the name to this resource
      },
      unregister: (name) => {
        this.rpcTable.delete(name);
        void this.bridge.call(this.resource + "/unregisterRpc", [name], false);
      },
      callClient: async (player, name, args, timeoutMs) => {
        try {
          return (await this.bridge.call(this.resource + "/callClient", [player, name, args ?? null, timeoutMs ?? 10000], true)) as any;
        } catch (e) {
          // the engine's error is "code: message" (Server/Runtime/RuntimeBridge.cs CompleteLater)
          const m = /^([a-z]+): ([\s\S]*)$/.exec((e as Error).message ?? "");
          throw m ? new RpcError(m[1], m[2]) : new RpcError("handler", String((e as Error).message ?? e));
        }
      },
    };
  }

  /** The mirrored state of a player (by handle), or undefined when the engine has not sent it yet. */
  player(handle: number): PlayerState | undefined { return players.get(handle); }

  /** "adder" | "Adder" | "-1216765807" → the enum value, or undefined: `gtan.parseEnum(gtan.enums.VehicleHash, args[0])`. */
  parseEnum(table: Enums.EnumTable, text: string | number | undefined | null): number | undefined { return parseEnum(table, text); }
  /** The canonical member name of a value ("Adder"), for messages. */
  enumName(table: Enums.EnumTable, value: number): string { return enumName(table, value); }

  on(event: string, handler: EventHandler) { let set = this.handlers.get(event); if (!set) { set = new Set(); this.handlers.set(event, set); } set.add(handler); }
  off(event: string, handler: EventHandler) { this.handlers.get(event)?.delete(handler); }

  log(...parts: unknown[]) { this.bridge.log(this.resource, "info", parts.map(String).join(" ")); }
  warn(...parts: unknown[]) { this.bridge.log(this.resource, "warn", parts.map(String).join(" ")); }

  /** Called by the runtime for every event of this resource; returns { cancel } for cancelable events and the answer of an rpcRequest. */
  async dispatch(event: string, args: unknown[]): Promise<Record<string, unknown>> {
    if (event === "rpcRequest") return this.answerRpc(args as [number, string, string | null]);
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

  /** An RPC request from a client (through the engine): { ok: true, value } or { ok: false, code, message }. */
  private async answerRpc([player, name, json]: [number, string, string | null]): Promise<Record<string, unknown>> {
    const entry = this.rpcTable.get(name);
    if (!entry) return { ok: false, code: "unknown", message: "no handler for " + name };
    try {
      if (entry.options?.allow && !(await entry.options.allow(player))) return { ok: false, code: "denied", message: "not allowed" };
      const value = await entry.handler(player, json == null || json === "" ? undefined : JSON.parse(json));
      return { ok: true, value: value === undefined ? null : value };
    } catch (e) {
      const err = e as { code?: unknown; message?: unknown; stack?: string };
      this.warn("rpc " + name + " failed: " + (err?.stack ?? String(e)));
      return { ok: false, code: typeof err?.code === "string" ? err.code : "handler", message: String(err?.message ?? e) };
    }
  }

  /** True once the engine unloaded this resource; api calls are dropped from then on. */
  get isDisposed() { return this.disposed; }

  /** Unload: forget handlers, commands and RPC handlers (the module instance is dropped by the loader); later api calls are ignored. */
  dispose() { this.disposed = true; this.handlers.clear(); this.commandTable.clear(); this.rpcTable.clear(); }
}
