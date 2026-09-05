// The page script. gtan.rpc.call(name, args) (injected by the browser host, see types/cef.d.ts) is answered by the owning
// client script's handler when it registered the name (API.rpc.register), otherwise by the server's (gtan.rpc.register).
document.getElementById("ask").addEventListener("click", function () {
    gtan.rpc.call("__NAME__:time").then(function (reply) {
        document.getElementById("time").textContent = "Server time: " + new Date(reply.t).toLocaleTimeString();
    }, function (error) {
        document.getElementById("time").textContent = "Failed: " + error.code + " " + error.message;
    });
});

document.getElementById("close").addEventListener("click", function () {
    gtan.rpc.call("__NAME__:close");
});
