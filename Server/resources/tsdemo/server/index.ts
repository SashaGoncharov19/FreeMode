// A server resource in TypeScript, run by the Bun runtime (runtime/main.ts). The engine passes one `gtan` per resource:
// gtan.api = Server/API.cs over the bridge, gtan.players = the state mirror, gtan.on = engine events, gtan.commands = "/…".
import type { Gtan } from "../../../../runtime/gtan";

export default function main(gtan: Gtan) {
  gtan.on("playerConnected", (player: number) => {
    void gtan.api.sendChatMessageToPlayer(player, "~g~tsdemo~w~: hello from Bun " + Bun.version);
  });

  gtan.commands.register("tsping", (player, ...args) => {
    void gtan.api.sendChatMessageToPlayer(player, "tsdemo: pong " + args.join(" "));
  });

  gtan.commands.register("tspos", async (player) => {
    const p = gtan.player(player);
    const pos = p?.position ?? ((await gtan.api.getEntityPosition(player)) as { x: number; y: number; z: number });
    void gtan.api.sendChatMessageToPlayer(player, `tsdemo: you are at ${pos.x.toFixed(1)}, ${pos.y.toFixed(1)}, ${pos.z.toFixed(1)}`);
  });

  gtan.on("chatMessage", (player: number, message: string) => {
    if (message.trim() !== "tsdemo?") return;
    void gtan.api.sendChatMessageToPlayer(player, "tsdemo: yes, " + gtan.players.size + " player(s) mirrored");
    return { cancel: true };
  });
}
