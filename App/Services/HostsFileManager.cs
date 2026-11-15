using System.Diagnostics;
using System.IO;

namespace SharkBlocker.Services;

/// <summary>
/// Bloqueia sites redirecionando o domínio para 127.0.0.1 no arquivo hosts,
/// e protege esse arquivo contra edição — inclusive por um Bloco de Notas
/// aberto "Como Administrador".
///
/// IMPORTANTE — sincronização entre processos: o App e o serviço Watchdog
/// são DOIS PROCESSOS SEPARADOS que podem tentar ler/alterar/escrever o
/// mesmo arquivo hosts quase ao mesmo tempo (o Watchdog reforça a cada 5s
/// independente do app estar aberto). Sem coordenação, um processo pode
/// escrever uma versão desatualizada por cima do que o outro acabou de
/// gravar — cada operação abaixo agora usa um Mutex nomeado do Windows
/// ("Global\...") pra garantir que só um processo mexa no hosts por vez,
/// eliminando essa condição de corrida.
///
/// Proteção de permissão: usamos os SIDs bem-conhecidos do Windows (não os
/// nomes localizados dos grupos) pra negar escrita tanto pra "Usuários"
/// quanto pra "Administradores". O Watchdog roda como NT AUTHORITY\SYSTEM —
/// conta separada, não pertence ao grupo Administradores — então continua
/// com acesso total mesmo com essas negações. Um Bloco de Notas aberto por
/// um Administrador (mesmo elevado) carrega o SID de Administradores no
/// token, então a negação vale pra ele too.
///
/// Limite honesto: negar "escrita" (W) não é o mesmo que negar "alterar
/// permissões" (WRITE_DAC). Um Administrador que rode `icacls` manualmente
/// ainda consegue reverter — é o mesmo caminho que o próprio app usa
/// internamente (Unprotect) pra conseguir escrever antes de reproteger.
/// </summary>
public static class HostsFileManager
{
    public static readonly string HostsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    private const string MarkerStart = "# === SharkBlocker START ===";
    private const string MarkerEnd = "# === SharkBlocker END ===";

    private const string SafeSearchMarkerStart = "# === SharkBlocker SAFESEARCH START ===";
    private const string SafeSearchMarkerEnd = "# === SharkBlocker SAFESEARCH END ===";

    private const string SidAdministrators = "*S-1-5-32-544";
    private const string SidUsers = "*S-1-5-32-545";

    // "Global\" torna o mutex visível entre sessões/serviços, não só dentro
    // do mesmo processo — é o que sincroniza App (sessão do usuário) e
    // Watchdog (SYSTEM, sessão 0) entre si.
    private static readonly Mutex HostsMutex = new(false, "Global\\SharkBlocker_HostsFileMutex");

    private static void WithHostsLock(Action action)
    {
        var acquired = false;
        try
        {
            acquired = HostsMutex.WaitOne(TimeSpan.FromSeconds(15));
            
            // 🔧 BUG FIX #5: CRÍTICO - Não executar sem lock!
            if (!acquired)
            {
                Logger.Log("ERRO CRÍTICO: não consegui adquirir lock do hosts em 15s. " +
                           "Outro processo pode estar preso. Recusando executar a ação pra evitar corrida de dados.");
                return;  // Não executa sem lock!
            }
            
            action();
        }
        catch (AbandonedMutexException)
        {
            // Um processo anterior morreu sem liberar o mutex — o .NET ainda
            // nos dá posse dele aqui. Prossegue normalmente.
            acquired = true;
            action();
        }
        finally
        {
            if (acquired)
            {
                try { HostsMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    // ---- Subdomínios (o hosts NÃO aceita curinga) ----
    // Além de "d" e "www.d", cada site digitado pela pessoa ganha linhas para estes prefixos comuns
    // (versão mobile, AMP e idiomas). Cobre o que a extensão não alcança: Firefox, apps com navegador
    // embutido e anônimo fora do bloqueio. Subdomínios fora da lista continuam livres no hosts — a
    // extensão (onde roda) bloqueia qualquer subdomínio. Configurável em BlockState.HostsSubdomainPrefixes.
    public static readonly string[] DefaultSubdomainPrefixes =
        { "m", "mobile", "amp", "touch", "en", "pt", "pt-br", "es", "fr", "de", "it" };

    /// <summary>
    /// Só os PRIMEIROS N domínios distintos da lista são expandidos. Os chamadores passam os sites digitados
    /// antes dos importados (BlockedWebsites.Concat(ImportedWebsites)), então listas importadas de dezenas de
    /// milhares de domínios NÃO são multiplicadas (o hosts ficaria enorme e o DNS do Windows, lento).
    /// Com os 11 prefixos padrão: no máximo ~6.500 linhas extras.
    /// </summary>
    public const int MaxExpandedDomains = 500;

    private const int MaxSubdomainPrefixes = 40;
    private static bool _expansionCapWarned; // avisa uma vez por processo (o Watchdog monta o bloco a cada 5 s)

    private static readonly System.Text.RegularExpressions.Regex LabelSyntax = new(
        @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly HashSet<string> SecondLevelLabels = new(StringComparer.Ordinal)
        { "co", "com", "org", "net", "gov", "edu", "ac", "or", "ne", "go" };

    /// <summary>
    /// Valida os prefixos: um rótulo DNS por item, minúsculo, sem "www" (já é gerado), sem repetição,
    /// no máximo 40. null = usa a lista padrão; lista vazia = sem expansão.
    /// </summary>
    public static List<string> SanitizeSubdomainPrefixes(IEnumerable<string>? custom)
    {
        var source = custom ?? DefaultSubdomainPrefixes;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in source)
        {
            var p = raw?.Trim().Trim('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(p) || p == "www" || !LabelSyntax.IsMatch(p) || !seen.Add(p)) continue;
            result.Add(p);
            if (result.Count >= MaxSubdomainPrefixes) break;
        }
        return result;
    }

    /// <summary>
    /// true para "site.com" e "bbc.co.uk" / "globo.com.br" (domínio-raiz). false para "old.reddit.com":
    /// expandir um subdomínio ("m.old.reddit.com") só incharia o arquivo.
    /// </summary>
    private static bool IsApexLike(string d)
    {
        var labels = d.Split('.');
        if (labels.Length == 2) return true;
        return labels.Length == 3 && labels[2].Length == 2 && SecondLevelLabels.Contains(labels[1]);
    }

    /// <param name="subdomainPrefixes">
    /// Prefixos de subdomínio (BlockState.HostsSubdomainPrefixes). TODOS os chamadores (app, HostsWatcher,
    /// Watchdog) devem passar o mesmo valor, senão cada um reescreve o bloco do outro.
    /// </param>
    public static void ApplyBlockedSites(IEnumerable<string> domains, IEnumerable<string>? subdomainPrefixes = null)
    {
        var domainListSnapshot = domains.ToList(); // materializa antes de entrar no lock
        var prefixes = SanitizeSubdomainPrefixes(subdomainPrefixes);
        Exception? captured = null;

        WithHostsLock(() =>
        {
            try
            {
                var domainList = domainListSnapshot.Distinct().Where(d => !string.IsNullOrWhiteSpace(d)).ToList();

                var intendedBlock = BuildBlockLines(domainList, prefixes);

                var currentLines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
                var currentBlock = ExtractMarkedBlock(currentLines, MarkerStart, MarkerEnd);

                // Otimização importante pra listas grandes (dezenas de milhares
                // de domínios importados): se o bloco já está exatamente como
                // deveria estar, NÃO reescreve o arquivo (nem chama icacls, nem
                // ipconfig). Sem isso, o Watchdog reescrevia o hosts inteiro a
                // cada 5s, o tempo todo, mesmo sem nada ter mudado — com um
                // hosts de ~140 mil linhas (70 mil domínios x 2), isso pesava
                // bastante e deixava a navegação visivelmente mais lenta.
                if (currentBlock != null && currentBlock.SequenceEqual(intendedBlock))
                {
                    return;
                }

                UnprotectInternal();

                var lines = currentLines;
                RemoveOurBlock(lines);
                if (intendedBlock.Count > 0)
                {
                    lines.Add(MarkerStart);
                    lines.AddRange(intendedBlock);
                    lines.Add(MarkerEnd);
                }

                File.WriteAllLines(HostsPath, lines);
                FlushDnsCache();

                // 🔧 BUG FIX #3: Proteger o arquivo imediatamente após escrever
                ProtectInternal();

                var afterContent = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
                if (domainList.Count > 0 && !afterContent.Contains(MarkerStart))
                {
                    Logger.Log("AVISO: hosts foi escrito mas o marcador de bloqueio NÃO aparece na releitura logo em seguida. " +
                               "Sinal de que algo (antivírus, outro processo) reverteu a alteração. " +
                               $"Caminho: {HostsPath}");
                }
                else
                {
                    Logger.Log($"ApplyBlockedSites OK: {domainList.Count} domínio(s) escritos e protegidos ({intendedBlock.Count} linhas; {prefixes.Count} prefixo(s) de subdomínio).");
                }
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ApplyBlockedSites", ex);
                if (ex is UnauthorizedAccessException) LogHostsStateThrottled();
                captured = ex;
            }
        });

        if (captured != null) throw captured;
    }

    private static readonly System.Text.RegularExpressions.Regex DomainSyntax = new(
        @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Reduz o que a pessoa digitou/colou a um domínio válido para o hosts, ou devolve null se não
    /// for um domínio. "https://www.YouTube.com:443/watch?v=1" vira "youtube.com".
    /// CORRIGIDO: antes só eram removidos "http(s)://" e a "/" final, então uma URL com caminho
    /// ("youtube.com/watch?v=x") gerava uma linha inválida no hosts (nada era bloqueado), e
    /// "www.site.com" virava "www.www.site.com" (o site sem www ficava livre).
    /// </summary>
    public static string? NormalizeDomain(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = input.Trim().ToLowerInvariant();

        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];

        var cut = s.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) s = s[..cut];

        var at = s.LastIndexOf('@');
        if (at >= 0) s = s[(at + 1)..];

        var colon = s.LastIndexOf(':');
        if (colon >= 0 && s[(colon + 1)..].All(char.IsDigit)) s = s[..colon];

        s = s.Trim('.');
        if (s.StartsWith("www.", StringComparison.Ordinal)) s = s[4..];

        try { s = new System.Globalization.IdnMapping().GetAscii(s); }
        catch { return null; }

        return DomainSyntax.IsMatch(s) ? s : null;
    }

    private static List<string> BuildBlockLines(List<string> domainList, IReadOnlyList<string> prefixes)
    {
        var result = new List<string>(domainList.Count * 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);      // domínios da lista já tratados
        var generated = new HashSet<string>(StringComparer.Ordinal); // subdomínios gerados (poucos milhares, no máx.)
        var expanded = 0;

        foreach (var domain in domainList)
        {
            var d = NormalizeDomain(domain);
            if (d == null || !seen.Add(d)) continue;

            // Se este nome já foi gerado como subdomínio de outro site, não repete a linha.
            if (!generated.Contains(d)) result.Add($"127.0.0.1 {d}");
            result.Add($"127.0.0.1 www.{d}");

            if (prefixes.Count == 0 || !IsApexLike(d)) continue;
            if (expanded >= MaxExpandedDomains)
            {
                if (!_expansionCapWarned)
                {
                    _expansionCapWarned = true;
                    Logger.Log($"hosts: subdomínios gerados só para os {MaxExpandedDomains} primeiros sites da lista " +
                               "(o resto fica só com o domínio e o www; a extensão cobre os subdomínios onde roda).");
                }
                continue;
            }
            expanded++;

            foreach (var p in prefixes)
            {
                var sub = $"{p}.{d}";
                if (seen.Contains(sub)) continue; // já listado pela pessoa, antes deste
                if (generated.Add(sub)) result.Add($"127.0.0.1 {sub}");
            }
        }
        return result;
    }

    /// <summary>Retorna as linhas ENTRE os marcadores (sem incluir os próprios marcadores), ou null se não existirem.</summary>
    private static List<string>? ExtractMarkedBlock(List<string> lines, string markerStart, string markerEnd)
    {
        var startIdx = lines.IndexOf(markerStart);
        var endIdx = lines.IndexOf(markerEnd);
        if (startIdx < 0 || endIdx <= startIdx) return null;
        return lines.GetRange(startIdx + 1, endIdx - startIdx - 1);
    }

    public static void ClearBlockedSites()
    {
        WithHostsLock(() =>
        {
            try
            {
                UnprotectInternal();
                if (!File.Exists(HostsPath)) return;
                var lines = File.ReadAllLines(HostsPath).ToList();
                RemoveOurBlock(lines);
                File.WriteAllLines(HostsPath, lines);
                FlushDnsCache();
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ClearBlockedSites", ex);
            }
        });
    }

    /// <summary>Nega escrita p/ Usuários e Administradores. Seguro de chamar a qualquer momento.</summary>
    public static void Protect()
    {
        WithHostsLock(() =>
        {
            try
            {
                // OTIMIZAÇÃO: o Watchdog chama isto a cada 5 s. Se o arquivo já está protegido, não
                // dispara 2 processos icacls.exe à toa. Qualquer dúvida na checagem = comportamento antigo.
                if (IsAlreadyProtected()) return;

                File.SetAttributes(HostsPath, FileAttributes.ReadOnly | FileAttributes.System);
                RunIcacls($"\"{HostsPath}\" /deny {SidAdministrators}:(W)");
                RunIcacls($"\"{HostsPath}\" /deny {SidUsers}:(W)");
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.Protect", ex);
            }
        });
    }

    // ---- Leitura do estado atual (sem spawnar processo) ----
    private static readonly System.Security.Principal.SecurityIdentifier AdminSid = new("S-1-5-32-544");
    private static readonly System.Security.Principal.SecurityIdentifier UsersSid = new("S-1-5-32-545");

    private static bool HasExplicitDeny(System.Security.AccessControl.AuthorizationRuleCollection rules,
        System.Security.Principal.SecurityIdentifier sid, bool requireWriteData)
    {
        foreach (System.Security.AccessControl.FileSystemAccessRule r in rules)
        {
            if (r.AccessControlType != System.Security.AccessControl.AccessControlType.Deny) continue;
            if (!sid.Equals(r.IdentityReference)) continue;
            if (requireWriteData && (r.FileSystemRights & System.Security.AccessControl.FileSystemRights.WriteData) == 0) continue;
            return true;
        }
        return false;
    }

    /// <summary>true só se TEM certeza: ReadOnly+System e negação de escrita explícita p/ Administradores e Usuários.</summary>
    private static bool IsAlreadyProtected()
    {
        try
        {
            if (!File.Exists(HostsPath)) return false;
            var attrs = File.GetAttributes(HostsPath);
            if ((attrs & FileAttributes.ReadOnly) == 0 || (attrs & FileAttributes.System) == 0) return false;

            var sec = new FileInfo(HostsPath).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            var rules = sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
            return HasExplicitDeny(rules, AdminSid, true) && HasExplicitDeny(rules, UsersSid, true);
        }
        catch { return false; }
    }

    /// <summary>true só se TEM certeza: sem ReadOnly/System e sem nenhuma negação explícita p/ Administradores/Usuários.</summary>
    private static bool IsAlreadyUnprotected()
    {
        try
        {
            if (!File.Exists(HostsPath)) return false;
            var attrs = File.GetAttributes(HostsPath);
            if ((attrs & FileAttributes.ReadOnly) != 0 || (attrs & FileAttributes.System) != 0) return false;

            var sec = new FileInfo(HostsPath).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            var rules = sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier));
            return !HasExplicitDeny(rules, AdminSid, false) && !HasExplicitDeny(rules, UsersSid, false);
        }
        catch { return false; }
    }

    // 🔧 BUG FIX #3 & #4: Novo método ProtectInternal para uso dentro de WithHostsLock
    /// <summary>Versão interna de Protect que não adquire o lock novamente — usada por métodos que já estão dentro de WithHostsLock.</summary>
    private static void ProtectInternal()
    {
        try
        {
            if (!File.Exists(HostsPath)) return;
            if (IsAlreadyProtected()) return;

            File.SetAttributes(HostsPath, FileAttributes.ReadOnly | FileAttributes.System);
            RunIcacls($"\"{HostsPath}\" /deny {SidAdministrators}:(W)");
            RunIcacls($"\"{HostsPath}\" /deny {SidUsers}:(W)");
        }
        catch (Exception ex)
        {
            Logger.LogException("HostsFileManager.ProtectInternal", ex);
        }
    }

    public static void ResetAcl()
    {
        WithHostsLock(() =>
        {
            try
            {
                if (!File.Exists(HostsPath)) return;

                RunIcacls($"\"{HostsPath}\" /grant *S-1-5-18:F");       // SYSTEM: controle total
                RunIcacls($"\"{HostsPath}\" /grant {SidAdministrators}:F"); // Administradores: controle total
                RunIcacls($"\"{HostsPath}\" /grant {SidUsers}:RX");     // Usuários: leitura e execução
                RunIcacls($"\"{HostsPath}\" /remove:d {SidAdministrators}"); // remove negação antiga, se houver
                RunIcacls($"\"{HostsPath}\" /remove:d {SidUsers}");

                Logger.Log("ACL do hosts restaurado explicitamente pro padrão conhecido-bom (sem usar /reset).");
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ResetAcl", ex);
            }
        });
    }

    private static DateTime _lastStateLogUtc = DateTime.MinValue;

    /// <summary>
    /// Diagnóstico de "acesso negado" no hosts (rodada 2026-11-15): 1 linha com quem somos, atributos, dono e todas as regras da ACL,
    /// no máximo a cada 10 min (o Watchdog tenta a cada 5 s e antes enchia o log só com o mesmo erro). Nunca lança.
    /// </summary>
    private static void LogHostsStateThrottled()
    {
        try
        {
            if (DateTime.UtcNow - _lastStateLogUtc < TimeSpan.FromMinutes(10)) return;
            _lastStateLogUtc = DateTime.UtcNow;

            var who = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var attrs = File.GetAttributes(HostsPath);
            var sec = new FileInfo(HostsPath).GetAccessControl(
                System.Security.AccessControl.AccessControlSections.Access | System.Security.AccessControl.AccessControlSections.Owner);
            var owner = sec.GetOwner(typeof(System.Security.Principal.NTAccount));
            var rules = sec.GetAccessRules(true, true, typeof(System.Security.Principal.NTAccount))
                .Cast<System.Security.AccessControl.FileSystemAccessRule>()
                .Select(r => $"{(r.AccessControlType == System.Security.AccessControl.AccessControlType.Deny ? "NEGA" : "permite")} {r.IdentityReference} {r.FileSystemRights}{(r.IsInherited ? " (herdada)" : "")}");
            Logger.Log($"hosts (acesso negado) como {who}: atributos {attrs}; dono {owner}; ACL: {string.Join(" | ", rules)}");
        }
        catch (Exception ex)
        {
            Logger.LogException("HostsFileManager.LogHostsStateThrottled", ex);
        }
    }

    /// <summary>Remove as negações explícitas de Administradores/Usuários pela API de ACL (plano B do icacls). Nunca lança.</summary>
    private static void RemoveDenyRulesViaAcl()
    {
        try
        {
            if (!File.Exists(HostsPath)) return;
            var fi = new FileInfo(HostsPath);
            var sec = fi.GetAccessControl(System.Security.AccessControl.AccessControlSections.Access);
            var removed = 0;
            foreach (System.Security.AccessControl.FileSystemAccessRule r in
                     sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
            {
                if (r.AccessControlType != System.Security.AccessControl.AccessControlType.Deny) continue;
                if (!AdminSid.Equals(r.IdentityReference) && !UsersSid.Equals(r.IdentityReference)) continue;
                sec.RemoveAccessRuleSpecific(r); // devolve void; o resultado é conferido por IsAlreadyUnprotected depois
                removed++;
            }
            if (removed == 0) return;
            fi.SetAccessControl(sec);
            Logger.Log($"hosts: o icacls não removeu a negação de escrita; removi {removed} regra(s) pela API de ACL.");
        }
        catch (Exception ex)
        {
            Logger.LogException("HostsFileManager.RemoveDenyRulesViaAcl", ex);
        }
    }

    public static void Unprotect() => WithHostsLock(UnprotectInternal);

    /// <summary>Versão "crua", sem adquirir o lock de novo — usada pelos métodos que já estão dentro de WithHostsLock.</summary>
    private static void UnprotectInternal()
    {
        try
        {
            // OTIMIZAÇÃO: sem nada a remover, não dispara 2 icacls.exe (o Watchdog chama isto a cada 5 s fora do bloqueio).
            if (IsAlreadyUnprotected()) return;

            RunIcacls($"\"{HostsPath}\" /remove:d {SidAdministrators}");
            RunIcacls($"\"{HostsPath}\" /remove:d {SidUsers}");
            // Rodada 2026-11-15: no PC do usuário o "icacls /remove:d" saía com código 0 e a negação continuava (o Watchdog,
            // que é SYSTEM mas tem o grupo Administradores no token, ficava barrado a cada 5 s). Se ainda há negação, remove-a
            // direto pela API de ACL do .NET (não depende do icacls).
            if (!IsAlreadyUnprotected()) RemoveDenyRulesViaAcl();
            if (File.Exists(HostsPath))
            {
                var attrs = File.GetAttributes(HostsPath);
                attrs &= ~FileAttributes.ReadOnly;
                attrs &= ~FileAttributes.System;
                File.SetAttributes(HostsPath, attrs);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("HostsFileManager.Unprotect", ex);
        }
    }

    public static void ApplySafeSearchEntries(IEnumerable<(string Domain, string Ip)> entries)
    {
        var entryListSnapshot = entries.ToList();

        WithHostsLock(() =>
        {
            try
            {
                var intendedBlock = entryListSnapshot.Select(e => $"{e.Ip} {e.Domain}").ToList();

                var currentLines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
                var currentBlock = ExtractMarkedBlock(currentLines, SafeSearchMarkerStart, SafeSearchMarkerEnd);

                if (currentBlock != null && currentBlock.SequenceEqual(intendedBlock))
                {
                    return;
                }

                UnprotectInternal();

                var lines = currentLines;
                RemoveMarkedBlock(lines, SafeSearchMarkerStart, SafeSearchMarkerEnd);

                if (intendedBlock.Count > 0)
                {
                    lines.Add(SafeSearchMarkerStart);
                    lines.AddRange(intendedBlock);
                    lines.Add(SafeSearchMarkerEnd);
                }

                File.WriteAllLines(HostsPath, lines);
                FlushDnsCache();
                
                // 🔧 BUG FIX #4: Proteger após escrita (mesmo que ApplyBlockedSites)
                ProtectInternal();
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ApplySafeSearchEntries", ex);
                if (ex is UnauthorizedAccessException) LogHostsStateThrottled();
            }
        });
    }

    public static void ClearSafeSearchEntries()
    {
        WithHostsLock(() =>
        {
            try
            {
                UnprotectInternal();
                if (!File.Exists(HostsPath)) return;
                var lines = File.ReadAllLines(HostsPath).ToList();
                RemoveMarkedBlock(lines, SafeSearchMarkerStart, SafeSearchMarkerEnd);
                File.WriteAllLines(HostsPath, lines);
                FlushDnsCache();
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.ClearSafeSearchEntries", ex);
            }
        });
    }

    /// <summary>
    /// Escreve uma linha de teste isolada, confere se ela aparece numa
    /// releitura, e remove em seguida. Não mexe no bloqueio real.
    /// </summary>
    public static (bool Success, string Message) SelfTest()
    {
        const string testMarker = "# SharkBlocker-SELFTEST";
        const string testDomain = "sharkblocker-selftest.invalid";
        var result = (Success: false, Message: "");

        WithHostsLock(() =>
        {
            try
            {
                UnprotectInternal();

                var lines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
                lines.RemoveAll(l => l.Contains(testMarker) || l.Contains(testDomain));
                lines.Add($"127.0.0.1 {testDomain} {testMarker}");
                File.WriteAllLines(HostsPath, lines);

                var reread = File.ReadAllText(HostsPath);
                var wroteOk = reread.Contains(testDomain);

                var cleanLines = File.ReadAllLines(HostsPath).ToList();
                cleanLines.RemoveAll(l => l.Contains(testMarker) || l.Contains(testDomain));
                File.WriteAllLines(HostsPath, cleanLines);

                result = wroteOk
                    ? (true, $"OK: a linha de teste apareceu normalmente em {HostsPath} logo após escrever.")
                    : (false, $"FALHOU: escrevi em {HostsPath} sem erro, mas a linha de teste NÃO apareceu na " +
                              "releitura imediata. Sinal forte de algo revertendo a alteração por fora do app " +
                              "(antivírus, outro software de proteção do hosts, ou política de grupo).");
            }
            catch (Exception ex)
            {
                Logger.LogException("HostsFileManager.SelfTest", ex);
                result = (false, $"FALHOU com exceção: {ex.GetType().Name}: {ex.Message}");
            }
        });

        return result;
    }

    public static bool ContainsOurBlock(IEnumerable<string> expectedDomains)
    {
        if (!File.Exists(HostsPath)) return false;
        var content = File.ReadAllText(HostsPath);
        return expectedDomains
            .Select(NormalizeDomain)
            .Where(d => d != null)
            .All(d => content.Contains(d!, StringComparison.OrdinalIgnoreCase));
    }

    private static void RemoveOurBlock(List<string> lines) => RemoveMarkedBlock(lines, MarkerStart, MarkerEnd);

    private static void RemoveMarkedBlock(List<string> lines, string markerStart, string markerEnd)
    {
        RemoveSingleBlock(lines, markerStart, markerEnd);

        // Compatibilidade: versões antigas (nome anterior do app) gravaram blocos com outro marcador.
        // Sem isto, um bloco antigo ficaria preso no hosts depois da atualização.
        RemoveSingleBlock(lines,
            markerStart.Replace("SharkBlocker", "ColdTurkeyClone"),
            markerEnd.Replace("SharkBlocker", "ColdTurkeyClone"));
    }

    private static void RemoveSingleBlock(List<string> lines, string markerStart, string markerEnd)
    {
        var startIdx = lines.IndexOf(markerStart);
        var endIdx = lines.IndexOf(markerEnd);
        if (startIdx >= 0 && endIdx >= startIdx)
            lines.RemoveRange(startIdx, endIdx - startIdx + 1);
    }

    private static void FlushDnsCache()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false };
            Process.Start(psi)?.WaitForExit();
        }
        catch { }
    }

    private static void RunIcacls(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            var stdout = p?.StandardOutput.ReadToEnd();
            var stderr = p?.StandardError.ReadToEnd();
            p?.WaitForExit();
            if (p != null && p.ExitCode != 0)
            {
                Logger.Log($"icacls \"{arguments}\" saiu com código {p.ExitCode}. stdout: {stdout} | stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("HostsFileManager.RunIcacls", ex);
        }
    }
}
