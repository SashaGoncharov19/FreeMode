// Runs inside the CEF page. gtan.rpc.call(name, args) (T-008) asks the server's handler of that name through the
// resource's client script and resolves with its answer; on an old client without gtan.rpc the page falls back to
// resourceCall("authSubmit", ...) and the client script's "auth:result" event calls showError here.

var mode = "login";

function setMode(newMode) {
    mode = newMode;
    document.getElementById("tab-login").classList.toggle("active", mode === "login");
    document.getElementById("tab-register").classList.toggle("active", mode === "register");
    document.getElementById("confirm-row").classList.toggle("hidden", mode !== "register");
    document.getElementById("submit").textContent = mode === "login" ? "Log in" : "Create account";
    showError("");
}

function setBusy(busy) {
    document.getElementById("submit").disabled = busy;
}

// Called from client.js with the server's reason when a login or registration failed.
function showError(text) {
    document.getElementById("error").textContent = text || "";
    setBusy(false);
}

document.getElementById("tab-login").addEventListener("click", function () { setMode("login"); });
document.getElementById("tab-register").addEventListener("click", function () { setMode("register"); });

document.getElementById("form").addEventListener("submit", function (event) {
    event.preventDefault();

    var name = document.getElementById("name").value.trim();
    var password = document.getElementById("password").value;

    if (!/^[A-Za-z0-9_]{3,20}$/.test(name)) { showError("The name must be 3-20 letters, digits or underscores."); return; }
    if (password.length < 6) { showError("The password must have at least 6 characters."); return; }
    if (mode === "register" && password !== document.getElementById("confirm").value) { showError("The passwords do not match."); return; }

    setBusy(true);
    showError("");

    if (typeof gtan !== "undefined" && gtan.rpc) {
        gtan.rpc.call("auth:" + mode, { name: name, password: password }).then(function (result) {
            // success: the server's "auth:result" event makes client.js close this form
            if (!result || !result.ok) showError(result ? result.message : "No answer.");
        }, function (error) {
            showError(error.code === "timeout" ? "The server did not answer in time." : (error.message || String(error)));
        });
    } else if (typeof resourceCall === "function") {
        resourceCall("authSubmit", mode, name, password);
    } else {
        showError("Not running inside GTA Network.");
    }
});

document.getElementById("name").focus();
