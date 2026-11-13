using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SharkBlocker.Services;

/// <summary>
/// Desofuscação de texto: "n4k3d", "p0rn", "p.o.r.n", "роrn" (letras russas), "ｐｏｒｎ" (largura
/// total), "pooorn" (esticado), caracteres invisíveis... Espelho em C# de
/// BrowserExtension\deobf.js — mude os dois juntos.
///
/// A busca roda no texto original E nas versões "limpas". Cuidados contra falso positivo (uma trava
/// de tela custa 30 minutos):
///  - dígitos só viram letras em palavras que MISTURAM letras e dígitos ("n4k3d"); "455" continua número;
///  - símbolos (@ $ ! + €) só viram letra quando estão ENTRE letras ("n@ked", não "sexo!");
///  - "p o r n" / "p.o.r.n" só é juntado com 3 ou mais caracteres soltos em sequência.
/// </summary>
public static class Deobfuscator
{
    private const int MaxLen = 300_000;

    // Letras de outros alfabetos que parecem letras latinas.
    private static readonly Dictionary<char, string> Homoglyphs = new()
    {
        // cirílico
        ['а'] = "a", ['в'] = "b", ['е'] = "e", ['ё'] = "e", ['з'] = "3", ['и'] = "u", ['к'] = "k", ['м'] = "m",
        ['н'] = "h", ['о'] = "o", ['р'] = "p", ['с'] = "c", ['т'] = "t", ['у'] = "y", ['х'] = "x", ['і'] = "i",
        ['ї'] = "i", ['ј'] = "j", ['ѕ'] = "s", ['һ'] = "h", ['ԁ'] = "d", ['ԛ'] = "q", ['ԝ'] = "w", ['ѵ'] = "v",
        ['ү'] = "y", ['ӏ'] = "i", ['ց'] = "g", ['ɡ'] = "g", ['ᴏ'] = "o", ['ᴘ'] = "p",
        // grego
        ['α'] = "a", ['β'] = "b", ['ε'] = "e", ['ι'] = "i", ['κ'] = "k", ['ν'] = "v", ['ο'] = "o", ['ρ'] = "p",
        ['τ'] = "t", ['υ'] = "u", ['χ'] = "x", ['γ'] = "y", ['η'] = "n", ['ω'] = "w", ['μ'] = "u",
        // outros
        ['ı'] = "i", ['ø'] = "o", ['ß'] = "b", ['đ'] = "d", ['ł'] = "l", ['æ'] = "ae", ['œ'] = "oe",
        // mais cirílico / grego / armênio que imitam letras
        ['п'] = "n", ['г'] = "r", ['ь'] = "b", ['ш'] = "w", ['ѡ'] = "w", ['σ'] = "o", ['ς'] = "s", ['π'] = "n", ['δ'] = "d",
        ['ո'] = "n", ['ս'] = "u", ['օ'] = "o", ['հ'] = "h", ['զ'] = "q", ['ɑ'] = "a", ['ɩ'] = "i",
        // versaletes ("ᴘᴏʀɴ") — o NFKC não converte esses
        ['ᴀ'] = "a", ['ʙ'] = "b", ['ᴄ'] = "c", ['ᴅ'] = "d", ['ᴇ'] = "e", ['ꜰ'] = "f", ['ʜ'] = "h", ['ɪ'] = "i", ['ᴊ'] = "j", ['ᴋ'] = "k",
        ['ʟ'] = "l", ['ᴍ'] = "m", ['ɴ'] = "n", ['ʀ'] = "r", ['ꜱ'] = "s", ['ᴛ'] = "t", ['ᴜ'] = "u", ['ᴠ'] = "v", ['ᴡ'] = "w", ['ʏ'] = "y",
        ['ᴢ'] = "z", ['ǫ'] = "q",
    };

    /// <summary>Letras em "emoji": círculos/quadrados (🅿🅾🆁🅽) e indicadores regionais (🇵🇴🇷🇳).</summary>
    private static char? EmojiLetter(int cp)
    {
        if (cp >= 0x1F1E6 && cp <= 0x1F1FF) return (char)('a' + cp - 0x1F1E6);
        if (cp >= 0x1F150 && cp <= 0x1F169) return (char)('a' + cp - 0x1F150);
        if (cp >= 0x1F170 && cp <= 0x1F189) return (char)('a' + cp - 0x1F170);
        if (cp >= 0x1F130 && cp <= 0x1F149) return (char)('a' + cp - 0x1F130);
        return null;
    }

    private static readonly Dictionary<char, char> Leet = new()
    {
        ['0'] = 'o', ['1'] = 'i', ['2'] = 'z', ['3'] = 'e', ['4'] = 'a', ['5'] = 's', ['6'] = 'g', ['7'] = 't', ['8'] = 'b', ['9'] = 'g',
    };

    private static readonly Dictionary<char, char> Symbols = new()
    {
        ['@'] = 'a', ['$'] = 's', ['!'] = 'i', ['+'] = 't', ['€'] = 'e', ['£'] = 'e', ['¡'] = 'i', ['¢'] = 'c', ['§'] = 's', ['¥'] = 'y', ['©'] = 'c', ['µ'] = 'u',
    };

    private static readonly Regex Invisible = new(
        "[\u00ad\u034f\u061c\u115f\u1160\u17b4\u17b5\u180b-\u180e\u200b-\u200f\u202a-\u202e\u2060-\u206f\u3164\ufe00-\ufe0f\ufeff\uffa0]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SandwichedSymbol = new(
        @"(?<=\p{L})[@$!+€£¡¢§¥©µ](?=\p{L})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "p*rn", "s#x": o asterisco/cerquilha escondendo uma vogal (só entre letras).
    private static readonly Regex MaskChar = new(
        @"(?<=\p{L})[*#](?=\p{L})", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Texto escrito com códigos: "&#112;orn", "&#x70;orn", "\u0070orn", "\x70orn", "%70orn".
    private static readonly Regex NumEntity = new(
        @"&#(x[0-9a-f]{1,6}|\d{1,7});?", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex BackslashEscape = new(
        @"\\u([0-9a-f]{4})|\\x([0-9a-f]{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string DecodeEscapes(string s)
    {
        var t = s;
        if (t.Contains("&#", StringComparison.Ordinal))
        {
            t = NumEntity.Replace(t, m =>
            {
                var g = m.Groups[1].Value;
                int n;
                var ok = g[0] is 'x' or 'X'
                    ? int.TryParse(g.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)
                    : int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out n);
                return ok && n > 0 && n <= 0x10FFFF && (n < 0xD800 || n > 0xDFFF) ? char.ConvertFromUtf32(n) : m.Value;
            });
        }
        if (t.Contains('\\'))
        {
            t = BackslashEscape.Replace(t, m =>
            {
                var hex = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                return ((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString();
            });
        }
        for (var i = 0; i < 2 && t.Contains('%'); i++)
        {
            string d;
            try { d = Uri.UnescapeDataString(t); } catch { break; }
            if (d == t) break;
            t = d;
        }
        return t;
    }

    private static readonly Regex NonAlnum = new(
        @"[^\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Letras desenhadas com símbolos ("p()rn", "|<ink"). Ordem importa: os maiores primeiro.
    private static readonly (string From, string To)[] AsciiArt =
    {
        ("|\\/|", "m"), ("|\\|", "n"), ("\\/\\/", "w"), ("|_|", "u"), ("(_)", "u"), ("|-|", "h"), ("/\\", "a"),
        ("\\/", "v"), ("|<", "k"), ("|)", "d"), ("|3", "b"), ("|2", "r"), ("|>", "p"), ("()", "o"), ("[]", "o"),
    };
    private static readonly Regex ArtProbe = new(@"[|()\[\]\\/<>]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string ArtFold(string t)
    {
        foreach (var (from, to) in AsciiArt) t = t.Replace(from, to, StringComparison.Ordinal);
        return t;
    }

    private static readonly Regex Run3 = new(@"(.)\1{2,}", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Run4 = new(@"(.)\1{3,}", RegexOptions.Compiled | RegexOptions.Singleline);

    private static string Base(string? s)
    {
        var t = s ?? "";
        if (t.Length > MaxLen) t = t.Substring(0, MaxLen);

        t = DecodeEscapes(t);
        t = t.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        t = Invisible.Replace(t, "");

        var decomposed = t.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) continue;
            if (rune.IsBmp && Homoglyphs.TryGetValue((char)rune.Value, out var rep)) { sb.Append(rep); continue; }
            if (rune.Value >= 0x1F130 && EmojiLetter(rune.Value) is { } letter) { sb.Append(letter); continue; }
            sb.Append(rune.ToString());
        }

        return SandwichedSymbol.Replace(sb.ToString(), m => Symbols[m.Value[0]].ToString());
    }

    private static bool Mixed(string tok) => tok.Any(char.IsLetter) && tok.Any(char.IsDigit);

    private static string LeetFold(string tok, char oneAs)
    {
        var sb = new StringBuilder(tok.Length);
        foreach (var c in tok)
        {
            if (c == '1') sb.Append(oneAs);
            else if (Leet.TryGetValue(c, out var l)) sb.Append(l);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Collapse1(string s) => Run3.Replace(s, "$1");   // "pooorn" -> "porn"
    private static string Collapse2(string s) => Run3.Replace(s, "$1$1"); // "booobs" -> "boobs"
    private static string Collapse3(string s) => Run4.Replace(s, "$1$1$1"); // "xxxxx" -> "xxx"

    private static List<string> Tokenize(string t) =>
        NonAlnum.Split(t).Where(x => x.Length > 0).ToList();

    // "p o r n" / "p.o.r.n" / "n-4-k-3-d": junta 3+ caracteres soltos em sequência.
    private static List<string> JoinedRuns(List<string> tokens)
    {
        var result = new List<string>();
        var run = new List<string>();

        void Flush()
        {
            if (run.Count >= 3) result.Add(string.Concat(run));
            run.Clear();
        }

        foreach (var tok in tokens)
        {
            if (tok.Length == 1) run.Add(tok); else Flush();
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Versões "limpas" do texto, cada uma como tokens separados por espaço. A primeira é a
    /// principal; as outras cobrem variações ("1" = i ou l; letras esticadas).
    /// </summary>
    public static List<string> Variants(string? text)
    {
        var b = Base(text);
        var tokens = Tokenize(b);
        if (tokens.Count == 0) return new List<string>();

        var all = tokens.Concat(JoinedRuns(tokens)).ToList();

        string Primary(string tok) => Mixed(tok) ? LeetFold(tok, 'i') : tok;
        string Build(Func<string, string> fn) => string.Join(" ", all.Select(fn));

        var list = new List<string>
        {
            Build(Primary),
            Build(tok => Mixed(tok) && tok.Contains('1') ? LeetFold(tok, 'l') : Primary(tok)),
            Build(tok => Collapse1(Primary(tok))),
            Build(tok => Collapse2(Primary(tok))),
            Build(tok => Collapse3(Primary(tok))),
        };

        // "p*rn", "s#x", "f*ck": testa cada vogal no lugar do asterisco/cerquilha.
        if (MaskChar.IsMatch(b))
        {
            foreach (var v in "aeiou")
            {
                var filled = Tokenize(MaskChar.Replace(b, v.ToString()));
                var filledAll = filled.Concat(JoinedRuns(filled));
                list.Add(string.Join(" ", filledAll.Select(Primary)));
            }
        }

        // "p()rn", "|<ink": letras desenhadas com símbolos.
        if (ArtProbe.IsMatch(b))
        {
            var art = ArtFold(b);
            if (art != b)
            {
                var toks = Tokenize(art);
                var allArt = toks.Concat(JoinedRuns(toks));
                list.Add(string.Join(" ", allArt.Select(Primary)));
            }
        }
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Forma principal do texto limpo (para comparar uma palavra-chave cadastrada).</summary>
    public static string FoldOne(string? text)
    {
        var v = Variants(text);
        return v.Count > 0 ? v[0] : "";
    }

    /// <summary>Só letras e números, sem espaços ("po rn" -> "porn"). Use só em textos curtos (buscas).</summary>
    public static string Squash(string? s) => NonAlnum.Replace(s ?? "", "");
}
