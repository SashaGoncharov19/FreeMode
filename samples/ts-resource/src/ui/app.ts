// A CEF page script: the bridge the browser host injects (cef.d.ts). One-way calls into the owning client script.
const button = document.getElementById("send") as HTMLButtonElement | null;
button?.addEventListener("click", () => {
    resourceCall("uiClicked", Date.now());
    gtan.eval("API.sendChatMessage('ui button clicked')");
});

// RPC (T-008): a request/response call answered by the client script's handler or the server's.
document.getElementById("ping")?.addEventListener("click", async () => {
    try {
        const reply = await gtan.rpc.call<{ t: number }>("freeroam:ping", { from: "ui" });
        console.log("server time", reply.t);
    } catch (error) {
        const rpcError = error as RpcError;
        console.error("rpc failed", rpcError.code, rpcError.message);
    }
});
