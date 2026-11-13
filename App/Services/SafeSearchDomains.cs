namespace SharkBlocker.Services;

/// <summary>
/// Domínios de busca mapeados para os IPs "VIP" oficiais que forçam
/// SafeSearch / Modo Restrito, documentados pelo próprio Google
/// (support.google.com/websearch/answer/186669 para busca,
/// support.google.com/a/answer/6214622 para o YouTube) e usados de forma
/// equivalente para o Bing. Verificado em setembro de 2026.
///
/// Esses IPs raramente mudam (são endereços dedicados do Google/Microsoft
/// para essa finalidade específica), mas se o SafeSearch parar de ser
/// aplicado depois de algum tempo, confirme rodando em um prompt:
///   nslookup forcesafesearch.google.com
///   nslookup restrict.youtube.com
/// e atualize as constantes abaixo se o valor retornado for diferente.
/// </summary>
public static class SafeSearchDomains
{
    private const string GoogleSafeSearchIp = "216.239.38.120";
    private const string YoutubeStrictIp = "216.239.38.120";   // Modo Restrito ESTRITO
    private const string BingSafeSearchIp = "204.79.197.220";

    // O Google atende busca por domínio de país (google.com.br, google.pt, ...).
    // Se um deles ficar de fora, a busca por ele escapa do SafeSearch no hosts.
    // A lista abaixo cobre os mais comuns; adicione o seu sufixo aqui se faltar.
    private static readonly string[] GoogleSuffixes =
    {
        "com", "com.br", "pt", "co.uk", "ca", "com.au", "de", "fr", "es", "it", "nl", "be", "ch", "at",
        "se", "no", "dk", "fi", "ie", "pl", "ru", "gr", "cz", "ro", "hu", "bg", "com.ua",
        "com.ar", "cl", "com.co", "com.mx", "com.pe", "com.ve", "com.uy", "com.ec", "com.bo", "com.py",
        "com.do", "com.gt", "co.in", "co.jp", "co.kr", "com.tr", "co.za", "com.sg", "com.hk", "com.tw",
        "co.nz", "co.il", "com.eg", "com.sa", "ae", "co.id", "co.th", "com.vn", "com.ph", "com.my",
        "com.pk", "com.bd", "com.ng", "co.ke",
    };

    private static IEnumerable<string> GoogleDomains =>
        GoogleSuffixes
            .SelectMany(suffix => new[] { $"google.{suffix}", $"www.google.{suffix}" })
            .Distinct(StringComparer.OrdinalIgnoreCase);

    // Lista oficial de hostnames que o Google recomenda redirecionar juntos.
    private static readonly string[] YoutubeDomains =
    {
        "youtube.com", "www.youtube.com", "m.youtube.com",
        "youtubei.googleapis.com", "youtube.googleapis.com",
        "www.youtube-nocookie.com",
    };

    private static readonly string[] BingDomains =
    {
        "www.bing.com", "bing.com", "www2.bing.com", "www3.bing.com",
    };

    public static IEnumerable<(string Domain, string Ip)> GetHostsEntries()
    {
        foreach (var d in GoogleDomains) yield return (d, GoogleSafeSearchIp);
        foreach (var d in YoutubeDomains) yield return (d, YoutubeStrictIp);
        foreach (var d in BingDomains) yield return (d, BingSafeSearchIp);

        // Proxies, VPNs e navegadores remotos conhecidos (BrowserExtension\proxy-terms.js, lista
        // "domains"): bloqueados no hosts, então valem em QUALQUER navegador e sem extensão.
        // Só o domínio e o "www." (o hosts não aceita curinga: subdomínios ficam com a extensão e
        // com o vigia de desktop).
        foreach (var d in ProxyBlockedDomains())
        {
            yield return (d, "127.0.0.1");
            if (!d.StartsWith("www.", StringComparison.Ordinal)) yield return ("www." + d, "127.0.0.1");
        }

        // U7: DoH por nome (só o nome exato, sem "www.").
        foreach (var d in DohBlockedHosts()) yield return (d, "127.0.0.1");
    }

    // U7: nomes de provedores de DNS-sobre-HTTPS (BrowserExtension\proxy-terms.js, lista "dohHosts").
    // Sem achar o resolvedor, o navegador cai para o DNS do sistema, que consulta o hosts antes.
    // Só o nome exato (o hosts não aceita curinga: ex. "<id>.dns.nextdns.io" não é coberto).
    // Isto NÃO substitui a política de navegador que desliga o DoH (BrowserPolicyManager): é a 2ª camada.
    private static IEnumerable<string> DohBlockedHosts() =>
        TermLists.DohHosts()
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0 && d.Contains('.') && !d.Contains('/') && !d.Contains(' ') && !d.Contains('*'))
            .Distinct();

    private static IEnumerable<string> ProxyBlockedDomains() =>
        TermLists.ProxyDomains()
            .Select(d => d.Trim().ToLowerInvariant())
            .Where(d => d.Length > 0 && !d.Contains('/') && !d.Contains(' '))
            .Distinct();
}
