using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SharkBlocker.Services;

/// <summary>
/// VERSAO MELHORADA - Bloqueia processos de navegadores alternativos:
/// - Tor Browser (firefox.exe em pasta Tor)
/// - DuckDuckGo
/// - Epic Privacy Browser
/// - Navegadores portáveis
/// - VPNs/Proxies
///
/// Mata o processo em tempo real + monitora inicializações.
/// </summary>
public static class ProcessBlocker
{
    // Nomes de processo bloqueados (com variações).
    // CORRIGIDO: a lista anterior também matava msiexec (instaladores/Windows Update), thunderbird,
    // e programas de antivírus (bitdefender, avastclient). Isso quebrava o PC durante o bloqueio.
    private static readonly string[] BlockedProcessNames = new[]
    {
        "tor",
        "firefox",           // Firefox normal + Tor Browser (a extensão não cobre o Firefox)
        "duckduckgo",
        "epic",
        "opera",
        "operadriver",
        "vivaldi",
        "openvpn",
        "wireguard",
        "vpnclient",
        "expressvpn",
        "nordvpn",
        "surfshark",
        "cyberghost",
        "hotspotshield",
        "windscribe",
        "zenmate",
        "hideme",
        "mullvad",
        "protonvpn",
        "purevpn",
        // Auditoria U2: VPNs/proxies que a lista não cobria. Nomes exatos de processo; o que variar de
        // nome (instaladores, versões portáteis) é pego pelos trechos de caminho logo abaixo.
        "warp-svc",          // Cloudflare WARP (serviço)
        "warp-taskbar",      // Cloudflare WARP (bandeja)
        "cloudflare warp",   // Cloudflare WARP (interface)
        "psiphon3",
        "psiphon",
        "psiphon-tunnel-core",
        "lantern",
        "tunnelbear",
        "ultrasurf",
        // Auditoria U6: navegadores alternativos cujo executável tem nome próprio. Os que se chamam
        // "chrome.exe"/"browser.exe" (Yandex, Iron, Chromium...) ou foram RENOMEADOS são pegos pelos
        // metadados do arquivo (BrowserSignatures, mais abaixo).
        "chromium",
        "waterfox",
        "librewolf",
        "floorp",
        "palemoon",
        "thorium",
        "supermium",
        "maxthon",
        "slimjet",
        "falkon"
    };

    // OPÇÃO, DESLIGADA POR PADRÃO (BlockState.BlockOptionalVpnTools): ferramentas de rede privada que muita gente
    // usa de forma legítima (trabalho, acesso ao próprio servidor, desenvolvimento). Só entram na varredura se a
    // pessoa ligar a opção na aba Proteções. Só nomes exatos de processo — nada de trecho genérico de caminho.
    // LIMITE HONESTO: serviços com reinício automático (ex.: tailscaled) voltam sozinhos e são encerrados de novo no
    // próximo ciclo (~3-5 s): a conexão cai, mas o processo "pisca".
    private static readonly string[] OptionalVpnProcessNames = new[]
    {
        "tailscale",
        "tailscaled",
        "tailscale-ipn",
        "zerotier-one_x64",
        "zerotier-one_x86",
        "hamachi-2",
        "hamachi-2-ui",
        "ngrok",
        "cloudflared"
    };

    // OPÇÃO, DESLIGADA POR PADRÃO (BlockState.BlockVirtualization) — auditoria U8 / backlog #11: máquinas virtuais, WSL e
    // Windows Sandbox têm rede e `hosts` próprios, então contornam hosts, políticas de navegador e extensão de uma vez.
    // Só nomes exatos de processo (nunca trecho de caminho). NÃO entram serviços de segundo plano (VBoxSVC,
    // vmware-hostd, vmcompute...): derrubá-los não ajuda e pode quebrar o Windows. Fora do alcance de propósito:
    // `bash` (também é o Git Bash) e `msrdc` (também é o Área de Trabalho Remota).
    // LIMITES HONESTOS: (1) `vmmem`/`vmmemWSL` é o processo do Windows que segura a memória da VM do WSL2/Hyper-V; o
    // Windows pode RECUSAR encerrá-lo (acesso negado, registrado no log): o que sempre cai são os programas que iniciam
    // e mostram a VM (`wsl`, `wslhost`, `VirtualBoxVM`, `vmware-vmx`...). (2) Emuladores Android (qemu) também são
    // encerrados. (3) VM que já estava rodando antes do bloqueio é encerrada no próximo ciclo (~3-5 s), sem aviso.
    private static readonly string[] VirtualizationProcessNames = new[]
    {
        "wsl", "wslhost", "wslg", "vmmem", "vmmemWSL",
        "WindowsSandbox", "WindowsSandboxClient", "WindowsSandboxRemoteSession", "WindowsSandboxServer",
        "vmconnect",
        "VirtualBox", "VirtualBoxVM", "VBoxHeadless", "VBoxSDL",
        "vmware", "vmplayer", "vmware-vmx",
        "qemu-system-x86_64", "qemu-system-i386", "qemu-system-aarch64"
    };

    // Trechos do CAMINHO que indicam navegadores/VPNs de burla.
    // CORRIGIDO: antes a lista tinha "temp", "appdata\\local", "portable", "k7", "panda", "socks",
    // "quarantine" e nomes de antivírus. Como a comparação é por "contém", isso encerrava QUALQUER
    // programa instalado em AppData\Local (Chrome instalado só pro usuário, Discord, Teams, OneDrive,
    // VS Code...) ou rodando de uma pasta Temp (inclusive o desinstalador e o próprio app, se
    // extraído lá) — e o antivírus. Ficam só trechos específicos de navegadores/VPNs.
    private static readonly string[] SuspiciousPathKeywords = new[]
    {
        "tor browser",
        // Instaladores e pastas do Tor Browser usam hífen/sublinhado/junto ("tor-browser-windows-...", "torbrowser-install-...").
        "tor-browser",
        "tor_browser",
        "torbrowser",
        "duckduckgo",
        "epic privacy",
        "openvpn",
        "wireguard",
        "expressvpn",
        "nordvpn",
        "surfshark",
        "cyberghost",
        "hotspot shield",
        "windscribe",
        "zenmate",
        "mullvad",
        "protonvpn",
        "purevpn",
        // Auditoria U2 (trechos específicos; nada de "warp" ou "lantern" soltos, que casariam com outros programas).
        "cloudflare warp",
        "psiphon",
        "\\lantern\\",
        "tunnelbear",
        "ultrasurf",
        "\\tor\\",
        "\\vpn\\",
        "\\proxy\\"
    };

    // Nunca encerrados, mesmo que apareçam numa lista (nossa própria lista ou a do usuário):
    // matar qualquer um deles derruba o Windows ou o próprio bloqueio.
    private static readonly HashSet<string> NeverKill = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
        "svchost", "dwm", "explorer", "fontdrvhost", "sihost", "ctfmon", "taskhostw", "conhost",
        "MsMpEng", "SecurityHealthService", "SharkBlocker", "SharkBlocker", "Watchdog"
    };

    // ---------------------------------------------------------------------------------------------
    // Auditoria U6 — navegadores identificados pelos METADADOS do arquivo, não pelo nome
    // ---------------------------------------------------------------------------------------------
    // Antes só o nome do processo contava: renomear firefox.exe para calc2.exe, copiar um navegador portátil ou
    // usar um derivado de Chromium fora da lista (Yandex, Iron, Arc...) escapava. O recurso de versão do .exe
    // (ProductName, FileDescription, InternalName, OriginalFilename, CompanyName) sobrevive ao renomear.
    //
    // Duas classes (durante o bloqueio temporizado, que é quando esta varredura roda):
    //  - SEMPRE bloqueado, em qualquer pasta: navegadores que a nossa extensão NÃO cobre (Firefox e derivados,
    //    Tor, Opera, Vivaldi, Yandex, Iron, Arc, Chromium...). Mesma regra que o nome já aplicava a firefox/opera/vivaldi.
    //  - Bloqueado só FORA dos diretórios de instalação padrão: Chrome, Edge e Brave. Instalados (Program Files e
    //    as pastas por usuário do próprio instalador) ficam livres, porque é neles que a extensão e as políticas
    //    valem; cópia portátil ou renomeada, não. O mesmo vale para um "chrome.exe" de origem desconhecida.
    //
    // A comparação é por PALAVRAS inteiras (nunca por trecho de texto): "Brave Frontier" não casa com "brave"
    // sem a empresa certa, "Opera" não casa com "Cooperation", WebView2 e atualizadores do Edge são excluídos.
    //
    // LIMITE HONESTO: os textos de metadados abaixo são os que cada navegador costuma ter, conferidos de memória,
    // não por teste neste projeto. Para conferir um navegador de verdade:
    //   (Get-Item 'C:\caminho\arquivo.exe').VersionInfo | Format-List *
    // e ajuste/crie a assinatura. Programas portáteis que NÃO guardam metadados (ou os apagam) continuam
    // escapando; nesses casos só o nome e os trechos de caminho valem.
    private sealed record BrowserSignature(
        string Label,
        string[] Words,                       // todas devem aparecer em ProductName/FileDescription/InternalName/OriginalFilename
        string[]? CompanyWords = null,        // se houver, todas devem aparecer em CompanyName
        string[]? ExcludeWords = null,        // se alguma aparecer, a assinatura não vale
        bool AllowedWhenInstalled = false);   // true = só bloqueia fora dos diretórios padrão

    private sealed record BrowserFingerprint(string? Label, bool AllowedWhenInstalled);

    private static readonly BrowserFingerprint NotABrowser = new(null, false);

    private static readonly BrowserSignature[] BrowserSignatures =
    {
        // --- não cobertos pela extensão: sempre bloqueados durante o bloqueio ---
        new("Tor Browser", new[] { "tor", "browser" }),
        new("Firefox", new[] { "firefox" }),
        new("LibreWolf", new[] { "librewolf" }),
        new("Waterfox", new[] { "waterfox" }),
        new("Floorp", new[] { "floorp" }),
        new("Mullvad Browser", new[] { "mullvad", "browser" }),
        new("Pale Moon", new[] { "pale", "moon" }),
        new("Opera", new[] { "opera", "browser" }),
        new("Opera", new[] { "opera" }, CompanyWords: new[] { "opera" }),
        new("Vivaldi", new[] { "vivaldi" }),
        new("Yandex Browser", new[] { "yandex", "browser" }),
        new("SRWare Iron", new[] { "srware" }),
        new("Arc", new[] { "arc" }, CompanyWords: new[] { "browser", "company" }),
        new("Epic Privacy Browser", new[] { "epic", "privacy" }),
        new("DuckDuckGo", new[] { "duckduckgo" }),
        new("Comodo Dragon", new[] { "comodo", "dragon" }),
        new("Slimjet", new[] { "slimjet" }),
        new("Cent Browser", new[] { "cent", "browser" }),
        new("Maxthon", new[] { "maxthon" }),
        new("Thorium", new[] { "thorium" }),
        new("Supermium", new[] { "supermium" }),
        new("Falkon", new[] { "falkon" }),
        new("Chromium", new[] { "chromium" }),
        new("Avast Secure Browser", new[] { "avast", "secure", "browser" }),
        new("AVG Secure Browser", new[] { "avg", "secure", "browser" }),

        // --- cobertos pela extensão: só bloqueados fora dos diretórios de instalação padrão ---
        new("Google Chrome", new[] { "google", "chrome" }, CompanyWords: new[] { "google" }, AllowedWhenInstalled: true),
        new("Microsoft Edge", new[] { "microsoft", "edge" }, CompanyWords: new[] { "microsoft" },
            ExcludeWords: new[] { "webview", "webview2", "update", "updater" }, AllowedWhenInstalled: true),
        new("Brave", new[] { "brave" }, CompanyWords: new[] { "brave" }, AllowedWhenInstalled: true),
    };

    // Pastas por usuário onde o instalador normal de Chrome/Edge/Brave grava. Minúsculas, barras invertidas.
    private static readonly System.Text.RegularExpressions.Regex PerUserBrowserInstallDir = new(
        @"^[a-z]:\\users\\[^\\]+\\appdata\\local\\(google\\chrome[^\\]*|bravesoftware\\brave-browser[^\\]*|microsoft\\edge[^\\]*)\\application\\",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly Dictionary<string, (DateTime WriteUtc, BrowserFingerprint Result)> FingerprintCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FingerprintLock = new();
    private static bool _fingerprintErrorLogged;

    private static HashSet<string> Words(params string?[] parts)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            foreach (var w in System.Text.RegularExpressions.Regex.Split(part, @"[^\p{L}\p{N}]+"))
                if (w.Length > 0) set.Add(w);
        }
        return set;
    }

    /// <summary>Classifica um executável pelos textos do recurso de versão (função pura, sem acesso a disco).</summary>
    private static BrowserFingerprint ClassifyBrowser(string? productName, string? fileDescription, string? internalName,
                                                      string? originalFilename, string? companyName)
    {
        var words = Words(productName, fileDescription, internalName, originalFilename);
        var company = Words(companyName);

        foreach (var sig in BrowserSignatures)
        {
            if (!sig.Words.All(words.Contains)) continue;
            if (sig.CompanyWords != null && !sig.CompanyWords.All(company.Contains)) continue;
            if (sig.ExcludeWords != null && sig.ExcludeWords.Any(words.Contains)) continue;
            return new BrowserFingerprint(sig.Label, sig.AllowedWhenInstalled);
        }

        // Derivados de Chromium costumam manter o nome original "chrome.exe" no recurso de versão mesmo quando o
        // arquivo se chama outra coisa. Sem assinatura conhecida, trata como Chrome: livre só nas pastas padrão
        // (assim um Brave instalado, se os metadados dele forem diferentes do esperado, não é derrubado).
        if (string.Equals(originalFilename, "chrome.exe", StringComparison.OrdinalIgnoreCase))
            return new BrowserFingerprint("Chromium (derivado desconhecido)", true);

        return NotABrowser;
    }

    private static BrowserFingerprint GetBrowserFingerprint(string exePath)
    {
        DateTime write;
        try { write = System.IO.File.GetLastWriteTimeUtc(exePath); }
        catch { return NotABrowser; }

        lock (FingerprintLock)
        {
            if (FingerprintCache.TryGetValue(exePath, out var cached) && cached.WriteUtc == write)
                return cached.Result;
        }

        BrowserFingerprint result;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            result = ClassifyBrowser(vi.ProductName, vi.FileDescription, vi.InternalName, vi.OriginalFilename, vi.CompanyName);
        }
        catch (Exception ex)
        {
            // Arquivo sumiu/sem permissão: não é motivo para matar nada. Loga só a primeira vez.
            if (!_fingerprintErrorLogged)
            {
                _fingerprintErrorLogged = true;
                Logger.LogException("ProcessBlocker.GetBrowserFingerprint", ex);
            }
            result = NotABrowser;
        }

        lock (FingerprintLock)
        {
            if (FingerprintCache.Count > 2000) FingerprintCache.Clear(); // varredura de muitos .exe distintos: recomeça
            FingerprintCache[exePath] = (write, result);
        }
        return result;
    }

    private static bool IsStandardInstallPath(string exePath)
    {
        var p = exePath.Replace('/', '\\').ToLowerInvariant();
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Environment.GetFolderPath(folder);
            if (root.Length == 0) continue;
            if (p.StartsWith(root.TrimEnd('\\').ToLowerInvariant() + "\\", StringComparison.Ordinal)) return true;
        }
        return PerUserBrowserInstallDir.IsMatch(p);
    }

    /// <summary>
    /// true se o executável é um navegador conhecido pelos metadados e deve ser encerrado durante o bloqueio.
    /// Pastas do Windows nunca são examinadas (rápido e seguro). Qualquer falha = false (não mata).
    /// </summary>
    private static bool IsBlockedBrowserByMetadata(string exePath, out string label)
    {
        label = "";
        if (string.IsNullOrEmpty(exePath)) return false;

        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windowsDir.Length > 0 &&
            exePath.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return false;

        var fp = GetBrowserFingerprint(exePath);
        if (fp.Label == null) return false;
        if (fp.AllowedWhenInstalled && IsStandardInstallPath(exePath)) return false;

        label = fp.Label;
        return true;
    }

    private static bool IsProtected(Process proc)
    {
        try
        {
            if (proc.Id == Environment.ProcessId || proc.Id <= 4) return true;
            return NeverKill.Contains(proc.ProcessName);
        }
        catch
        {
            return true; // sem como identificar = não mexe
        }
    }

    // Executáveis que acabamos de encerrar, para o FirewallBlocker bloquear a saída de rede deles durante o bloqueio
    // (backlog #10). Só caminhos .exe fora da pasta do Windows; processos protegidos nunca chegam aqui (IsProtected).
    private static readonly object KilledGate = new();
    private static readonly HashSet<string> KilledPaths = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxKilledPaths = 256;

    private static void RecordKilledPath(Process proc)
    {
        try
        {
            var path = GetProcessPath(proc);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;

            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windowsDir.Length > 0 &&
                path.StartsWith(windowsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return;

            lock (KilledGate)
            {
                if (KilledPaths.Count < MaxKilledPaths) KilledPaths.Add(path);
            }
        }
        catch { /* só alimenta o firewall: nunca atrapalha o encerramento */ }
    }

    /// <summary>Entrega (e esvazia) os caminhos dos executáveis encerrados desde a última chamada.</summary>
    public static string[] TakeKilledPaths()
    {
        lock (KilledGate)
        {
            var all = KilledPaths.ToArray();
            KilledPaths.Clear();
            return all;
        }
    }

    public static void KillBlockedProcesses(IEnumerable<string> processNames, IEnumerable<string> pathKeywords)
    {
        var names = BuildNameSet(EffectiveNames(processNames));
        var keywords = BuildKeywordList(Combine(SuspiciousPathKeywords, pathKeywords));
        if (names.Count == 0 && keywords.Count == 0) return;

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!IsProtected(proc) && IsBlocked(proc, names, keywords, out var reason))
                {
                    Logger.Log($"KILLANDO PROCESSO BLOQUEADO: {proc.ProcessName} (PID: {proc.Id}, Path: {GetProcessPath(proc)}, motivo: {reason})");
                    RecordKilledPath(proc);
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { /* processo protegido ou já encerrado */ }
            finally
            {
                proc.Dispose(); // CORRIGIDO: os objetos Process da varredura nunca eram liberados
            }
        }
    }

    /// <summary>Checa e mata um único processo (usado pelo watcher em tempo real).</summary>
    public static void KillIfBlockedById(int processId, IEnumerable<string> processNames, IEnumerable<string> pathKeywords)
    {
        var names = BuildNameSet(EffectiveNames(processNames));
        var keywords = BuildKeywordList(Combine(SuspiciousPathKeywords, pathKeywords));
        if (names.Count == 0 && keywords.Count == 0) return;

        try
        {
            using var proc = Process.GetProcessById(processId);
            if (!IsProtected(proc) && IsBlocked(proc, names, keywords, out var reason))
            {
                Logger.Log($"KILLANDO PROCESSO BLOQUEADO (tempo real): {proc.ProcessName} (PID: {proc.Id}, motivo: {reason})");
                RecordKilledPath(proc);
                proc.Kill(entireProcessTree: true);
            }
        }
        catch { /* processo pode ter fechado */ }
    }

    /// <summary>
    /// Verifica se um processo é Firefox + em pasta Tor.
    /// Mais específico que apenas "firefox.exe".
    /// </summary>
    public static bool IsTorBrowser(Process proc)
    {
        try
        {
            if (!proc.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase))
                return false;

            var path = GetProcessPath(proc);
            return path.Contains("tor", StringComparison.OrdinalIgnoreCase) &&
                   path.Contains("browser", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return false;
    }

    /// <summary>Lista padrão + (se as opções estiverem ligadas) as ferramentas opcionais + a lista da pessoa.</summary>
    private static IEnumerable<string> EffectiveNames(IEnumerable<string>? custom)
    {
        IEnumerable<string> baseNames = BlockedProcessNames;
        try
        {
            // Estado em cache no StateManager. Se não puder ser lido, as listas opcionais NÃO entram: elas são
            // restrições extras, não a proteção principal.
            var state = StateManager.Load();
            if (state.BlockOptionalVpnTools) baseNames = baseNames.Concat(OptionalVpnProcessNames);
            if (state.BlockVirtualization) baseNames = baseNames.Concat(VirtualizationProcessNames);
        }
        catch { /* só a lista padrão */ }
        return Combine(baseNames, custom);
    }

    private static HashSet<string> BuildNameSet(IEnumerable<string> processNames) =>
        new(processNames.Select(n => n.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
            .Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);

    private static List<string> BuildKeywordList(IEnumerable<string> pathKeywords) =>
        pathKeywords.Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).ToList();

    private static bool IsBlocked(Process proc, HashSet<string> names, List<string> keywords, out string reason)
    {
        reason = "";

        // Checar por nome de processo
        if (names.Contains(proc.ProcessName)) { reason = "nome do processo"; return true; }

        // Firefox especial: checar se é Tor Browser
        if (proc.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase))
        {
            if (IsTorBrowser(proc)) { reason = "Tor Browser"; return true; }
        }

        // O caminho é lido uma vez só (QueryFullProcessImageName) e serve às duas checagens abaixo.
        var path = GetProcessPath(proc);

        // Checar caminho
        if (keywords.Count > 0 && !string.IsNullOrEmpty(path))
        {
            var lowerPath = path.ToLowerInvariant();
            if (keywords.Any(k => lowerPath.Contains(k))) { reason = "trecho de caminho"; return true; }
        }

        // Auditoria U6: navegador identificado pelos metadados do arquivo (pega renomeados, portáteis e derivados).
        if (IsBlockedBrowserByMetadata(path, out var label))
        {
            reason = $"navegador identificado pelos metadados do arquivo: {label}";
            return true;
        }

        return false;
    }

    // OTIMIZAÇÃO: Process.MainModule carrega a lista de TODOS os módulos (DLLs) do processo só para
    // devolver o caminho do .exe — centenas de ms numa varredura completa, e falha em processos de
    // outra arquitetura/elevados. QueryFullProcessImageName abre um handle limitado e lê só o caminho.
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

    /// <summary>Caminho completo do executável do processo, ou "" se não der para ler.</summary>
    public static string GetImagePath(int processId)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return "";

            var buffer = new StringBuilder(1024);
            var size = (uint)buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : "";
        }
        catch { return ""; }
        finally
        {
            if (handle != IntPtr.Zero) CloseHandle(handle);
        }
    }

    private static string GetProcessPath(Process proc)
    {
        try { return GetImagePath(proc.Id); } catch { return ""; }
    }

    private static IEnumerable<string> Combine(IEnumerable<string> defaults, IEnumerable<string>? custom)
    {
        var result = new HashSet<string>(defaults, StringComparer.OrdinalIgnoreCase);
        if (custom != null)
            foreach (var item in custom)
                result.Add(item);
        return result;
    }
}
