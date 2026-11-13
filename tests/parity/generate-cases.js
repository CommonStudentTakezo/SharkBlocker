// Gera tests/parity/cases.json a partir da extensão (a referência). Rodar:  node tests/parity/generate-cases.js
// Cada caso tem uma "intenção" (o que DEVE acontecer). O gerador confere a extensão contra a intenção e só
// grava o resultado se bater; divergências são listadas para revisão humana.
const fs = require("fs");
const path = require("path");
const { loadExtension } = require("../extension/harness");
const ext = loadExtension(path.join(__dirname, "..", "..", "BrowserExtension"));

const G = (q) => "https://www.google.com/search?q=" + encodeURIComponent(q);
const OPEN = { locked: false, sites: [], keywords: [], blockWebArchives: false };
const LOCKED = { locked: true, sites: [], keywords: [], blockWebArchives: false };
const LOCKED_USER = { locked: true, sites: ["meusite.com"], keywords: ["apostas"], blockWebArchives: false };
const LOCKED_ARCH = { locked: true, sites: [], keywords: [], blockWebArchives: true };

const url_cases = [];
function add(label, state, url, intent) { url_cases.push({ label, state, url, intent }); }

// --- proxies / navegadores remotos (entrada direta)
for (const u of ["https://browser.lol/", "https://app.browser.lol/", "https://www.browserling.com/", "https://croxyproxy.com/",
  "https://proxysite.com/", "https://free-proxy-list.net/", "https://unblock-sites.xyz/", "https://cloudbrowser.io/",
  "https://virtual-browser.net/", "https://onlinebrowser.app/", "https://nordvpn.com/pt-br/"]) {
  add("proxy direto (aberto)", OPEN, u, "proxy");
  add("proxy direto (bloqueio)", LOCKED, u, "proxy");
}
// --- palavras coladas na busca
for (const q of ["proxybrowser", "ProxyBrowser", "proxy-browser", "proxy browser", "freeproxysite", "pr0xy", "p r o x y",
  "unblockedgames66", "unblocked games", "browserlol", "browser lol", "freevpn", "torbrowser", "tor browser"]) {
  add("busca colada", LOCKED, G(q), "proxy");
}
// --- conteúdo normal não pode ser barrado
for (const q of ["história do brasil", "motor de busca", "doctor who", "vitória x flamengo", "protocolo http", "approximately",
  "bypass surgery recovery time", "best browser for developers", "browser compatibility table", "cloud computing",
  "nginx reverse proxy config", "proxy design pattern c#", "javascript proxy object", "haproxy tutorial", "receita de bolo",
  "how to unblock a number on whatsapp", "tabela periódica", "reação de oxirredução", "ácido sulfúrico"]) {
  add("busca normal", LOCKED, G(q), null);
  add("busca normal (aberto)", OPEN, G(q), null);
}
for (const u of ["https://www.google.com/", "https://github.com/", "https://pt.wikipedia.org/wiki/Servidor_proxy", "https://www.haproxy.org/",
  "https://proxyman.io/", "https://nginx.org/en/docs/", "https://www.youtube.com/watch?v=abc", "https://pt.wikipedia.org/wiki/Quimica"]) {
  add("site normal", LOCKED, u, null);
}
// --- adulto (termos genéricos, sem endereços reais): domínio, TLD e buscas ofuscadas
for (const u of ["https://www.pornhub.com/", "https://qualquer.xxx/", "https://xvideos.com/", "https://sub.xnxx.com/"]) add("adulto (domínio)", OPEN, u, "adult");
for (const q of ["porn", "p0rn", "p.o.r.n", "pooorn", "роrn", "ｐｏｒｎ", "n4k3d"]) add("adulto (busca ofuscada)", OPEN, G(q), "adult");
// --- exceção fixa: repositório do classificador (o endereço tem "nsfw"); o resto continua barrado
for (const u of ["https://github.com/GantMan/nsfw_model", "https://github.com/GantMan/nsfw_model/", "https://www.github.com/gantman/NSFW_model/releases/tag/1.1.0",
  "https://github.com/GantMan/nsfw_model/releases/download/1.1.0/nsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5"]) {
  add("exceção nsfw_model (aberto)", OPEN, u, null);
  add("exceção nsfw_model (bloqueio)", LOCKED, u, null);
}
for (const u of ["https://github.com/GantMan/nsfw_model?q=porn", "https://github.com/GantMan/nsfw_model/blob/master/pornhub.txt",
  "https://github.com/GantMan/nsfw_model/releases/download/1.1.0/porn.zip", "https://github.com/GantMan/nsfw_model/releases/download/1.1.0/nsfw_mobilenet2.h5?x=porn",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn/porn.h5", "https://s3.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.h5?x=porn",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn_extra/modelo.h5", "https://s3.amazonaws.com/outro_bucket/nsfwjscdn/modelo.h5",
  "https://github.com/outro/nsfw_model", "https://github.com/GantMan/nsfw_model_extra", "https://evil.example.com/github.com/GantMan/nsfw_model",
  "https://www.reddit.com/r/nsfw/"]) add("nsfw fora da exceção", OPEN, u, "adult");
// --- biquíni/bikini e variações (extensão 1.13)
for (const u of [
  "https://www.google.com/search?q=biquini",
  "https://www.google.com/search?q=biqu%C3%ADni+cavado",
  "https://www.google.com/search?q=Biquinis+2026",
  "https://www.google.com/search?q=bikini",
  "https://www.google.com/search?q=bikinis+praia",
  "https://www.google.com/search?q=B1QUIN1",
  "https://loja.example.com/moda/biquini-cavado",
  "https://loja.example.com/BIQUÍNI",
  "https://biquini.example.com/",
  "https://www.youtube.com/results?search_query=micro+biquini"]) {
  add("biquini (aberto)", OPEN, u, "adult");
  add("biquini (bloqueio)", LOCKED, u, "adult");
}
// --- Sites sempre bloqueados + só Shorts no YouTube (extensão 1.16)
for (const u of ["https://www.facebook.com/", "https://m.facebook.com/home", "https://www.reddit.com/r/brasil", "https://www.tiktok.com/@alguem",
  "https://www.instagram.com/p/abc/", "https://x.com/alguem", "https://t.me/canal", "https://duckduckgo.com/?q=oi", "https://www.netflix.com/browse"]) {
  add("site permanente (aberto)", OPEN, u, "url");
  add("site permanente (bloqueio)", LOCKED, u, "url");
}
for (const u of ["https://www.youtube.com/shorts/abc123", "https://m.youtube.com/shorts/abc123", "https://youtube.com/shorts"]) {
  add("short barrado (aberto)", OPEN, u, "url");
  add("short barrado (bloqueio)", LOCKED, u, "url");
}
for (const u of ["https://www.youtube.com/", "https://www.youtube.com/watch?v=abc", "https://www.youtube.com/shortsvideo", "https://youtu.be/abc",
  "https://music.youtube.com/watch?v=abc", "https://www.youtube.com/@canal/videos"]) {
  add("youtube normal liberado", OPEN, u, null);
}

// --- YouTube Music liberado com youtube.com bloqueado (extensão 1.15)
const LOCKED_YT = { locked: true, sites: ["youtube.com"], keywords: [], blockWebArchives: false };
const LOCKED_YTM = { locked: true, sites: ["music.youtube.com"], keywords: [], blockWebArchives: false };
add("youtube bloqueado", LOCKED_YT, "https://www.youtube.com/watch?v=abc", "url");
add("m.youtube bloqueado", LOCKED_YT, "https://m.youtube.com/", "url");
add("youtube music liberado", LOCKED_YT, "https://music.youtube.com/watch?v=abc", null);
add("sub de youtube music liberado", LOCKED_YT, "https://x.music.youtube.com/", null);
add("youtube music listado explicitamente", LOCKED_YTM, "https://music.youtube.com/", "url");

// --- Playboy e nomes de atrizes/atores adultos (extensão 1.14)
for (const u of ["https://www.google.com/search?q=playboy+magazine", "https://www.google.com/search?q=revista+playboy+antiga",
  "https://www.playboy.com/", "https://www.google.com/search?q=mia+khalifa", "https://www.google.com/search?q=Riley+Reid+fotos",
  "https://example.com/models/sasha-grey", "https://www.google.com/search?q=playmate+of+the+month"]) {
  add("playboy/nome (aberto)", OPEN, u, "adult");
  add("playboy/nome (bloqueio)", LOCKED, u, "adult");
}
for (const u of ["https://www.google.com/search?q=playboy+carti", "https://www.google.com/search?q=riley+keough",
  "https://www.google.com/search?q=sasha+obama"]) {
  add("não é nome/playboy", OPEN, u, null);
}
for (const u of [
  "https://www.google.com/search?q=receita+de+bolo",
  "https://example.com/biquinho",
  "https://example.com/liquini"]) add("não é biquini", OPEN, u, null);
// --- exceção do S3 do modelo: endpoints regionais, %2F e // (extensão 1.12)
for (const u of [
  "https://s3.us-east-1.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
  "https://s3-us-west-2.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
  "https://s3.dualstack.us-east-1.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com//ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn%2Fnsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com/ir_public/NSFWJSCDN/nsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn/TFJS_nsfw_mobilenet/tfjs_quant_nsfw_mobilenet/model.json"]) {
  add("exceção S3 do modelo (aberto)", OPEN, u, null);
  add("exceção S3 do modelo (bloqueio)", LOCKED, u, null);
}
for (const u of [
  "https://s3.us-east-1.amazonaws.com/ir_public/nsfwjscdn/porn.h5",
  "https://s3.us-east-1.amazonaws.com/outro_bucket/nsfwjscdn/modelo.h5",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn%2Fporn.h5",
  "https://s3.amazonaws.com/ir_public/?prefix=nsfwjscdn/",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn/modelo.h5#nsfw=porn",
  "https://s3.us-east-1.amazonaws.com.evil.example.com/ir_public/nsfwjscdn/modelo.h5",
  "https://s3.evil.example.com/ir_public/nsfwjscdn/modelo.h5",
  "https://s3.amazonaws.com/ir_public/x/nsfwjscdn"]) add("S3 fora da exceção", OPEN, u, "adult");
// --- URL embutida
add("destino embutido (?url=)", OPEN, "https://redirecionador.example.org/?url=https://www.pornhub.com/", "adult");
add("destino embutido (base64)", OPEN, "https://redirecionador.example.org/?u=" + Buffer.from("https://www.pornhub.com/").toString("base64"), "adult");
add("destino embutido (caminho)", OPEN, "https://web.archive.example.org/web/2020/https://www.pornhub.com/", "adult");
// --- listas do usuário e palavras: só valem no bloqueio
add("site do usuário (bloqueio)", LOCKED_USER, "https://sub.meusite.com/x", "url");
add("site do usuário (aberto)", { ...LOCKED_USER, locked: false }, "https://sub.meusite.com/x", null);
add("palavra do usuário na busca (bloqueio)", LOCKED_USER, G("apostas esportivas"), "keyword_search");
add("palavra do usuário na busca (aberto)", { ...LOCKED_USER, locked: false }, G("apostas esportivas"), null);
// --- gateway e IP literal: só no bloqueio
add("tradutor (bloqueio)", LOCKED, "https://translate.google.com/translate?u=https://exemplo.com", "gateway");
add("tradutor (aberto)", OPEN, "https://translate.google.com/translate?u=https://exemplo.com", null);
add("IP público (bloqueio)", LOCKED, "http://203.0.113.5/", "ip");
add("IP privado (bloqueio)", LOCKED, "http://192.168.0.1/", null);
add("IP público (aberto)", OPEN, "http://203.0.113.5/", null);
add("arquivo web (opção ligada)", LOCKED_ARCH, "https://web.archive.org/web/2020/https://exemplo.com", "gateway");
add("arquivo web (opção desligada)", LOCKED, "https://web.archive.org/web/2020/https://exemplo.com", null);

const stateToJs = (s) => ({ locked: s.locked, blockedWebsites: s.sites, blockedKeywords: s.keywords, blockWebArchives: s.blockWebArchives, permanentProtectionsEnabled: true });
let bad = 0;
for (const c of url_cases) {
  ext.setState(stateToJs(c.state));
  const got = ext.evaluateUrl(c.url);
  c.expected = got === undefined ? null : got;
  if (c.expected !== c.intent) { bad++; console.log(`  DIVERGE da intenção: ${c.label}\n     url=${c.url}\n     intenção=${c.intent} obtido=${c.expected}`); }
}
// serviceMatches / isPublicLiteralIp: carrega o matcher.js sozinho
const vm = require("vm");
const ctx = { console, URL, URLSearchParams, RegExp, Map, Set };
vm.createContext(ctx);
const msrc = fs.readFileSync(path.join(__dirname, "..", "..", "BrowserExtension", "matcher.js"), "utf8");
const M = vm.runInContext(msrc + "\n;CTCMatcher;", ctx, { filename: "matcher.js" });
if (!M) { console.log("ERRO: não achei o objeto exportado do matcher.js"); process.exit(2); }

const service_cases = [
  ["translate.google.com.br", "/translate", "translate.google.*/translate", true],
  ["translate.google.com", "/website", "translate.google.*/translate", false],
  ["web.archive.org", "/web/2020/x", "archive.org/web", true],
  ["archive.org", "/details/livro", "archive.org/web", false],
  ["sub.exemplo.com", "/", "exemplo.com", true],
  ["outroexemplo.com", "/", "exemplo.com", false],
  ["translate.google.co.uk", "/translate", "translate.google.*/translate", true],
].map(([host, p, pattern, intent]) => ({ host, path: p, pattern, expected: M.serviceMatches(host, p, pattern), intent }));
const ip_cases = [
  ["8.8.8.8", true], ["203.0.113.5", true], ["10.0.0.1", false], ["127.0.0.1", false], ["192.168.1.10", false], ["172.16.0.1", false],
  ["169.254.1.1", false], ["100.64.0.1", false], ["[::1]", false], ["[2001:4860:4860::8888]", true], ["[fe80::1]", false],
  ["[fc00::1]", false], ["exemplo.com", false], ["localhost", false],
].map(([host, intent]) => ({ host, expected: M.isPublicLiteralIp(host), intent }));
for (const c of [...service_cases, ...ip_cases]) if (c.expected !== c.intent) { bad++; console.log("  DIVERGE:", JSON.stringify(c)); }

const out = {
  version: 1,
  nota: "Casos de paridade entre a extensão (JS, referência), o app de PC (C#) e o app Android (Kotlin). Gerado por tests/parity/generate-cases.js. NÃO edite à mão: edite o gerador e rode de novo.",
  url_cases: url_cases.map(({ label, state, url, expected }) => ({ label, state, url, expected })),
  service_cases: service_cases.map(({ host, path: p, pattern, expected }) => ({ host, path: p, pattern, expected })),
  ip_cases: ip_cases.map(({ host, expected }) => ({ host, expected })),
};
fs.writeFileSync(path.join(__dirname, "cases.json"), JSON.stringify(out, null, 1));
console.log(`\n${url_cases.length} casos de URL, ${service_cases.length} de serviço, ${ip_cases.length} de IP. Divergências da intenção: ${bad}`);
process.exit(bad ? 1 : 0);
