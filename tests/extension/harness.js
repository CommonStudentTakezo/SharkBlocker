// Carrega o background.js da extensão num sandbox (sem navegador) e expõe evaluateUrl.
const vm = require("vm");
const fs = require("fs");
const path = require("path");

function loadExtension(dir) {
  const ctxCalls = { fetch: [], tabsUpdate: [] };
  const ctx = {
    console, URL, URLSearchParams, setTimeout, clearTimeout, Map, Set, RegExp,
    decodeURIComponent, encodeURIComponent, atob: (s) => Buffer.from(s, "base64").toString("binary"),
    TextDecoder,
    fetch: (url, opts) => { ctxCalls.fetch.push({ url: String(url), body: opts && opts.body }); return Promise.reject(new Error("offline")); },
    chrome: {
      storage: { local: { get: async () => ({}), set: async () => {} } },
      runtime: { onInstalled: { addListener() {} }, onStartup: { addListener() {} }, onMessage: { addListener() {} }, getURL: (p) => p },
      alarms: { create() {}, onAlarm: { addListener() {} } },
      webNavigation: { onBeforeNavigate: { addListener() {} }, onHistoryStateUpdated: { addListener() {} } },
      tabs: { update: async (id, props) => { ctxCalls.tabsUpdate.push({ id, props }); } },
    },
    importScripts: (...files) => {
      for (const f of files) vm.runInContext(fs.readFileSync(path.join(dir, f), "utf8"), ctx, { filename: f });
    },
  };
  vm.createContext(ctx);
  let src = fs.readFileSync(path.join(dir, "background.js"), "utf8");
  src += "\n;globalThis.__ctc = { evaluateUrl, blockTab, LOCK_REASONS, setState(s){ cachedState = { locked:false, blockedKeywords:[], blockedWebsites:[], permanentProtectionsEnabled:false, ...s }; } };";
  vm.runInContext(src, ctx, { filename: "background.js" });
  ctx.__ctc.calls = ctxCalls;
  return ctx.__ctc;
}
module.exports = { loadExtension };
