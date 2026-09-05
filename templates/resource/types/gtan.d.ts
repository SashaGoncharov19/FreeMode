// The `gtan` object a TypeScript server resource receives: the public surface of `class Gtan` in runtime/gtan/index.ts of the
// GTA Network repository, kept in step with it by hand (the two generated files next to this one come from the server build).
// Copied into every resource by `gtanetwork create`, so a resource folder type-checks on its own.
import type { ServerApi } from "./api.generated";
import type * as Enums from "./enums.generated";

export type Vec3 = { x: number; y: number; z: number };
/** One player as the engine mirrors it 10 times a second (position, health, vehicle, ...). */
export interface PlayerState {
  handle: number; name: string; position: Vec3; rotation: Vec3; health: number; armor: number; dimension: number;
  vehicle: number; seat: number; model: number; dead: boolean;
}
export type EventHandler = (...args: any[]) => unknown | Promise<unknown>;
export type CommandHandler = (player: number, ...args: string[]) => unknown | Promise<unknown>;
/** An RPC handler: the caller's handle and the arguments as sent (any JSON value); return a JSON-serialisable value or a Promise;
 * throw to fail the call (an Error with a `code` chooses the code the caller sees, default "handler"). */
export type RpcHandler = (player: number, args: any) => unknown | Promise<unknown>;
export interface RpcOptions {
  /** Called before the handler: false answers the caller with the code "denied" without running it. */
  allow?: (player: number) => boolean | Promise<boolean>;
}
export type EnumTable = Readonly<Record<string, number>>;

export interface Gtan {
  /** The server API (Server/API.cs) over the bridge; every member returns a Promise; players and entities are handles. */
  readonly api: ServerApi;
  readonly players: ReadonlyMap<number, PlayerState>;
  readonly resource: string;
  readonly dir: string;
  readonly settings: Record<string, string>;
  /** True once the engine unloaded this resource (stop, or a gamemode replaced it); api calls are dropped from then on. */
  readonly isDisposed: boolean;
  /** The enums the server API uses, as runtime tables: gtan.enums.VehicleHash.Adder, gtan.enums.WeaponHash.CarbineRifle, ... */
  readonly enums: typeof Enums;
  readonly commands: {
    register(name: string, handler: CommandHandler, options?: { aliases?: string[] }): void;
    unregister(name: string): void;
  };
  readonly rpc: {
    /** Answers API.rpc.call(name, args) from client scripts and gtan.rpc.call from their CEF pages. Registering a name again replaces the handler. */
    register(name: string, handler: RpcHandler, options?: RpcOptions): void;
    unregister(name: string): void;
    /** Calls a handler the player's client script registered with API.rpc.register(name, handler); rejects with an Error whose `code` names the failure. */
    callClient<T = unknown>(player: number, name: string, args?: unknown, timeoutMs?: number): Promise<T>;
  };
  /** The mirrored state of a player (by handle), or undefined when the engine has not sent it yet. */
  player(handle: number): PlayerState | undefined;
  /** "adder" | "Adder" | "-1216765807" → the enum value, or undefined. */
  parseEnum(table: EnumTable, text: string | number | undefined | null): number | undefined;
  /** The canonical member name of a value ("Adder"), for messages. */
  enumName(table: EnumTable, value: number): string;
  /** Subscribe to an engine event: the names of Server/API.cs without the "on" prefix ("playerConnected", "chatMessage", ...). */
  on(event: string, handler: EventHandler): void;
  off(event: string, handler: EventHandler): void;
  log(...parts: unknown[]): void;
  warn(...parts: unknown[]): void;
}
