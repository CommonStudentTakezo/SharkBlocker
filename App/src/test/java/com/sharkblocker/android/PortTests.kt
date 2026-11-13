package com.sharkblocker.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.File
import java.time.LocalDateTime
import java.time.ZoneId

/** Testes das partes puras (sem Android): rode com `gradlew test` ou pelo Android Studio. */
class PortTests {
    private val utc = ZoneId.of("UTC")
    private val open = UrlPolicy.Opts(false, emptyList(), emptyList(), false)

    @Before
    fun loadTerms() {
        val dir = listOf("src/main/assets/terms", "app/src/main/assets/terms").map { File(it) }.first { it.exists() }
        TermLists.set(TermLists.parse(File(dir, "adult-terms.js").readText(), File(dir, "proxy-terms.js").readText()))
    }

    private fun epoch(y: Int, mo: Int, d: Int, h: Int, mi: Int): Long =
        LocalDateTime.of(y, mo, d, h, mi).atZone(utc).toInstant().toEpochMilli()

    // ---------- listas de termos ----------
    @Test fun termListsAreParsed() {
        val s = TermLists.current()
        assertTrue(s.adultUrlTerms.size > 20)
        assertTrue(s.adultStrongTerms.contains("pornhub"))
        assertTrue(s.adultTlds.contains("xxx"))
    }

    // ---------- URL / busca ----------
    @Test fun adultDomainIsBlocked() {
        assertEquals("adult", UrlPolicy.evaluate("https://www.pornhub.com/", open))
        assertEquals("adult", UrlPolicy.evaluate("https://megaxvideosbr.com/", open))
        assertEquals("adult", UrlPolicy.evaluate("https://qualquer.xxx/", open))
    }

    @Test fun normalSitesPass() {
        assertNull(UrlPolicy.evaluate("https://pt.wikipedia.org/wiki/Quimica", open))
        assertNull(UrlPolicy.evaluate("https://www.google.com/search?q=tabela+periodica", open))
        assertNull(UrlPolicy.evaluateQuery("receita de bolo de cenoura", open))
    }

    @Test fun obfuscatedSearchIsBlocked() {
        assertEquals("adult", UrlPolicy.evaluateQuery("porn", open))
        assertEquals("adult", UrlPolicy.evaluateQuery("p0rn", open))
        assertEquals("adult", UrlPolicy.evaluateQuery("p.o.r.n", open))
    }

    @Test fun userSitesAndKeywordsOnlyWhileLocked() {
        val locked = UrlPolicy.Opts(true, listOf("meusite.com"), listOf("apostas"), false)
        assertEquals("url", UrlPolicy.evaluate("https://sub.meusite.com/x", locked))
        assertEquals("keyword_search", UrlPolicy.evaluateQuery("apostas esportivas", locked))
        val unlocked = UrlPolicy.Opts(false, listOf("meusite.com"), listOf("apostas"), false)
        assertNull(UrlPolicy.evaluate("https://sub.meusite.com/x", unlocked))
    }

    @Test fun embeddedTargetIsEvaluated() {
        assertEquals("adult", UrlPolicy.evaluate("https://redirecionador.example.org/?url=https://www.pornhub.com/", open))
    }

    @Test fun publicIpBlockedOnlyWhileLocked() {
        val locked = UrlPolicy.Opts(true, emptyList(), emptyList(), false)
        assertEquals("ip", UrlPolicy.evaluate("http://203.0.113.5/", locked))
        assertNull(UrlPolicy.evaluate("http://192.168.0.1/", locked))
        assertNull(UrlPolicy.evaluate("http://203.0.113.5/", open))
    }

    // ---------- matcher ----------
    @Test fun matcherHandlesGluedWords() {
        val m = TermMatcher.create(TermMatcher.Config(listOf("proxy"), listOf("unblocked games"), emptyList(), listOf("games", "free"), emptyList(), emptyList()))
        assertNotNull(m.test("croxyproxy", "host"))
        assertNotNull(m.test("unblockedgames", "host"))
        assertNull(m.test("historia do brasil", "query"))
    }

    @Test fun serviceMatching() {
        assertTrue(TermMatcher.serviceMatches("translate.google.com.br", "/translate", "translate.google.*/translate"))
        assertFalse(TermMatcher.serviceMatches("translate.google.com", "/website", "translate.google.*/translate"))
        assertTrue(TermMatcher.serviceMatches("web.archive.org", "/web/2020/x", "archive.org/web"))
    }

    @Test fun ipClassification() {
        assertTrue(TermMatcher.isPublicLiteralIp("8.8.8.8"))
        assertFalse(TermMatcher.isPublicLiteralIp("10.0.0.1"))
        assertFalse(TermMatcher.isPublicLiteralIp("[::1]"))
        assertTrue(TermMatcher.isPublicLiteralIp("[2001:4860:4860::8888]"))
        assertFalse(TermMatcher.isPublicLiteralIp("exemplo.com"))
    }

    // ---------- desofuscação ----------
    @Test fun deobfuscation() {
        assertTrue(Deobfuscator.variants("p0rn").any { it.split(" ").contains("porn") })
        assertTrue(Deobfuscator.variants("pooorn").any { it.split(" ").contains("porn") })
        assertTrue(Deobfuscator.variants("роrn").any { it.split(" ").contains("porn") }) // 'о' cirílico
        assertEquals("porn", Deobfuscator.squash("p o r n"))
    }

    // ---------- agenda ----------
    @Test fun nightlyWindowCrossesMidnight() {
        val every = Schedule(0x7F, 22 * 60, 6 * 60)
        val at2300 = epoch(2026, 10, 26, 23, 0)
        assertEquals(epoch(2026, 10, 27, 6, 0), Scheduler.activeUntil(listOf(every), at2300, utc))
        val at0300 = epoch(2026, 10, 27, 3, 0)
        assertEquals(epoch(2026, 10, 27, 6, 0), Scheduler.activeUntil(listOf(every), at0300, utc))
        assertNull(Scheduler.activeUntil(listOf(every), epoch(2026, 10, 27, 12, 0), utc))
    }

    @Test fun chainedWindowsBecomeOne() {
        val a = Schedule(0x7F, 20 * 60, 22 * 60)
        val b = Schedule(0x7F, 22 * 60, 23 * 60)
        assertEquals(epoch(2026, 10, 26, 23, 0), Scheduler.activeUntil(listOf(a, b), epoch(2026, 10, 26, 20, 30), utc))
    }

    @Test fun invalidSchedulesAreIgnored() {
        assertFalse(Scheduler.isValid(Schedule(0, 60, 120)))
        assertFalse(Scheduler.isValid(Schedule(1, 60, 60)))
        assertNull(Scheduler.activeUntil(listOf(Schedule(0, 60, 120)), epoch(2026, 10, 26, 1, 30), utc))
    }

    // ---------- DNS ----------
    private fun query(name: String, type: Int): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        out.write(byteArrayOf(0x12, 0x34, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0))
        for (label in name.split(".")) { out.write(label.length); out.write(label.toByteArray()) }
        out.write(0); out.write(type shr 8); out.write(type and 0xFF); out.write(0); out.write(1)
        return out.toByteArray()
    }

    @Test fun dnsQuestionParsingAndReplies() {
        val q = query("www.exemplo.com", 1)
        val p = DnsMsg.parseQuestion(q)!!
        assertEquals("www.exemplo.com", p.name)
        assertEquals(1, p.type)
        val nx = DnsMsg.reply(q, p, 3, null)
        assertEquals(0x12.toByte(), nx[0]); assertEquals(0x34.toByte(), nx[1])
        assertEquals(3, nx[3].toInt() and 0x0F)       // NXDOMAIN
        assertEquals(0, nx[7].toInt())                // sem resposta
        val a = DnsMsg.reply(q, p, 0, byteArrayOf(1, 2, 3, 4))
        assertEquals(1, a[7].toInt())                 // 1 resposta
        assertEquals(4, a[a.size - 1].toInt())
    }

    // ---------- cripto ----------
    @Test fun hexAndPbkdf2() {
        val b = byteArrayOf(0, 1, 127, -1)
        assertEquals(b.toList(), Crypto.unhex(Crypto.hex(b)).toList())
        val salt = ByteArray(16) { it.toByte() }
        val h1 = Crypto.pbkdf2("senha-forte-123".toCharArray(), salt, 1000)
        val h2 = Crypto.pbkdf2("senha-forte-123".toCharArray(), salt, 1000)
        val h3 = Crypto.pbkdf2("senha-forte-124".toCharArray(), salt, 1000)
        assertTrue(Crypto.eq(h1, h2)); assertFalse(Crypto.eq(h1, h3))
    }

    @Test fun passwordPolicy() {
        assertNotNull(PasswordGate.policyError("curta"))
        assertNotNull(PasswordGate.policyError("12345678"))
        assertNotNull(PasswordGate.policyError("aaaaaaaaaa"))
        assertNull(PasswordGate.policyError("c4valo-Verde-77"))
    }

    // ---------- exceção fixa: repositório do classificador (o endereço tem "nsfw") ----------
    @Test fun nsfwModelRepoIsAllowedButNothingElseWithNsfw() {
        // o repositório e suas páginas passam
        assertNull(UrlPolicy.evaluate("https://github.com/GantMan/nsfw_model", open))
        assertNull(UrlPolicy.evaluate("https://github.com/GantMan/nsfw_model/", open))
        assertNull(UrlPolicy.evaluate("https://www.github.com/gantman/NSFW_model/releases/tag/1.1.0", open))
        assertNull(UrlPolicy.evaluate("https://github.com/GantMan/nsfw_model/releases/download/1.1.0/nsfw_mobilenet2.224x224.h5", open))
        // o resto da URL continua sendo avaliado
        assertEquals("adult", UrlPolicy.evaluate("https://github.com/GantMan/nsfw_model?q=porn", open))
        assertEquals("adult", UrlPolicy.evaluate("https://github.com/GantMan/nsfw_model/blob/master/pornhub.txt", open))
        // qualquer outro endereço com "nsfw" continua barrado
        assertEquals("adult", UrlPolicy.evaluate("https://github.com/GantMan/nsfw_model/releases/download/1.1.0/porn.zip", open))
        assertEquals("adult", UrlPolicy.evaluate("https://github.com/GantMan/nsfw_model/releases/download/1.1.0/nsfw_mobilenet2.h5?x=porn", open))
        assertEquals("adult", UrlPolicy.evaluate("https://github.com/outro/nsfw_model", open))
        assertEquals("adult", UrlPolicy.evaluate("https://github.com/GantMan/nsfw_model_extra", open))
        assertEquals("adult", UrlPolicy.evaluate("https://evil.example.com/github.com/GantMan/nsfw_model", open))
        assertEquals("adult", UrlPolicy.evaluate("https://www.reddit.com/r/nsfw/", open))
        assertEquals("adult", UrlPolicy.evaluateQuery("nsfw", open))
    }

    // ---------- estado: campos da primeira revisão da quarentena ----------
    @Test fun blockStateKeepsReviewFields() {
        val s = BlockState()
        s.quarantineSince = 123L
        s.quarantineReviewDone = true
        s.approvedApps.add("a.b|9")
        val back = BlockState.fromJson(s.toJson())
        assertEquals(123L, back.quarantineSince)
        assertTrue(back.quarantineReviewDone)
        assertEquals(listOf("a.b|9"), back.approvedApps)
        // estado antigo (sem os campos novos): a revisão ainda está pendente
        assertFalse(BlockState.fromJson("{}").quarantineReviewDone)
        assertEquals(0L, BlockState.fromJson("{}").quarantineSince)
    }

    // ---------- detector de imagens (limiares) ----------
    @Test fun imageThresholdsAreOrdered() {
        assertTrue(ImageGuard.threshold(1) > ImageGuard.threshold(2))
        assertTrue(ImageGuard.threshold(2) > ImageGuard.threshold(3))
        assertEquals(ImageGuard.threshold(2), ImageGuard.threshold(99), 0f)
    }
}
