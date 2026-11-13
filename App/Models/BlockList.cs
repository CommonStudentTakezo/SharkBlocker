namespace SharkBlocker.Models;

public class BlockState
{
    public List<string> BlockedWebsites { get; set; } = new();

    /// <summary>Versão da lista padrão de sites (DefaultSites) já juntada à lista acima; 0 = ainda não juntada.</summary>
    public int DefaultSitesVersion { get; set; }

    // Domínios importados de um arquivo grande (ex: blocklist de terceiros).
    // Ficam separados de BlockedWebsites pra não travar a caixa de texto da
    // interface com dezenas de milhares de linhas.
    public List<string> ImportedWebsites { get; set; } = new();
    public string? ImportedListLabel { get; set; }

    public List<string> BlockedKeywords { get; set; } = new();

    /// <summary>
    /// Prefixos de subdomínio que o hosts também bloqueia para cada site (o hosts não aceita curinga):
    /// "m.site.com", "pt.site.com"... null (padrão) = lista embutida em HostsFileManager.DefaultSubdomainPrefixes;
    /// lista vazia = não expandir subdomínios. Só vale para os sites digitados pela pessoa (ver
    /// HostsFileManager.MaxExpandedDomains). Sempre atribua uma lista NOVA (ver ShallowCopy).
    /// </summary>
    public List<string>? HostsSubdomainPrefixes { get; set; }

    // Nome exato de processo (sem .exe), ex: "steam", "discord"
    public List<string> BlockedProcesses { get; set; } = new();

    // Trecho do CAMINHO COMPLETO do executável (ex: "tor browser", "duckduckgo").
    // Usado para pegar programas cujo nome de processo sozinho não ajuda
    // (ex: o Tor Browser roda como "firefox.exe" por baixo dos panos).
    public List<string> BlockedProcessPathKeywords { get; set; } = new();

    // Caminho manual do Watchdog.exe, caso o app não consiga adivinhar (ex:
    // rodando de uma pasta de build/publish em vez da instalação via Setup).
    public string? WatchdogExePathOverride { get; set; }

    /// <summary>
    /// Conta e caminho com que a abertura automática foi registrada. O Watchdog usa isso para
    /// RECRIAR a tarefa agendada caso alguém a apague (sem ela o app não reabre sozinho).
    /// </summary>
    public string? AutoStartUser { get; set; }
    public string? AutoStartExePath { get; set; }

    // Proteções permanentes: SafeSearch (hosts + prefs nativas), DoH desligado,
    // extensão instalada, pasta protegida contra deleção. Independentes do
    // timer de bloqueio — ver PermanentProtections.cs.
    public bool PermanentProtectionsEnabled { get; set; }

    // true quando a pessoa DESATIVOU as proteções permanentes de propósito
    // (senha + confirmações). Impede o app de reativá-las sozinho na próxima
    // abertura — a ativação automática só vale pra quem nunca desativou.
    public bool PermanentProtectionsOptOut { get; set; }

    /// <summary>
    /// Opção (DESLIGADA por padrão): também encerrar ferramentas de VPN/rede privada de uso legítimo comum
    /// (Tailscale, ZeroTier, Hamachi, ngrok, cloudflared...) — ver ProcessBlocker.OptionalVpnProcessNames.
    /// Ligar não pede senha (só restringe mais); desligar passa por UninstallFlow.TryAuthorizeRemoval.
    /// </summary>
    public bool BlockOptionalVpnTools { get; set; }

    /// <summary>
    /// Opção (DESLIGADA por padrão): encerrar máquinas virtuais, WSL e Windows Sandbox durante o bloqueio
    /// (auditoria U8 / backlog #11) — ver ProcessBlocker.VirtualizationProcessNames. Uma VM tem rede e hosts
    /// próprios e contorna hosts, políticas de navegador e extensão. Ligar não pede senha (só restringe mais);
    /// desligar passa por UninstallFlow.TryAuthorizeRemoval.
    /// </summary>
    public bool BlockVirtualization { get; set; }

    /// <summary>
    /// Opção (DESLIGADA por padrão): detector de imagens adultas na tela (ImageGuard + NsfwClassifier, modelo ONNX no PC).
    /// Ligar não pede senha (só restringe mais); desligar passa por UninstallFlow.TryAuthorizeRemoval.
    /// </summary>
    public bool ImageDetectorEnabled { get; set; }

    /// <summary>Sensibilidade do detector: 1 = Cautelosa (0,90), 2 = Padrão (0,80), 3 = Sensível (0,65).</summary>
    public int ImageSensitivity { get; set; } = 2;

    /// <summary>Quantas travas por imagem nas últimas 24 h (escada 5 → 15 → 30 min). Zera sozinha; ver StateManager.StartImageScreenLock.</summary>
    public int ImageStrikes { get; set; }

    /// <summary>Quando foi a última trava por imagem (UTC); passadas 24 h, a escada recomeça.</summary>
    public DateTime? ImageStrikeUtc { get; set; }

    /// <summary>
    /// Opção (DESLIGADA por padrão) — backlog #9: filtro de DNS local no Watchdog (127.0.0.1:53), DNS do Windows
    /// apontado para ele e DoT/DoH/DNS comum externos bloqueados no firewall. Ver DnsFilterController. Mexe no DNS
    /// do PC inteiro, por isso é opt-in. Ligar não pede senha (só restringe mais); desligar passa por
    /// UninstallFlow.TryAuthorizeRemoval.
    /// </summary>
    public bool LocalDnsFilterEnabled { get; set; }

    /// <summary>
    /// Opção (DESLIGADA por padrão): manter o modo anônimo/InPrivate/janela privada DESATIVADO o tempo todo —
    /// não só durante o bloqueio temporizado — enquanto as proteções permanentes estiverem ligadas. Fora do
    /// bloqueio a extensão em geral não roda no anônimo; esta opção fecha essa brecha. Ligar não pede senha
    /// (só restringe mais); desligar passa por UninstallFlow.TryAuthorizeRemoval.
    /// Ver BrowserPolicyManager.IncognitoBlockedAlways.
    /// </summary>
    public bool BlockIncognitoAlways { get; set; }

    /// <summary>
    /// Opção (DESLIGADA por padrão) — auditoria U7: durante o bloqueio, também barrar serviços de arquivo web
    /// (Wayback Machine, archive.today...: BrowserExtension\proxy-terms.js, lista "archiveServices"), que mostram
    /// uma cópia de outro site e têm uso legítimo grande. Ligar não pede senha (só restringe mais); desligar
    /// passa por UninstallFlow.TryAuthorizeRemoval. A extensão lê isto em /state ("blockWebArchives").
    /// </summary>
    public bool BlockWebArchives { get; set; }

    public DateTime? LockedUntilUtc { get; set; }

    // Assinatura HMAC do LockedUntilUtc acima — ver StateManager.
    public string? LockSignature { get; set; }

    // Trava de tela (desinstalar ou busca inapropriada, 30 min): fica gravada, com
    // assinatura, pra que fechar/matar o app ou reiniciar o PC durante a espera NÃO
    // zere o tempo — ao reabrir, a tela volta a travar só pelo que faltava.
    public DateTime? ScreenLockUntilUtc { get; set; }

    // Por que a tela foi travada ("uninstall" ou "search"). Só serve pra escolher o texto
    // mostrado; não é assinado e não influencia o tempo.
    public string? ScreenLockReason { get; set; }
    public string? ScreenLockSignature { get; set; }

    public string? PasswordHash { get; set; }

    /// <summary>Bloqueios agendados (recorrentes). Sempre atribua uma lista NOVA; não altere por dentro (ver ShallowCopy).</summary>
    public List<BlockSchedule> Schedules { get; set; } = new();

    /// <summary>
    /// true depois da primeira vez que uma agenda foi salva. A partir daí, qualquer mudança exige a
    /// trava de tela de 30 min e a senha (ver UninstallFlow.TryAuthorizeScheduleChange).
    /// </summary>
    public bool SchedulesConfigured { get; set; }

    /// <summary>
    /// Cópia rasa (as listas são compartilhadas). Usada pelo cache do StateManager: quem recebe o
    /// estado pode trocar propriedades à vontade, mas NÃO deve alterar uma lista por dentro
    /// (.Add/.Remove) — sempre atribua uma lista nova.
    /// </summary>
    public BlockState ShallowCopy() => (BlockState)MemberwiseClone();

    public bool IsScreenLockActive =>
        ScreenLockUntilUtc.HasValue && ScreenLockUntilUtc.Value > DateTime.UtcNow;

    public bool IsLocked => LockedUntilUtc.HasValue && LockedUntilUtc.Value > DateTime.UtcNow;
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);
}

/// <summary>
/// Um horário recorrente de bloqueio. A janela COMEÇA nos dias marcados em <see cref="Days"/> e
/// dura de <see cref="StartTime"/> até <see cref="EndTime"/>; se o fim é menor ou igual ao início,
/// ela atravessa a meia-noite (ex.: 22:00 às 06:00 de seg começa na segunda e acaba na terça).
/// </summary>
public class BlockSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;

    public List<DayOfWeek> Days { get; set; } = new()
    {
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    };

    public TimeSpan StartTime { get; set; } = new TimeSpan(22, 0, 0);
    public TimeSpan EndTime { get; set; } = new TimeSpan(6, 0, 0);
}
