using System.IO;
using System.Text.RegularExpressions;

namespace SharkBlocker.Services;

/// <summary>
/// Lê as listas de termos que moram na pasta da extensão (BrowserExtension\adult-terms.js e
/// BrowserExtension\proxy-terms.js), para que a camada de DESKTOP (este app e o Watchdog) e a
/// extensão usem EXATAMENTE as mesmas listas: edita-se um arquivo só.
///
/// Este arquivo é compilado também pelo Watchdog (ver Watchdog.csproj), então NÃO pode usar
/// nada que exista só no App (janelas, Dialogs...). Também nunca lança exceção: se a pasta ou
/// um arquivo não existir, devolve listas vazias.
/// </summary>
public static class TermLists
{
    public sealed class Snapshot
    {
        public List<string> AdultUrlTerms { get; } = new();
        public List<string> AdultSearchTerms { get; } = new();
        public List<string> AdultStrongTerms { get; } = new();
        public List<string> AdultTlds { get; } = new();
        public List<string> PermanentSites { get; } = new();

        public List<string> ProxyDomains { get; } = new();
        public List<string> ProxyAllowDomains { get; } = new();
        public List<string> ProxyStrong { get; } = new();
        public List<string> ProxyWords { get; } = new();
        public List<string> ProxyHostOnlyWords { get; } = new();
        public List<string> ProxyCompanions { get; } = new();
        public List<string> ProxySafePhrases { get; } = new();
        public List<string> ProxySafeTokens { get; } = new();

        // U7: tradutor/cache (gatewayServices), arquivo web (archiveServices, só com a opção ligada) e
        // provedores de DNS-sobre-HTTPS por nome (dohHosts). Formato dos dois primeiros: "host" ou
        // "host/prefixo" (ver UrlPolicy.ServiceMatches e matcher.js serviceMatches).
        public List<string> GatewayServices { get; } = new();
        public List<string> ArchiveServices { get; } = new();
        public List<string> DohHosts { get; } = new();
    }

    private static readonly object Gate = new();
    private static Snapshot _cached = new();
    private static DateTime _cachedStampUtc = DateTime.MinValue;

    /// <summary>Pasta da extensão. App e Watchdog ficam um nível abaixo da raiz da instalação.</summary>
    public static string ExtensionFolder
    {
        get
        {
            var viaParent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "BrowserExtension"));
            if (Directory.Exists(viaParent)) return viaParent;
            return Path.Combine(AppContext.BaseDirectory, "BrowserExtension");
        }
    }

    /// <summary>Listas atuais. Relê os arquivos só quando mudam (data de modificação).</summary>
    public static Snapshot Current()
    {
        try
        {
            var adultPath = Path.Combine(ExtensionFolder, "adult-terms.js");
            var proxyPath = Path.Combine(ExtensionFolder, "proxy-terms.js");

            var stamp = MaxStamp(adultPath, proxyPath);
            lock (Gate)
            {
                if (stamp == _cachedStampUtc && _cachedStampUtc != DateTime.MinValue) return _cached;

                var snap = new Snapshot();
                if (File.Exists(adultPath))
                {
                    var js = StripLineComments(File.ReadAllText(adultPath));
                    snap.AdultUrlTerms.AddRange(ArrayByConstName(js, "CTC_URL_TERMS"));
                    snap.AdultSearchTerms.AddRange(ArrayByConstName(js, "CTC_SEARCH_TERMS"));
                    snap.PermanentSites.AddRange(ArrayByConstName(js, "CTC_PERMANENT_SITES"));
                    snap.AdultStrongTerms.AddRange(ArrayByConstName(js, "CTC_STRONG_TERMS"));
                    snap.AdultTlds.AddRange(ArrayByConstName(js, "CTC_ADULT_TLDS"));
                }
                if (File.Exists(proxyPath))
                {
                    var js = StripLineComments(File.ReadAllText(proxyPath));
                    snap.ProxyDomains.AddRange(ArrayByKey(js, "domains"));
                    snap.ProxyAllowDomains.AddRange(ArrayByKey(js, "allowDomains"));
                    snap.ProxyStrong.AddRange(ArrayByKey(js, "strong"));
                    snap.ProxyWords.AddRange(ArrayByKey(js, "words"));
                    snap.ProxyHostOnlyWords.AddRange(ArrayByKey(js, "hostOnlyWords"));
                    snap.ProxyCompanions.AddRange(ArrayByKey(js, "companions"));
                    snap.ProxySafePhrases.AddRange(ArrayByKey(js, "safePhrases"));
                    snap.ProxySafeTokens.AddRange(ArrayByKey(js, "safeTokens"));
                    snap.GatewayServices.AddRange(ArrayByKey(js, "gatewayServices"));
                    snap.ArchiveServices.AddRange(ArrayByKey(js, "archiveServices"));
                    snap.DohHosts.AddRange(ArrayByKey(js, "dohHosts"));
                }

                _cached = snap;
                _cachedStampUtc = stamp;
                return _cached;
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("TermLists.Current", ex);
            return new Snapshot();
        }
    }

    /// <summary>Domínios de proxy/VPN/navegador remoto conhecidos (para o arquivo hosts).</summary>
    public static IReadOnlyList<string> ProxyDomains() => Current().ProxyDomains;

    /// <summary>Sites bloqueados SEMPRE (CTC_PERMANENT_SITES em adult-terms.js).</summary>
    public static IReadOnlyList<string> PermanentSites() => Current().PermanentSites;

    /// <summary>Provedores de DNS-sobre-HTTPS por nome (para o arquivo hosts) — auditoria U7.</summary>
    public static IReadOnlyList<string> DohHosts() => Current().DohHosts;

    private static DateTime MaxStamp(params string[] paths)
    {
        var max = DateTime.MinValue;
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var t = File.GetLastWriteTimeUtc(p);
            if (t > max) max = t;
        }
        return max;
    }

    // Os arquivos .js usam só comentários de linha (//) e nenhuma string com "//" dentro.
    private static string StripLineComments(string js) => Regex.Replace(js, @"//[^\r\n]*", "");

    private static List<string> ArrayByConstName(string js, string constName)
    {
        var m = Regex.Match(js, @"const\s+" + Regex.Escape(constName) + @"\s*=\s*\[(.*?)\]\s*;", RegexOptions.Singleline);
        return m.Success ? StringLiterals(m.Groups[1].Value) : new List<string>();
    }

    private static List<string> ArrayByKey(string js, string key)
    {
        // Diferencia maiúsculas: "domains" não casa com "allowDomains", "words" não casa com "hostOnlyWords".
        var m = Regex.Match(js, @"\b" + Regex.Escape(key) + @"\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
        return m.Success ? StringLiterals(m.Groups[1].Value) : new List<string>();
    }

    private static List<string> StringLiterals(string chunk)
    {
        var list = new List<string>();
        foreach (Match m in Regex.Matches(chunk, "\"((?:[^\"\\\\]|\\\\.)*)\""))
        {
            try { list.Add(Regex.Unescape(m.Groups[1].Value)); }
            catch { list.Add(m.Groups[1].Value); }
        }
        return list;
    }
}
