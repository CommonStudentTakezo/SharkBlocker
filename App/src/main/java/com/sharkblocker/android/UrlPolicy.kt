package com.sharkblocker.android

import java.net.URLEncoder
import java.text.Normalizer

/**
 * Regras de URL e de texto de busca. Porte de evaluateUrl (BrowserExtension/background.js) e UrlPolicy.cs.
 * Devolve o MOTIVO ("url" | "keyword" | "keyword_search" | "adult" | "proxy" | "gateway" | "ip") ou null.
 * No celular as proteções permanentes estão sempre ligadas (o Device Owner as sustenta).
 */
object UrlPolicy {
    const val MAX_EMBED_DEPTH = 2

    /** Motivos que travam a tela por 30 min; os demais só barram a página. */
    val LOCK_REASONS = setOf("adult", "proxy", "keyword_search")

    class Opts(
        val locked: Boolean,
        val sites: List<String>,
        val keywords: List<String>,
        val blockWebArchives: Boolean,
    )

    private val SEARCH_KEYS = setOf(
        "q", "query", "search", "search_query", "p", "text", "s", "k", "wd", "keyword", "keywords", "term", "terms",
        "searchterm", "qry", "kw", "word", "searchtext", "search_term", "find", "qstr", "sq", "qq", "input", "qs",
        "query_text", "searchquery", "search_text", "q1", "q2", "ss", "qt", "key", "what", "phrase", "words",
    )

    private val MARKS = Regex("[\\u0300-\\u036f]")
    private val CJK = Regex("[\\u3040-\\u30ff\\u3400-\\u9fff\\uac00-\\ud7af]")
    private val SEP = Regex("[-_.]+")

    private fun norm(s: String?): String =
        MARKS.replace(Normalizer.normalize((s ?: "").lowercase(), Normalizer.Form.NFD), "")

    private fun safeDecode(s: String): String = try {
        java.net.URLDecoder.decode(s, "UTF-8")
    } catch (_: Throwable) {
        s
    }

    /**
     * Exceções FIXAS (só no código, não editáveis no app): endereços técnicos cujo caminho contém "nsfw" sem ser conteúdo adulto.
     * Só o trecho exato "host/dono/repositório" é neutralizado; o resto da URL (outras pastas, busca, fragmento) continua
     * sendo avaliado normalmente, e qualquer outro endereço com "nsfw" continua barrado.
     * Par = (trecho em minúsculas sem "www.", texto neutro que o substitui).
     */
    private val ADULT_EXCEPTIONS = listOf(
        Pair("github.com/gantman/nsfw_model", "github.com/gantman/repo-modelo"),   // repositório do classificador (e /releases, /issues...)
    )

    /** host + caminho com o trecho de exceção (se houver) trocado pelo texto neutro. */
    private fun neutralizedHead(host: String, path: String): String {
        val h = host.lowercase().removePrefix("www.")
        val key = h + path.lowercase()
        for ((prefix, safe) in ADULT_EXCEPTIONS) {
            if (key == prefix || key.startsWith("$prefix/")) // dentro deste repositório a sigla "nsfw" (ex.: o arquivo nsfw_mobilenet2....h5 em /releases/download/) não é sinal de
            // conteúdo adulto; os demais termos continuam valendo no resto do caminho, na busca e no fragmento.
            return safe + path.substring(prefix.length - h.length).replace("nsfw", "modelo", ignoreCase = true)
        }
        return host + path
    }

    fun hostMatches(hostname: String, domain: String): Boolean {
        val h = hostname.lowercase()
        val d = domain.lowercase().replace(Regex("^https?://"), "").replace(Regex("^www\\."), "").replace(Regex("/.*$"), "")
        return d.isNotEmpty() && (h == d || h.endsWith(".$d"))
    }

    // ---- estruturas derivadas das listas (recalculadas se a lista mudar) ----

    private class Derived(val snap: TermLists.Snapshot) {
        val urlRe: Regex? = wordRegex(snap.adultUrlTerms)
        val searchRe: Regex? = wordRegex(snap.adultSearchTerms)
        val strong: List<String> = snap.adultStrongTerms.map { norm(it) }.filter { it.length >= 4 }
        val sqStrong: List<String> = snap.adultStrongTerms.map { Deobfuscator.squash(norm(it)) }.filter { it.length >= 6 }.distinct()
        val sqExact: Set<String> = (snap.adultUrlTerms + snap.adultSearchTerms)
            .map { Deobfuscator.squash(norm(it)) }.filter { it.length >= 3 }.toSet()
        val proxy: TermMatcher.Matcher = TermMatcher.create(
            TermMatcher.Config(snap.proxyStrong, snap.proxyWords, snap.proxyHostOnlyWords, snap.proxyCompanions,
                snap.proxySafePhrases, snap.proxySafeTokens)
        )
    }

    private fun wordRegex(terms: List<String>): Regex? {
        val all = terms.map { norm(it) }.filter { it.isNotEmpty() }
        val parts = all.filter { !CJK.containsMatchIn(it) }.map { Regex.escape(it) }
        val cjk = all.filter { CJK.containsMatchIn(it) }.map { Regex.escape(it) }
        val pieces = ArrayList<String>()
        if (parts.isNotEmpty()) pieces.add("(?:^|[^\\p{L}\\p{N}])(?:" + parts.joinToString("|") + ")(?=\$|[^\\p{L}\\p{N}])")
        if (cjk.isNotEmpty()) pieces.add("(?:" + cjk.joinToString("|") + ")")
        return if (pieces.isEmpty()) null else Regex(pieces.joinToString("|"))
    }

    @Volatile private var derivedCache: Derived? = null

    private fun derived(): Derived {
        val snap = TermLists.current()
        val c = derivedCache
        if (c != null && c.snap === snap) return c
        val d = Derived(snap)
        derivedCache = d
        return d
    }

    /** Junta TODOS os parâmetros de busca (inclusive os do fragmento), separados por " ; ". */
    private fun searchQuery(u: ParsedUrl): String {
        val parts = ArrayList<String>()
        for ((k, v) in u.params) if (v.isNotEmpty() && k.lowercase() in SEARCH_KEYS) parts.add(SEP.replace(v, " "))
        if (u.rawFragment.contains("=")) {
            val frag = u.rawFragment.replace(Regex("^#/?\\??"), "")
            val p = ParsedUrl.parse("https://x.invalid/?$frag")
            if (p != null) for ((k, v) in p.params) if (v.isNotEmpty() && k.lowercase() in SEARCH_KEYS) parts.add(SEP.replace(v, " "))
        }
        return parts.joinToString(" ; ")
    }

    private fun evaluateProxy(u: ParsedUrl, query: String, d: Derived): String? {
        val host = u.host.lowercase()
        if (d.snap.proxyAllowDomains.any { hostMatches(host, it) }) return null
        if (d.snap.proxyDomains.any { hostMatches(host, it) }) return "proxy"
        if (d.proxy.test(host, "host") != null) return "proxy"
        if (query.isNotEmpty() && d.proxy.test(query, "query") != null) return "proxy"
        return null
    }

    private fun evaluateGateway(u: ParsedUrl, o: Opts, d: Derived): String? {
        val host = u.host.lowercase()
        if (d.snap.proxyAllowDomains.any { hostMatches(host, it) }) return null
        if (TermMatcher.anyServiceMatches(host, u.path, d.snap.gatewayServices) != null) return "gateway"
        if (o.blockWebArchives && TermMatcher.anyServiceMatches(host, u.path, d.snap.archiveServices) != null) return "gateway"
        return null
    }

    fun evaluate(url: String, o: Opts, depth: Int = 0): String? {
        val u = ParsedUrl.parse(url) ?: return null
        val d = derived()

        val full = safeDecode(neutralizedHead(u.host, u.path) + " " + u.rawQuery + " " + u.rawFragment)
        val decoded = norm(full)
        val sq = searchQuery(u)
        val query = norm(sq)
        val decodedFolds = Deobfuscator.variants(full)
        val queryFolds = Deobfuscator.variants(sq)
        val decodedAll = listOf(decoded) + decodedFolds
        val queryAll = if (query.isNotEmpty()) listOf(query) + queryFolds else emptyList()

        if (o.locked) {
            for (site in o.sites) if (hostMatches(u.host, site)) return "url"
            class Kw(val nk: String, val fk: String, val sk: String)
            val kws = ArrayList<Kw>()
            for (k in o.keywords) {
                val nk = norm(k).trim()
                if (nk.isEmpty()) continue
                val fk = Deobfuscator.foldOne(k)
                kws.add(Kw(nk, fk, Deobfuscator.squash(fk)))
            }
            for (k in kws) {
                if (queryAll.any { it.contains(k.nk) || (k.fk.isNotEmpty() && it.contains(k.fk)) }) return "keyword_search"
                if (k.sk.length >= 4 && queryFolds.any { Deobfuscator.squash(it).contains(k.sk) }) return "keyword_search"
            }
            for (k in kws) {
                if (decodedAll.any { it.contains(k.nk) || (k.fk.isNotEmpty() && it.contains(k.fk)) }) return "keyword"
            }
        }

        val urlRe = d.urlRe
        if (urlRe != null && (decodedAll.any { urlRe.containsMatchIn(it) } || queryAll.any { urlRe.containsMatchIn(it) })) return "adult"
        val searchRe = d.searchRe
        if (searchRe != null && queryAll.any { searchRe.containsMatchIn(it) }) return "adult"

        if (queryAll.isNotEmpty()) {
            val squashed = queryAll.filter { it.length <= 160 }.map { Deobfuscator.squash(it) }
            if (squashed.any { x -> x.isNotEmpty() && (d.sqExact.contains(x) || d.sqStrong.any { t -> x.contains(t) }) }) return "adult"
        }

        if (d.strong.isNotEmpty() && decodedAll.any { dd -> d.strong.any { t -> dd.contains(t) } }) return "adult"
        val host = u.host.lowercase()
        if (d.snap.adultTlds.any { host.endsWith(".$it") }) return "adult"

        val proxy = evaluateProxy(u, query, d)
        if (proxy != null) return proxy

        if (depth < MAX_EMBED_DEPTH) {
            for (inner in TermMatcher.embeddedUrls(u)) {
                val r = evaluate(inner, o, depth + 1)
                if (r != null) return r
            }
        }

        if (o.locked) {
            val gateway = evaluateGateway(u, o, d)
            if (gateway != null) return gateway
            if (TermMatcher.isPublicLiteralIp(u.host)) return "ip"
        }
        return null
    }

    /** Texto digitado numa caixa de busca (sem URL): passa pelas mesmas regras como se fosse "?q=texto". */
    fun evaluateQuery(text: String, o: Opts): String? {
        if (text.isBlank() || text.length > 400) return null
        return evaluate("https://busca.invalid/?q=" + URLEncoder.encode(text, "UTF-8"), o)
    }

    /** Endereço como aparece numa barra de endereço (pode vir sem "https://"). */
    fun evaluateAddress(raw: String, o: Opts): String? {
        val t = raw.trim()
        if (t.isEmpty() || t.contains(' ')) return null
        val withScheme = if (Regex("^[a-zA-Z][a-zA-Z0-9+.-]*://").containsMatchIn(t)) t else "https://$t"
        return evaluate(withScheme, o)
    }
}
