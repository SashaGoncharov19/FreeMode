// __NAME__: the server half. It runs in the Bun runtime the GTA Network server starts (one Bun process for every TypeScript
// resource); `gtan` is this resource's handle to the engine: gtan.api is the whole server API (types/api.generated.d.ts —
// players and entities are handles, every call returns a Promise), gtan.on the engine's events, gtan.commands the chat
// commands, gtan.rpc request/response calls from client scripts and CEF pages, gtan.players a 10 Hz mirror of the players.
// Edit this file while the server runs: the runtime reloads the resource.
import type { Gtan } from "../types/gtan";

export default function main(gtan: Gtan) {
  const { api } = gtan;

  async function nameOf(player: number): Promise<string> {
    return gtan.player(player)?.name || ((await api.getPlayerName(player)) as string) || "player";
  }

  // "resourceStart" fires once this module is loaded ("serverResourceStart" would fire for every resource the server starts).
  gtan.on("resourceStart", () => {
    void api.consoleOutput("__NAME__ started (TypeScript)");
  });

  gtan.on("playerConnected", async (player: number) => {
    void api.sendChatMessageToPlayer(player, "~g~__NAME__~w~: welcome, " + (await nameOf(player)) + ". Try ~y~/hello~w~ and ~y~/panel~w~.");
  });

  gtan.commands.register("hello", async (player) => {
    void api.sendChatMessageToPlayer(player, "__NAME__: hello, " + (await nameOf(player)));
  });

  // /panel is handled by the client script (client/index.ts); this registration keeps the server from answering "Command not found".
  gtan.commands.register("panel", () => {});

  // A request/response call (T-008): the CEF page asks through gtan.rpc.call("__NAME__:time"), the answer resolves its Promise.
  gtan.rpc.register("__NAME__:time", () => ({ t: Date.now() }));
}
