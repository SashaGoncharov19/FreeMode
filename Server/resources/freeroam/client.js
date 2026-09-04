// Client-side script. Streamed to every player and executed by the in-game JavaScript engine (V8).
// "API" is the client scripting API (Client/Javascript/JavascriptHook.cs).

API.onResourceStart.connect(function () {
    API.sendNotification("~g~freeroam~w~ client script loaded. Type ~y~/help");
    // Client -> server event: the server answers in chat, which proves the whole JS chain works.
    API.triggerServerEvent("freeroam:clientReady");
});

API.onServerEventTrigger.connect(function (eventName, args) {
    if (eventName === "freeroam:shard") {
        API.showShard(args[0], 5000);
    }
});

// Fired with the full chat line ("/ping", "/veh adder") before it is sent to the server; it cannot be
// cancelled here, so every client-side command also needs a server-side [Command] (see freeroam.cs).
API.onChatCommand.connect(function (msg) {
    if (msg === "/ping") {
        API.sendNotification("~b~ping~w~ sent, the server answers in chat");
    }
});

API.onPlayerEnterVehicle.connect(function (vehicle) {
    API.displaySubtitle("~g~Enjoy the ride!", 2500);
});
