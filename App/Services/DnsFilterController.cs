using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using SharkBlocker.Models;

namespace SharkBlocker.Services;

/// <summary>
/// Backlog #9 — comanda o filtro de DNS local (<see cref="LocalDnsFilter"/>) a partir do Watchdog.
///
/// O QUE FAZ, quando a opção "Filtro de DNS local" está ligada E há bloqueio ativo ou proteções permanentes:
///  1. sobe o servidor DNS em 127.0.0.1:53 (e ::1) e faz um AUTOTESTE (consulta de verdade, com resposta);
///  2. só se o autoteste passar: aponta o DNS de todos os adaptadores ativos para 127.0.0.1 (e ::1), guardando antes
///     o DNS original (arquivo dns-backup.json: estático ou automático/DHCP);
///  3. cria regras de firewall que fecham os desvios: DNS-sobre-TLS (porta 853), DNS-sobre-HTTPS para os
///     resolvedores públicos conhecidos (porta 443) e DNS comum (porta 53) para QUALQUER servidor que não seja o
///     filtro ou o servidor de cima (Cloudflare Família).
///
/// SEGURANÇA DE REVERSÃO (é a parte mais arriscada do projeto: DNS quebrado = PC "sem internet"):
///  - nada é alterado no Windows se o filtro não subir ou o autoteste falhar;
///  - FAIL-OPEN: se o autoteste falhar 2 vezes seguidas com o filtro ativo, desfaz tudo (firewall, DNS) e tenta de
///    novo só depois de 2 min. Aqui disponibilidade vence: o hosts, as políticas e a extensão continuam valendo;
///  - desfaz tudo quando a opção é desligada, quando o bloqueio e as proteções permanentes acabam, quando o serviço
///    para, na desinstalação e no emergency-fix-hosts.bat;
///  - a restauração só mexe em adaptadores que AINDA apontam para 127.0.0.1/::1 e usa o DNS original salvo (ou
///    volta ao automático), nunca em um DNS que a pessoa trocou depois.
///
/// LIMITES HONESTOS: (1) se o Watchdog for MORTO à força com o DNS já trocado, o DNS fica em 127.0.0.1 sem ninguém
/// escutando até o serviço reiniciar (ele reinicia sozinho; senão use o emergency-fix-hosts.bat); (2) DoH para um
/// resolvedor que não esteja na lista (um servidor próprio) não é pego pelo firewall; (3) VPN/VM têm o próprio DNS;
/// (4) o Cloudflare Família recusa conteúdo adulto/malware, mas não é infalível.
///
/// SEM WPF: compilado também pelo Watchdog. Só o Watchdog (SYSTEM) chama <see cref="Tick"/>.
/// </summary>
public static class DnsFilterController
{
    private static readonly object Gate = new();
    private static LocalDnsFilter? _filter;
    private static bool _applied;
    private static int _busy;
    private static DateTime _nextAttemptUtc = DateTime.MinValue;
    private static DateTime _lastHealthUtc = DateTime.MinValue;
    private static DateTime _lastReapplyUtc = DateTime.MinValue;
    private static DateTime _lastRulesCheckUtc = DateTime.MinValue;
    private static DateTime _lastDomainsUtc = DateTime.MinValue;
    private static string _domainsKey = "";
    private static int _healthFailures;
    private static DateTime _lastOrphanCheckUtc = DateTime.MinValue;
    private static DateTime _lastStatsLogUtc = DateTime.MinValue;

    private static readonly TimeSpan HealthEvery = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReapplyEvery = TimeSpan.FromSeconds(60);   // pega adaptador novo (Wi-Fi que conectou depois)
    private static readonly TimeSpan RulesCheckEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DomainsEvery = TimeSpan.FromSeconds(30);

    private static readonly string BackupFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SharkBlocker", "dns-backup.json");

    private const string RulePrefix = "SharkBlocker-DNS-";
    private const string RuleProbeName = RulePrefix + "Plain53-UDP";

    // Resolvedores públicos de DNS-sobre-HTTPS conhecidos (IPv4 e IPv6). Lista conferida de memória; não é completa.
    private const string DohAddresses =
        "1.1.1.1,1.0.0.1,1.1.1.2,1.0.0.2,1.1.1.3,1.0.0.3,8.8.8.8,8.8.4.4,9.9.9.9,149.112.112.112,9.9.9.10,149.112.112.10," +
        "208.67.222.222,208.67.220.220,208.67.222.123,208.67.220.123,94.140.14.14,94.140.15.15,94.140.14.15,94.140.15.16," +
        "185.228.168.9,185.228.169.9,185.228.168.10,185.228.169.11,45.90.28.0/24,45.90.30.0/24," +
        "2606:4700:4700::1111,2606:4700:4700::1001,2606:4700:4700::1112,2606:4700:4700::1002,2606:4700:4700::1113," +
        "2606:4700:4700::1003,2001:4860:4860::8888,2001:4860:4860::8844,2620:fe::fe,2620:fe::9,2620:119:35::35,2620:119:53::53";

    // Porta 53 bloqueada para TODOS, exceto: 127.0.0.0/8, 1.0.0.3 e 1.1.1.3 (servidores de cima) e ::1.
    private const string Plain53BlockedRanges =
        "0.0.0.0-1.0.0.2,1.0.0.4-1.1.1.2,1.1.1.4-126.255.255.255,128.0.0.0-255.255.255.255," +
        "::,::2-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff";

    /// <summary>true quando o filtro DEVE estar ligado agora.</summary>
    public static bool WantsFilter(BlockState state) =>
        state.LocalDnsFilterEnabled && (state.IsLocked || state.PermanentProtectionsEnabled);

    /// <summary>Chamado a cada ciclo do Watchdog. Rápido: o trabalho pesado roda em segundo plano (um por vez).</summary>
    public static void Tick(BlockState state)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                lock (Gate) { TickCore(state); }
            }
            catch (Exception ex) { Logger.LogException("DnsFilterController.Tick", ex); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary>Serviço parando: devolve o DNS ao normal antes de sair.</summary>
    public static void Shutdown()
    {
        try
        {
            lock (Gate)
            {
                // DNS PRIMEIRO: o serviço pode ser morto à força poucos segundos depois (reinstalação); com o DNS já normal
                // o PC segue com internet mesmo que o resto da limpeza seja interrompido (ver RepairIfOrphaned).
                if (_filter != null || _applied || File.Exists(BackupFile)) Deactivate("serviço parando", dnsFirst: true);
            }
        }
        catch (Exception ex) { Logger.LogException("DnsFilterController.Shutdown", ex); }
    }

    /// <summary>
    /// Desfaz tudo o que o filtro mudou no Windows (firewall + DNS) sem precisar do filtro rodando. Usado na
    /// desinstalação. Idempotente e nunca lança.
    /// </summary>
    public static void RestoreSystemDns()
    {
        try
        {
            lock (Gate)
            {
                RemoveFirewall(deep: true);
                RunPowerShell(RestoreScript());
            }
        }
        catch (Exception ex) { Logger.LogException("DnsFilterController.RestoreSystemDns", ex); }
    }

    // ---------------------------------------------------------------- ciclo

    // ---------------------------------------------------------------- autoteste de PONTA A PONTA (rodada 2026-11-19)
    // O autoteste antigo só pergunta ao filtro em 127.0.0.1. Se o Windows não consegue resolver sites por OUTRO motivo (hosts ilegível
    // para o Cliente DNS, cache negativo, DNS órfão em outro adaptador...), ele passava e o PC ficava com DNS_PROBE_FINISHED_NXDOMAIN
    // até o emergency-fix-hosts.bat. Aqui se pergunta ao resolvedor do PRÓPRIO Windows (o que os navegadores usam).

    private static DateTime _lastSystemProbeUtc = DateTime.MinValue;
    private static DateTime _systemHealCooldownUtc = DateTime.MinValue;
    private static int _systemFailures;
    private static readonly TimeSpan SystemProbeEvery = TimeSpan.FromSeconds(30);

    [System.Runtime.InteropServices.DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    private static extern bool DnsFlushResolverCache();

    /// <summary>true se o resolvedor do Windows resolve um nome que sempre existe. Esvazia o cache antes (cache positivo mascara a falha).</summary>
    private static bool SystemResolverWorks(out string detail)
    {
        try { DnsFlushResolverCache(); } catch { /* sem a API: testa mesmo assim */ }
        detail = "";
        foreach (var host in new[] { "dns.msftncsi.com", "www.microsoft.com" })
        {
            try
            {
                var task = Dns.GetHostAddressesAsync(host);
                if (!task.Wait(4000)) { detail = $"{host}: sem resposta em 4 s"; continue; }
                if (task.Result.Length > 0) return true;
                detail = $"{host}: resposta vazia";
            }
            catch (AggregateException ex)
            {
                var inner = ex.InnerException;
                detail = $"{host}: {inner?.GetType().Name}" +
                         (inner is System.Net.Sockets.SocketException se ? $" ({se.SocketErrorCode})" : "");
            }
            catch (Exception ex) { detail = $"{host}: {ex.GetType().Name}"; }
        }
        return false;
    }

    private static bool InternetReachable() =>
        Upstreams.Any(u => LocalDnsFilter.ProbeServer(u, "cloudflare.com", 2500));

    private static IEnumerable<IPAddress> Upstreams => LocalDnsFilter.Upstreams;

    private static void CheckSystemResolver(DateTime now)
    {
        var every = now < _resumeWatchUntilUtc ? TimeSpan.FromSeconds(5) : SystemProbeEvery;
        if (now - _lastSystemProbeUtc < every) return;
        _lastSystemProbeUtc = now;

        if (SystemResolverWorks(out var detail)) { _systemFailures = 0; return; }
        if (!InternetReachable()) { _systemFailures = 0; return; } // sem internet de verdade: não é culpa do DNS/hosts
        if (++_systemFailures < 2 || now < _systemHealCooldownUtc) return;

        _systemFailures = 0;
        _systemHealCooldownUtc = now.AddMinutes(10);
        Logger.Log($"DNS de ponta a ponta: a internet está de pé (o servidor de cima responde), mas o Windows NÃO resolve sites ({detail}). Reparando: DNS volta ao normal, proteção do hosts suspensa por 30 min e cache de DNS limpo.");
        try { HostsFileManager.LogHostsStateNow(); } catch { }

        // 1) filtro de DNS fora do caminho (fail-open) e sem nova tentativa por 10 min
        if (_filter != null || _applied || File.Exists(BackupFile)) Deactivate("o Windows não resolve sites (autoteste de ponta a ponta)");
        else if (AnyAdapterPointsToFilter()) { RemoveFirewall(deep: false); RunPowerShell(RestoreScript()); }
        _nextAttemptUtc = now.AddMinutes(10);

        // 2) hosts sem a proteção (leitura/escrita normais) por um tempo
        HostsFileManager.SuspendProtection(TimeSpan.FromMinutes(30), "o Windows não resolvia sites com a internet de pé");

        // 3) confere o resultado, para o log dizer se o reparo funcionou
        var ok = SystemResolverWorks(out var after);
        Logger.Log(ok
            ? "DNS de ponta a ponta: o Windows voltou a resolver sites depois do reparo."
            : $"DNS de ponta a ponta: AINDA não resolve depois do reparo ({after}). A causa é outra: rode diagnostico-dns.ps1 e envie o relatório.");
    }

    // Retomada após suspensão/hibernação (rodada 2026-11-19): o Watchdog roda a cada ~5 s; um intervalo bem maior entre dois ciclos
    // significa que o PC dormiu. Ao acordar a rede ainda não voltou e os soquetes do filtro podem estar mortos: reinicia o filtro,
    // limpa o cache de DNS e checa o resolvedor do Windows a cada ciclo por 3 min (em vez de a cada 30 s).
    private static DateTime _lastTickUtc = DateTime.MinValue;
    private static DateTime _resumeWatchUntilUtc = DateTime.MinValue;

    private static void DetectResume(DateTime now)
    {
        var gap = _lastTickUtc == DateTime.MinValue ? TimeSpan.Zero : now - _lastTickUtc;
        _lastTickUtc = now;
        if (gap < TimeSpan.FromSeconds(45)) return;

        Logger.Log($"Filtro de DNS: o PC voltou de suspensão/hibernação (ciclo parado por {(int)gap.TotalSeconds} s). Reiniciando o filtro e limpando o cache de DNS.");
        try { DnsFlushResolverCache(); } catch { }
        if (_filter != null)
        {
            // Soquetes antigos podem estar mortos: derruba só o filtro em memória; o ciclo seguinte sobe um novo e refaz o autoteste.
            try { _filter.Dispose(); } catch { }
            _filter = null;
            _nextAttemptUtc = DateTime.MinValue;
        }
        _resumeWatchUntilUtc = now.AddMinutes(3);
        _lastSystemProbeUtc = DateTime.MinValue;
        _systemFailures = 0;
    }

    private static void TickCore(BlockState state)
    {
        var now = DateTime.UtcNow;

        DetectResume(now);
        CheckSystemResolver(now);

        if (!WantsFilter(state))
        {
            if ((_filter != null || _applied || File.Exists(BackupFile)) && now >= _nextAttemptUtc)
            {
                Deactivate(state.LocalDnsFilterEnabled ? "sem bloqueio nem proteções permanentes" : "opção desligada");
                if (File.Exists(BackupFile)) _nextAttemptUtc = now.AddSeconds(60); // não conseguiu restaurar: tenta de novo em 1 min
            }
            if (_filter == null) HealOrphan(now, "sem bloqueio nem proteções permanentes");
            return;
        }

        if (_filter == null)
        {
            if (now < _nextAttemptUtc) { HealOrphan(now, "aguardando nova tentativa"); return; }

            var candidate = new LocalDnsFilter();
            try
            {
                candidate.SetBlockedDomains(BuildDomains(state));
                _domainsKey = DomainsKey(state);
                _lastDomainsUtc = now;
                candidate.Start();
            }
            catch (Exception ex)
            {
                candidate.Dispose();
                Logger.Log($"Filtro de DNS: não iniciou ({ex.Message}). Nada foi alterado no Windows. Nova tentativa em 2 min.");
                _nextAttemptUtc = now.AddMinutes(2);
                HealOrphan(now, "o filtro não iniciou");
                return;
            }

            if (!LocalDnsFilter.Probe("cloudflare.com"))
            {
                candidate.Dispose();
                Logger.Log("Filtro de DNS: o autoteste não teve resposta (sem internet ou servidor de cima inalcançável). Nada foi alterado no Windows. Nova tentativa em 1 min.");
                _nextAttemptUtc = now.AddMinutes(1);
                HealOrphan(now, "o autoteste não teve resposta");
                return;
            }

            _filter = candidate;
            RunPowerShell(ApplyScript(candidate.ListensOnIPv6));
            ApplyFirewall();
            _applied = true;
            _healthFailures = 0;
            _lastHealthUtc = _lastReapplyUtc = _lastRulesCheckUtc = now;
            Logger.Log($"Filtro de DNS ATIVO (127.0.0.1{(candidate.ListensOnIPv6 ? " e ::1" : "")}); DNS do Windows apontado para ele; DoT/DoH/porta 53 bloqueados no firewall.");
            return;
        }

        // Já ativo: manter lista, saúde, DNS dos adaptadores e regras.
        RefreshDomains(state, now);

        if (now - _lastHealthUtc >= HealthEvery)
        {
            _lastHealthUtc = now;
            if (LocalDnsFilter.Probe("cloudflare.com"))
            {
                _healthFailures = 0;
                if (now - _lastStatsLogUtc >= TimeSpan.FromMinutes(5))
                {
                    _lastStatsLogUtc = now;
                    Logger.Log($"Filtro de DNS: {_filter.Queries} consultas, {_filter.BlockedCount} bloqueadas, {_filter.UpstreamFailures} sem resposta do servidor de cima.");
                }
            }
            else if (++_healthFailures >= 2)
            {
                Logger.Log("Filtro de DNS: o autoteste falhou 2 vezes seguidas. DESFAZENDO tudo (fail-open) para não deixar o PC sem internet; nova tentativa em 2 min.");
                Deactivate("autoteste falhou");
                _nextAttemptUtc = now.AddMinutes(2);
                return;
            }
        }

        if (now - _lastReapplyUtc >= ReapplyEvery)
        {
            _lastReapplyUtc = now;
            RunPowerShell(ApplyScript(_filter.ListensOnIPv6));
        }

        if (now - _lastRulesCheckUtc >= RulesCheckEvery)
        {
            _lastRulesCheckUtc = now;
            if (!FirewallBlocker.RunNetsh($"advfirewall firewall show rule name=\"{RuleProbeName}\"")) ApplyFirewall();
        }
    }

    /// <summary>true se algum adaptador de rede ainda tem 127.0.0.1/::1 como servidor DNS (sem PowerShell: barato).</summary>
    private static bool AnyAdapterPointsToFilter()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var a in ni.GetIPProperties().DnsAddresses)
                    if (IPAddress.IsLoopback(a)) return true;
            }
        }
        catch { /* sem informação: não mexe */ }
        return false;
    }

    /// <summary>
    /// REDE DE SEGURANÇA contra "DNS órfão": o Windows aponta para 127.0.0.1/::1 mas o filtro NÃO está atendendo (serviço morto à
    /// força, reinício sem internet, autoteste que falhou, backup perdido). Sem isto só o emergency-fix-hosts.bat consertava.
    /// Se ninguém responde em 127.0.0.1:53, devolve o DNS ao normal. Se OUTRO programa responde ali (Pi-hole, AdGuard Home...),
    /// não mexe. No máximo uma checagem a cada 30 s e só enquanto este filtro não está atendendo.
    /// </summary>
    private static void HealOrphan(DateTime now, string why)
    {
        if (_filter != null) return;
        if (now - _lastOrphanCheckUtc < TimeSpan.FromSeconds(30)) return;
        _lastOrphanCheckUtc = now;
        if (!AnyAdapterPointsToFilter()) return;
        if (LocalDnsFilter.Probe("cloudflare.com", 2000) || LocalDnsFilter.SomeoneListens()) return;
        Logger.Log($"Filtro de DNS: o DNS do Windows aponta para 127.0.0.1/::1, mas o filtro não está atendendo ({why}). Devolvendo o DNS ao normal (fail-open).");
        RemoveFirewall(deep: false);
        RunPowerShell(RestoreScript());
        _applied = false;
    }

    /// <summary>
    /// Conserta o "DNS órfão" SEM depender do filtro deste processo: chamado pelo App ao abrir e por quem acabou de matar o
    /// Watchdog (reinstalação). Causa real observada em 2026-11-09: a reinstalação esperava só ~5 s pelo serviço parar e depois
    /// matava o Watchdog no meio da restauração (firewall removido, DNS ainda em 127.0.0.1, backup ainda no disco) e, sem serviço,
    /// ninguém consertava: só o emergency-fix. Não faz nada se alguém atende em 127.0.0.1:53 (o filtro vivo, Pi-hole...) ou se
    /// um Watchdog está vivo e o DNS já não aponta para o filtro. Devolve true se restaurou.
    /// </summary>
    public static bool RepairIfOrphaned(string why)
    {
        try
        {
            lock (Gate)
            {
                if (_filter != null) return false;
                var points = AnyAdapterPointsToFilter();
                if (points && (LocalDnsFilter.Probe("cloudflare.com", 2000) || LocalDnsFilter.SomeoneListens())) return false;
                if (!points && (!File.Exists(BackupFile) || System.Diagnostics.Process.GetProcessesByName("Watchdog").Length > 0)) return false;
                Logger.Log($"Filtro de DNS: DNS órfão detectado ({why}): {(points ? "o Windows aponta para 127.0.0.1/::1 e ninguém atende" : "sobrou o backup do DNS")}. Devolvendo o DNS ao normal.");
                RemoveFirewall(deep: true);
                RunPowerShell(RestoreScript());
                _applied = false;
                return true;
            }
        }
        catch (Exception ex) { Logger.LogException("DnsFilterController.RepairIfOrphaned", ex); return false; }
    }

    private static void RefreshDomains(BlockState state, DateTime now)
    {
        var key = DomainsKey(state);
        if (key == _domainsKey && now - _lastDomainsUtc < DomainsEvery) return;
        _domainsKey = key;
        _lastDomainsUtc = now;
        _filter?.SetBlockedDomains(BuildDomains(state));
    }

    private static string DomainsKey(BlockState s) =>
        $"{s.IsLocked}|{s.BlockedWebsites?.Count ?? 0}|{s.ImportedWebsites?.Count ?? 0}";

    /// <summary>Sempre: os sites permanentes. Com bloqueio ativo: também os da lista da pessoa (e todos os subdomínios deles).</summary>
    private static HashSet<string> BuildDomains(BlockState s)
    {
        // Sites SEMPRE bloqueados (adult-terms.js): o filtro só roda com bloqueio ou proteções permanentes, então valem sempre.
        var all = new List<string>(TermLists.PermanentSites());
        if (s.IsLocked)
        {
            all.AddRange(s.BlockedWebsites ?? new List<string>());
            all.AddRange(s.ImportedWebsites ?? new List<string>());
        }
        return DnsFilterCore.BuildDomainSet(all);
    }

    /// <summary>Ordem: firewall PRIMEIRO, depois o DNS, depois o servidor (quem usa o DNS nunca fica sem resposta).</summary>
    private static void Deactivate(string reason, bool dnsFirst = false)
    {
        Logger.Log($"Filtro de DNS: desativando ({reason}).");
        if (dnsFirst)
        {
            RunPowerShell(RestoreScript());
            RemoveFirewall(deep: false);
        }
        else
        {
            RemoveFirewall(deep: false);
            RunPowerShell(RestoreScript());
        }
        try { _filter?.Dispose(); } catch { }
        _filter = null;
        _applied = false;
        Logger.Log("Filtro de DNS: desativado (DNS e firewall devolvidos ao normal).");
    }

    // ---------------------------------------------------------------- firewall

    private static void ApplyFirewall()
    {
        var rules = new (string Name, string Args)[]
        {
            ("DoT-TCP", "protocol=TCP remoteport=853"),
            ("DoT-UDP", "protocol=UDP remoteport=853"),
            ("DoH-TCP", $"protocol=TCP remoteport=443 remoteip={DohAddresses}"),
            ("DoH-UDP", $"protocol=UDP remoteport=443 remoteip={DohAddresses}"),
            ("Plain53-UDP", $"protocol=UDP remoteport=53 remoteip={Plain53BlockedRanges}"),
            ("Plain53-TCP", $"protocol=TCP remoteport=53 remoteip={Plain53BlockedRanges}"),
        };
        foreach (var (name, args) in rules)
        {
            var full = RulePrefix + name;
            FirewallBlocker.RunNetsh($"advfirewall firewall delete rule name=\"{full}\"");
            if (!FirewallBlocker.RunNetsh($"advfirewall firewall add rule name=\"{full}\" dir=out action=block enable=yes profile=any {args}"))
                Logger.Log($"Filtro de DNS: não consegui criar a regra de firewall {full}.");
        }
    }

    private static void RemoveFirewall(bool deep)
    {
        foreach (var name in new[] { "DoT-TCP", "DoT-UDP", "DoH-TCP", "DoH-UDP", "Plain53-UDP", "Plain53-TCP" })
            FirewallBlocker.RunNetsh($"advfirewall firewall delete rule name=\"{RulePrefix}{name}\"");

        if (deep)
        {
            RunPowerShell(
                "Get-NetFirewallRule -DisplayName 'SharkBlocker-DNS-*' -ErrorAction SilentlyContinue | " +
                "Remove-NetFirewallRule -ErrorAction SilentlyContinue");
        }
    }

    // ---------------------------------------------------------------- DNS do Windows (PowerShell)

    private static bool RunPowerShell(string script)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return FirewallBlocker.Run(exe, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", 60_000);
    }

    /// <summary>
    /// Guarda o DNS ORIGINAL de cada adaptador ativo (só o que ainda não é nosso; nunca grava 127.0.0.1 como
    /// "original") e aponta o DNS para o filtro. "NameServer" vazio no registro = automático (DHCP).
    /// </summary>
    private static string ApplyScript(bool withIPv6)
    {
        var servers = withIPv6 ? "'127.0.0.1','::1'" : "'127.0.0.1'";
        return """
            $ErrorActionPreference = 'Continue'
            $bak = '__BAK__'
            $saved = @()
            if (Test-Path -LiteralPath $bak) { try { $saved = @(Get-Content -Raw -LiteralPath $bak | ConvertFrom-Json) } catch { $saved = @() } }
            $changed = $false
            foreach ($a in @(Get-NetAdapter | Where-Object { $_.Status -eq 'Up' })) {
              $g = [string]$a.InterfaceGuid
              $cur = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue).ServerAddresses)
              $mine = ($cur -contains '127.0.0.1') -or ($cur -contains '::1')
              $known = $saved | Where-Object { $_.Guid -eq $g }
              if ((-not $known) -and (-not $mine)) {
                $v4 = [string]((Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\$g" -ErrorAction SilentlyContinue).NameServer)
                $v6 = [string]((Get-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\$g" -ErrorAction SilentlyContinue).NameServer)
                $saved += [pscustomobject]@{ Guid = $g; Alias = [string]$a.Name; V4 = $v4; V6 = $v6 }
                $changed = $true
              }
              if ($changed) { ConvertTo-Json -InputObject @($saved) | Set-Content -LiteralPath $bak -Encoding UTF8; $changed = $false }
              try { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses @(__SERVERS__) -ErrorAction Stop }
              catch { try { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses @('127.0.0.1') -ErrorAction Stop } catch { } }
            }
            Clear-DnsClientCache -ErrorAction SilentlyContinue
            """.Replace("__BAK__", BackupFile.Replace("'", "''")).Replace("__SERVERS__", servers);
    }

    /// <summary>
    /// Devolve o DNS dos adaptadores que ainda apontam para o filtro: o original salvo (estático) ou automático
    /// (DHCP). Adaptador sem registro salvo volta ao automático. Depois apaga o backup.
    /// </summary>
    private static string RestoreScript()
    {
        return """
            $ErrorActionPreference = 'Continue'
            $bak = '__BAK__'
            $saved = @()
            if (Test-Path -LiteralPath $bak) { try { $saved = @(Get-Content -Raw -LiteralPath $bak | ConvertFrom-Json) } catch { $saved = @() } }
            foreach ($a in @(Get-NetAdapter)) {
              $cur = @((Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ErrorAction SilentlyContinue).ServerAddresses)
              if (($cur -notcontains '127.0.0.1') -and ($cur -notcontains '::1')) { continue }
              $g = [string]$a.InterfaceGuid
              $s = $saved | Where-Object { $_.Guid -eq $g } | Select-Object -First 1
              $static = @()
              if ($s) {
                if ($s.V4) { $static += @(([string]$s.V4) -split '[,\s]+' | Where-Object { $_ }) }
                if ($s.V6) { $static += @(([string]$s.V6) -split '[,\s]+' | Where-Object { $_ }) }
              }
              try {
                if ($static.Count -gt 0) { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ServerAddresses $static -ErrorAction Stop }
                else { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop }
              } catch { try { Set-DnsClientServerAddress -InterfaceIndex $a.ifIndex -ResetServerAddresses -ErrorAction Stop } catch { } }
            }
            Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue
            Clear-DnsClientCache -ErrorAction SilentlyContinue
            """.Replace("__BAK__", BackupFile.Replace("'", "''"));
    }
}
