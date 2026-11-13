// SharkBlocker — service worker (Manifest V3).
//
// Notas de design (corrigem a versão anterior):
//  - Service workers MV3 são encerrados quando ociosos; setInterval some junto.
//    Agora o estado é recarregado por chrome.alarms + a cada navegação (se estiver velho).
//  - Se o app/serviço não responde, MANTÉM o último estado conhecido (guardado em
//    chrome.storage). Antes, qualquer falha de rede virava "tudo desbloqueado", então
//    bastava parar o serviço para escapar.
//  - Sites: compara o HOSTNAME (x.com não bloqueia box.com). Palavras do usuário: substring.
//    Termos embutidos de ADULTO: palavra inteira ("nude" não casa com "denuded").
//  - Proxy/VPN/navegador remoto (browser.lol...): ver proxy-terms.js e matcher.js. Vale com o
//    bloqueio ativo OU com as proteções permanentes ligadas (senão um proxy anularia o filtro
//    adulto). Olha o nome do site e o texto da busca, aceita palavras coladas
//    ("proxybrowser") e avalia o destino real de URLs embutidas (?url=..., arquivo, tradutor).
//  - U7 (só DURANTE O BLOQUEIO): tradutor/cache de buscador ("gateway"), arquivo web (só com a opção
//    BlockWebArchives ligada) e navegação por IP literal público ("ip"; localhost e faixas privadas
//    passam). Nenhum dos três trava a tela: só mostram a página de bloqueio (como "url").
//  - Bloqueio nunca é pulado por "limite de alertas": só a notificação é limitada.

importScripts("adult-terms.js", "proxy-terms.js", "matcher.js", "deobf.js");
// adult-terms.js → CTC_URL_TERMS, CTC_SEARCH_TERMS | proxy-terms.js → CTC_PROXY | matcher.js → CTCMatcher

const STATE_URL = "http://127.0.0.1:57411/state?ext=1"; // "ext=1" = sinal de vida da extensão
const EVENT_URL = "http://127.0.0.1:57411/event";
const STATE_MAX_AGE_MS = 5000;

const EMPTY_STATE = {
  locked: false,
  blockedKeywords: [],
  blockedWebsites: [],
  permanentProtectionsEnabled: false,
  blockWebArchives: false, // U7: opção (desligada) de bloquear serviços de arquivo web
};

const proxyMatcher = CTCMatcher.create(CTC_PROXY);
const MAX_EMBED_DEPTH = 2; // URL dentro de URL dentro de URL: para por aqui

let cachedState = { ...EMPTY_STATE };
let lastFetch = 0;
let loadedFromStorage = false;

async function loadStoredState() {
  if (loadedFromStorage) return;
  loadedFromStorage = true;
  try {
    const data = await chrome.storage.local.get("ctcState");
    if (data?.ctcState) cachedState = { ...EMPTY_STATE, ...data.ctcState };
  } catch (_) { /* sem storage: segue com o vazio */ }
}

async function refreshState(force = false) {
  await loadStoredState();
  if (!force && Date.now() - lastFetch < STATE_MAX_AGE_MS) return;
  lastFetch = Date.now();
  try {
    const res = await fetch(STATE_URL, { cache: "no-store" });
    if (res.ok) {
      cachedState = { ...EMPTY_STATE, ...(await res.json()) };
      await chrome.storage.local.set({ ctcState: cachedState });
    }
  } catch (_) {
    // App/serviço indisponível: mantém o último estado conhecido (de propósito).
  }
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create("ctc-poll", { periodInMinutes: 0.5 });
  refreshState(true);
});
chrome.runtime.onStartup.addListener(() => {
  chrome.alarms.create("ctc-poll", { periodInMinutes: 0.5 });
  refreshState(true);
});
chrome.alarms.onAlarm.addListener((a) => { if (a.name === "ctc-poll") refreshState(true); });

// ---------- normalização / correspondência ----------

function norm(s) {
  return String(s || "").toLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "");
}

function escapeRegex(s) { return s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"); }

const regexCache = new Map();
function wordRegex(terms) {
  const key = terms.join("\u0001");
  if (regexCache.has(key)) return regexCache.get(key);
  // Japonês/chinês/coreano não separam palavras com espaço: esses termos casam como trecho.
  const isCjk = (t) => /[\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af]/u.test(t);
  const all = terms.map(norm).filter(Boolean);
  const parts = all.filter((t) => !isCjk(t)).map(escapeRegex);
  const cjk = all.filter(isCjk).map(escapeRegex);
  const pieces = [];
  if (parts.length) pieces.push(`(?:^|[^\\p{L}\\p{N}])(?:${parts.join("|")})(?=$|[^\\p{L}\\p{N}])`);
  if (cjk.length) pieces.push(`(?:${cjk.join("|")})`);
  const re = pieces.length ? new RegExp(pieces.join("|"), "u") : null;
  regexCache.set(key, re);
  return re;
}

let squashCache = null;
function squashSets() {
  if (squashCache) return squashCache;
  const sq = (t) => CTCDeobf.squash(norm(t));
  const strong = (typeof CTC_STRONG_TERMS !== "undefined" ? CTC_STRONG_TERMS : []).map(sq).filter((t) => t.length >= 6);
  const exact = new Set([...CTC_URL_TERMS, ...CTC_SEARCH_TERMS].map(sq).filter((t) => t.length >= 3));
  squashCache = { strong: [...new Set(strong)], exact };
  return squashCache;
}

let strongCache = null;
function strongAdultTerms() {
  if (strongCache) return strongCache;
  // typeof: se a lista de termos for de uma versão antiga (sem a constante), segue sem esta camada.
  strongCache = typeof CTC_STRONG_TERMS !== "undefined"
    ? CTC_STRONG_TERMS.map(norm).filter((t) => t.length >= 4)
    : [];
  return strongCache;
}

function safeDecode(s) {
  try { return decodeURIComponent(s.replace(/\+/g, " ")); } catch (_) { return s; }
}

// Liberações fixas: hosts que NÃO são bloqueados quando a lista do usuário bloqueia só um domínio-PAI (ex.: youtube.com
// bloqueado, music.youtube.com liberado). Se o usuário listar o próprio host liberado, vale a lista dele.
// ESPELHOS: UrlPolicy.cs, DnsFilterCore.AlwaysAllowedHosts (LocalDnsFilter.cs) e UrlPolicy.kt (Android).
const CTC_ALWAYS_ALLOWED_HOSTS = ["music.youtube.com"];

function normDomainEntry(domain) {
  return domain.toLowerCase().replace(/^https?:\/\//, "").replace(/^www\./, "").replace(/\/.*$/, "");
}

function allowedByException(hostname, domain) {
  const h = hostname.toLowerCase();
  const d = normDomainEntry(domain);
  return CTC_ALWAYS_ALLOWED_HOSTS.some((a) =>
    (h === a || h.endsWith("." + a)) && !(d === a || d.endsWith("." + a)));
}

function hostMatches(hostname, domain) {
  const h = hostname.toLowerCase();
  const d = domain.toLowerCase().replace(/^https?:\/\//, "").replace(/^www\./, "").replace(/\/.*$/, "");
  return !!d && (h === d || h.endsWith("." + d));
}

const SEARCH_KEYS = new Set([
  "q", "query", "search", "search_query", "p", "text", "s", "k", "wd", "keyword", "keywords", "term", "terms",
  "searchterm", "qry", "kw", "word", "searchtext", "search_term", "find", "qstr", "sq", "qq", "input", "qs",
  "query_text", "searchquery", "search_text", "q1", "q2", "ss", "qt", "key", "what", "phrase", "words",
]);

// CORRIGIDO: antes devolvia só o PRIMEIRO parâmetro de busca encontrado (dava para esconder o termo
// em outro parâmetro ou repetindo "q="). Agora junta TODOS — inclusive os do fragmento "#q=..." —
// separados por " ; " (que não vira frase entre parâmetros diferentes).
function searchQuery(u) {
  const parts = [];
  const collect = (params) => {
    for (const [k, v] of params) {
      if (v && SEARCH_KEYS.has(String(k).toLowerCase())) parts.push(v.replace(/[-_.]+/g, " "));
    }
  };
  collect(u.searchParams);
  if (u.hash && u.hash.includes("=")) {
    try { collect(new URLSearchParams(u.hash.replace(/^#\/?\??/, ""))); } catch (_) { /* fragmento estranho */ }
  }
  return parts.join(" ; ");
}

// Proxy/VPN/navegador remoto só é barrado com bloqueio ativo OU proteções permanentes ligadas.
// Mude esta função se quiser outra regra (ex.: só com bloqueio ativo).
function proxyProtectionActive() {
  return !!(cachedState.locked || cachedState.permanentProtectionsEnabled);
}

function evaluateProxy(u, query) {
  const host = u.hostname.toLowerCase();
  if ((CTC_PROXY.allowDomains || []).some((d) => hostMatches(host, d))) return null;
  if ((CTC_PROXY.domains || []).some((d) => hostMatches(host, d))) return "proxy";
  if (proxyMatcher.test(host, "host")) return "proxy";
  if (query && proxyMatcher.test(query, "query")) return "proxy";
  return null;
}

// U7: tradutor de página, cache de buscador e (com a opção ligada) arquivo web. Só durante o bloqueio.
function evaluateGateway(u) {
  const host = u.hostname.toLowerCase();
  if ((CTC_PROXY.allowDomains || []).some((d) => hostMatches(host, d))) return null;
  if (CTCMatcher.anyServiceMatches(host, u.pathname, CTC_PROXY.gatewayServices)) return "gateway";
  if (cachedState.blockWebArchives && CTCMatcher.anyServiceMatches(host, u.pathname, CTC_PROXY.archiveServices)) return "gateway";
  return null;
}

// Retorna o motivo do bloqueio ("url" | "keyword" | "keyword_search" | "adult" | "proxy" | "gateway" | "ip") ou null.
// Exceções FIXAS (só no código; não há tela para editar): endereços técnicos cujo caminho contém "nsfw" sem ser
// conteúdo adulto. Só o trecho exato "host/dono/repositório" é neutralizado; o resto da URL (subpastas, busca,
// fragmento) continua sendo avaliado, e qualquer outro endereço com "nsfw" continua barrado.
// Par = [trecho em minúsculas sem "www.", texto neutro que o substitui]. Espelho: UrlPolicy.cs (AdultExceptions).
const CTC_ADULT_EXCEPTIONS = [
  ["github.com/gantman/nsfw_model", "github.com/gantman/repo-modelo"], // repositório do classificador (e /releases, /issues...)
  ["s3.amazonaws.com/ir_public/nsfwjscdn", "s3.amazonaws.com/ir_public/modelo-cdn"], // pasta pública de onde o repositório baixa os arquivos .h5 do modelo
];

// Host canônico para as exceções: os endpoints regionais do S3 (s3.us-east-1..., s3-us-west-2..., dualstack) valem como
// s3.amazonaws.com. Só muda o NOME do host para comparar; a pasta e o bucket continuam exatos.
const CTC_S3_REGIONAL = /^s3[.-](?:dualstack\.)?[a-z]{2}(?:-[a-z]+)+-\d\.amazonaws\.com$/;
function canonExceptionHost(hostname) {
  const h = String(hostname).toLowerCase().replace(/^www\./, "").replace(/\.$/, "");
  return CTC_S3_REGIONAL.test(h) ? "s3.amazonaws.com" : h;
}

// host + caminho com o trecho de exceção (se houver) trocado pelo texto neutro.
// O caminho é comparado DECODIFICADO e sem "//" repetido (nsfwjscdn%2Farquivo e //ir_public/... valem como o endereço normal).
function neutralizedHead(hostname, pathname) {
  const h = canonExceptionHost(hostname);
  const dpath = safeDecode(String(pathname)).replace(/\/{2,}/g, "/");
  const key = h + dpath.toLowerCase();
  for (const [prefix, safe] of CTC_ADULT_EXCEPTIONS) {
    if (key === prefix || key.startsWith(prefix + "/")) {
      // dentro deste repositório a sigla "nsfw" (ex.: o arquivo nsfw_mobilenet2....h5 em /releases/download/) não é sinal de
      // conteúdo adulto; os demais termos (porn, hentai...) continuam valendo no resto do caminho, na busca e no fragmento.
      return safe + dpath.slice(prefix.length - h.length).replace(/nsfw/gi, "modelo");
    }
  }
  return hostname + pathname;
}

// Shorts do YouTube: barrados por CAMINHO (o resto do YouTube fica liberado). Cobre youtube.com, www., m. e afins.
function isYouTubeShorts(u) {
  return hostMatches(u.hostname, "youtube.com") && /^\/shorts(\/|$)/i.test(u.pathname);
}

// Sites sempre bloqueados (CTC_PERMANENT_SITES em adult-terms.js) e Shorts: valem com proteções permanentes OU bloqueio ativo.
function evaluatePermanentSites(u) {
  if (!proxyProtectionActive()) return null;
  for (const d of CTC_PERMANENT_SITES) {
    if (hostMatches(u.hostname, d) && !allowedByException(u.hostname, d)) return "url";
  }
  if (isYouTubeShorts(u)) return "url";
  return null;
}

function evaluateUrl(url, depth = 0) {
  let u;
  try { u = new URL(url); } catch (_) { return null; }
  if (!/^https?:$/.test(u.protocol)) return null;

  const fullText = safeDecode(neutralizedHead(u.hostname, u.pathname) + " " + u.search + " " + u.hash);
  const decoded = norm(fullText);
  const query = norm(searchQuery(u));

  // Versões "limpas" do texto: sem leetspeak (n4k3d), sem letras de outros alfabetos, sem
  // caracteres invisíveis, sem separadores (p.o.r.n) e sem letras esticadas (pooorn).
  const decodedFolds = CTCDeobf.variants(fullText);
  const queryFolds = CTCDeobf.variants(searchQuery(u));
  const decodedAll = [decoded, ...decodedFolds];
  const queryAll = query ? [query, ...queryFolds] : [];

  if (cachedState.locked) {
    for (const d of cachedState.blockedWebsites || []) {
      if (hostMatches(u.hostname, d) && !allowedByException(u.hostname, d)) return "url";
    }
    // Palavras do usuário: no texto original E nas versões limpas. A palavra cadastrada também é
    // limpa ("p0rn" na lista vale "porn"). Em buscas (texto curto), junta tudo sem espaços
    // ("po rn") para palavras de 4+ letras.
    const kws = [];
    for (const k of cachedState.blockedKeywords || []) {
      const nk = norm(k).trim();
      if (!nk) continue;
      const fk = CTCDeobf.foldOne(k);
      kws.push({ nk, fk, sk: CTCDeobf.squash(fk) });
    }
    for (const { nk, fk, sk } of kws) {
      if (queryAll.some((q) => q.includes(nk) || (fk && q.includes(fk)))) return "keyword_search"; // digitou o termo: trava a tela
      if (sk.length >= 4 && queryFolds.some((q) => CTCDeobf.squash(q).includes(sk))) return "keyword_search";
    }
    for (const { nk, fk } of kws) {
      if (decodedAll.some((d) => d.includes(nk) || (fk && d.includes(fk)))) return "keyword"; // só aparece na URL: só a página de bloqueio
    }
  }

  // Conteúdo adulto: SEMPRE ligado enquanto a extensão está instalada (não depende do app
  // estar respondendo). Termos de URL valem na URL inteira; termos de busca só no que foi buscado.
  const urlRe = wordRegex(CTC_URL_TERMS);
  if (urlRe && (decodedAll.some((d) => urlRe.test(d)) || queryAll.some((q) => urlRe.test(q)))) return "adult";

  const searchRe = wordRegex(CTC_SEARCH_TERMS);
  if (searchRe && queryAll.some((q) => searchRe.test(q))) return "adult";

  // Texto "picado" (p o r n h u b, po😀rn, p.o.r.n.h.u.b): sem nenhum separador, SÓ em buscas (texto
  // curto). Termos fortes de 6+ letras valem como trecho; e uma busca que, sem separadores, é
  // exatamente um termo da lista (ex.: "po rn", "s e x") também.
  if (queryAll.length) {
    const sq = squashSets();
    const squashed = queryAll.filter((q) => q.length <= 160).map((q) => CTCDeobf.squash(q));
    if (squashed.some((x) => x && (sq.exact.has(x) || sq.strong.some((t) => x.includes(t))))) return "adult";
  }

  // Termos FORTES: valem como trecho em qualquer parte da URL (pega domínios colados, tipo
  // "megaxvideosbr.com"). E terminações exclusivamente adultas (.xxx, .porn, .adult, .sex).
  const strong = strongAdultTerms();
  if (strong.length && decodedAll.some((d) => strong.some((t) => d.includes(t)))) return "adult";
  const host = u.hostname.toLowerCase();
  if (typeof CTC_ADULT_TLDS !== "undefined" && CTC_ADULT_TLDS.some((t) => host.endsWith("." + t))) return "adult";

  // Sites sempre bloqueados e Shorts: DEPOIS dos motivos mais específicos (lista da pessoa, adulto) e antes de proxy.
  const permanent = evaluatePermanentSites(u);
  if (permanent) return permanent;

  if (proxyProtectionActive()) {
    const proxy = evaluateProxy(u, query);
    if (proxy) return proxy;
  }

  // Destino real escondido na URL (proxy, redirecionador, arquivo, tradutor): avalia também.
  // Assim "qualquer-proxy.com/?url=siteBloqueado.com" não passa só porque o proxy é desconhecido.
  if (depth < MAX_EMBED_DEPTH) {
    for (const inner of CTCMatcher.embeddedUrls(u)) {
      const r = evaluateUrl(inner, depth + 1);
      if (r) return r;
    }
  }

  // U7, só durante o bloqueio: tradutor/cache/arquivo web e IP literal público (o IP ignora o hosts).
  // Fica DEPOIS do destino embutido: se o alvo escondido já é barrado por um motivo mais específico
  // (site da lista, adulto), é esse motivo que vale.
  if (cachedState.locked) {
    const gateway = evaluateGateway(u);
    if (gateway) return gateway;
    if (CTCMatcher.isPublicLiteralIp(u.hostname)) return "ip";
  }

  return null;
}

// ---------- SafeSearch por parâmetro (reforço; o principal é hosts + política) ----------

function enforceSafeSearchParam(url) {
  try {
    const u = new URL(url);
    const host = u.hostname.toLowerCase();

    if (host === "duckduckgo.com" || host.endsWith(".duckduckgo.com")) {
      if (u.searchParams.get("kp") !== "1") { u.searchParams.set("kp", "1"); return u.toString(); }
    } else if (/(^|\.)google\.[a-z.]+$/.test(host) && u.pathname.startsWith("/search")) {
      if (u.searchParams.get("safe") !== "active") { u.searchParams.set("safe", "active"); return u.toString(); }
    } else if (host === "bing.com" || host.endsWith(".bing.com")) {
      if (u.searchParams.get("adlt") !== "strict") { u.searchParams.set("adlt", "strict"); return u.toString(); }
    } else if (host === "search.brave.com") {
      if (u.searchParams.get("safesearch") !== "strict") { u.searchParams.set("safesearch", "strict"); return u.toString(); }
    } else if (host === "yandex.com" || host.endsWith(".yandex.com") || host === "yandex.ru") {
      // sem parâmetro confiável; coberto por DNS/hosts quando disponível
    } else if (host === "search.yahoo.com") {
      if (u.searchParams.get("vm") !== "r") { u.searchParams.set("vm", "r"); return u.toString(); }
    }
  } catch (_) { }
  return null;
}

// ---------- bloqueio ----------

let lastNotify = 0;

// Motivos que TRAVAM A TELA do PC por 30 min (o app faz isso). "url" (um site comum da sua lista)
// e "keyword" (termo só no meio de uma URL ou no texto da página) só mostram a página de bloqueio:
// com palavra curta na lista, travar por acerto no meio da URL travaria o PC sem motivo.
const LOCK_REASONS = new Set(["adult", "proxy", "keyword_search"]);

function reportInappropriate(reason) {
  // Avisa o app/serviço, que inicia a trava de tela de 30 min. Falha em silêncio se o
  // serviço estiver fora do ar (o bloqueio da página já aconteceu; a camada de desktop do app
  // também vigia a barra de endereço por conta própria).
  fetch(EVENT_URL, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ type: "inappropriate", reason }),
  }).catch(() => {});
}

function blockTab(tabId, reason, opts = {}) {
  if (opts.lock !== false && LOCK_REASONS.has(reason)) reportInappropriate(reason);
  const page = chrome.runtime.getURL("blocked.html") + `?reason=${encodeURIComponent(reason)}&timestamp=${Date.now()}`;
  chrome.tabs.update(tabId, { url: page }).catch(() => {});
  if (Date.now() - lastNotify > 5000) {
    lastNotify = Date.now();
    console.log(`[CTC] bloqueado (${reason})`);
  }
}

chrome.webNavigation.onBeforeNavigate.addListener(async (details) => {
  if (details.frameId !== 0) return;
  await refreshState();

  const reason = evaluateUrl(details.url);
  if (reason) { blockTab(details.tabId, reason); return; }

  const safeUrl = enforceSafeSearchParam(details.url);
  if (safeUrl) chrome.tabs.update(details.tabId, { url: safeUrl }).catch(() => {});
});

// Navegações que não disparam onBeforeNavigate (SPAs com history.pushState).
chrome.webNavigation.onHistoryStateUpdated.addListener(async (details) => {
  if (details.frameId !== 0) return;
  await refreshState();
  const reason = evaluateUrl(details.url);
  if (reason) blockTab(details.tabId, reason);
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type === "CTC_KEYWORD_FOUND" && sender.tab?.id != null) {
    blockTab(sender.tab.id, "keyword", { lock: false });
    return false;
  }
  if (message?.type === "CTC_GET_STATE") {
    refreshState().then(() => sendResponse(cachedState));
    return true; // resposta assíncrona
  }
  return false;
});

loadStoredState().then(() => refreshState(true));
