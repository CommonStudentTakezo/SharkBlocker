// Testes da extensão (sem navegador). Rodar:  node tests/extension/run-tests.js
const path = require("path");
const { loadExtension } = require("./harness");
const ext = loadExtension(path.join(__dirname, "..", "..", "BrowserExtension"));

let pass = 0, fail = 0;
function check(label, state, url, expected) {
  ext.setState(state);
  const got = ext.evaluateUrl(url);
  const ok = expected === got;
  ok ? pass++ : fail++;
  if (!ok) console.log(`  FALHOU  ${label}\n          url=${url}\n          esperado=${expected}  obtido=${got}`);
}
const G = (q) => "https://www.google.com/search?q=" + encodeURIComponent(q);

const LOCKED = { locked: true };
const PERM = { locked: false, permanentProtectionsEnabled: true };
const OFF = { locked: false, permanentProtectionsEnabled: false };

// ---------------------------------------------------------------- 1) o que você reportou
console.log("1) Navegadores remotos / proxies (entrada direta)");
[
  "https://browser.lol/", "https://browser.lol/en/blog/x", "https://app.browser.lol/",
  "https://www.browserling.com/", "https://croxyproxy.com/", "https://proxysite.com/",
  "https://free-proxy-list.net/", "https://myproxyserver.xyz/", "https://unblock-sites.xyz/",
  "https://unblockedgames66.github.io/", "https://hidester.com/", "https://filterbypass.me/",
  "https://cloudbrowser.io/", "https://virtual-browser.net/", "https://onlinebrowser.app/",
  "https://nordvpn.com/pt-br/", "https://rammerhead.example.org/", "https://exemplo.com.br/vpn-gratis",
].forEach((u) => {
  // o último (vpn só no caminho) NÃO deve bloquear: caminho não é checado
  const expected = u.endsWith("/vpn-gratis") ? null : "proxy";
  check("proxy direto", LOCKED, u, expected);
});

console.log("2) Palavras coladas na busca");
for (const q of [
  "proxybrowser", "ProxyBrowser", "proxy-browser", "proxy_browser", "proxy browser",
  "freeproxysite", "myproxy2", "proxy2024", "pr0xy", "p r o x y", "unblockedgames66",
  "unblocked games", "bypassblocker", "browserlol", "browser.lol", "browser lol",
  "freevpn", "melhorvpngratis", "torbrowser", "tor browser", "socks5", "contornarbloqueio",
  "hidemyip",
]) check("busca coladas", LOCKED, G(q), "proxy");

// ---------------------------------------------------------------- 3) sem falsos positivos
console.log("3) Buscas e sites normais NÃO podem ser barrados");
for (const q of [
  "história do brasil", "motor de busca", "autor do livro", "doctor who", "tortuga",
  "vitória x flamengo", "protocolo http", "factory pattern c#", "approximately",
  "bypass surgery recovery time", "cirurgia de bypass gástrico", "online browser games",
  "best browser for developers", "browser compatibility table", "cloud computing",
  "nginx reverse proxy config", "reverse proxy tutorial", "proxy design pattern c#",
  "javascript proxy object", "haproxy tutorial", "proxyman download", "cloud browser isolation pdf",
  "receita de bolo", "how to unblock a number on whatsapp",
]) check("busca normal", LOCKED, G(q), null);

for (const url of [
  "https://www.google.com/", "https://github.com/", "https://developer.mozilla.org/en-US/docs/Web/JavaScript/Reference/Global_Objects/Proxy",
  "https://pt.wikipedia.org/wiki/Servidor_proxy", "https://www.haproxy.org/", "https://proxyman.io/",
  "https://stackoverflow.com/search?q=proxy+pass", "https://nginx.org/en/docs/", "https://www.youtube.com/watch?v=abc",
  "https://tortuga.com.br/", "https://historia.uol.com.br/", "https://browsergames.com.br/",
]) check("site normal", LOCKED, url, null);

// ---------------------------------------------------------------- 4) quando vale
console.log("4) Gate: proxy só com bloqueio ativo OU proteções permanentes");
check("sem nada ligado", OFF, "https://browser.lol/", null);
check("sem nada ligado (busca)", OFF, G("proxybrowser"), null);
check("só proteções permanentes", PERM, "https://browser.lol/", "proxy");
check("só proteções permanentes (busca)", PERM, G("proxybrowser"), "proxy");
check("só bloqueio ativo", LOCKED, "https://browser.lol/", "proxy");

// ---------------------------------------------------------------- 5) URLs embutidas
console.log("5) Destino escondido dentro de outra URL");
const U = { locked: true, blockedWebsites: ["facebook.com"] };
check("?url=https://alvo", U, "https://qualquer.site/go?url=" + encodeURIComponent("https://facebook.com/x"), "url");
check("?url=alvo (sem esquema)", U, "https://qualquer.site/go?url=facebook.com", "url");
check("tradutor ?u=", U, "https://translate.google.com/translate?sl=auto&tl=pt&u=https://www.facebook.com/", "url");
check("arquivo /web/2020/https://", U, "https://web.archive.org/web/2020/https://facebook.com/", "url");
check("base64 aHR0c...", U, "https://qualquer.site/?u=" + Buffer.from("https://facebook.com/").toString("base64"), "url");
check("translate.goog", U, "https://facebook-com.translate.goog/?_x_tr_sl=en", "url");
check("proxy desconhecido + destino adulto", OFF, "https://sitequalquer.dev/?u=" + encodeURIComponent("https://pornhub.com/"), "adult");
check("arquivo + proxy conhecido", PERM, "https://web.archive.org/web/2020/https://croxyproxy.com/", "proxy");
check("redirecionador legítimo continua ok", U, "https://www.google.com/url?q=" + encodeURIComponent("https://example.org/artigo"), null);
check("OAuth redirect_uri legítimo continua ok", U, "https://accounts.example.com/oauth?redirect_uri=" + encodeURIComponent("https://app.example.org/cb"), null);

// ---------------------------------------------------------------- 6) comportamento antigo preservado
console.log("6) Regressão: o que já funcionava continua igual");
check("adulto por URL", OFF, "https://www.pornhub.com/", "adult");
check("adulto por busca", OFF, G("conteúdo adulto"), "adult");
check("'nude' não casa com 'denuded'", OFF, G("denuded forest"), null);
check("site da lista (hostname exato)", U, "https://www.facebook.com/", "url");
check("x.com não bloqueia box.com", { locked: true, blockedWebsites: ["x.com"] }, "https://www.box.com/", null);
check("palavra do usuário (substring na URL)", { locked: true, blockedKeywords: ["xadrez"] }, "https://site.com/clube-de-xadrezistas", "keyword");
check("lista do usuário só vale com lock", { locked: false, blockedWebsites: ["facebook.com"] }, "https://www.facebook.com/", null);

// ---------------------------------------------------------------- 7) trava de tela (eventos)
console.log("7) Quais motivos pedem a trava de tela do PC");
const KW = { locked: true, blockedKeywords: ["xadrez"], blockedWebsites: ["facebook.com"] };
check("termo proibido DIGITADO na busca", KW, G("aprender xadrez"), "keyword_search");
check("termo proibido só no meio da URL (sem trava)", KW, "https://site.com/clube-de-xadrezistas", "keyword");
function lockEvents(reason, opts) {
  ext.calls.fetch.length = 0;
  ext.blockTab(1, reason, opts);
  return ext.calls.fetch.filter((c) => c.url.endsWith("/event")).length;
}
const expectLock = (reason, opts, want) => {
  const got = lockEvents(reason, opts) === 1;
  got === want ? pass++ : (fail++, console.log(`  FALHOU  trava para '${reason}': esperado=${want} obtido=${got}`));
};
expectLock("adult", undefined, true);
expectLock("proxy", undefined, true);
expectLock("keyword_search", undefined, true);
expectLock("keyword", undefined, false);
expectLock("url", undefined, false);
expectLock("keyword", { lock: false }, false);
expectLock("gateway", undefined, false); // U7: tradutor/cache/arquivo web só mostram a página de bloqueio
expectLock("ip", undefined, false);      // U7: IP literal idem
expectLock("adult", { lock: false }, false);

// ---------------------------------------------------------------- 8) CSP da página de bloqueio
console.log("8) blocked.html não pode ter script/handler inline (CSP do Manifest V3)");
const fs = require("fs");
const dir = path.join(__dirname, "..", "..", "BrowserExtension");
const html = fs.readFileSync(path.join(dir, "blocked.html"), "utf8");
const inlineScript = /<script(?![^>]*\bsrc\s*=)[^>]*>/i.test(html);
const inlineHandler = /<[a-z][^>]*\son[a-z]+\s*=/i.test(html);
const jsUrl = /href\s*=\s*["']\s*javascript:/i.test(html);
for (const [label, bad] of [["script inline", inlineScript], ["handler on*= inline", inlineHandler], ["href javascript:", jsUrl]]) {
  bad ? (fail++, console.log(`  FALHOU  blocked.html tem ${label}`)) : pass++;
}
fs.existsSync(path.join(dir, "blocked.js")) && /src\s*=\s*["']blocked\.js["']/.test(html) ? pass++ : (fail++, console.log("  FALHOU  blocked.js não está referenciado"));

// ---------------------------------------------------------------- 9) ofuscação (n4k3d, p0rn...)
console.log("9) Termos disfarçados também são pegos; textos normais não");
for (const q of [
  "n4k3d", "p0rn", "p0rn hub", "s3x video", "n@ked", "p.o.r.n", "p o r n", "p-o-r-n", "p_0_r_n",
  "pooorn", "pornnn", "ｐｏｒｎ", "\u0440\u043ern", "p\u043ern", "po\u200brn", "p\u00adorn", "n\u0430ked",
  "N4K3D girls", "nuuude", "n u d e", "xxxxx",
]) check("ofuscado na busca: " + q, OFF, G(q), "adult");
for (const u of [
  "https://p0rn-girls.com/", "https://p0rnhub.com/", "https://www.xv1deos.com/", "https://x-v-i-d-e-o-s.com/",
  "https://exemplo.com/p0rn/video",
]) check("ofuscado na URL: " + u, OFF, u, "adult");

for (const q of [
  "455 plus 3d model", "h264 codec", "essex county", "assessment of learning", "a55 road",
  "classroom", "mass spectrometry", "xxxl shirt size", "s3curity camera", "p0wer supply", "3d printing",
  "ph 7 solution", "c4 plant photosynthesis", "co2 emissions 2024", "h2o molecule", "naCl 0.9", "b12 vitamin",
  "ácido sulfúrico h2so4", "pen drive 64gb", "windows 11 24h2",
]) check("normal (sem falso positivo): " + q, OFF, G(q), null);

// palavras do usuário (só com bloqueio ativo): disfarces também valem, nos dois sentidos
const KWS = (...k) => ({ locked: true, blockedKeywords: k });
check("kw: busca leet", KWS("cassino"), G("c4ss1n0"), "keyword_search");
check("kw: palavra cadastrada em leet", KWS("p0ker"), G("poker online"), "keyword_search");
check("kw: separada por pontos", KWS("cassino"), G("c.a.s.s.i.n.o"), "keyword_search");
check("kw: dividida em dois pedaços", KWS("cassino"), G("cass ino"), "keyword_search");
check("kw: esticada", KWS("cassino"), G("cassinooo"), "keyword_search");
check("kw: invisível", KWS("cassino"), G("cas\u200bsino"), "keyword_search");
check("kw: letras russas", KWS("cassino"), G("c\u0430ssin\u043e"), "keyword_search");
check("kw: na URL", KWS("cassino"), "https://exemplo.com/c4ss1n0", "keyword");
check("kw: sem relação", KWS("cassino"), G("receita de bolo"), null);

// ---------------------------------------------------------------- 10) novas camadas
console.log("10) Domínios colados, TLDs adultos e novas ofuscações");
for (const u of [
  "https://megaxvideosbr.com/", "https://assistirpornhub.net/", "https://hentaihaven.org/", "https://freepornvideos.tv/",
  "https://sitequalquer.xxx/", "https://exemplo.porn/", "https://exemplo.adult/", "https://exemplo.sex/",
  "https://exemplo.com/videos/xnxx-brasil", "https://www.megaonlyfansleaks.com/", "https://pornub.com/",
]) check("adulto colado/TLD: " + u, OFF, u, "adult");

for (const q of [
  "p*rn", "s#x video", "pr0n", "&#112;&#111;&#114;&#110;", "%2570orn", "\\u0070orn", "\u1d18\u1d0f\u0280\u0274",
  "\u{1F17F}\u{1F17E}\u{1F181}\u{1F17D}", "\u{1F1F5}\u{1F1F4}\u{1F1F7}\u{1F1F3}", "o n l y f a n s leaks",
  "r34", "nissan skyline r34", "gozada", "casa novinha", "novinhas", "paint stripper", "trepando", "pron", "leaked nudes", "mulheres nuas", "videos porno", "sex cams",
]) check("ofuscado/novo termo na busca: " + q, OFF, G(q), "adult");

for (const q of [
  "que gozado esse filme",
  "jerome kern", "sussex university", "snake_case naming",
  "pronunciation guide", "cum laude",
]) check("normal (sem falso positivo): " + q, OFF, G(q), null);

for (const u of [
  "https://www.jeromeny.com/", "https://sussex.ac.uk/", "https://www.popcorntime.org/", "https://www.essex.gov.uk/",
  "https://yourbrainonporn.com/", "https://www.xtremesports.com/", "https://www.sextant-navigation.org/",
  "https://exemplo.com.br/", "https://pt.wikipedia.org/wiki/Anime",
]) check("site normal: " + u, OFF, u, null);

// ---------------------------------------------------------------- 11) máximo de dificuldade
console.log("11) Palavras restauradas, texto picado, arte ASCII e parâmetros escondidos");
for (const q of [
  "chupando", "chupando manga", "coomer", "sexo com ela", "sexo na praia", "sexo no carro", "sexo em casa", "sexo casual", "casal fazendo",
  "transando", "mamada", "nudity", "topless", "one night stand", "hookup sites", "orgia", "rabuda",
  "sex", "sexo", "s e x", "s.e.x", "se xo", "po rn", "p o r n h u b", "p\u{1F600}o\u{1F600}rn", "pornh ub", "xv ideos",
  "p()rn", "n/\\ked", "|\\|ude", "5ex0", "masturbation", "порно", "секс", "エロ動画 無料", "tette", "tetas", "nudify ai",
  "deepfake nudes", "conto erotico", "bate papo erotico", "sex chat room",
]) check("restaurado/novo na busca: " + q, OFF, G(q), "adult");

// termo escondido em OUTRO parâmetro, repetido, ou no fragmento (#)
for (const u of [
  "https://www.google.com/search?q=receita&text=porn", "https://exemplo.com/s?q=bolo&q=porn",
  "https://exemplo.com/s?q=bolo&Q=nudes", "https://exemplo.com/app#q=pornhub", "https://exemplo.com/app#/search?q=nudes",
  "https://exemplo.com/p#xvideos", "https://exemplo.com/?kw=hentai&x=1", "https://exemplo.com/?find=nudes&q=ok",
]) check("escondido: " + u, OFF, u, "adult");

for (const u of [
  "https://exemplo.com/?q=bolo&text=receita", "https://exemplo.com/p#secao-2", "https://exemplo.com/app#q=bolo+de+cenoura",
  "https://pt.wikipedia.org/wiki/Essex", "https://exemplo.com/?q=nu+de+frente+para+o+mar&x=1".replace("nu+de","casa+de"),
]) check("normal (parâmetros/fragmento): " + u, OFF, u, null);

for (const u of ["https://site.porno/", "https://qualquer.sexy/", "https://x.sex/", "https://sitehentaihaven.net/", "https://nudify-app.com/"])
  check("TLD/marca: " + u, OFF, u, "adult");

// ---------------------------------------------------------------- 12) U7: tradutor, cache, arquivo web, IP literal
console.log("12) U7: tradutor/cache (só no bloqueio), arquivo web (opção), IP literal (só no bloqueio)");
const LK = { locked: true };
const LK_ARCH = { locked: true, blockWebArchives: true };

// tradutor de páginas e cache de buscador: barrados com bloqueio ativo, mesmo sem destino na lista
for (const u of [
  "https://translate.google.com/translate?sl=auto&tl=pt&u=exemplo-qualquer.org",
  "https://translate.google.com.br/translate?hl=pt&u=exemplo-qualquer.org",
  "https://translate.google.co.uk/website?sl=en&tl=pt&u=exemplo-qualquer.org",
  "https://translate.googleusercontent.com/translate_c?u=exemplo-qualquer.org",
  "https://exemplo--qualquer-org.translate.goog/?_x_tr_sl=en", "https://translate.yandex.com/translate?url=exemplo-qualquer.org",
  "https://www.translatetheweb.com/?from=&to=pt&a=exemplo-qualquer.org", "https://www.microsofttranslator.com/bv.aspx?a=x",
  "https://webcache.googleusercontent.com/search?q=cache:exemplo-qualquer.org", "https://cc.bingj.com/cache.aspx?q=x",
  "https://cachedview.nl/", "https://yandexwebcache.net/yandbtm?url=x",
]) check("gateway no bloqueio: " + u, LK, u, "gateway");

// o tradutor de TEXTO e o resto do domínio continuam abrindo; fora do bloqueio nada muda
for (const u of [
  "https://translate.google.com/", "https://translate.google.com/?sl=en&tl=pt&text=hello&op=translate",
  "https://translate.google.evil.com/translate", "https://www.deepl.com/translator", "https://notranslate.goog.exemplo.org/",
]) check("tradutor de texto/falso positivo: " + u, LK, u, null);
check("gateway sem bloqueio (extensão não age)", OFF, "https://translate.google.com/translate?u=exemplo-qualquer.org", null);
check("gateway só com proteções permanentes (não age)", PERM, "https://exemplo--qualquer-org.translate.goog/", null);
// quando o DESTINO está na lista, o motivo continua sendo o do destino (mais específico)
check("tradutor + destino listado vira 'url'", U, "https://translate.google.com/translate?u=https://www.facebook.com/", "url");
check("translate.goog + destino listado vira 'url'", U, "https://facebook-com.translate.goog/?_x_tr_sl=en", "url");
check("tradutor + destino adulto vira 'adult'", OFF, "https://translate.google.com/translate?u=https://pornhub.com/", "adult");

// arquivo web: só com a opção ligada
for (const u of [
  "https://web.archive.org/web/2020/https://exemplo-qualquer.org/", "https://web.archive.org/", "https://archive.org/web/",
  "https://archive.is/exemplo-qualquer.org", "https://archive.ph/abcde", "https://archive.today/abcde",
  "https://ghostarchive.org/archive/x", "https://megalodon.jp/x", "https://arquivo.pt/wayback/20200101/https://exemplo-qualquer.org",
]) {
  check("arquivo web com opção ligada: " + u, LK_ARCH, u, "gateway");
  check("arquivo web com opção desligada: " + u, LK, u, null);
}
for (const u of ["https://archive.org/details/algum-livro", "https://archive.org/website-info", "https://arquivo.pt/", "https://www.archivecompany.com/"])
  check("resto do archive.org passa mesmo com a opção ligada: " + u, LK_ARCH, u, null);
check("arquivo web com opção ligada, sem bloqueio (não age)", { locked: false, blockWebArchives: true }, "https://archive.is/x", null);
check("arquivo + destino listado continua 'url'", { ...U, blockWebArchives: true }, "https://web.archive.org/web/2020/https://facebook.com/", "url");
check("arquivo (opção desligada) + destino listado continua 'url'", U, "https://web.archive.org/web/2020/https://facebook.com/", "url");

// IP literal: IPv4 e IPv6 públicos barrados no bloqueio; localhost e faixas privadas passam
for (const u of [
  "http://203.0.113.5/", "https://8.8.8.8/", "http://8.8.8.8:8080/algo?x=1", "http://172.32.0.1/", "http://11.0.0.1/",
  "http://2130706433.example.org/".replace("2130706433.example.org", "134744072"), // 8.8.8.8 em decimal
  "http://0x08080808/", "http://010.8.8.8/".replace("010.", "0310.").replace("0310.8.8.8", "0x8.8.8.8"),
  "http://[2001:4860:4860::8888]/", "https://[2606:4700:4700::1111]:443/", "http://[::ffff:8.8.8.8]/", "http://[64:ff9b::808:808]/",
]) check("IP público no bloqueio: " + u, LK, u, "ip");
for (const u of [
  "http://127.0.0.1:57411/state", "http://127.1/", "http://2130706433/", "http://10.0.0.1/", "http://192.168.0.1/admin",
  "http://172.16.5.4/", "http://172.31.255.255/", "http://169.254.10.10/", "http://100.64.1.1/", "http://0.0.0.0:8080/",
  "http://[::1]/", "http://[::1]:3000/", "http://[fe80::1]/", "http://[fd00::1234]/", "http://[::ffff:127.0.0.1]/",
  "http://[::ffff:192.168.1.1]/", "http://[64:ff9b::a00:1]/", "http://localhost:3000/", "http://nas.local/",
]) check("IP local/privado passa: " + u, LK, u, null);
check("IP público sem bloqueio (não age)", OFF, "http://203.0.113.5/", null);
check("IP público só com proteções permanentes (não age)", PERM, "http://203.0.113.5/", null);
check("IP público no destino embutido", LK, "https://qualquer.site/go?url=" + encodeURIComponent("http://203.0.113.5/"), "ip");
check("IP privado no destino embutido passa", LK, "https://qualquer.site/go?url=" + encodeURIComponent("http://192.168.0.1/"), null);
check("IP no host + termo adulto no caminho: 'adult' ganha", LK, "http://203.0.113.5/pornhub", "adult");
check("nome de site com números não é IP", LK, "https://www.1password.com/", null);
check("versão parecida com IP no caminho passa", LK, "https://exemplo.org/downloads/1.2.3.4/", null);

// matcher: funções puras
const vmM = require("vm"), fsM = require("fs");
const mctx = { URL }; vmM.createContext(mctx);
vmM.runInContext(fsM.readFileSync(path.join(__dirname, "..", "..", "BrowserExtension", "matcher.js"), "utf8") + ";globalThis.M = CTCMatcher;", mctx);
const M = mctx.M;
const eq = (label, got, want) => { got === want ? pass++ : (fail++, console.log(`  FALHOU  ${label}: esperado=${want} obtido=${got}`)); };
eq("serviceMatches host", M.serviceMatches("a.translate.goog", "/", "translate.goog"), true);
eq("serviceMatches sufixo parecido", M.serviceMatches("mytranslate.goog", "/", "translate.goog"), false);
eq("serviceMatches .* com.br", M.serviceMatches("translate.google.com.br", "/translate", "translate.google.*/translate"), true);
eq("serviceMatches .* domínio falso", M.serviceMatches("translate.google.evil.com", "/translate", "translate.google.*/translate"), false);
eq("serviceMatches caminho por segmento", M.serviceMatches("archive.org", "/website", "archive.org/web"), false);
eq("serviceMatches caminho exato", M.serviceMatches("archive.org", "/web", "archive.org/web"), true);
eq("serviceMatches caminho filho", M.serviceMatches("archive.org", "/web/2020/x", "archive.org/web"), true);
eq("ip: ponto final no host", M.isPublicLiteralIp("1.2.3.4."), true);
eq("ip: 999.1.1.1 não é IP", M.isPublicLiteralIp("999.1.1.1"), false);
eq("ip: 172.15.x público", M.isPublicLiteralIp("172.15.0.1"), true);
eq("ip: 172.32.x público", M.isPublicLiteralIp("172.32.0.1"), true);
eq("ip: 100.63.x público", M.isPublicLiteralIp("100.63.0.1"), true);
eq("ip: 100.128.x público", M.isPublicLiteralIp("100.128.0.1"), true);
eq("ip: [::] local", M.isPublicLiteralIp("[::]"), false);
eq("ip: :::: inválido", M.isPublicLiteralIp("[1:2:3:4:5:6:7:8:9]"), false);
eq("ip: documentação 2001:db8 é público", M.isPublicLiteralIp("[2001:db8::1]"), true);
eq("ip: nome não é IP", M.isPublicLiteralIp("example.com"), false);

// o que vai pro hosts (DoH) precisa estar bem formado e sem curingas
const ptSrc = fsM.readFileSync(path.join(__dirname, "..", "..", "BrowserExtension", "proxy-terms.js"), "utf8");
const pctx = {}; vmM.createContext(pctx);
vmM.runInContext(ptSrc + ";globalThis.P = CTC_PROXY;", pctx);
const doh = pctx.P.dohHosts || [];
eq("dohHosts não vazia", doh.length > 20, true);
eq("dohHosts só nomes exatos", doh.every((d) => /^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$/.test(d) && d.includes(".") && !d.includes("*")), true);
eq("dohHosts sem duplicados", new Set(doh).size, doh.length);
eq("gatewayServices e archiveServices sem '//' nem ']' (TermLists.cs)", [...pctx.P.gatewayServices, ...pctx.P.archiveServices, ...doh].every((d) => !d.includes("//") && !d.includes("]")), true);
eq("archive.org inteiro NÃO está na lista", pctx.P.archiveServices.includes("archive.org"), false);
eq("translate.google.com inteiro NÃO está na lista", pctx.P.gatewayServices.some((d) => /^translate\.google\.[^/]*$/.test(d)), false);

console.log("EX) Exceção fixa: repositório do classificador (o endereço tem \"nsfw\")");
[
  "https://github.com/GantMan/nsfw_model", "https://github.com/GantMan/nsfw_model/", "https://www.github.com/gantman/NSFW_model/releases/tag/1.1.0",
  "https://github.com/GantMan/nsfw_model/issues/12",
  "https://github.com/GantMan/nsfw_model/releases/download/1.1.0/nsfw_mobilenet2.224x224.h5",
  "https://github.com/GantMan/nsfw_model/blob/master/nsfw_model/nsfw_model.py",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
].forEach((u) => { check("exceção nsfw_model (aberto): " + u, OFF, u, null); check("exceção nsfw_model (bloqueio): " + u, LOCKED, u, null); });
[
  "https://github.com/GantMan/nsfw_model?q=porn", "https://github.com/GantMan/nsfw_model/blob/master/pornhub.txt",
  "https://github.com/GantMan/nsfw_model/releases/download/1.1.0/porn.zip", "https://github.com/GantMan/nsfw_model/releases/download/1.1.0/nsfw_mobilenet2.h5?x=porn",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn/porn.h5", "https://s3.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.h5?x=porn",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn_extra/modelo.h5", "https://s3.amazonaws.com/outro_bucket/nsfwjscdn/modelo.h5",
  "https://ir_public.s3.amazonaws.com/nsfwjscdn/modelo.h5", "https://s3.amazonaws.com/ir_public/nsfw/modelo.h5",
  "https://github.com/outro/nsfw_model", "https://github.com/GantMan/nsfw_model_extra", "https://github.com/GantMan/nsfw_models",
  "https://evil.example.com/github.com/GantMan/nsfw_model", "https://github.com.evil.example.com/GantMan/nsfw_model",
  "https://www.reddit.com/r/nsfw/", "https://nsfw_model.example.com/",
].forEach((u) => check("segue barrado: " + u, OFF, u, "adult"));
check("busca 'nsfw' segue barrada", OFF, G("nsfw"), "adult");
console.log("BQ) biquíni/bikini e variações (extensão 1.13)");
[
  "https://www.google.com/search?q=biquini",
  "https://www.google.com/search?q=biqu%C3%ADni+cavado",
  "https://www.google.com/search?q=Biquinis+2026",
  "https://www.google.com/search?q=bikini",
  "https://www.google.com/search?q=bikinis+praia",
  "https://www.google.com/search?q=B1QUIN1",
  "https://loja.example.com/moda/biquini-cavado",
  "https://loja.example.com/BIQUÍNI",
  "https://biquini.example.com/",
  "https://www.youtube.com/results?search_query=micro+biquini",
].forEach((u) => { check("biquini barrado (aberto): " + u, OFF, u, "adult"); check("biquini barrado (bloqueio): " + u, LOCKED, u, "adult"); });
[
  "https://www.google.com/search?q=receita+de+bolo",
  "https://example.com/biquinho",
  "https://example.com/liquini",
].forEach((u) => check("não é biquini: " + u, OFF, u, null));

console.log("YM) YouTube Music liberado com youtube.com bloqueado (extensão 1.15)");
{
  const YT = { locked: true, blockedWebsites: ["youtube.com"] };
  check("youtube.com bloqueado", YT, "https://www.youtube.com/watch?v=abc", "url");
  check("m.youtube.com bloqueado", YT, "https://m.youtube.com/", "url");
  check("music.youtube.com liberado", YT, "https://music.youtube.com/watch?v=abc", null);
  check("sub de music.youtube.com liberado", YT, "https://x.music.youtube.com/", null);
  check("lista explícita music.youtube.com vale", { locked: true, blockedWebsites: ["music.youtube.com"] }, "https://music.youtube.com/", "url");
  check("music.youtube.com.evil.com não é liberado", { locked: true, blockedWebsites: ["evil.com"] }, "https://music.youtube.com.evil.com/", "url");
}

console.log("PS) Sites sempre bloqueados + só Shorts no YouTube (extensão 1.16)");
[
  "https://www.facebook.com/", "https://m.facebook.com/home", "https://www.reddit.com/r/brasil", "https://old.reddit.com/",
  "https://www.tiktok.com/@alguem", "https://www.instagram.com/p/abc/", "https://x.com/alguem", "https://twitter.com/alguem",
  "https://t.me/canal", "https://duckduckgo.com/?q=oi", "https://www.netflix.com/browse", "https://pt.linkedin.com/in/alguem",
].forEach((u) => { check("site permanente (aberto): " + u, PERM, u, "url"); check("site permanente (bloqueio): " + u, LOCKED, u, "url"); });
[
  "https://www.youtube.com/shorts/abc123", "https://m.youtube.com/shorts/abc123", "https://youtube.com/shorts", "https://www.youtube.com/SHORTS/abc",
].forEach((u) => { check("Short barrado (aberto): " + u, PERM, u, "url"); check("Short barrado (bloqueio): " + u, LOCKED, u, "url"); });
[
  "https://www.youtube.com/", "https://www.youtube.com/watch?v=abc", "https://www.youtube.com/results?search_query=aula+de+quimica",
  "https://www.youtube.com/@canal/videos", "https://www.youtube.com/shortsvideo", "https://youtu.be/abc", "https://music.youtube.com/watch?v=abc",
  "https://www.youtube.com/feed/subscriptions", "https://i.ytimg.com/vi/abc/hq.jpg",
].forEach((u) => check("YouTube normal liberado: " + u, PERM, u, null));

console.log("PB) Playboy e nomes de atrizes/atores adultos (extensão 1.14)");
[
  "https://www.google.com/search?q=playboy+magazine",
  "https://www.google.com/search?q=Playboy+Magazine+2020",
  "https://www.google.com/search?q=revista+playboy+antiga",
  "https://www.playboy.com/",
  "https://www.google.com/search?q=mia+khalifa",
  "https://www.google.com/search?q=Riley+Reid+fotos",
  "https://example.com/models/sasha-grey",
  "https://www.youtube.com/results?search_query=sunny+leone",
  "https://www.google.com/search?q=playmate+of+the+month",
].forEach((u) => { check("playboy/nome barrado (aberto): " + u, OFF, u, "adult"); check("playboy/nome barrado (bloqueio): " + u, LOCKED, u, "adult"); });
[
  "https://www.google.com/search?q=playboy+carti",
  "https://www.google.com/search?q=mia+wasikowska",
  "https://www.google.com/search?q=riley+keough",
  "https://www.google.com/search?q=sasha+obama",
  "https://www.google.com/search?q=receita+de+bolo",
].forEach((u) => check("não é nome/playboy: " + u, OFF, u, null));

console.log("EX2) Exceção do S3 do modelo: endpoints regionais, %2F e // (extensão 1.12)");
[
  "https://s3.us-east-1.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
  "https://s3-us-west-2.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
  "https://s3.dualstack.us-east-1.amazonaws.com/ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com//ir_public/nsfwjscdn/nsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn%2Fnsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com/ir_public/NSFWJSCDN/nsfw_mobilenet2.224x224.h5",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn/TFJS_nsfw_mobilenet/tfjs_quant_nsfw_mobilenet/model.json",
].forEach((u) => { check("S3 do modelo (aberto): " + u, OFF, u, null); check("S3 do modelo (bloqueio): " + u, LOCKED, u, null); });
[
  "https://s3.us-east-1.amazonaws.com/ir_public/nsfwjscdn/porn.h5",
  "https://s3.us-east-1.amazonaws.com/outro_bucket/nsfwjscdn/modelo.h5",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn%2Fporn.h5",
  "https://s3.amazonaws.com/ir_public/?prefix=nsfwjscdn/",
  "https://s3.amazonaws.com/ir_public/nsfwjscdn/modelo.h5#nsfw=porn",
  "https://s3.us-east-1.amazonaws.com.evil.example.com/ir_public/nsfwjscdn/modelo.h5",
  "https://s3.evil.example.com/ir_public/nsfwjscdn/modelo.h5",
  "https://s3.amazonaws.com/ir_public/x/nsfwjscdn",
].forEach((u) => check("S3 segue barrado: " + u, OFF, u, "adult"));


console.log(`\n${pass} ok, ${fail} falha(s)`);
process.exit(fail ? 1 : 0);
