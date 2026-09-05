// Server script in TypeScript against the server API shape (server.d.ts). Today C# resources see this API as a class
// member; the Bun runtime (T-006) exposes the same members from `@gtanetwork/server`. Type-checked here, not executed.

API.onResourceStart.connect(() => {
    API.consoleOutput(LogCat.Info, "ts-resource started");
});

API.onPlayerConnected.connect((player: Client) => {
    API.sendChatMessageToPlayer(player, `~g~ts-resource~w~: welcome, ${player.name}`);
});

API.onChatCommand.connect((sender, command, cancel) => {
    if (command !== "/tsveh") return;
    cancel.Cancel = true;
    const vehicle: Vehicle = API.createVehicle(VehicleHash.Adder, API.getEntityPosition(sender), API.getEntityRotation(sender), 0, 0);
    API.sendChatMessageToPlayer(sender, `spawned vehicle ${vehicle.handle.Value}`);
});

// RPC (T-008): a handler for API.rpc.call("ts:time") from client scripts, and a call into the client's "ts:hello" handler.
API.registerRpc("ts:time", (_sender, _args) => ({ t: Date.now() }));
API.onPlayerConnected.connect((player: Client) => {
    API.callClient(player, "ts:hello", { who: player.name }).then((reply) => API.consoleOutput(`client says ${String(reply)}`));
});

API.onClientEventTrigger.connect((sender, eventName, ...args) => {
    if (eventName !== "ts:ping") return;
    const [x, y, z] = args as [number, number, number];
    API.triggerClientEvent(sender, "ts:pong", `got ${x.toFixed(0)}, ${y.toFixed(0)}, ${z.toFixed(0)}`);
});
