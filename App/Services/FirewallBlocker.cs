using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SharkBlocker.Services;

/// <summary>
/// Backlog #10 — regras do Firewall do Windows contra VPNs/proxies/Tor, só DURANTE o bloqueio temporizado.
///
/// Por que: o ProcessBlocker encerra esses programas a cada ~3-5 s, mas um serviço que reinicia sozinho (ou um
/// programa que abre e conecta em poucos segundos) ainda consegue trafegar nesse intervalo. Uma regra de SAÍDA
/// bloqueada para o EXECUTÁVEL fecha essa janela: mesmo que ele abra, não conecta.
///
/// Quais executáveis (nunca regra genérica de rede, para não mexer na internet do resto do PC):
///  1. os que o próprio ProcessBlocker acabou de encerrar (ProcessBlocker.TakeKilledPaths); e
///  2. os de pastas de instalação de VPNs conhecidas (Program Files / AppData\Local\Programs), descobertos uma vez
///     por bloqueio (DiscoverInstalledPaths).
/// Pastas do Windows e processos protegidos nunca entram (ver ProcessBlocker.RecordKilledPath).
///
/// Ciclo de vida: Watchdog chama EnforceWhileLocked a cada ciclo com bloqueio ativo e RemoveIfActive quando não há;
/// a desinstalação e o emergency-fix-hosts.bat chamam RemoveAll/limpeza profunda. Os nomes das regras ficam num
/// arquivo (firewall-rules.txt) para a limpeza achar o que criamos mesmo depois de reiniciar o Watchdog ou o PC.
///
/// SEM WPF: este arquivo é compilado também pelo Watchdog.
///
/// LIMITES HONESTOS: (1) se o Firewall do Windows estiver desligado ou gerenciado por outro programa, as regras não
/// têm efeito (o ProcessBlocker segue valendo); (2) programa renomeado/copiado para outra pasta só é coberto na
/// primeira vez que for encerrado; (3) não bloqueia VPN dentro de máquina virtual (ver a opção BlockVirtualization).
/// </summary>
public static class FirewallBlocker
{
    private const string RulePrefix = "SharkBlocker-FW-";
    private const int MaxNewRulesPerCycle = 40;
    private const int MaxDiscoveredFiles = 150;
    private const int MaxTrackedRules = 400;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Applied = new(StringComparer.OrdinalIgnoreCase); // caminho -> nome da regra
    private static bool _loaded;
    private static bool _discovered;

    private static readonly string TrackFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SharkBlocker", "firewall-rules.txt");

    // Trechos (só letras e números, minúsculos) do NOME da pasta de instalação de VPNs/proxies conhecidos.
    private static readonly string[] VendorFolderKeys =
    {
        "nordvpn", "protonvpn", "mullvad", "wireguard", "openvpn", "expressvpn", "surfshark", "cyberghost",
        "windscribe", "psiphon", "tunnelbear", "hotspotshield", "zenmate", "purevpn", "ultrasurf"
    };

    /// <summary>true quando há regras nossas registradas (checagem barata, por ciclo).</summary>
    public static bool HasRules
    {
        get
        {
            try { return File.Exists(TrackFile) && new FileInfo(TrackFile).Length > 0; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Com bloqueio ativo: garante regras de saída bloqueada para os executáveis informados e para as VPNs instaladas.
    /// Idempotente (não repete o que já aplicou) e nunca lança.
    /// </summary>
    public static void EnforceWhileLocked(IEnumerable<string> killedPaths)
    {
        try
        {
            lock (Gate)
            {
                EnsureLoaded();

                var wanted = new List<string>(killedPaths ?? Array.Empty<string>());
                if (!_discovered)
                {
                    _discovered = true;
                    wanted.AddRange(DiscoverInstalledPaths());
                }

                var added = 0;
                var changed = false;
                foreach (var raw in wanted)
                {
                    if (added >= MaxNewRulesPerCycle) break;
                    var path = Normalize(raw);
                    if (path == null || Applied.ContainsKey(path)) continue;
                    if (Applied.Count >= MaxTrackedRules) break;

                    var name = RuleName(path);
                    // Idempotente: o netsh aceita nomes repetidos, então apaga qualquer regra antiga do mesmo nome.
                    RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                    var ok = RunNetsh(
                        $"advfirewall firewall add rule name=\"{name}\" dir=out action=block " +
                        $"program=\"{path}\" enable=yes profile=any description=\"SharkBlocker: bloqueio temporizado\"");
                    if (!ok) continue;

                    Applied[path] = name;
                    added++;
                    changed = true;
                    Logger.Log($"Firewall: saída bloqueada para {path}");
                }

                if (changed) SaveTracked();
            }
        }
        catch (Exception ex) { Logger.LogException("FirewallBlocker.EnforceWhileLocked", ex); }
    }

    /// <summary>Sem bloqueio ativo: se ainda há regras nossas, remove. Barato quando não há nada a fazer.</summary>
    public static void RemoveIfActive()
    {
        if (!HasRules) return;
        RemoveAll(deep: false);
    }

    /// <summary>
    /// Remove todas as regras que criamos. <paramref name="deep"/> = também varre por nome (SharkBlocker-FW-*) via
    /// PowerShell, para pegar regras de um registro perdido (usado na desinstalação). Nunca lança.
    /// </summary>
    public static void RemoveAll(bool deep)
    {
        try
        {
            lock (Gate)
            {
                EnsureLoaded();
                var removedAny = false;
                foreach (var name in Applied.Values.Distinct().ToList())
                {
                    RunNetsh($"advfirewall firewall delete rule name=\"{name}\"");
                    removedAny = true;
                }

                if (deep)
                {
                    var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                    Run(ps,
                        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                        "\"Get-NetFirewallRule -DisplayName 'SharkBlocker-FW-*' -ErrorAction SilentlyContinue | " +
                        "Remove-NetFirewallRule -ErrorAction SilentlyContinue\"", 30_000);
                    removedAny = true;
                }

                Applied.Clear();
                _discovered = false;
                try { if (File.Exists(TrackFile)) File.Delete(TrackFile); } catch { }
                if (removedAny) Logger.Log("Firewall: regras do bloqueio removidas.");
            }
        }
        catch (Exception ex) { Logger.LogException("FirewallBlocker.RemoveAll", ex); }
    }

    // ---------------------------------------------------------------- descoberta

    private static IEnumerable<string> DiscoverInstalledPaths()
    {
        var found = new List<string>();
        try
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs")
            };

            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var dir in SafeDirs(root))
                {
                    var key = KeyOf(Path.GetFileName(dir));
                    if (key == "cloudflare")
                    {
                        // Cloudflare WARP mora em "Cloudflare\Cloudflare WARP" (não bloqueia o resto da Cloudflare).
                        foreach (var sub in SafeDirs(dir))
                            if (KeyOf(Path.GetFileName(sub)).Contains("warp")) AddExes(sub, found);
                    }
                    else if (VendorFolderKeys.Any(k => key.Contains(k)))
                    {
                        AddExes(dir, found);
                    }
                    if (found.Count >= MaxDiscoveredFiles) return found;
                }
            }
        }
        catch (Exception ex) { Logger.LogException("FirewallBlocker.DiscoverInstalledPaths", ex); }
        return found;
    }

    private static void AddExes(string dir, List<string> into)
    {
        try
        {
            foreach (var exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories))
            {
                into.Add(exe);
                if (into.Count >= MaxDiscoveredFiles) return;
            }
        }
        catch { /* pasta sem acesso: ignora */ }
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static string KeyOf(string name) =>
        new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // ---------------------------------------------------------------- caminhos, nomes e registro

    /// <summary>Caminho completo e seguro para a regra, ou null (não é .exe existente, está no Windows, ou tem aspas).</summary>
    private static string? Normalize(string? raw)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var path = Path.GetFullPath(raw.Trim());
            if (path.Contains('"') || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(path)) return null;

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (windows.Length > 0 && path.StartsWith(windows.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return null;
            return path;
        }
        catch { return null; }
    }

    private static string RuleName(string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return RulePrefix + Convert.ToHexString(hash, 0, 6);
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(TrackFile)) return;
            foreach (var line in File.ReadAllLines(TrackFile))
            {
                var i = line.IndexOf('|');
                if (i <= 0) continue;
                var name = line[..i];
                var path = line[(i + 1)..];
                if (name.StartsWith(RulePrefix, StringComparison.Ordinal) && path.Length > 0) Applied[path] = name;
            }
        }
        catch (Exception ex) { Logger.LogException("FirewallBlocker.EnsureLoaded", ex); }
    }

    private static void SaveTracked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TrackFile)!);
            File.WriteAllLines(TrackFile, Applied.Select(kv => kv.Value + "|" + kv.Key));
        }
        catch (Exception ex) { Logger.LogException("FirewallBlocker.SaveTracked", ex); }
    }

    // ---------------------------------------------------------------- execução

    internal static bool RunNetsh(string arguments) =>
        Run(Path.Combine(Environment.SystemDirectory, "netsh.exe"), arguments, 15_000);

    /// <summary>Roda um programa do sistema (caminho completo, sem shell). true se saiu com código 0.</summary>
    internal static bool Run(string exe, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                Logger.Log($"{Path.GetFileName(exe)} não terminou em {timeoutMs / 1000} s — encerrado.");
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.LogException("FirewallBlocker.Run", ex);
            return false;
        }
    }
}
