// SharkBlocker — desofuscação de texto (contra "n4k3d", "p0rn", "p.o.r.n", "роrn" com letras
// russas, "ｐｏｒｎ" de largura total, letras esticadas "pooorn", caracteres invisíveis...).
//
// ATENÇÃO: existe um espelho em C# (App\Services\Deobfuscator.cs). Mude os dois juntos.
//
// A ideia: antes de procurar um termo, o texto é "limpo" de todo disfarce comum e a busca roda no
// original E nas versões limpas. Cuidados contra falso positivo (uma trava de tela custa 30 min):
//   - dígitos só viram letras em palavras que MISTURAM letras e dígitos ("n4k3d"); um "455" puro
//     continua sendo número;
//   - símbolos (@ $ ! + €) só viram letra quando estão ENTRE letras ("n@ked", não "sexo!");
//   - "p o r n" / "p.o.r.n" só é juntado com 3 ou mais caracteres soltos em sequência.
(function (root) {
  "use strict";

  // Letras de outros alfabetos que parecem letras latinas.
  const HOMOGLYPHS = {
    // cirílico
    "а": "a", "в": "b", "е": "e", "ё": "e", "з": "3", "и": "u", "к": "k", "м": "m", "н": "h", "о": "o",
    "р": "p", "с": "c", "т": "t", "у": "y", "х": "x", "і": "i", "ї": "i", "ј": "j", "ѕ": "s", "һ": "h",
    "ԁ": "d", "ԛ": "q", "ԝ": "w", "ѵ": "v", "ү": "y", "ӏ": "i", "ց": "g", "ɡ": "g", "ᴏ": "o", "ᴘ": "p",
    // grego
    "α": "a", "β": "b", "ε": "e", "ι": "i", "κ": "k", "ν": "v", "ο": "o", "ρ": "p", "τ": "t", "υ": "u",
    "χ": "x", "γ": "y", "η": "n", "ω": "w", "μ": "u",
    // outros
    "ı": "i", "ø": "o", "ß": "b", "đ": "d", "ł": "l", "æ": "ae", "œ": "oe",
    // mais cirílico / grego / armênio que imitam letras
    "п": "n", "г": "r", "ь": "b", "ш": "w", "ѡ": "w", "σ": "o", "ς": "s", "π": "n", "δ": "d",
    "ո": "n", "ս": "u", "օ": "o", "հ": "h", "զ": "q", "ɑ": "a", "ɩ": "i",
    // versaletes ("ᴘᴏʀɴ") — o NFKC não converte esses
    "ᴀ": "a", "ʙ": "b", "ᴄ": "c", "ᴅ": "d", "ᴇ": "e", "ꜰ": "f", "ʜ": "h", "ɪ": "i", "ᴊ": "j", "ᴋ": "k",
    "ʟ": "l", "ᴍ": "m", "ɴ": "n", "ʀ": "r", "ꜱ": "s", "ᴛ": "t", "ᴜ": "u", "ᴠ": "v", "ᴡ": "w", "ʏ": "y",
    "ᴢ": "z", "ǫ": "q",
  };

  // Letras em "emoji": círculos/quadrados negativos (🅿🅾🆁🅽) e indicadores regionais (🇵🇴🇷🇳).
  function emojiLetter(cp) {
    if (cp >= 0x1f1e6 && cp <= 0x1f1ff) return String.fromCharCode(97 + cp - 0x1f1e6);
    if (cp >= 0x1f150 && cp <= 0x1f169) return String.fromCharCode(97 + cp - 0x1f150);
    if (cp >= 0x1f170 && cp <= 0x1f189) return String.fromCharCode(97 + cp - 0x1f170);
    if (cp >= 0x1f130 && cp <= 0x1f149) return String.fromCharCode(97 + cp - 0x1f130);
    return null;
  }

  // Dígitos que imitam letras (só aplicados em palavras que misturam letras e dígitos).
  const LEET = { "0": "o", "1": "i", "2": "z", "3": "e", "4": "a", "5": "s", "6": "g", "7": "t", "8": "b", "9": "g" };
  // Símbolos que imitam letras (só quando estão entre letras).
  const SYMBOLS = { "@": "a", "$": "s", "!": "i", "+": "t", "€": "e", "£": "e", "¡": "i", "¢": "c", "§": "s", "¥": "y", "©": "c", "µ": "u" };

  // Caracteres invisíveis / de formatação usados para quebrar palavras sem aparecer.
  const INVISIBLE = /[\u00ad\u034f\u061c\u115f\u1160\u17b4\u17b5\u180b-\u180e\u200b-\u200f\u202a-\u202e\u2060-\u206f\u3164\ufe00-\ufe0f\ufeff\uffa0]/g;

  const MAX_LEN = 300000;

  // Texto escrito com códigos: "&#112;orn", "&#x70;orn", "\u0070orn", "\x70orn", "%70orn" (até 2 camadas).
  function decodeEscapes(s) {
    let t = s;
    if (t.indexOf("&#") !== -1) {
      t = t.replace(/&#(x[0-9a-f]{1,6}|\d{1,7});?/gi, (m, g) => {
        const n = /^x/i.test(g) ? parseInt(g.slice(1), 16) : parseInt(g, 10);
        return n > 0 && n <= 0x10ffff ? String.fromCodePoint(n) : m;
      });
    }
    if (t.indexOf("\\") !== -1) {
      t = t.replace(/\\u([0-9a-f]{4})|\\x([0-9a-f]{2})/gi, (m, a, b) => String.fromCharCode(parseInt(a || b, 16)));
    }
    for (let i = 0; i < 2 && t.indexOf("%") !== -1; i++) {
      let d;
      try { d = decodeURIComponent(t); }
      catch (_) { d = t.replace(/%([0-7][0-9a-f])/gi, (m, h) => String.fromCharCode(parseInt(h, 16))); }
      if (d === t) break;
      t = d;
    }
    return t;
  }

  function base(s) {
    let t = String(s == null ? "" : s);
    if (t.length > MAX_LEN) t = t.slice(0, MAX_LEN);
    t = decodeEscapes(t);
    t = t.normalize("NFKC").toLowerCase().replace(INVISIBLE, "");
    t = t.normalize("NFD").replace(/[\u0300-\u036f]/g, "");
    t = Array.from(t, (c) => {
      if (HOMOGLYPHS[c] !== undefined) return HOMOGLYPHS[c];
      const cp = c.codePointAt(0);
      if (cp >= 0x1f130) { const e = emojiLetter(cp); if (e) return e; }
      return c;
    }).join("");
    t = t.replace(/(?<=\p{L})[@$!+€£¡¢§¥©µ](?=\p{L})/gu, (m) => SYMBOLS[m]);
    return t;
  }

  function mixed(tok) {
    return /\p{L}/u.test(tok) && /\p{N}/u.test(tok);
  }

  function leet(tok, oneAs) {
    return Array.from(tok, (c) => (c === "1" ? oneAs : (LEET[c] !== undefined ? LEET[c] : c))).join("");
  }

  const collapse1 = (s) => s.replace(/(.)\1{2,}/gu, "$1"); // "pooorn" -> "porn"
  const collapse2 = (s) => s.replace(/(.)\1{2,}/gu, "$1$1"); // "booobs" -> "boobs"
  const collapse3 = (s) => s.replace(/(.)\1{3,}/gu, "$1$1$1"); // "xxxxx" -> "xxx"

  function tokenize(t) {
    return t.split(/[^\p{L}\p{N}]+/u).filter(Boolean);
  }

  // "p o r n" / "p.o.r.n" / "n-4-k-3-d": junta 3+ caracteres soltos em sequência.
  function joinedRuns(tokens) {
    const out = [];
    let run = [];
    const flush = () => { if (run.length >= 3) out.push(run.join("")); run = []; };
    for (const tok of tokens) {
      if (Array.from(tok).length === 1) run.push(tok); else flush();
    }
    flush();
    return out;
  }

  // Devolve versões "limpas" do texto, cada uma como tokens separados por espaço.
  // A primeira é a principal; as outras cobrem variações ("1" = i ou l; letras esticadas).
  // Letras desenhadas com símbolos ("p()rn", "|<ink", "n/\\ked"). Ordem importa: os maiores primeiro.
  const ASCII_ART = [
    ["|\\/|", "m"], ["|\\|", "n"], ["\\/\\/", "w"], ["|_|", "u"], ["(_)", "u"], ["|-|", "h"], ["/\\", "a"],
    ["\\/", "v"], ["|<", "k"], ["|)", "d"], ["|3", "b"], ["|2", "r"], ["|>", "p"], ["()", "o"], ["[]", "o"],
  ];
  const ART_PROBE = /[|()\[\]\\/<>]/;
  function artFold(t) {
    let out = t;
    for (const [from, to] of ASCII_ART) out = out.split(from).join(to);
    return out;
  }

  const MASK_PROBE = /(?<=\p{L})[*#](?=\p{L})/u;
  const MASK_FILL = /(?<=\p{L})[*#](?=\p{L})/gu;

  function variants(text) {
    const b = base(text);
    const tokens = tokenize(b);
    if (tokens.length === 0) return [];
    const extras = joinedRuns(tokens);
    const all = tokens.concat(extras);

    const build = (fn) => all.map(fn).join(" ");
    const primary = (tok) => (mixed(tok) ? leet(tok, "i") : tok);
    const list = [
      build(primary),
      build((tok) => (mixed(tok) && tok.includes("1") ? leet(tok, "l") : primary(tok))),
      build((tok) => collapse1(primary(tok))),
      build((tok) => collapse2(primary(tok))),
      build((tok) => collapse3(primary(tok))),
    ];

    // "p*rn", "s#x", "f*ck": o asterisco/cerquilha escondendo uma vogal. Testa cada vogal no lugar.
    // (Só * e #; "_" e "-" são separadores comuns em endereços e gerariam ruído.)
    if (MASK_PROBE.test(b)) {
      for (const v of "aeiou") {
        const filled = tokenize(b.replace(MASK_FILL, v));
        const filledAll = filled.concat(joinedRuns(filled));
        list.push(filledAll.map(primary).join(" "));
      }
    }

    // "p()rn", "|<ink": letras desenhadas com símbolos.
    if (ART_PROBE.test(b)) {
      const art = artFold(b);
      if (art !== b) {
        const toks = tokenize(art);
        const allArt = toks.concat(joinedRuns(toks));
        list.push(allArt.map(primary).join(" "));
      }
    }
    return Array.from(new Set(list));
  }

  // Forma principal do texto limpo (para comparar uma palavra-chave cadastrada).
  function foldOne(text) {
    const v = variants(text);
    return v.length ? v[0] : "";
  }

  // Só letras e números, sem espaços ("po rn" -> "porn"). Use só em textos curtos (buscas).
  function squash(s) {
    return String(s || "").replace(/[^\p{L}\p{N}]+/gu, "");
  }

  root.CTCDeobf = { variants, foldOne, squash };
})(typeof globalThis !== "undefined" ? globalThis : this);
