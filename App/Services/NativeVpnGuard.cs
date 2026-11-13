using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

namespace SharkBlocker.Services;

/// <summary>
/// Auditoria U2 — VPN NATIVA do Windows (Configurações &gt; Rede &gt; VPN: PPTP, L2TP, SSTP, IKEv2).
/// Ela não tem processo de terceiros para encerrar: é o próprio serviço de acesso remoto (RAS) do Windows.
/// Durante o bloqueio temporizado, detecta conexões RAS/VPN ativas (adaptadores de rede do tipo PPP em estado
/// "Up") e as desconecta com o rasdial.exe do sistema (<c>rasdial "NOME" /disconnect</c>), registrando no log.
/// Também REGISTRA (sem agir) adaptadores de túnel de VPNs de terceiros (TAP/Wintun/WireGuard/WARP...): o
/// jeito de derrubá-los é encerrar o programa dono, o que o ProcessBlocker já faz.
///
/// Chamado a cada ciclo do bloqueio por DOIS lados, de propósito: o App (na sessão do usuário, onde existem as
/// conexões VPN "só deste usuário") e o Watchdog (conta SYSTEM, que enxerga as conexões "para todos os usuários").
///
/// LIMITE HONESTO: (1) a detecção é por tipo de adaptador, não pelo nome do perfil: uma conexão RAS que o Windows
/// não exponha como adaptador PPP "Up" passa; (2) o Watchdog (SYSTEM) NÃO consegue desconectar conexões VPN
/// "só deste usuário" — quem consegue é o App, então se o app estiver fechado a desconexão espera ele voltar
/// (o Watchdog o reabre em segundos); (3) nada impede a pessoa de reconectar logo em seguida: a próxima
/// varredura (~3-5 s) desconecta de novo; (4) VPN em máquina virtual/WSL fica fora do alcance (ver auditoria U8).
/// </summary>
public static class NativeVpnGuard
{
    private static readonly object Gate = new();
    private static DateTime _lastRunUtc = DateTime.MinValue;
    private static readonly Dictionary<string, DateTime> LastFailureByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoggedAdapters = new(StringComparer.OrdinalIgnoreCase);

    // Pedaços do nome/descrição que denunciam adaptador de VPN de terceiros (só para registro em log).
    private static readonly string[] VpnAdapterHints =
    {
        "tap-windows", "wintun", "wireguard", "openvpn", "cloudflare warp", "tunnelbear", "mullvad",
        "nordlynx", "protonvpn", "psiphon", "vpn"
    };

    /// <summary>
    /// Desconecta as conexões VPN/RAS ativas. Chamar SÓ durante o bloqueio temporizado. No máximo uma varredura
    /// a cada 3 s por processo. Nunca lança: falhas vão para o log.
    /// </summary>
    /// <returns>Quantas conexões foram desconectadas com sucesso nesta chamada.</returns>
    public static int DisconnectActiveVpnConnections(string origem)
    {
        lock (Gate)
        {
            if ((DateTime.UtcNow - _lastRunUtc).TotalSeconds < 3) return 0;
            _lastRunUtc = DateTime.UtcNow;
        }

        var disconnected = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;

                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Ppp)
                    {
                        // "RAS (Dial In) Interface" é a ponta de ENTRADA do Windows, não uma VPN do usuário.
                        if (nic.Description.Contains("Dial In", StringComparison.OrdinalIgnoreCase)) continue;
                        if (InCooldown(nic.Name)) continue;

                        Logger.Log($"VPN NATIVA/RAS ativa durante o bloqueio ({origem}): '{nic.Name}' ({nic.Description}) — desconectando.");
                        if (Disconnect(nic.Name)) disconnected++;
                    }
                    else if (LooksLikeThirdPartyVpnAdapter(nic))
                    {
                        LogAdapterOnce(nic, origem);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogException("NativeVpnGuard (adaptador)", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("NativeVpnGuard.DisconnectActiveVpnConnections", ex);
        }

        return disconnected;
    }

    private static bool Disconnect(string connectionName)
    {
        // 1º pelo nome do perfil (o nome do adaptador RAS é o nome da conexão); 2º, se falhar, todas as
        // conexões RAS do contexto atual (rasdial /disconnect sem nome).
        if (RunRasdial(connectionName, "/disconnect"))
        {
            Logger.Log($"VPN '{connectionName}' desconectada (rasdial).");
            return true;
        }
        if (RunRasdial("/disconnect"))
        {
            Logger.Log($"VPN '{connectionName}' desconectada (rasdial /disconnect geral).");
            return true;
        }

        lock (Gate) LastFailureByName[connectionName] = DateTime.UtcNow;
        Logger.Log($"Não consegui desconectar a VPN '{connectionName}' neste contexto (conexão de outro usuário ou perfil desconhecido). " +
                   "Nova tentativa em ~30 s; o app, na sessão do usuário, costuma conseguir.");
        return false;
    }

    // Depois de uma falha, não insiste (nem enche o log) por 30 s para o mesmo nome.
    private static bool InCooldown(string name)
    {
        lock (Gate)
            return LastFailureByName.TryGetValue(name, out var t) && (DateTime.UtcNow - t).TotalSeconds < 30;
    }

    private static bool LooksLikeThirdPartyVpnAdapter(NetworkInterface nic)
    {
        var text = (nic.Name + " " + nic.Description).ToLowerInvariant();
        return VpnAdapterHints.Any(h => text.Contains(h));
    }

    private static void LogAdapterOnce(NetworkInterface nic, string origem)
    {
        lock (Gate)
        {
            if (!LoggedAdapters.Add(nic.Name)) return;
        }
        Logger.Log($"Adaptador de VPN de terceiros ativo durante o bloqueio ({origem}): '{nic.Name}' ({nic.Description}). " +
                   "Só registro: o ProcessBlocker encerra o programa dono se ele estiver na lista.");
    }

    private static bool RunRasdial(params string[] args)
    {
        try
        {
            // Caminho completo do sistema (o Watchdog roda como SYSTEM: nada de depender do PATH).
            var exe = Path.Combine(Environment.SystemDirectory, "rasdial.exe");
            var psi = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) psi.ArgumentList.Add(a); // sem shell: o nome da conexão não vira comando

            using var p = Process.Start(psi);
            if (p == null) return false;

            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(); } catch { }
                Logger.Log("rasdial.exe não terminou em 10 s — encerrado.");
                return false;
            }
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.LogException("NativeVpnGuard.RunRasdial", ex);
            return false;
        }
    }
}
