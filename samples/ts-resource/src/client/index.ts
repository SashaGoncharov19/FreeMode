// Client script in TypeScript. It runs in the game (ClearScript V8) exactly like a client.js: `API` is the ScriptContext of
// this script, events are .NET events (`connect`), `Keys`/`Vector3` are host types. Bundled by the server (T-005).

API.onResourceStart.connect(() => {
    API.sendChatMessage("~g~ts-resource~w~: client script started");
});

API.onKeyDown.connect((_sender, e) => {
    if (e.KeyCode !== Keys.F9 || API.isChatOpen()) return;
    const player: LocalHandle = API.getLocalPlayer();
    const pos: Vector3 = API.getEntityPosition(player);
    API.sendChatMessage(`position ${pos.X.toFixed(1)} ${pos.Y.toFixed(1)} ${pos.Z.toFixed(1)}`);
    API.triggerServerEvent("ts:ping", pos.X, pos.Y, pos.Z);
});

API.onServerEventTrigger.connect((eventName, args) => {
    if (eventName === "ts:pong") API.sendNotification(`server: ${String(args[0])}`);
});
