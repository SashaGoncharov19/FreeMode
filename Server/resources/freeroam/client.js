// Client-side script. Streamed to every player and executed by the in-game JavaScript engine (V8).
// "API" is the client scripting API (Client/Javascript/JavascriptHook.cs).

API.onResourceStart.connect(function () {
    API.sendNotification("~g~freeroam~w~ client script loaded. Type ~y~/help");
});

API.onServerEventTrigger.connect(function (eventName, args) {
    if (eventName === "freeroam:shard") {
        API.showShard(args[0], 5000);
    }
});

API.onChatCommand.connect(function (command, cancel) {
    // Client-side command: /ping asks the server for the round trip time.
    if (command === "/ping") {
        API.triggerServerEvent("freeroam:ping");
        cancel.Cancel = true;
    }
});

API.onPlayerEnterVehicle.connect(function (vehicle) {
    API.displaySubtitle("~g~Enjoy the ride!", 2500);
});
