// Roda em toda página. Varre o TEXTO visível atrás das palavras-chave que VOCÊ cadastrou
// no app — e só enquanto há um bloqueio temporizado ativo. (Os termos embutidos de conteúdo
// adulto não são varridos no texto: só na URL/busca, no background.js, para não bloquear
// páginas legítimas de notícia, saúde ou educação.)
(function () {
  const MAX_SCAN_LENGTH = 200000;
  let lastCheckedLength = 0;
  let cachedRegex = null;
  let cachedKey = null;

  // CTCDeobf vem de deobf.js (carregado antes deste arquivo, ver manifest.json).
  const norm = (s) => String(s || "").toLowerCase().normalize("NFD").replace(/[\u0300-\u036f]/g, "");
  const escapeRegex = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

  function getRegex(keywords) {
    const key = keywords.join("\u0001");
    if (cachedKey === key) return cachedRegex;
    const parts = keywords.map(norm).map((k) => k.trim()).filter(Boolean).map(escapeRegex);
    cachedRegex = parts.length ? new RegExp(parts.join("|")) : null;
    cachedKey = key;
    return cachedRegex;
  }

  function scanPage() {
    try {
      chrome.storage.local.get("ctcState", (data) => {
        const state = data?.ctcState;
        if (!state?.locked || !state.blockedKeywords?.length) return;

        const fullText = document.body?.innerText || "";
        if (fullText.length === lastCheckedLength) return;
        lastCheckedLength = fullText.length;

        const raw = fullText.length > MAX_SCAN_LENGTH ? fullText.slice(0, MAX_SCAN_LENGTH) : fullText;
        const regex = getRegex(state.blockedKeywords);
        let found = !!(regex && regex.test(norm(raw)));

        // Também procura nas versões "limpas" do texto (n4k3d, p.o.r.n, letras esticadas...),
        // comparando com as palavras cadastradas também limpas.
        if (!found && typeof CTCDeobf !== "undefined") {
          const folds = CTCDeobf.variants(raw);
          const foldedKeywords = state.blockedKeywords.map((k) => CTCDeobf.foldOne(k)).filter(Boolean);
          const foldedRegex = getRegex(foldedKeywords);
          found = !!(foldedRegex && folds.some((f) => foldedRegex.test(f)));
        }

        if (found) {
          chrome.runtime.sendMessage({ type: "CTC_KEYWORD_FOUND" });
        }
      });
    } catch (_) { /* extensão recarregada: ignora */ }
  }

  scanPage();

  const observer = new MutationObserver(() => {
    clearTimeout(window.__ctcScanTimeout);
    window.__ctcScanTimeout = setTimeout(scanPage, 800);
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });
})();
