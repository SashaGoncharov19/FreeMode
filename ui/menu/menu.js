// The main menu page. The client (Client/GUI/CefMenu.cs) pushes its whole state with gtanMenu.update(state) —
// { version, status, servers: [{address, name, gamemode, map, players, maxPlayers, passworded, source, online, favorite, recent}],
// settings: {...} } — and gets our actions through resourceCall("menu:<action>", ...): ready, refresh, connect(host, port, password),
// favorite(address, "true"|"false"), forget(address), settings(json), native, quit. gtanMenu.hide() fades the page before the browser closes.
(function () {
  var state = { version: "", status: "", servers: [], settings: null };
  var selected = null;
  var settingsLoaded = false;
  var el = function (id) { return document.getElementById(id); };
  function call() { if (typeof resourceCall === "function") resourceCall.apply(null, arguments); }
  function esc(s) { return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) { return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]; }); }
  function where(row) { return row.source === "lan" ? "LAN" : row.source === "verified" ? "Verified" : row.source === "internet" ? "Internet" : row.favorite ? "Favourite" : row.recent ? "Recent" : ""; }

  // ---- tabs ----
  Array.prototype.forEach.call(document.querySelectorAll(".tab"), function (tab) {
    tab.addEventListener("click", function () {
      Array.prototype.forEach.call(document.querySelectorAll(".tab"), function (t) { t.classList.toggle("active", t === tab); });
      Array.prototype.forEach.call(document.querySelectorAll(".panel"), function (p) { p.classList.toggle("active", p.id === "tab-" + tab.getAttribute("data-tab")); });
    });
  });

  // ---- server list ----
  function visibleServers() {
    var filter = el("filter").value.trim().toLowerCase();
    var onlyOnline = el("only-online").checked;
    return state.servers.filter(function (row) {
      if (onlyOnline && !row.online && !row.favorite && !row.recent) return false;
      if (!filter) return true;
      return (row.name + " " + row.address + " " + row.gamemode + " " + row.map).toLowerCase().indexOf(filter) >= 0;
    });
  }

  function renderList() {
    var rows = visibleServers();
    var list = el("list");
    if (rows.length === 0) {
      list.innerHTML = '<div class="empty">' + (state.servers.length === 0 ? "No servers yet — a LAN server answers within a second; favourites and recent servers are listed even when they are down." : "Nothing matches the filter.") + "</div>";
    } else {
      list.innerHTML = rows.map(function (row) {
        var players = row.online ? row.players + " / " + row.maxPlayers : "—";
        var mode = row.online ? esc(row.gamemode) + (row.map ? " (" + esc(row.map) + ")" : "") : "no answer";
        return '<div class="row' + (row.online ? "" : " offline") + (selected === row.address ? " selected" : "") + '" data-address="' + esc(row.address) + '">' +
          '<div><div class="name">' + (row.passworded ? '<span class="lock" title="password">&#128274;</span>' : "") + esc(row.name) + '</div><div class="addr">' + esc(row.address) + "</div></div>" +
          '<div class="mode">' + mode + "</div>" +
          '<div class="players">' + players + "</div>" +
          '<div class="where">' + esc(where(row)) + "</div>" +
          '<button class="star' + (row.favorite ? " on" : "") + '" data-fav="' + esc(row.address) + '" title="' + (row.favorite ? "Remove from favourites" : "Add to favourites") + '">&#9733;</button>' +
          "</div>";
      }).join("");
    }
    var row = findSelected();
    el("connect-selected").disabled = !row;
    el("row-password").classList.toggle("hidden", !(row && row.passworded));
    el("selected").textContent = row
      ? row.name + " — " + row.address + (row.online ? " · " + row.players + " of " + row.maxPlayers + " players" : " · no answer yet") + (row.passworded ? " · password protected" : "")
      : "Select a server, then Connect (or double-click it). Favourites are kept in settings.xml.";
  }

  function findSelected() {
    for (var i = 0; i < state.servers.length; i++) if (state.servers[i].address === selected) return state.servers[i];
    return null;
  }

  function connectTo(row, password) {
    var parts = row.address.split(":");
    var port = parts.length > 1 ? parts[parts.length - 1] : "4499";
    var host = parts.length > 1 ? parts.slice(0, -1).join(":") : row.address;
    if (row.passworded && !password) { el("row-password").classList.remove("hidden"); el("row-password").focus(); setStatus("This server needs a password."); return; }
    setStatus("Connecting to " + row.address + "…");
    call("menu:connect", host, port, password || "");
  }

  el("list").addEventListener("click", function (event) {
    var star = event.target.closest ? event.target.closest("[data-fav]") : null;
    if (star) {
      var address = star.getAttribute("data-fav");
      var row = state.servers.filter(function (r) { return r.address === address; })[0];
      call("menu:favorite", address, row && row.favorite ? "false" : "true");
      event.stopPropagation();
      return;
    }
    var rowEl = event.target.closest ? event.target.closest(".row") : null;
    if (!rowEl) return;
    selected = rowEl.getAttribute("data-address");
    renderList();
  });
  el("list").addEventListener("dblclick", function (event) {
    var rowEl = event.target.closest ? event.target.closest(".row") : null;
    if (!rowEl || (event.target.closest && event.target.closest("[data-fav]"))) return;
    selected = rowEl.getAttribute("data-address");
    var row = findSelected();
    if (row) connectTo(row, el("row-password").value);
  });
  el("connect-selected").addEventListener("click", function () { var row = findSelected(); if (row) connectTo(row, el("row-password").value); });
  el("row-password").addEventListener("keydown", function (event) { if (event.key === "Enter") { var row = findSelected(); if (row) connectTo(row, el("row-password").value); } });
  el("filter").addEventListener("input", renderList);
  el("only-online").addEventListener("change", renderList);
  el("refresh").addEventListener("click", function () { setStatus("Searching for servers…"); call("menu:refresh"); });

  // ---- direct connect ----
  el("direct").addEventListener("submit", function (event) {
    event.preventDefault();
    var host = el("dc-host").value.trim();
    if (!host) { el("dc-host").focus(); return; }
    setStatus("Connecting to " + host + "…");
    call("menu:connect", host, el("dc-port").value.trim() || "4499", el("dc-password").value);
  });

  // ---- settings ----
  function fillSettings(s) {
    if (!s) return;
    var form = el("settings");
    Object.keys(s).forEach(function (key) {
      var input = form.elements[key];
      if (!input) return;
      if (input.type === "checkbox") input.checked = !!s[key]; else input.value = s[key] == null ? "" : s[key];
    });
    settingsLoaded = true;
  }
  el("settings").addEventListener("submit", function (event) {
    event.preventDefault();
    var form = el("settings"), values = {};
    Array.prototype.forEach.call(form.elements, function (input) {
      if (!input.name) return;
      if (input.type === "checkbox") values[input.name] = input.checked;
      else if (input.type === "number") values[input.name] = parseInt(input.value, 10) || 0;
      else values[input.name] = input.value;
    });
    call("menu:settings", JSON.stringify(values));
  });

  // ---- footer ----
  el("native").addEventListener("click", function () { call("menu:native"); });
  el("quit").addEventListener("click", function () { if (window.confirm("Quit GTA Network and return to the desktop?")) call("menu:quit"); });
  function setStatus(text) { el("status").textContent = text || ""; }

  window.gtanMenu = {
    update: function (s) {
      if (!s) return;
      var settingsChanged = !settingsLoaded || JSON.stringify(s.settings) !== JSON.stringify(state.settings);
      state = s;
      el("version").textContent = s.version ? "GTAN " + s.version : "";
      setStatus(s.status);
      renderList();
      if (settingsChanged) fillSettings(s.settings);
    },
    hide: function () { document.body.classList.add("hidden"); }
  };

  renderList();
  call("menu:ready");
})();
