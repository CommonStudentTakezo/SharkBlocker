// Confere a extensão (JS) contra tests/parity/cases.json. Rodar:  node tests/parity/run-parity.js
const fs = require("fs"), path = require("path"), vm = require("vm");
const { loadExtension } = require("../extension/harness");
const ext = loadExtension(path.join(__dirname, "..", "..", "BrowserExtension"));
const cases = JSON.parse(fs.readFileSync(path.join(__dirname, "cases.json"), "utf8"));
let fail = 0;
for (const c of cases.url_cases) {
  ext.setState({ locked: c.state.locked, blockedWebsites: c.state.sites, blockedKeywords: c.state.keywords, blockWebArchives: c.state.blockWebArchives, permanentProtectionsEnabled: true });
  const got = ext.evaluateUrl(c.url) ?? null;
  if (got !== c.expected) { fail++; console.log(`FALHOU ${c.label}: ${c.url} esperado=${c.expected} obtido=${got}`); }
}
const ctx = { console, URL, URLSearchParams, RegExp, Map, Set }; vm.createContext(ctx);
const M = vm.runInContext(fs.readFileSync(path.join(__dirname, "..", "..", "BrowserExtension", "matcher.js"), "utf8") + "\n;CTCMatcher;", ctx);
for (const c of cases.service_cases) if (M.serviceMatches(c.host, c.path, c.pattern) !== c.expected) { fail++; console.log("FALHOU serviço", JSON.stringify(c)); }
for (const c of cases.ip_cases) if (M.isPublicLiteralIp(c.host) !== c.expected) { fail++; console.log("FALHOU ip", JSON.stringify(c)); }
console.log(fail ? `\n${fail} falha(s)` : `\nOK: ${cases.url_cases.length + cases.service_cases.length + cases.ip_cases.length} casos de paridade`);
process.exit(fail ? 1 : 0);
