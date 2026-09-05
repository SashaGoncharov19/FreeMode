// Client-side script in TypeScript. The server bundles it with Bun at resource start (T-005) into the one JavaScript text the
// in-game engine (V8) runs, exactly like the client.js it replaces; `API` is the client scripting API (types/client.d.ts).
// tsconfig.json next to this resource type-checks it in an editor or with `bun x tsc --noEmit -p tsconfig.json`.

API.onResourceStart.connect(() => {
    API.sendNotification("~g~freeroam~w~ client script loaded. Type ~y~/help");
    // Client -> server event: the server answers in chat, which proves the whole chain works.
    API.triggerServerEvent("freeroam:clientReady");
});

API.onServerEventTrigger.connect((eventName: string, args: unknown[]) => {
    if (eventName === "freeroam:shard") API.showShard(String(args[0]), 5000);
});

// Fired with the full chat line ("/ping", "/veh adder") before it is sent to the server; it cannot be cancelled here,
// so every client-side command also needs a server-side [Command] (see freeroam.cs).
API.onChatCommand.connect((msg: string) => {
    if (msg === "/ping") API.sendNotification("~b~ping~w~ sent, the server answers in chat");
});

API.onPlayerEnterVehicle.connect((_vehicle: LocalHandle) => {
    API.displaySubtitle("~g~Enjoy the ride!", 2500);
});
