using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SharkBlocker.Models;

namespace SharkBlocker.Services;

/// <summary>
/// Porte em C# do avaliador de URLs da extensão (BrowserExtension\background.js + matcher.js).
/// Existe para a camada de DESKTOP: o <see cref="BrowserUrlWatcher"/> lê a barra de endereço do
/// navegador e usa isto, então o bloqueio NÃO depende da extensão estar instalada. As listas vêm
/// dos mesmos arquivos da extensão (<see cref="TermLists"/>), e a lógica é a mesma:
///   - sites/palavras da sua lista (só com bloqueio ativo);
///   - conteúdo adulto (palavra inteira, sem acento/maiúscula);
///   - proxy/VPN/navegador remoto: domínios conhecidos, nome do site e texto da busca, com
///     detecção de palavras coladas ("proxybrowser");
///   - destino escondido dentro de outra URL (?url=, base64, arquivo, translate.goog);
///   - U7, só com bloqueio ativo: tradutor/cache/arquivo web ("gateway") e IP literal público ("ip").
///     Nenhum dos dois trava a tela (como "url"): o vigia de desktop só age nos <see cref="LockReasons"/>.
///
/// Se mudar a regra no background.js, mude aqui também (e vice-versa).
/// </summary>
public static class UrlPolicy
{
    /// <summary>Motivos que travam a tela. "url" e "keyword" (só no meio da URL) não travam.</summary>
    public static readonly HashSet<string> LockReasons = new() { "adult", "proxy", "keyword_search" };

    private const int MaxEmbedDepth = 2;

    /// <summary>
    /// Exceções FIXAS (só no código): endereços técnicos cujo caminho contém "nsfw" sem ser conteúdo adulto. Só o trecho
    /// exato "host/dono/repositório" é neutralizado; o resto da URL (subpastas, busca, fragmento) continua sendo avaliado,
    /// e qualquer outro endereço com "nsfw" continua barrado. Par = (trecho em minúsculas sem "www.", texto neutro).
    /// Espelho: background.js (CTC_ADULT_EXCEPTIONS).
    /// </summary>
    private static readonly (string Prefix, string Safe)[] AdultExceptions =
    {
        ("github.com/gantman/nsfw_model", "github.com/gantman/repo-modelo"), // repositório do classificador
        ("s3.amazonaws.com/ir_public/nsfwjscdn", "s3.amazonaws.com/ir_public/modelo-cdn"), // pasta pública dos arquivos .h5 do modelo
    };

    private static readonly Regex S3Regional = new(@"^s3[.-](?:dualstack\.)?[a-z]{2}(?:-[a-z]+)+-\d\.amazonaws\.com$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Host canônico para as exceções: endpoints regionais do S3 (s3.us-east-1..., s3-us-west-2...) valem como s3.amazonaws.com.</summary>
    private static string CanonExceptionHost(string host)
    {
        var h = host.ToLowerInvariant();
        if (h.StartsWith("www.", StringComparison.Ordinal)) h = h.Substring(4);
        h = h.TrimEnd('.');
        return S3Regional.IsMatch(h) ? "s3.amazonaws.com" : h;
    }

    /// <summary>
    /// host + caminho com o trecho de exceção (se houver) trocado pelo texto neutro. O caminho é comparado DECODIFICADO e sem "//"
    /// repetido (nsfwjscdn%2Farquivo e //ir_public/... valem como o endereço normal). Espelho: neutralizedHead (background.js).
    /// </summary>
    private static string NeutralizedHead(string host, string path, bool typing)
    {
        var h = CanonExceptionHost(host);
        var dpath = Regex.Replace(SafeDecode(path), "/{2,}", "/");
        var key = h + dpath.ToLowerInvariant();
        foreach (var (prefix, safe) in AdultExceptions)
        {
            if (key == prefix || key.StartsWith(prefix + "/", StringComparison.Ordinal))
                // dentro deste repositório a sigla "nsfw" (ex.: o arquivo nsfw_mobilenet2....h5 em /releases/download/) não é sinal
                // de conteúdo adulto; os demais termos continuam valendo no resto do caminho, na busca e no fragmento.
                return safe + dpath.Substring(prefix.Length - h.Length).Replace("nsfw", "modelo", StringComparison.OrdinalIgnoreCase);
            // texto ainda sendo digitado na barra de endereço: um começo do endereço liberado também vale
            if (typing && key.Length < prefix.Length && prefix.StartsWith(key, StringComparison.Ordinal))
                return safe;
        }
        return host + path;
    }

    /// <summary>Devolve o motivo ("url" | "keyword" | "keyword_search" | "adult" | "proxy" | "gateway" | "ip") ou null.</summary>
    public static string? Evaluate(string url, BlockState state) =>
        Evaluate(url, state, TermLists.Current(), 0, false);

    /// <summary>
    /// Igual a Evaluate, mas para o texto lido da barra de endereço a cada 1,5 s, que pode estar ainda sendo DIGITADO
    /// (ex.: "github.com/GantMan/nsfw" antes de chegar em "_model"). Só muda a exceção fixa: um começo do endereço liberado
    /// também é aceito; query e fragmento continuam sendo avaliados. A extensão não precisa disso (só vê a navegação já enviada).
    /// </summary>
    public static string? EvaluateTyped(string url, BlockState state) =>
        Evaluate(url, state, TermLists.Current(), 0, true);

    private static string? Evaluate(string url, BlockState state, TermLists.Snapshot terms, int depth, bool typing)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return null;

        var engine = GetEngine(terms);
        var host = u.IdnHost.ToLowerInvariant();
        var decodedRaw = SafeDecode(NeutralizedHead(host, u.AbsolutePath, typing) + " " + u.Query + " " + u.Fragment);
        var queryRaw = SearchQuery(u);
        var decoded = NormPlain(decodedRaw);
        var query = NormPlain(queryRaw);

        // Versões "limpas" do texto: sem leetspeak (n4k3d), sem letras de outros alfabetos, sem
        // caracteres invisíveis, sem separadores (p.o.r.n) e sem letras esticadas (pooorn).
        var decodedFolds = Deobfuscator.Variants(decodedRaw);
        var queryFolds = Deobfuscator.Variants(queryRaw);
        var decodedAll = new List<string> { decoded };
        decodedAll.AddRange(decodedFolds);
        var queryAll = new List<string>();
        if (query.Length > 0)
        {
            queryAll.Add(query);
            queryAll.AddRange(queryFolds);
        }

        if (state.IsLocked)
        {
            foreach (var d in state.BlockedWebsites ?? new List<string>())
            {
                if (HostMatches(host, d) && !AllowedByException(host, d)) return "url";
            }

            // Palavras do usuário: no texto original E nas versões limpas. A palavra cadastrada
            // também é limpa ("p0rn" na lista vale "porn"). Em buscas (texto curto), junta tudo
            // sem espaços ("po rn") para palavras de 4+ letras.
            var kws = new List<(string Nk, string Fk, string Sk)>();
            foreach (var k in state.BlockedKeywords ?? new List<string>())
            {
                var nk = NormPlain(k).Trim();
                if (nk.Length == 0) continue;
                var fk = Deobfuscator.FoldOne(k);
                kws.Add((nk, fk, Deobfuscator.Squash(fk)));
            }

            foreach (var (nk, fk, sk) in kws)
            {
                if (queryAll.Any(q => q.Contains(nk, StringComparison.Ordinal) ||
                                      (fk.Length > 0 && q.Contains(fk, StringComparison.Ordinal))))
                    return "keyword_search";
                if (sk.Length >= 4 && queryFolds.Any(q => Deobfuscator.Squash(q).Contains(sk, StringComparison.Ordinal)))
                    return "keyword_search";
            }
            foreach (var (nk, fk, _) in kws)
            {
                if (decodedAll.Any(d => d.Contains(nk, StringComparison.Ordinal) ||
                                        (fk.Length > 0 && d.Contains(fk, StringComparison.Ordinal))))
                    return "keyword";
            }
        }

        if (engine.AdultUrl != null &&
            (decodedAll.Any(d => engine.AdultUrl.IsMatch(d)) || queryAll.Any(q => engine.AdultUrl.IsMatch(q))))
            return "adult";

        if (engine.AdultSearch != null && queryAll.Any(q => engine.AdultSearch.IsMatch(q)))
            return "adult";

        // Texto "picado" (p o r n h u b, po😀rn): sem separador nenhum, SÓ em buscas (texto curto).
        // Termos fortes de 6+ letras valem como trecho; e uma busca que, sem separadores, é
        // exatamente um termo da lista (ex.: "po rn", "s e x") também.
        if (queryAll.Count > 0)
        {
            foreach (var q in queryAll)
            {
                if (q.Length > 160) continue;
                var sq = Deobfuscator.Squash(q);
                if (sq.Length == 0) continue;
                if (engine.SquashExact.Contains(sq) || engine.SquashStrong.Any(t => sq.Contains(t, StringComparison.Ordinal)))
                    return "adult";
            }
        }

        // Termos FORTES: valem como trecho em qualquer parte da URL (domínio colado, tipo
        // "megaxvideosbr.com"). E terminações exclusivamente adultas (.xxx, .porn, .adult, .sex).
        if (engine.AdultStrong.Count > 0 &&
            decodedAll.Any(d => engine.AdultStrong.Any(t => d.Contains(t, StringComparison.Ordinal))))
            return "adult";
        if (engine.AdultTlds.Any(t => host.EndsWith("." + t, StringComparison.Ordinal)))
            return "adult";

        // Sites sempre bloqueados (adult-terms.js) e Shorts do YouTube: com proteções permanentes OU bloqueio ativo.
        if (state.IsLocked || state.PermanentProtectionsEnabled)
        {
            foreach (var d in terms.PermanentSites)
            {
                if (HostMatches(host, d) && !AllowedByException(host, d)) return "url";
            }
            if (HostMatches(host, "youtube.com") && Regex.IsMatch(u.AbsolutePath, @"^/shorts(/|$)", RegexOptions.IgnoreCase)) return "url";
        }

        if (state.IsLocked || state.PermanentProtectionsEnabled)
        {
            var proxy = EvaluateProxy(engine, host, query);
            if (proxy != null) return proxy;
        }

        if (depth < MaxEmbedDepth)
        {
            foreach (var inner in EmbeddedUrls(u))
            {
                var r = Evaluate(inner, state, terms, depth + 1, false);
                if (r != null) return r;
            }
        }

        // U7, só durante o bloqueio: tradutor/cache/arquivo web e IP literal público (o IP ignora o hosts).
        // Fica DEPOIS do destino embutido: um motivo mais específico do alvo (site da lista, adulto) ganha.
        if (state.IsLocked)
        {
            var gateway = EvaluateGateway(engine, host, u.AbsolutePath, state.BlockWebArchives);
            if (gateway != null) return gateway;
            if (IsPublicLiteralIp(u)) return "ip";
        }

        return null;
    }

    private static string? EvaluateGateway(Engine engine, string host, string path, bool archivesOn)
    {
        if (engine.Allow.Any(d => HostMatches(host, d))) return null;
        if (engine.Gateway.Any(p => ServiceMatches(host, path, p))) return "gateway";
        if (archivesOn && engine.Archive.Any(p => ServiceMatches(host, path, p))) return "gateway";
        return null;
    }

    /// <summary>
    /// Padrão "host" ou "host/prefixo" (espelho de CTCMatcher.serviceMatches). ".*" no fim do host = qualquer
    /// sufixo de país (com, com.br, co.uk). O caminho casa por segmento inteiro ("/web" não casa com "/website").
    /// </summary>
    internal static bool ServiceMatches(string hostname, string pathname, string pattern)
    {
        var p = Regex.Replace(pattern.Trim().ToLowerInvariant(), @"^https?://", "");
        p = Regex.Replace(p, @"^www\.", "");
        if (p.Length == 0) return false;

        var slash = p.IndexOf('/');
        var hostPat = slash < 0 ? p : p.Substring(0, slash);
        var pathPat = slash < 0 ? "" : p.Substring(slash).TrimEnd('/');
        var h = hostname.ToLowerInvariant().TrimEnd('.');

        bool hostOk;
        if (hostPat.EndsWith(".*", StringComparison.Ordinal))
        {
            var baseName = Regex.Escape(hostPat.Substring(0, hostPat.Length - 2));
            hostOk = Regex.IsMatch(h, @"(?:^|\.)" + baseName + @"\.[a-z]{2,3}(?:\.[a-z]{2})?$", RegexOptions.CultureInvariant);
        }
        else
        {
            hostOk = h == hostPat || h.EndsWith("." + hostPat, StringComparison.Ordinal);
        }
        if (!hostOk) return false;
        if (pathPat.Length == 0) return true;

        var path = (string.IsNullOrEmpty(pathname) ? "/" : pathname).ToLowerInvariant();
        return path == pathPat || path.StartsWith(pathPat + "/", StringComparison.Ordinal);
    }

    // ---------- U7: IP literal ----------

    /// <summary>
    /// O host da URL é um número de IP PÚBLICO? (IPv4 ou IPv6; localhost e faixas privadas/locais dão false.)
    /// Só olha hosts que o próprio Uri reconhece como IP: nomes ("localhost") nunca entram.
    /// </summary>
    internal static bool IsPublicLiteralIp(Uri u)
    {
        if (u.HostNameType != UriHostNameType.IPv4 && u.HostNameType != UriHostNameType.IPv6) return false;

        var text = u.DnsSafeHost.Trim('[', ']');
        var scope = text.IndexOf('%');
        if (scope >= 0) text = text.Substring(0, scope);
        if (!IPAddress.TryParse(text, out var ip)) return false;
        return !IsPrivateOrLocal(ip);
    }

    private static bool IsPrivateOrLocal(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 100.64.0.0/10 (CGNAT, Tailscale)
                || (b[0] == 169 && b[1] == 254)                 // link-local
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168);
        }

        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (b.All(x => x == 0)) return true;                                      // ::
        if (IPAddress.IsLoopback(ip)) return true;                                // ::1
        if (b[0] == 0xfe && ((b[1] & 0xc0) == 0x80 || (b[1] & 0xc0) == 0xc0)) return true;          // fe80::/10 e fec0::/10
        if ((b[0] & 0xfe) == 0xfc) return true;                                   // fc00::/7 (ULA)

        var embedded = new IPAddress(new[] { b[12], b[13], b[14], b[15] });
        if (b.Take(12).All(x => x == 0)) return IsPrivateOrLocal(embedded);       // ::a.b.c.d (obsoleto)
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
            return IsPrivateOrLocal(embedded);                                    // 64:ff9b::/96 (NAT64)
        return false;
    }

    private static string? EvaluateProxy(Engine engine, string host, string query)
    {
        if (engine.Allow.Any(d => HostMatches(host, d))) return null;
        if (engine.Domains.Any(d => HostMatches(host, d))) return "proxy";
        if (engine.Proxy.Test(host, hostScope: true) != null) return "proxy";
        if (query.Length > 0 && engine.Proxy.Test(query, hostScope: false) != null) return "proxy";
        return null;
    }

    // ---------- motor (compilado uma vez por versão dos arquivos de lista) ----------

    private sealed class Engine
    {
        public Regex? AdultUrl { get; init; }
        public Regex? AdultSearch { get; init; }
        public List<string> AdultStrong { get; init; } = new();
        public HashSet<string> SquashExact { get; init; } = new(StringComparer.Ordinal);
        public List<string> SquashStrong { get; init; } = new();
        public List<string> AdultTlds { get; init; } = new();
        public List<string> Domains { get; init; } = new();
        public List<string> Allow { get; init; } = new();
        public List<string> Gateway { get; init; } = new();
        public List<string> Archive { get; init; } = new();
        public ProxyMatcher Proxy { get; init; } = null!;
    }

    private static readonly object EngineGate = new();
    private static TermLists.Snapshot? _engineSnapshot;
    private static Engine? _engine;

    private static Engine GetEngine(TermLists.Snapshot snap)
    {
        lock (EngineGate)
        {
            if (_engine == null || !ReferenceEquals(snap, _engineSnapshot))
            {
                _engine = new Engine
                {
                    AdultUrl = WordRegex(snap.AdultUrlTerms),
                    AdultSearch = WordRegex(snap.AdultSearchTerms),
                    AdultStrong = snap.AdultStrongTerms.Select(NormPlain).Where(t => t.Length >= 4).ToList(),
                    SquashExact = snap.AdultUrlTerms.Concat(snap.AdultSearchTerms)
                        .Select(t => Deobfuscator.Squash(NormPlain(t))).Where(t => t.Length >= 3)
                        .ToHashSet(StringComparer.Ordinal),
                    SquashStrong = snap.AdultStrongTerms
                        .Select(t => Deobfuscator.Squash(NormPlain(t))).Where(t => t.Length >= 6).Distinct().ToList(),
                    AdultTlds = snap.AdultTlds.Select(t => t.Trim().TrimStart('.').ToLowerInvariant()).Where(t => t.Length > 0).ToList(),
                    Domains = snap.ProxyDomains,
                    Allow = snap.ProxyAllowDomains,
                    Gateway = snap.GatewayServices,
                    Archive = snap.ArchiveServices,
                    Proxy = new ProxyMatcher(snap),
                };
                _engineSnapshot = snap;
            }
            return _engine;
        }
    }

    // ---------- normalização ----------

    private static readonly Regex CamelBoundary = new(@"([\p{Ll}\p{N}])(\p{Lu})", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex NonAlnumSplit = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>minúsculas + sem acento (equivale ao norm() do background.js).</summary>
    private static string NormPlain(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var decomposed = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>NormPlain + separa camelCase antes de minusculizar (equivale ao normalize() do matcher.js).</summary>
    private static string NormCamel(string? s) =>
        string.IsNullOrEmpty(s) ? "" : NormPlain(CamelBoundary.Replace(s, "$1 $2"));

    private static List<string> Tokenize(string normalized) =>
        NonAlnumSplit.Split(normalized).Where(t => t.Length > 0).ToList();

    /// <summary>Palavra inteira, para os termos de conteúdo adulto ("nude" não casa com "denuded").</summary>
    private static Regex? WordRegex(IEnumerable<string> terms)
    {
        // Japonês/chinês/coreano não separam palavras com espaço: esses termos casam como trecho.
        static bool IsCjk(string t) => t.Any(c => (c >= '\u3040' && c <= '\u30ff') || (c >= '\u3400' && c <= '\u9fff') || (c >= '\uac00' && c <= '\ud7af'));
        var all = terms.Select(NormPlain).Where(t => t.Length > 0).ToList();
        var parts = all.Where(t => !IsCjk(t)).Select(Regex.Escape).ToList();
        var cjk = all.Where(IsCjk).Select(Regex.Escape).ToList();
        var pieces = new List<string>();
        if (parts.Count > 0) pieces.Add(@"(?:^|[^\p{L}\p{N}])(?:" + string.Join("|", parts) + @")(?=$|[^\p{L}\p{N}])");
        if (cjk.Count > 0) pieces.Add(@"(?:" + string.Join("|", cjk) + @")");
        if (pieces.Count == 0) return null;
        return new Regex(string.Join("|", pieces), RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    // ---------- URL: partes ----------

    private static string SafeDecode(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch { return s; }
    }

    /// <summary>
    /// true se [host] é uma liberação fixa (<see cref="DnsFilterCore.AlwaysAllowedHosts"/>, ex.: music.youtube.com) e a
    /// entrada [domain] bloqueia só um domínio-PAI dela. Espelha allowedByException do background.js.
    /// </summary>
    private static bool AllowedByException(string host, string domain)
    {
        var h = host.ToLowerInvariant();
        var d = domain.Trim().ToLowerInvariant();
        d = Regex.Replace(d, @"^https?://", "");
        d = Regex.Replace(d, @"^www\.", "");
        d = Regex.Replace(d, @"/.*$", "");
        foreach (var a in DnsFilterCore.AlwaysAllowedHosts)
        {
            var hostIsAllowed = h == a || h.EndsWith("." + a, StringComparison.Ordinal);
            var entryIsExplicit = d == a || d.EndsWith("." + a, StringComparison.Ordinal);
            if (hostIsAllowed && !entryIsExplicit) return true;
        }
        return false;
    }

    private static bool HostMatches(string hostname, string domain)
    {
        var h = hostname.ToLowerInvariant();
        var d = domain.Trim().ToLowerInvariant();
        d = Regex.Replace(d, @"^https?://", "");
        d = Regex.Replace(d, @"^www\.", "");
        d = Regex.Replace(d, @"/.*$", "");
        return d.Length > 0 && (h == d || h.EndsWith("." + d, StringComparison.Ordinal));
    }

    private static List<KeyValuePair<string, string>> QueryPairs(Uri u)
    {
        var list = new List<KeyValuePair<string, string>>();
        var q = u.Query;
        if (q.StartsWith('?')) q = q.Substring(1);
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var key = i < 0 ? part : part.Substring(0, i);
            var val = i < 0 ? "" : part.Substring(i + 1);
            list.Add(new KeyValuePair<string, string>(SafeDecode(key), SafeDecode(val)));
        }
        return list;
    }

    private static readonly HashSet<string> SearchParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "q", "query", "search", "search_query", "p", "text", "s", "k", "wd", "keyword", "keywords", "term", "terms",
        "searchterm", "qry", "kw", "word", "searchtext", "search_term", "find", "qstr", "sq", "qq", "input", "qs",
        "query_text", "searchquery", "search_text", "q1", "q2", "ss", "qt", "key", "what", "phrase", "words",
    };

    /// <summary>
    /// O que foi digitado numa busca. CORRIGIDO: junta TODOS os parâmetros de busca (repetidos, em
    /// outro nome ou no fragmento "#q=..."), separados por " ; ". Antes só o primeiro era olhado.
    /// </summary>
    private static string SearchQuery(Uri u)
    {
        var parts = new List<string>();
        foreach (var pair in QueryPairs(u))
        {
            if (pair.Value.Length > 0 && SearchParams.Contains(pair.Key))
                parts.Add(Regex.Replace(pair.Value, @"[-_.]+", " "));
        }

        var frag = u.Fragment;
        if (frag.Length > 1 && frag.Contains('='))
        {
            foreach (var part in frag.TrimStart('#', '/', '?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var i = part.IndexOf('=');
                if (i <= 0) continue;
                var key = SafeDecode(part.Substring(0, i));
                var val = SafeDecode(part.Substring(i + 1));
                if (val.Length > 0 && SearchParams.Contains(key))
                    parts.Add(Regex.Replace(val, @"[-_.]+", " "));
            }
        }
        return string.Join(" ; ", parts);
    }

    // ---------- URLs embutidas em outra URL ----------

    private static readonly HashSet<string> EmbedParams = new(StringComparer.Ordinal)
    {
        "url", "u", "uri", "link", "target", "site", "dest", "destination", "redirect",
        "redirect_url", "redirect_uri", "redir", "goto", "go", "href", "to", "host", "domain",
        "page", "web", "website", "address", "src",
    };

    private static readonly Regex DomainRe = new(
        @"^(?:[a-z0-9-]+\.)+[a-z]{2,}(?::\d+)?(?:[/?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InnerUrlRe = new(
        @"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PathUrlRe = new(
        @"https?:/+[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Base64UrlRe = new(
        @"^aHR0c[A-Za-z0-9+/_-]{6,}={0,2}$", RegexOptions.CultureInvariant);
    private static readonly Regex SchemeStartRe = new(
        @"^(?:https?:)?//", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SingleSlashSchemeRe = new(
        @"^https?:/[^/]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? ToHttpUrl(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var normalized = Regex.Replace(s.Trim(), @"^(https?):/+", "$1://", RegexOptions.IgnoreCase);
        if (!Regex.IsMatch(normalized, @"^https?://", RegexOptions.IgnoreCase)) return null;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var parsed)) return null;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return null;
        return parsed.AbsoluteUri;
    }

    // Valores em base64 que começam com "http" (aHR0c...), como alguns proxies fazem.
    private static string? DecodeBase64Url(string v)
    {
        if (!Base64UrlRe.IsMatch(v)) return null;
        try
        {
            var b = v.Replace('-', '+').Replace('_', '/');
            b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(b));
            return Regex.IsMatch(text, @"^https?://", RegexOptions.IgnoreCase) ? text : null;
        }
        catch { return null; }
    }

    // "exemplo-com.translate.goog" -> "exemplo.com" ("--" é um hífen de verdade).
    private static string? TranslateGoogTarget(string host)
    {
        var m = Regex.Match(host.ToLowerInvariant(), @"^(.+)\.translate\.goog$");
        if (!m.Success) return null;
        return m.Groups[1].Value.Replace("--", "\u0000").Replace("-", ".").Replace("\u0000", "-");
    }

    private static List<string> EmbeddedUrls(Uri u)
    {
        var found = new List<string>();
        void Add(string? s)
        {
            var h = ToHttpUrl(s);
            if (h != null && !found.Contains(h)) found.Add(h);
        }

        foreach (var pair in QueryPairs(u))
        {
            var key = pair.Key;
            var v = pair.Value.Trim();
            if (v.Length == 0) continue;

            var b64 = DecodeBase64Url(v);
            if (b64 != null) { Add(b64); continue; }

            if (SchemeStartRe.IsMatch(v) || SingleSlashSchemeRe.IsMatch(v))
            {
                Add(v.StartsWith("//", StringComparison.Ordinal) ? "https:" + v : v);
                continue;
            }

            if (EmbedParams.Contains(key.ToLowerInvariant()) && DomainRe.IsMatch(v))
            {
                Add("https://" + v);
                continue;
            }

            var inner = InnerUrlRe.Match(v); // "cache:https://alvo"
            if (inner.Success) Add(inner.Value);
        }

        var pathPart = PathUrlRe.Match(SafeDecode(u.AbsolutePath + u.Fragment));
        if (pathPart.Success) Add(pathPart.Value);

        var translated = TranslateGoogTarget(u.IdnHost);
        if (translated != null) Add("https://" + translated + "/");

        return found.Take(5).ToList();
    }

    // ---------- correspondência de proxy/VPN (porte do matcher.js) ----------

    private sealed class ProxyMatcher
    {
        private const int MaxTokens = 120;
        private const int MaxTokenLen = 80;
        private const int MinPiece = 3;

        private sealed class Scope
        {
            public readonly HashSet<string> Single = new();
            public readonly List<string> Phrases = new();
            public readonly Dictionary<string, bool> Dict = new(); // peça -> é termo?
            public int MaxLen = MinPiece;
        }

        private readonly List<string> _strong;
        private readonly HashSet<string> _safeTokens;
        private readonly Scope _queryScope;
        private readonly Scope _hostScope;
        private readonly Regex? _safeRe;

        public ProxyMatcher(TermLists.Snapshot t)
        {
            _strong = t.ProxyStrong
                .Select(s => NormCamel(s).Replace(" ", ""))
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();
            var companions = t.ProxyCompanions.Select(c => NormCamel(c).Replace(" ", "")).ToList();
            _safeTokens = new HashSet<string>(t.ProxySafeTokens.Select(x => NormCamel(x)));

            _queryScope = BuildScope(_strong, t.ProxyWords, companions);
            _hostScope = BuildScope(_strong, t.ProxyWords.Concat(t.ProxyHostOnlyWords), companions);

            var safeParts = t.ProxySafePhrases
                .Select(p => string.Join(@"[^\p{L}\p{N}]+", Tokenize(NormCamel(p)).Select(Regex.Escape)))
                .Where(p => p.Length > 0)
                .ToList();
            _safeRe = safeParts.Count == 0
                ? null
                : new Regex(@"(?<![\p{L}\p{N}])(?:" + string.Join("|", safeParts) + @")(?![\p{L}\p{N}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static Scope BuildScope(List<string> strong, IEnumerable<string> words, List<string> companions)
        {
            var sc = new Scope();

            void Put(string piece, bool isTerm)
            {
                if (piece.Length < MinPiece) return;
                if (isTerm) sc.Dict[piece] = true;
                else if (!sc.Dict.ContainsKey(piece)) sc.Dict[piece] = false;
                if (piece.Length > sc.MaxLen) sc.MaxLen = piece.Length;
            }

            foreach (var w in words)
            {
                var toks = Tokenize(NormCamel(w));
                if (toks.Count == 0) continue;
                if (toks.Count == 1) sc.Single.Add(toks[0]);
                else sc.Phrases.Add(" " + string.Join(" ", toks) + " ");
                Put(string.Concat(toks), true); // versão colada: "browser lol" -> "browserlol"
            }
            foreach (var s in strong) Put(s, true);
            foreach (var c in companions) Put(c, false);
            return sc;
        }

        /// <summary>O termo que casou, ou null. hostScope=true usa também as palavras só-de-hostname.</summary>
        public string? Test(string text, bool hostScope)
        {
            var scope = hostScope ? _hostScope : _queryScope;
            var t = NormCamel(text);
            if (t.Trim().Length == 0) return null;
            if (_safeRe != null) t = _safeRe.Replace(t, " ");

            var tokens = Tokenize(t);
            if (tokens.Count == 0) return null;
            if (tokens.Count > MaxTokens) tokens = tokens.GetRange(0, MaxTokens);

            var padded = " " + string.Join(" ", tokens) + " ";
            foreach (var ph in scope.Phrases)
            {
                if (padded.Contains(ph, StringComparison.Ordinal)) return ph.Trim();
            }

            var candidates = new List<string>(tokens);
            candidates.AddRange(JoinSingleLetterRuns(tokens));

            foreach (var tok in candidates)
            {
                if (tok.Length > MaxTokenLen || _safeTokens.Contains(tok)) continue;

                var hit = MatchToken(tok, scope);
                if (hit != null) return hit;

                // leetspeak ("pr0xy"): só em palavras que misturam letras e números.
                if (tok.Any(char.IsLetter) && tok.Any(char.IsDigit))
                {
                    var folded = FoldLeet(tok);
                    if (folded != tok && !_safeTokens.Contains(folded))
                    {
                        hit = MatchToken(folded, scope);
                        if (hit != null) return hit;
                    }
                }
            }
            return null;
        }

        private string? MatchToken(string tok, Scope scope)
        {
            if (scope.Single.Contains(tok)) return tok;
            foreach (var s in _strong)
            {
                if (tok.Contains(s, StringComparison.Ordinal)) return s;
            }
            if (Segmentable(tok, scope.Dict, scope.MaxLen)) return tok;
            return null;
        }

        private static string FoldLeet(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                sb.Append(c switch
                {
                    '0' => 'o',
                    '1' => 'i',
                    '3' => 'e',
                    '4' => 'a',
                    '5' => 's',
                    '7' => 't',
                    _ => c,
                });
            }
            return sb.ToString();
        }

        // "p r o x y" / "v.p.n": junta sequências de 3+ tokens de uma letra só.
        private static List<string> JoinSingleLetterRuns(List<string> tokens)
        {
            var result = new List<string>();
            var run = new StringBuilder();
            var count = 0;

            void Flush()
            {
                if (count >= 3) result.Add(run.ToString());
                run.Clear();
                count = 0;
            }

            foreach (var t in tokens)
            {
                if (t.Length == 1 && char.IsLetter(t[0])) { run.Append(t); count++; }
                else Flush();
            }
            Flush();
            return result;
        }

        // O token inteiro é formado só por peças conhecidas (termos + companions, dígitos soltos
        // valem como enchimento) e tem ao menos UM termo? Programação dinâmica.
        // reach[i]: -1 = inalcançável, 0 = alcançável sem termo, 1 = alcançável com termo.
        private static bool Segmentable(string token, Dictionary<string, bool> dict, int maxLen)
        {
            var n = token.Length;
            var reach = new int[n + 1];
            Array.Fill(reach, -1);
            reach[0] = 0;

            for (var i = 0; i < n; i++)
            {
                if (reach[i] < 0) continue;

                if (char.IsDigit(token[i]))
                {
                    var j = i;
                    while (j < n && char.IsDigit(token[j])) j++;
                    for (var k = i + 1; k <= j; k++) reach[k] = Math.Max(reach[k], reach[i]);
                }

                for (var len = MinPiece; len <= maxLen && i + len <= n; len++)
                {
                    if (!dict.TryGetValue(token.Substring(i, len), out var isTerm)) continue;
                    var value = (reach[i] == 1 || isTerm) ? 1 : 0;
                    reach[i + len] = Math.Max(reach[i + len], value);
                }
            }
            return reach[n] == 1;
        }
    }
}
