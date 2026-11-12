// Pont entre l'interface de Nur Live (chargée depuis ton dossier Nur Live) et LiteBrowser.
// L'interface parle à « window.nur » comme dans l'application Nur Live ; ici, c'est le navigateur
// qui répond : statut des lives, matchs M8, notifications, compte Twitch, sauvegarde de l'état.
(function () {
  if (location.hostname !== "nurlive.home" || window.nur || !window.chrome || !chrome.webview) return;

  // ---------- Modules de Nur Live (core/sources.js et core/model.js), insérés par le navigateur ----------
  const mods = {};
  const require = (name) => mods[String(name).replace(/^\.\//, "").replace(/\.js$/, "")];
  mods.sources = (function () {
    const module = { exports: {} };
    const exports = module.exports;
    /*__SOURCES__*/
    return module.exports;
  })();
  mods.model = (function () {
    const module = { exports: {} };
    const exports = module.exports;
    /*__MODEL__*/
    return module.exports;
  })();
  const Model = mods.model;
  const Sources = mods.sources;

  // ---------- État ----------
  let state = Model.defaultState();
  const data = {
    status: {}, statusAt: 0, matches: [], tournaments: [], events: [], matchesAt: 0, matchErrors: [],
    matchesBusy: false, games: [], auth: { loggedIn: false, user: "" }, pinned: false, fullscreen: false,
  };
  const listeners = { snapshot: [], toast: [], auth: [], visible: [] };
  const snap = () => Object.assign({ state, port: 0, visible: true }, data);
  const post = (m) => chrome.webview.postMessage(m);
  const emit = () => {
    const s = snap();
    for (const fn of listeners.snapshot) { try { fn(s); } catch (e) { console.error(e); } }
  };
  const on = (kind) => (fn) => {
    listeners[kind].push(fn);
    return () => { listeners[kind] = listeners[kind].filter((x) => x !== fn); };
  };

  let saveTimer = 0;
  function save() {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => post({ type: "nlSave", state }), 250);
  }

  function dispatch(a) {
    const before = state;
    state = Model.reduce(state, a || {});
    if (state !== before) {
      save();
      if (a && a.type === "addSources") rpc("refresh");
    }
    return snap();
  }

  // ---------- Appels au navigateur (réponse asynchrone) ----------
  let seq = 0;
  const pending = new Map();
  function rpc(method, args) {
    return new Promise((resolve) => {
      const id = ++seq;
      pending.set(id, resolve);
      post({ type: "nlRpc", id, method, args: args || [] });
      setTimeout(() => { if (pending.has(id)) { pending.delete(id); resolve(null); } }, 30000);
    });
  }

  // « Regarder » : mêmes réglages que Nur Live (lives côte à côte + chat)
  function watch(w) {
    const ids = (w.ids || []).filter((x) => typeof x === "string" && x.includes(":"));
    if (!ids.length) return;
    const list = ids.map((id) => {
      const [platform, key] = id.split(":");
      const known = state.sources.find((s) => Sources.sourceId(s) === id);
      return known || { platform, key, label: key === "gentlemates" ? "Gentle Mates" : key };
    });
    state = Model.reduce(state, { type: "addSources", sources: list });
    const sids = list.map(Sources.sourceId);
    state = Model.reduce(state, { type: "setActive", ids: sids, focus: sids[0] });
    state = Model.reduce(state, { type: "layout", value: sids.length > 1 ? "side" : "focus" });
    if (w.chat !== false) {
      state = Model.reduce(state, { type: "chat", open: true, ids: sids.slice(0, 2) });
      state = Model.reduce(state, { type: "chat", tab: sids[0] });
    }
    if (w.matchId) state = Model.reduce(state, { type: "ui", patch: { matchId: w.matchId } });
    save();
    rpc("refresh");
    if (w.toast) for (const fn of listeners.toast) fn(w.toast);
  }

  let initDone;
  const ready = new Promise((r) => { initDone = r; });
  const keys = Object.keys(data);

  chrome.webview.addEventListener("message", (e) => {
    const d = e.data || {};
    switch (d.type) {
      case "nlInit":
        state = Model.sanitize(d.state || {});
        for (const k of keys) if (d[k] !== undefined) data[k] = d[k];
        if (d.watch) watch(d.watch);
        initDone(snap());
        emit();
        break;
      case "nlData":
        for (const k of keys) if (d[k] !== undefined) data[k] = d[k];
        if (d.state) state = Model.sanitize(d.state);
        emit();
        break;
      case "nlWatch": watch(d); emit(); break;
      case "nlAuth":
        data.auth = d.auth || data.auth;
        for (const fn of listeners.auth) fn(data.auth);
        emit();
        break;
      case "nlToast": for (const fn of listeners.toast) fn({ title: d.title || "", body: d.body || "" }); break;
      case "nlVisible": for (const fn of listeners.visible) fn(!!d.visible); break;
      case "nlRpcResult": {
        const f = pending.get(d.id);
        if (f) { pending.delete(d.id); f(d.result); }
        break;
      }
    }
  });

  window.nur = {
    get: () => ready.then(() => snap()),
    act: (a) => Promise.resolve(dispatch(a)),
    add: async (raw, start) => {
      const parts = Sources.splitEntries(raw).slice(0, 20);
      const found = [], bad = [];
      for (const p of parts) {
        let s = null;
        try { s = await Sources.parseEntry(p, async () => null); } catch (_) { s = null; }
        if (s) found.push(s); else bad.push(p);
      }
      if (found.length) dispatch({ type: "addSources", sources: found, start: !!start });
      return { snapshot: snap(), added: found.map(Sources.sourceId), bad };
    },
    search: async (q) => { const r = await rpc("search", [String(q || "")]); return Array.isArray(r) ? r : []; },
    refresh: async () => { await rpc("refresh"); return snap(); },
    matches: async (force) => { await rpc("matches", [force !== false]); return snap(); },
    watchMatch: async (id) => {
      const r = await rpc("pickMatch", [id]);
      if (r && r.ids && r.ids.length) watch({ ids: r.ids, matchId: id, toast: r.toast });
      return snap();
    },
    watchEvent: async (id) => {
      const r = await rpc("pickEvent", [id]);
      if (r && r.ids && r.ids.length) watch({ ids: r.ids });
      return snap();
    },
    inBrowser: true, // dans LiteBrowser : pas de réglages Windows (démarrage, horloge) propres à l'appli seule
    testAlert: () => rpc("testAlert"),
    probe: (resumeIds, muteIds) => rpc("probe", [resumeIds || [], muteIds || []]),
    log: (line) => rpc("log", [String(line)]),
    open: (url) => rpc("open", [url]),
    pin: async () => { data.pinned = !!(await rpc("pin")); return snap(); },
    fullscreen: async (v) => { data.fullscreen = !!(await rpc("fullscreen", [v === undefined ? null : v])); emit(); return true; },
    openLogs: () => rpc("openLogs"),
    twitchLogin: async () => { const a = await rpc("twitchLogin"); if (a) data.auth = a; return snap(); },
    twitchLogout: async () => {
      const a = await rpc("twitchLogout");
      if (a) { data.auth = a; for (const fn of listeners.auth) fn(a); }
      return snap();
    },
    onSnapshot: on("snapshot"),
    onToast: on("toast"),
    onAuth: on("auth"),
    onVisible: on("visible"),
  };

  post({ type: "nlInit" });
})();
