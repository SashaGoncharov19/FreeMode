// A CEF page script: the bridge the browser host injects (cef.d.ts). One-way calls into the owning client script.
const button = document.getElementById("send") as HTMLButtonElement | null;
button?.addEventListener("click", () => {
    resourceCall("uiClicked", Date.now());
    gtan.eval("API.sendChatMessage('ui button clicked')");
});
