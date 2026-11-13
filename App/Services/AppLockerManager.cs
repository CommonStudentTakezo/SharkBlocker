using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SharkBlocker.Services;

/// <summary>
/// VERSAO MELHORADA - Bloqueia instalação E execução de:
/// - Tor Browser (todas as variantes)
/// - DuckDuckGo
/// - Epic Privacy Browser
/// - Navegadores portáveis
/// - VPNs (ExpressVPN, NordVPN, Surfshark, etc)
/// - Proxies
/// - Ferramentas de contorno
///
/// Funciona em Windows 11 Pro/Enterprise/Education via AppLocker.
/// Para Home, use ProcessBlocker (mata depois que abre).
/// </summary>
public static class AppLockerManager
{
    // Caminhos padrão bloqueados
    private static readonly string[] DefaultBlockedPaths =
    {
        // Tor Browser - muitas localizações possíveis
        @"%OSDRIVE%\Users\*\Desktop\Tor Browser\*\firefox.exe",
        @"%OSDRIVE%\Users\*\Downloads\Tor Browser\*\firefox.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\Tor Browser\*\firefox.exe",
        @"%OSDRIVE%\Users\*\AppData\Roaming\Tor Browser\*\firefox.exe",
        @"%PROGRAMFILES%\Tor Browser\*\firefox.exe",
        @"%PROGRAMFILES(X86)%\Tor Browser\*\firefox.exe",
        @"%OSDRIVE%\Tor Browser\*\firefox.exe",
        @"%OSDRIVE%\TorBrowser\*\firefox.exe",

        // DuckDuckGo - local instalação
        @"%OSDRIVE%\Users\*\AppData\Local\DuckDuckGo\*\DuckDuckGo.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\Programs\DuckDuckGo\*\DuckDuckGo.exe",
        @"%PROGRAMFILES%\DuckDuckGo\*\DuckDuckGo.exe",
        @"%PROGRAMFILES(X86)%\DuckDuckGo\*\DuckDuckGo.exe",

        // Epic Privacy Browser
        @"%OSDRIVE%\Users\*\AppData\Local\Programs\Epic Privacy Browser\*\Epic.exe",
        @"%PROGRAMFILES%\Epic Privacy Browser\*\Epic.exe",
        @"%PROGRAMFILES(X86)%\Epic Privacy Browser\*\Epic.exe",

        // Opera (alternativa frequente)
        @"%OSDRIVE%\Users\*\AppData\Local\Programs\Opera\*\Opera.exe",
        @"%PROGRAMFILES%\Opera\*\Opera.exe",

        // Vivaldi (alternativa)
        @"%OSDRIVE%\Users\*\AppData\Local\Vivaldi\*\Vivaldi.exe",
        @"%PROGRAMFILES%\Vivaldi\*\Vivaldi.exe",

        // VPNs - ExpressVPN
        @"%OSDRIVE%\Program Files\ExpressVPN\*\expressvpn.exe",
        @"%PROGRAMFILES%\ExpressVPN\*\expressvpn.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\ExpressVPN\*\expressvpn.exe",

        // NordVPN
        @"%PROGRAMFILES%\NordVPN\*\NordVPN.exe",
        @"%PROGRAMFILES(X86)%\NordVPN\*\NordVPN.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\NordVPN\*\NordVPN.exe",

        // Surfshark
        @"%PROGRAMFILES%\Surfshark\*\Surfshark.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\Surfshark\*\Surfshark.exe",

        // CyberGhost
        @"%PROGRAMFILES%\CyberGhost\*\CyberGhost.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\CyberGhost\*\CyberGhost.exe",

        // ProtonVPN
        @"%PROGRAMFILES%\ProtonVPN\*\ProtonVPN.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\ProtonVPN\*\ProtonVPN.exe",

        // Hotspot Shield
        @"%PROGRAMFILES%\Hotspot Shield\*\HotspotShield.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\Hotspot Shield\*\*.exe",

        // Windscribe
        @"%OSDRIVE%\Users\*\AppData\Local\Windscribe\*\*.exe",

        // Zenmate
        @"%OSDRIVE%\Users\*\AppData\Local\Zenmate\*\*.exe",

        // Mullvad
        @"%PROGRAMFILES%\Mullvad VPN\*\mullvad.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\Mullvad\*\*.exe",

        // OpenVPN
        @"%PROGRAMFILES%\OpenVPN\*\openvpn.exe",
        @"%PROGRAMFILES(X86)%\OpenVPN\*\openvpn.exe",

        // WireGuard
        @"%PROGRAMFILES%\WireGuard\*\wireguard.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\WireGuard\*\wireguard.exe",

        // Auditoria U2: Cloudflare WARP, Psiphon, Lantern, TunnelBear e UltraSurf.
        @"%PROGRAMFILES%\Cloudflare\Cloudflare WARP\*.exe",
        @"%PROGRAMFILES(X86)%\Cloudflare\Cloudflare WARP\*.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\Psiphon*\*.exe",
        @"%OSDRIVE%\Users\*\Downloads\psiphon*.exe",
        @"%OSDRIVE%\Users\*\Desktop\psiphon*.exe",
        @"%PROGRAMFILES%\Lantern\*.exe",
        @"%OSDRIVE%\Users\*\AppData\Roaming\Lantern\*.exe",
        @"%OSDRIVE%\Users\*\AppData\Local\Lantern\*.exe",
        @"%PROGRAMFILES%\TunnelBear\*.exe",
        @"%PROGRAMFILES(X86)%\TunnelBear\*.exe",
        @"%OSDRIVE%\Users\*\Downloads\*ultrasurf*.exe",
        @"%OSDRIVE%\Users\*\Desktop\*ultrasurf*.exe",

        // Firefox (opcional - descomente se quiser bloquear mesmo a versão normal)
        // @"%PROGRAMFILES%\Mozilla Firefox\*\firefox.exe",
        // @"%OSDRIVE%\Users\*\AppData\Local\Mozilla Firefox\*\firefox.exe",

        // Proxies genéricos
        @"%OSDRIVE%\Users\*\AppData\Local\*Proxy*\*.exe",
        @"%PROGRAMFILES%\*Proxy*\*.exe",

        // Instaladores suspeitos em Downloads/Desktop
        @"%OSDRIVE%\Users\*\Downloads\*tor*.exe",
        @"%OSDRIVE%\Users\*\Downloads\*vpn*.exe",
        @"%OSDRIVE%\Users\*\Downloads\*duckduckgo*.exe",
        @"%OSDRIVE%\Users\*\Desktop\*tor*.exe",
        @"%OSDRIVE%\Users\*\Desktop\*vpn*.exe",
        @"%OSDRIVE%\Users\*\Desktop\*duckduckgo*.exe",
    };

    public static bool IsSupported()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var editionId = key?.GetValue("EditionID") as string ?? "";
            var supported = editionId.Contains("Professional") || 
                           editionId.Contains("Enterprise") || 
                           editionId.Contains("Education");
            
            if (!supported)
            {
                Logger.Log($"AVISO: AppLocker nao suportado nesta edição do Windows: {editionId}. " +
                          "Use ProcessBlocker (mata processo) em vez de AppLocker (bloqueia instalacao).");
            }
            return supported;
        }
        catch (Exception ex)
        {
            Logger.LogException("AppLockerManager.IsSupported", ex);
            return false;
        }
    }

    /// <returns>true se aplicado com sucesso; false se falhou.</returns>
    public static bool Apply(IEnumerable<string>? extraPaths = null)
    {
        if (!IsSupported())
        {
            Logger.Log("AppLocker nao suportado nesta edicao do Windows.");
            return false;
        }

        var paths = DefaultBlockedPaths.Concat(extraPaths ?? Enumerable.Empty<string>())
            .Distinct()
            .ToList();

        Logger.Log($"Aplicando AppLocker com {paths.Count} regras de bloqueio...");

        // Gera XML de política com regra de "Negar Execução"
        var rulesXml = string.Join("\n", paths.Select((path, i) => $"""
              <FilePathRule Id="{Guid.NewGuid()}" Name="SharkBlocker bloqueio {i}" Description="" UserOrGroupSid="S-1-1-0" Action="Deny">
                <Conditions>
                  <FilePathCondition Path="{path}" />
                </Conditions>
              </FilePathRule>
            """));

        var policyXml = $"""
            <AppLockerPolicy Version="1">
              <RuleCollection Type="Exe" EnforcementMode="Enabled">
            {rulesXml}
              </RuleCollection>
            </AppLockerPolicy>
            """;

        var tempXmlFile = Path.Combine(Path.GetTempPath(), "SharkBlocker-AppLocker.xml");
        
        try
        {
            File.WriteAllText(tempXmlFile, policyXml);
        }
        catch (Exception ex)
        {
            Logger.LogException("AppLockerManager.Apply - Escrever XML", ex);
            return false;
        }

        var script = $"""
            Set-AppLockerPolicy -XmlPolicy '{tempXmlFile}' -Merge
            Set-Service -Name AppIDSvc -StartupType Automatic
            Start-Service -Name AppIDSvc
            """;

        return RunPowerShell(script, "Apply");
    }

    public static bool RemoveAll()
    {
        Logger.Log("Removendo TODAS as políticas AppLocker...");
        var script = """
            $empty = [xml]'<AppLockerPolicy Version="1"><RuleCollection Type="Exe" EnforcementMode="NotConfigured"/></AppLockerPolicy>'
            Set-AppLockerPolicy -XmlPolicy $empty
            """;
        return RunPowerShell(script, "RemoveAll");
    }

    /// <summary>Roda PowerShell script com tratamento de erro robusto.</summary>
    private static bool RunPowerShell(string innerScript, string context)
    {
        var scriptFile = Path.Combine(Path.GetTempPath(), $"SharkBlocker-{Guid.NewGuid():N}.ps1");

        var wrappedScript = $$"""
            $ErrorActionPreference = 'Stop'
            try {
            {{innerScript}}
              exit 0
            } catch {
              Write-Error $_.Exception.Message
              exit 1
            }
            """;

        try
        {
            File.WriteAllText(scriptFile, wrappedScript);

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptFile}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var p = Process.Start(psi);
            if (p == null)
            {
                Logger.Log($"ERRO: Process.Start() retornou null ao executar AppLocker script");
                return false;
            }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            var ok = p.ExitCode == 0;
            if (!ok)
            {
                Logger.Log($"AppLockerManager.{context} falhou (código {p.ExitCode}). " +
                          $"stdout: {stdout} | stderr: {stderr}");
            }
            else
            {
                Logger.Log($"AppLockerManager.{context} concluído com sucesso.");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Logger.LogException($"AppLockerManager.{context}", ex);
            return false;
        }
        finally
        {
            try { File.Delete(scriptFile); } catch { }
        }
    }
}
