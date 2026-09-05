// __NAME__: the client half. The server bundles this file with Bun at resource start (imports resolved, types erased, one
// IIFE) and the in-game engine (ClearScript V8) runs it; `API` is the client scripting API (types/client.d.ts). A bundled
// script has no global functions, so CEF pages talk to it through API.rpc (registered below), not resourceCall(name).
let panel: Browser | null = null;

function openPanel() {
    if (panel) return;
    const screen = API.getScreenResolution();
    panel = API.createCefBrowser(420, 300, true);
    API.waitUntilCefBrowserInit(panel);
    API.setCefBrowserPosition(panel, (screen.Width - 420) / 2, (screen.Height - 300) / 2);
    API.loadPageCefBrowser(panel, "ui/index.html");
    API.setCefDrawState(true);
    API.showCursor(true);
    API.setCanOpenChat(false);
}

function closePanel() {
    if (!panel) return;
    API.destroyCefBrowser(panel);
    panel = null;
    API.showCursor(false);
    API.setCanOpenChat(true);
}

API.onResourceStart.connect(() => {
    API.sendNotification("~g~__NAME__~w~ client script started. Type ~y~/panel");
});

API.onResourceStop.connect(() => closePanel());

// Chat commands reach the client script before the server; server/index.ts registers /panel too, so the server stays quiet.
API.onChatCommand.connect((msg: string) => {
    if (msg === "/panel") openPanel();
});

// The page calls gtan.rpc.call("__NAME__:close"): handled here. "__NAME__:time" has no client handler and goes to the server.
API.rpc.register("__NAME__:close", () => {
    closePanel();
    return true;
});
