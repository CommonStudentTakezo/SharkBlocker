// Correspondência de termos tolerante a palavras COLADAS ("proxybrowser") sem cair no
// extremo de bloquear qualquer palavra que contenha o termo ("tor" em "história").
//
// Três camadas, da mais ampla para a mais restrita:
//   strong : o termo vale como trecho de qualquer palavra.        "proxy" → "croxyproxy"
//   words  : palavra inteira OU palavra formada só por termo +    "unblock"+"games" →
//            "companions" (segmentação por programação dinâmica).   "unblockgames"
//   safe   : frases/palavras legítimas retiradas antes de comparar. "reverse proxy"
//
// Também extrai URLs embutidas em outra URL (proxy/redirecionador/arquivo/tradutor), para a
// extensão poder avaliar o destino real.
//
// Código puro (sem chrome.*): roda no service worker e em testes com Node.

const CTCMatcher = (function () {
  "use strict";

  const MAX_TOKENS = 120;
  const MAX_TOKEN_LEN = 80;
  const MIN_PIECE = 3;

  // ---------- normalização / tokenização ----------

  function normalize(s) {
    return String(s || "")
      .replace(/([\p{Ll}\p{N}])(\p{Lu})/gu, "$1 $2") // camelCase: "ProxyBrowser" → "Proxy Browser"
      .toLowerCase()
      .normalize("NFD")
      .replace(/[\u0300-\u036f]/g, "");
  }

  function tokenize(normalized) {
    return normalized.split(/[^\p{L}\p{N}]+/u).filter(Boolean);
  }

  const LEET = { "0": "o", "1": "i", "3": "e", "4": "a", "5": "s", "7": "t" };
  const hasLetter = (s) => /\p{L}/u.test(s);
  const hasDigit = (s) => /\p{N}/u.test(s);
  const foldLeet = (s) => s.replace(/[013457]/g, (c) => LEET[c]);

  // "p r o x y" / "v.p.n": junta sequências de 3+ tokens de uma letra só.
  function joinSingleLetterRuns(tokens) {
    const out = [];
    let run = [];
    const flush = () => { if (run.length >= 3) out.push(run.join("")); run = []; };
    for (const t of tokens) {
      if (t.length === 1 && /\p{L}/u.test(t)) run.push(t); else flush();
    }
    flush();
    return out;
  }

  const escapeRegex = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

  // ---------- segmentação: o token inteiro é formado só por peças conhecidas? ----------
  // dict: Map(peça → éTermo). Dígitos soltos ("proxy2", "unblock66") valem como enchimento.
  // Exige ao menos UMA peça que seja termo (não só companions).
  function segmentable(token, dict, maxLen) {
    const n = token.length;
    const reach = new Array(n + 1).fill(null); // null = inalcançável; false/true = "já usou um termo?"
    reach[0] = false;
    const merge = (a, b) => (a === null ? b : a || b);

    for (let i = 0; i < n; i++) {
      if (reach[i] === null) continue;

      if (/\p{N}/u.test(token[i])) {
        let j = i;
        while (j < n && /\p{N}/u.test(token[j])) j++;
        for (let k = i + 1; k <= j; k++) reach[k] = merge(reach[k], reach[i]);
      }

      for (let len = MIN_PIECE; len <= maxLen && i + len <= n; len++) {
        const isTerm = dict.get(token.substr(i, len));
        if (isTerm === undefined) continue;
        reach[i + len] = merge(reach[i + len], reach[i] || isTerm);
      }
    }
    return reach[n] === true;
  }

  // ---------- fábrica ----------

  function buildScope(strongList, wordList, companions) {
    const single = new Set();
    const phrases = [];
    const dict = new Map();
    let maxLen = MIN_PIECE;
    const put = (piece, isTerm) => {
      if (piece.length < MIN_PIECE) return;
      if (isTerm || !dict.has(piece)) dict.set(piece, isTerm || dict.get(piece) === true);
      if (piece.length > maxLen) maxLen = piece.length;
    };

    for (const w of wordList) {
      const toks = tokenize(normalize(w));
      if (!toks.length) continue;
      if (toks.length === 1) single.add(toks[0]); else phrases.push(" " + toks.join(" ") + " ");
      put(toks.join(""), true); // versão colada: "browser lol" → "browserlol"
    }
    for (const s of strongList) put(s, true);
    for (const c of companions) put(c, false);
    return { single, phrases, dict, maxLen };
  }

  /**
   * cfg: { strong, words, hostOnlyWords, companions, safePhrases, safeTokens } (ver proxy-terms.js)
   * Retorna { test(texto, escopo) } → o termo que casou, ou null.
   *   escopo "query": texto digitado numa busca  (usa strong + words)
   *   escopo "host" : nome do site                (usa strong + words + hostOnlyWords)
   */
  function create(cfg) {
    const strong = [...new Set((cfg.strong || []).map((s) => normalize(s).replace(/\s+/g, "")).filter(Boolean))];
    const companions = (cfg.companions || []).map((c) => normalize(c).replace(/\s+/g, ""));
    const safeTokens = new Set((cfg.safeTokens || []).map((t) => normalize(t)));

    const scopes = {
      query: buildScope(strong, cfg.words || [], companions),
      host: buildScope(strong, [...(cfg.words || []), ...(cfg.hostOnlyWords || [])], companions),
    };

    const safeParts = (cfg.safePhrases || [])
      .map((p) => tokenize(normalize(p)).map(escapeRegex).join("[^\\p{L}\\p{N}]+"))
      .filter(Boolean);
    const safeRe = safeParts.length
      ? new RegExp(`(?<![\\p{L}\\p{N}])(?:${safeParts.join("|")})(?![\\p{L}\\p{N}])`, "giu")
      : null;

    function matchToken(tok, scope) {
      if (scope.single.has(tok)) return tok;
      for (const s of strong) if (tok.includes(s)) return s;
      if (segmentable(tok, scope.dict, scope.maxLen)) return tok;
      return null;
    }

    function test(text, scopeName) {
      const scope = scopes[scopeName] || scopes.query;
      let t = normalize(text);
      if (!t.trim()) return null;
      if (safeRe) t = t.replace(safeRe, " ");

      let tokens = tokenize(t);
      if (!tokens.length) return null;
      if (tokens.length > MAX_TOKENS) tokens = tokens.slice(0, MAX_TOKENS);

      const padded = " " + tokens.join(" ") + " ";
      for (const ph of scope.phrases) if (padded.includes(ph)) return ph.trim();

      const candidates = tokens.concat(joinSingleLetterRuns(tokens));
      for (const tok of candidates) {
        if (tok.length > MAX_TOKEN_LEN || safeTokens.has(tok)) continue;

        let hit = matchToken(tok, scope);
        if (hit) return hit;

        // leetspeak ("pr0xy", "unbl0ck"): só em palavras que misturam letras e números.
        if (hasLetter(tok) && hasDigit(tok)) {
          const folded = foldLeet(tok);
          if (folded !== tok && !safeTokens.has(folded)) {
            hit = matchToken(folded, scope);
            if (hit) return hit;
          }
        }
      }
      return null;
    }

    return { test };
  }

  // ---------- URLs embutidas em outra URL ----------

  const EMBED_PARAMS = new Set([
    "url", "u", "uri", "link", "target", "site", "dest", "destination", "redirect",
    "redirect_url", "redirect_uri", "redir", "goto", "go", "href", "to", "host", "domain",
    "page", "web", "website", "address", "src",
  ]);
  const DOMAIN_RE = /^(?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d+)?(?:[/?#].*)?$/i;

  function safeDecode(s) {
    try { return decodeURIComponent(String(s).replace(/\+/g, " ")); } catch (_) { return String(s); }
  }

  function toHttpUrl(s) {
    const fixed = String(s).trim().replace(/^(https?):\/+/i, "$1://");
    if (!/^https?:\/\//i.test(fixed)) return null;
    try { return new URL(fixed).href; } catch (_) { return null; }
  }

  // Valores em base64 que começam com "http" (aHR0c...), como alguns proxies fazem.
  function decodeBase64Url(v) {
    if (typeof atob !== "function" || !/^aHR0c[A-Za-z0-9+/_-]{6,}={0,2}$/.test(v)) return null;
    try {
      const b = atob(v.replace(/-/g, "+").replace(/_/g, "/"));
      return /^https?:\/\//i.test(b) ? b : null;
    } catch (_) { return null; }
  }

  // "exemplo-com.translate.goog" → "exemplo.com" ("--" é um hífen de verdade).
  function translateGoogTarget(hostname) {
    const m = String(hostname).toLowerCase().match(/^(.+)\.translate\.goog$/);
    if (!m) return null;
    return m[1].replace(/--/g, "\u0000").replace(/-/g, ".").replace(/\u0000/g, "-");
  }

  /**
   * Destinos "escondidos" numa URL: ?url=https://alvo, ?u=aHR0c..., /web/2020/https://alvo
   * (arquivo), q=cache:https://alvo, alvo-com.translate.goog. Devolve até 5 URLs http(s).
   */
  function embeddedUrls(u) {
    const out = new Set();
    const add = (s) => { const h = s && toHttpUrl(s); if (h) out.add(h); };

    for (const [key, raw] of u.searchParams) {
      const v = String(raw).trim();
      if (!v) continue;
      const b64 = decodeBase64Url(v);
      if (b64) { add(b64); continue; }
      if (/^(?:https?:)?\/\//i.test(v) || /^https?:\/[^/]/i.test(v)) { add(v.startsWith("//") ? "https:" + v : v); continue; }
      if (EMBED_PARAMS.has(key.toLowerCase()) && DOMAIN_RE.test(v)) { add("https://" + v); continue; }
      const inner = v.match(/https?:\/\/[^\s"'<>]+/i); // "cache:https://alvo"
      if (inner) add(inner[0]);
    }

    const pathPart = safeDecode(u.pathname + u.hash).match(/https?:\/+[^\s"'<>]+/i);
    if (pathPart) add(pathPart[0]);

    const translated = translateGoogTarget(u.hostname);
    if (translated) add("https://" + translated + "/");

    return [...out].slice(0, 5);
  }

  // ---------- U7: serviços "gateway" (tradutor, cache, arquivo web) ----------

  /**
   * Um padrão de serviço casa com host + caminho? Formato: "host" ou "host/prefixo".
   *   "translate.goog"                 → o host e qualquer subdomínio
   *   "translate.google.(*)/translate" → /translate em translate.google.<com, com.br, co.uk...>
   *                                      (na lista, escreve-se ".*" no fim do host)
   *   "archive.org/web"                → só /web e /web/... (o resto do archive.org passa)
   * O caminho precisa casar por segmento inteiro: "/web" não casa com "/website".
   */
  function serviceMatches(hostname, pathname, pattern) {
    const p = String(pattern || "").trim().toLowerCase().replace(/^https?:\/\//, "").replace(/^www\./, "");
    if (!p) return false;
    const slash = p.indexOf("/");
    const hostPat = slash < 0 ? p : p.slice(0, slash);
    const pathPat = slash < 0 ? "" : p.slice(slash).replace(/\/+$/, "");
    const h = String(hostname || "").toLowerCase().replace(/\.$/, "");

    let hostOk;
    if (hostPat.endsWith(".*")) {
      const base = escapeRegex(hostPat.slice(0, -2));
      hostOk = new RegExp("(?:^|\\.)" + base + "\\.[a-z]{2,3}(?:\\.[a-z]{2})?$").test(h);
    } else {
      hostOk = h === hostPat || h.endsWith("." + hostPat);
    }
    if (!hostOk) return false;
    if (!pathPat) return true;
    const path = String(pathname || "/").toLowerCase();
    return path === pathPat || path.startsWith(pathPat + "/");
  }

  function anyServiceMatches(hostname, pathname, patterns) {
    for (const p of patterns || []) if (serviceMatches(hostname, pathname, p)) return p;
    return null;
  }

  // ---------- U7: IP literal ----------
  // O navegador (WHATWG URL) já converte formas estranhas de IPv4 ("http://2130706433/", "0x7f.1")
  // para a forma com pontos, então aqui basta o formato normal. IPv6 chega entre colchetes.

  function parseIPv4(s) {
    const m = /^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(String(s));
    if (!m) return null;
    const b = m.slice(1).map(Number);
    return b.every((n) => n <= 255) ? b : null;
  }

  // Devolve 8 grupos de 16 bits ou null. Aceita "::" e a cauda IPv4 ("::ffff:1.2.3.4").
  function parseIPv6(str) {
    let s = String(str).toLowerCase();
    if (!s.includes(":") || !/^[0-9a-f:.]+$/.test(s)) return null;
    const tail = /^(.*:)(\d+\.\d+\.\d+\.\d+)$/.exec(s);
    if (tail) {
      const v4 = parseIPv4(tail[2]);
      if (!v4) return null;
      s = tail[1] + ((v4[0] << 8) | v4[1]).toString(16) + ":" + ((v4[2] << 8) | v4[3]).toString(16);
    }
    const halves = s.split("::");
    if (halves.length > 2) return null;
    const part = (x) => (x === "" ? [] : x.split(":"));
    const head = part(halves[0]);
    const rest = halves.length === 2 ? part(halves[1]) : [];
    let groups;
    if (halves.length === 2) {
      if (head.length + rest.length > 7) return null;
      groups = [...head, ...new Array(8 - head.length - rest.length).fill("0"), ...rest];
    } else {
      groups = head;
    }
    if (groups.length !== 8 || !groups.every((g) => /^[0-9a-f]{1,4}$/.test(g))) return null;
    return groups.map((g) => parseInt(g, 16));
  }

  // localhost e faixas privadas/locais: nunca barradas (painel do roteador, impressora, o próprio app).
  function isPrivateIPv4(b) {
    const [a, c] = b;
    return a === 0 || a === 10 || a === 127 ||
      (a === 100 && c >= 64 && c <= 127) ||   // 100.64.0.0/10 (CGNAT, Tailscale)
      (a === 169 && c === 254) ||             // link-local
      (a === 172 && c >= 16 && c <= 31) ||
      (a === 192 && c === 168);
  }

  function isPrivateIPv6(g) {
    if (g.every((x) => x === 0)) return true;                                  // ::
    if (g.slice(0, 7).every((x) => x === 0) && g[7] === 1) return true;       // ::1
    if ((g[0] & 0xffc0) === 0xfe80) return true;                               // fe80::/10 link-local
    if ((g[0] & 0xffc0) === 0xfec0) return true;                               // fec0::/10 (site-local, obsoleto)
    if ((g[0] & 0xfe00) === 0xfc00) return true;                               // fc00::/7 (ULA)
    const embedded = [g[6] >> 8, g[6] & 255, g[7] >> 8, g[7] & 255];
    if (g.slice(0, 5).every((x) => x === 0) && g[5] === 0xffff) return isPrivateIPv4(embedded); // ::ffff:a.b.c.d
    if (g.slice(0, 6).every((x) => x === 0)) return isPrivateIPv4(embedded);                    // ::a.b.c.d (obsoleto)
    if (g[0] === 0x64 && g[1] === 0xff9b && g.slice(2, 6).every((x) => x === 0)) return isPrivateIPv4(embedded); // NAT64
    return false;
  }

  /**
   * O host é um IP literal PÚBLICO? (IPv4 ou IPv6; localhost e faixas privadas/locais dão false.)
   * "localhost" e nomes comuns dão false: só números de IP entram aqui.
   */
  function isPublicLiteralIp(hostname) {
    let h = String(hostname || "").trim().toLowerCase().replace(/\.$/, "");
    if (h.startsWith("[") && h.endsWith("]")) h = h.slice(1, -1);
    const v4 = parseIPv4(h);
    if (v4) return !isPrivateIPv4(v4);
    const v6 = parseIPv6(h);
    if (v6) return !isPrivateIPv6(v6);
    return false;
  }

  return { create, normalize, tokenize, embeddedUrls, serviceMatches, anyServiceMatches, isPublicLiteralIp };
})();
