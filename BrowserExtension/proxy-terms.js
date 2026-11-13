// Proxies, VPNs e navegadores remotos (browser.lol e similares) — dados editáveis.
// A lógica que usa estes dados está em matcher.js; a integração, em background.js.
//
// COMO A CORRESPONDÊNCIA FUNCIONA (resumo; detalhes em matcher.js)
//  - O texto é quebrado em "tokens" (palavras) ignorando acento, maiúscula e separadores
//    (espaço, hífen, ponto, underline). "Proxy-Browser", "proxy_browser" e "ProxyBrowser"
//    viram a mesma coisa.
//  - "strong": o termo vale como TRECHO de qualquer token. "proxy" pega "proxybrowser",
//    "croxyproxy", "freeproxylist". Só entram aqui sequências longas/distintivas, que
//    praticamente não aparecem dentro de palavras comuns (por isso "tor" NÃO está aqui).
//  - "words": palavra/expressão inteira. Também pega a versão COLADA se o resto do token for
//    formado só por "companions" (ex.: "unblockedgames" = unblocked + games), então termos
//    curtos/ambíguos continuam sem casar por acaso dentro de outras palavras.
//  - "hostOnlyWords": igual a "words", mas só vale no NOME DO SITE (hostname), nunca no que
//    você digita numa busca. Para termos que são comuns demais em buscas normais
//    ("online browser games", "bypass surgery").
//  - O CAMINHO da URL (/wiki/Proxy, /docs/proxy) NÃO é checado: artigos e documentação sobre
//    proxy continuam abrindo. Só o hostname e o texto da busca contam.
//
// U7 — SERVIÇOS "GATEWAY" (tradutor, cache, arquivo web), IP LITERAL E DoH POR NOME
//  - "gatewayServices": tradutores de página e caches de buscador, que mostram OUTRO site por dentro
//    deles. Valem só DURANTE O BLOQUEIO. Motivo "gateway" (página de bloqueio, sem trava de 30 min).
//  - "archiveServices": serviços de arquivo web (Wayback Machine, archive.today...). Têm uso legítimo
//    grande, então só valem com a OPÇÃO ligada na aba Proteções (vem DESLIGADA) e durante o bloqueio.
//  - Formato dos dois: "host" (vale o host e subdomínios) ou "host/prefixo" (só aquele caminho:
//    "translate.google.*/translate" = /translate em qualquer translate.google.<país>). ".*" no fim do
//    host = qualquer sufixo de país (com, com.br, co.uk...). Sem "//" nem "]" nas entradas (o
//    TermLists.cs lê este arquivo com expressões simples).
//  - IP literal (http://203.0.113.5/) é tratado em matcher.js, sem lista: durante o bloqueio, todo IP
//    público é barrado (localhost e faixas privadas passam). Motivo "ip".
//  - "dohHosts": nomes de provedores de DNS-sobre-HTTPS. Vão para o arquivo hosts (camada permanente)
//    apontando para 127.0.0.1, então o navegador não acha o resolvedor e cai no DNS do sistema (que
//    consulta o hosts antes). Só nomes exatos: o hosts não aceita curinga.
//
// Depois de editar, recarregue a extensão (ou deixe o app regerar o .crx).

const CTC_PROXY = {
  // Domínios conhecidos. Bloqueia o domínio E todos os subdomínios.
  // Só vale a pena listar aqui os que NÃO têm "proxy"/"vpn"/"unblock" no nome — esses a
  // heurística já pega sozinha. Esta lista é um ponto de partida: proxies novos aparecem o
  // tempo todo, e quando um escapar, acrescente o domínio aqui.
  domains: [
    // navegadores na nuvem / remotos (a página roda num servidor; a extensão só vê a entrada)
    "browser.lol", "browserling.com", "send.win",
    // proxies web / desbloqueadores com nome opaco
    "hidester.com", "filterbypass.me", "anonymouse.org", "newipnow.com", "blockaway.net",
    "whoer.net", "hide.me", "hidemyass.com",
    // VPNs / redes de anonimato com nome opaco
    "mullvad.net", "windscribe.com", "tunnelbear.com", "hola.org", "psiphon.ca", "ultrasurf.us",
    "getlantern.org", "torproject.org", "betternet.co", "speedify.com", "zenmate.com",
    "privateinternetaccess.com", "hotspotshield.com", "wireguard.com", "openvpn.net",
  ],

  // U7: tradutores de página e caches de buscador (valem só durante o bloqueio; ver cabeçalho).
  // A extensão já avalia o DESTINO de ?u=, translate.goog e cache:https://..., mas um destino sem
  // esquema ("cache:alvo.com"), um POST ou um formato novo escapam; aqui o próprio serviço é barrado.
  gatewayServices: [
    // tradutor de páginas
    "translate.google.*/translate", "translate.google.*/translate_c", "translate.google.*/translate_p",
    "translate.google.*/website", "translate.googleusercontent.com", "translate.goog",
    "translatetheweb.com", "microsofttranslator.com", "translate.yandex.*/translate",
    // cache de buscadores
    "webcache.googleusercontent.com", "cc.bingj.com", "cache.baiducontent.com", "yandexwebcache.net",
    "cachedview.nl", "cachedpages.com",
  ],

  // U7: arquivo web. Uso legítimo grande: só vale com a opção ligada (BlockState.BlockWebArchives).
  archiveServices: [
    "web.archive.org", "archive.org/web", "archive.is", "archive.ph", "archive.today", "archive.md",
    "archive.li", "archive.vn", "archive.fo", "ghostarchive.org", "megalodon.jp", "webcitation.org",
    "perma.cc", "arquivo.pt/wayback",
  ],

  // U7: provedores de DNS-sobre-HTTPS conhecidos (só nomes exatos; vão para o hosts).
  dohHosts: [
    "dns.google", "dns.google.com", "cloudflare-dns.com", "mozilla.cloudflare-dns.com",
    "chrome.cloudflare-dns.com", "one.one.one.one", "1dot1dot1dot1.cloudflare-dns.com",
    "dns.quad9.net", "dns9.quad9.net", "dns10.quad9.net", "dns11.quad9.net",
    "doh.opendns.com", "doh.familyshield.opendns.com", "dns.adguard.com", "dns.adguard-dns.com",
    "dns-family.adguard.com", "dns.nextdns.io", "doh.cleanbrowsing.org", "doh.dns.sb",
    "doh.libredns.gr", "doh.mullvad.net", "dns.mullvad.net", "doh.applied-privacy.net",
    "dns.digitale-gesellschaft.ch", "doh.li", "dns.alidns.com", "doh.pub",
  ],

  // Sites onde NUNCA se aplica a heurística de proxy (nem no nome do site, nem na busca feita
  // dentro deles). Útil p/ documentação técnica e para o portal de VPN do seu trabalho
  // (ex.: "vpn.suaempresa.com.br"). Subdomínios também valem.
  allowDomains: [
    "developer.mozilla.org", "learn.microsoft.com", "stackoverflow.com", "docs.python.org",
    "nginx.org", "haproxy.org", "kubernetes.io", "docs.docker.com",
  ],

  // Casa como TRECHO de qualquer palavra (nome do site e busca).
  strong: [
    "proxy", "proxies", "vpn", "anonymizer", "unblocker", "webunblocker", "torbrowser",
    "hidemyass", "surfshark", "cyberghost", "mullvad", "windscribe", "zenmate", "tunnelbear",
    "psiphon", "ultrasurf", "hotspotshield", "rammerhead", "womginx",
  ],

  // Palavra/expressão inteira (nome do site e busca). Versão colada também é detectada.
  words: [
    "socks5", "socks4", "tor browser", "anonymous browser", "anonymous browsing",
    "hide ip", "hide my ip", "fake ip", "ip hider",
    "browser lol", "browserling",
    "bypass blocker", "bypass block", "bypass filter", "bypass firewall", "bypass restrictions",
    "bypass website blocker",
    "unblocked games", "unblocked sites", "unblocked websites", "unblock sites", "unblock site",
    "unblock websites", "unblock website", "unblock youtube",
    "burlar bloqueio", "burlar o bloqueio", "contornar bloqueio", "contornar o bloqueio",
    "driblar bloqueio", "desbloquear site", "desbloquear sites", "navegador anonimo",
  ],

  // Igual a "words", mas SÓ no nome do site (hostname).
  hostOnlyWords: [
    "unblock", "unblocked", "bypass",
    "cloud browser", "virtual browser", "remote browser", "online browser", "browser online",
    "browser emulator", "browser simulator", "navegador virtual", "navegador na nuvem",
  ],

  // Palavras que costumam vir coladas aos termos acima. Só servem para reconhecer a versão
  // colada de "words"/"hostOnlyWords" (ex.: unblocked + games). Evite palavras de 1-2 letras.
  companions: [
    "browser", "browsers", "site", "sites", "website", "websites", "server", "servers", "list",
    "free", "online", "web", "unblock", "unblocked", "youtube", "school", "schools", "hide",
    "hider", "anonymous", "anon", "fast", "best", "pro", "plus", "premium", "download",
    "downloads", "app", "apps", "tool", "tools", "access", "secure", "private", "surf",
    "surfing", "game", "games", "tiktok", "facebook", "instagram", "netflix", "discord",
    "roblox", "twitter", "reddit", "work", "new", "top", "now", "lol", "link", "links",
    "gratis", "gratuito", "navegador", "melhor", "baixar", "acesso", "anonimo", "livre",
    "rapido", "seguro", "para", "cloud", "virtual", "remote",
  ],

  // Expressões legítimas (programação/infra) removidas do texto ANTES de comparar, para
  // "reverse proxy nginx" ou "proxy design pattern" não serem barrados.
  safePhrases: [
    "reverse proxy", "proxy pass", "proxy pattern", "proxy design pattern", "proxy object",
    "es6 proxy", "javascript proxy",
  ],

  // Palavras inteiras que contêm um termo "strong" mas são legítimas.
  safeTokens: ["haproxy", "proxyman"],
};
