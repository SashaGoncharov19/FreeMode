// The client feeds this page through gtanLoader.update(state) (Client/GUI/ConnectLoader.cs); state = { server, stage,
// detail, label, index, total, elapsed }. gtanLoader.hide() fades the page out before the client closes the browser.
(function () {
  var el = function (id) { return document.getElementById(id); };
  var stages = { connecting: "Connecting", connected: "Connected", downloading: "Downloading files", starting: "Starting scripts" };
  var startedAt = Date.now();
  var timer = setInterval(function () {
    var s = Math.round((Date.now() - startedAt) / 1000);
    el("elapsed").textContent = s + " s";
  }, 1000);

  window.gtanLoader = {
    update: function (state) {
      if (!state) return;
      if (state.server) el("server").textContent = state.server;
      el("stage").textContent = stages[state.stage] || state.stage || "";
      el("detail").textContent = state.detail || "";
      var pct = state.total > 0 ? Math.min(100, Math.round(100 * state.index / state.total)) : (state.stage === "connected" ? 10 : state.stage === "connecting" ? 3 : 0);
      el("bar").style.width = pct + "%";
    },
    hide: function () {
      clearInterval(timer);
      el("loader").classList.add("hidden");
    }
  };

  // tell the client the page is ready for updates (the client pushes the current state)
  if (typeof resourceCall === "function") resourceCall("loader:ready");
})();
