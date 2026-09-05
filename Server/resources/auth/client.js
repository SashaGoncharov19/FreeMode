// auth: client side. Shows the login/registration form (ui/index.html) in a CEF browser. The page itself calls the
// server's RPC handlers "auth:login" / "auth:register" (gtan.rpc.call, T-008) and shows the answer; this script
// closes the form when the server reports success through the "auth:result" event (also sent for /login and
// /register in chat). authSubmit stays for pages of older clients that only have resourceCall.

var authBrowser = null;

function authOpen() {
    if (authBrowser !== null) return;

    var screen = API.getScreenResolution();
    var width = 420;
    var height = 480;

    authBrowser = API.createCefBrowser(width, height, true);
    API.waitUntilCefBrowserInit(authBrowser);
    API.setCefBrowserPosition(authBrowser, (screen.Width - width) / 2, (screen.Height - height) / 2);
    API.loadPageCefBrowser(authBrowser, "ui/index.html");
    // Global switch of the CEF overlay (browsers and the cursor). Newer clients turn it on in createCefBrowser
    // already; older ones draw nothing without this call. Left on in authClose: other resources may show browsers too.
    API.setCefDrawState(true);
    API.showCursor(true);
    API.setCanOpenChat(false);
}

function authClose() {
    if (authBrowser === null) return;

    API.destroyCefBrowser(authBrowser);
    authBrowser = null;
    API.showCursor(false);
    API.setCanOpenChat(true);
}

// Called by the page through resourceCall("authSubmit", mode, name, password).
function authSubmit(mode, name, password) {
    API.triggerServerEvent(mode === "register" ? "auth:register" : "auth:login", name, password);
}

API.onResourceStart.connect(function () {
    authOpen();
});

API.onResourceStop.connect(function () {
    authClose();
});

API.onServerEventTrigger.connect(function (eventName, args) {
    if (eventName !== "auth:result") return;

    var ok = args[0];
    var message = args[1];

    if (ok) {
        authClose();
        API.sendNotification("~g~" + message);
    } else if (authBrowser !== null) {
        authBrowser.call("showError", message);
    }
});
