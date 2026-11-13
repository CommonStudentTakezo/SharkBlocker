using Microsoft.Win32;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharkBlocker.Models;

namespace SharkBlocker.Services;

/// <summary>
/// VERSAO MELHORADA - Bloqueios mais efetivos contra:
/// - Navegadores alternativos (DuckDuckGo, Tor Browser, Epic Privacy)
/// - Modo incógnito/anônimo em TODAS as variantes
/// - VPNs e proxies
/// - DNS-over-HTTPS
/// - Palavras-chave sugestivas de contorno
///
/// Políticas confirmadas em setembro de 2026.
/// </summary>
public static class BrowserPolicyManager
{
    // Políticas de navegação anônima/privada. Tudo em HKLM (vale pra todos os
    // usuários da máquina).
    //
    // CORRIGIDO: a versão anterior também listava caminhos "HKEY_CURRENT_USER\...".
    // Como estas chaves são abertas SEMPRE a partir de Registry.LocalMachine, isso
    // não gravava no HKCU de ninguém — tentava criar uma chave chamada
    // "HKEY_CURRENT_USER" dentro do HKLM (que o Windows nega) e gerava erro no log
    // a cada ciclo. Também havia uma linha do Chrome repetida.
    private static readonly (string KeyPath, string ValueName, int DisabledValue)[] IncognitoTargets =
    {
        (@"SOFTWARE\Policies\Google\Chrome", "IncognitoModeAvailability", 1),
        (@"SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability", 1),
        (@"SOFTWARE\Policies\BraveSoftware\Brave", "IncognitoModeAvailability", 1),
        (@"SOFTWARE\Policies\Mozilla\Firefox", "DisablePrivateBrowsing", 1),
        (@"SOFTWARE\Policies\Opera Software\Opera Stable", "IncognitoModeAvailability", 1),
        (@"SOFTWARE\Policies\Vivaldi", "IncognitoModeAvailability", 1),
    };

    // Chaves Chromium principais (HKLM).
    private static readonly string[] ChromiumPolicyKeys =
    {
        @"SOFTWARE\Policies\Google\Chrome",
        @"SOFTWARE\Policies\Microsoft\Edge",
        @"SOFTWARE\Policies\BraveSoftware\Brave",
    };

    // Opera e Vivaldi são Chromium e leem políticas do mesmo tipo em chaves próprias. Aqui só recebem o que
    // desliga o DNS-sobre-HTTPS (auditoria U5): sem isso o navegador resolve nomes por DoH e ignora o hosts.
    // PEDE VALIDAÇÃO em opera://policy e vivaldi://policy (DnsOverHttpsMode = off, BuiltInDnsClientEnabled = 0).
    private static readonly string[] DohOnlyPolicyKeys =
    {
        @"SOFTWARE\Policies\Opera Software\Opera Stable",
        @"SOFTWARE\Policies\Vivaldi",
    };

    /// <summary>
    /// true quando o modo anônimo deve ficar bloqueado MESMO sem bloqueio temporizado: a opção
    /// <see cref="BlockState.BlockIncognitoAlways"/> só vale com as proteções permanentes ligadas
    /// (é o Watchdog das proteções permanentes quem a mantém).
    /// </summary>
    public static bool IncognitoBlockedAlways(BlockState state) =>
        state.PermanentProtectionsEnabled && state.BlockIncognitoAlways;

    /// <summary>
    /// Bloqueia o modo anônimo/privado em todos os navegadores. Idempotente (só grava se mudou): o Watchdog a
    /// chama a cada ciclo durante o bloqueio e, com a opção BlockIncognitoAlways, também fora dele. Antes
    /// valia "uma vez por bloqueio" e apagar o valor no meio do bloqueio não era corrigido.
    /// </summary>
    public static void ApplyRestrictions()
    {
        foreach (var (keyPath, valueName, value) in IncognitoTargets)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(keyPath);
                if (key != null) SetIfDifferent(key, valueName, value, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.ApplyRestrictions ({keyPath})", ex);
            }
        }

    }

    public static void RemoveRestrictions()
    {
        foreach (var (keyPath, valueName, _) in IncognitoTargets)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key?.DeleteValue(valueName, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.RemoveRestrictions ({keyPath})", ex);
            }
        }
    }

    /// <summary>
    /// Desliga DNS-over-HTTPS e força SafeSearch nos navegadores Chromium.
    ///
    /// CORRIGIDO: a versão anterior também gravava ExtensionInstallBlocklist = ["*"],
    /// que BLOQUEIA TODA extensão do navegador — inclusive as que você já usa
    /// (gerenciador de senhas, uBlock...), que passavam a ser desativadas. Além
    /// disso, durante um bloqueio temporizado outro método sobrescrevia essa lista
    /// por IDs inválidos/errados (MetaMask e Privacy Badger não são VPN; dois IDs
    /// nem tinham formato válido), e depois do bloqueio ela nunca era limpa. Essa
    /// parte foi removida — veja CleanUpLegacyArtifacts, que desfaz o que a versão
    /// antiga deixou gravado.
    /// </summary>
    public static void ApplyPermanentChromiumPolicies()
    {
        CleanUpLegacyArtifacts();

        foreach (var keyPath in ChromiumPolicyKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(keyPath);
                if (key == null) continue;

                // Desligar DoH (senão o navegador ignora o hosts via DNS-sobre-HTTPS)
                SetIfDifferent(key, "DnsOverHttpsMode", "off", RegistryValueKind.String);
                SetIfDifferent(key, "BuiltInDnsClientEnabled", 0, RegistryValueKind.DWord);

                // Auditoria U5: sem modo visitante. O perfil visitante não carrega extensões (a nossa
                // inclusive), então era uma janela de navegação sem a extensão. 0 = desativado.
                SetIfDifferent(key, "BrowserGuestModeEnabled", 0, RegistryValueKind.DWord);

                // SafeSearch: Google (1 = ativo) e YouTube (2 = Modo Restrito estrito)
                SetIfDifferent(key, "ForceGoogleSafeSearch", 1, RegistryValueKind.DWord);
                SetIfDifferent(key, "ForceYouTubeRestrict", 2, RegistryValueKind.DWord);

                // Filtro de sites adultos do próprio Chrome (ignorado por Edge/Brave)
                SetIfDifferent(key, "SafeSitesFilterBehavior", 1, RegistryValueKind.DWord);

                // Bing só tem política nativa no Edge (2 = restrição estrita).
                if (keyPath.Contains("Microsoft\\Edge", StringComparison.OrdinalIgnoreCase))
                {
                    SetIfDifferent(key, "ForceBingSafeSearch", 2, RegistryValueKind.DWord);
                }

                // Auditoria U2: extensões de VPN/proxy da Web Store, sem depender de IDs.
                EnsureProxyPermissionBlocked(key, keyPath);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.ApplyPermanentChromiumPolicies ({keyPath})", ex);
            }
        }

        // Auditoria U5: DoH desligado também no Opera e no Vivaldi.
        foreach (var keyPath in DohOnlyPolicyKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(keyPath);
                if (key == null) continue;
                SetIfDifferent(key, "DnsOverHttpsMode", "off", RegistryValueKind.String);
                SetIfDifferent(key, "BuiltInDnsClientEnabled", 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.ApplyPermanentChromiumPolicies ({keyPath})", ex);
            }
        }

        ApplyFirefoxDoh(enabled: false, locked: true);
    }

    // ---------------------------------------------------------------------------------------------
    // Auditoria U2 — extensões de VPN/proxy: política ExtensionSettings
    // ---------------------------------------------------------------------------------------------
    // ExtensionSettings é um REG_SZ com JSON. Gravamos  "*": { "blocked_permissions": ["proxy"] }: o Chromium
    // não instala (e desativa, se já estiver instalada) extensão que PEÇA a permissão "proxy" — que é o que toda
    // extensão de VPN/proxy precisa para redirecionar o tráfego. Não depende de IDs de extensão.
    //
    // MESCLA, nunca sobrescreve: preserva todas as outras entradas do JSON (outros "*", entradas por ID, outras
    // chaves dentro do "*"). Se o valor existente não for um JSON de objeto que a gente consiga ler, NÃO mexe
    // (só loga): é melhor deixar de proteger do que apagar a política de um administrador.
    //
    // Remover (RemovePermanentChromiumPolicies) tira SOMENTE o "proxy" que ESTE app acrescentou: um marcador no
    // Registro (HKLM\SOFTWARE\SharkBlocker\PolicyMarkers) diz se fomos nós. Se "proxy" já estava lá antes de nós
    // (administrador), não gravamos o marcador e, portanto, nunca o removemos.
    //
    // PEDE VALIDAÇÃO em chrome://policy, edge://policy e brave://policy: a política deve aparecer como
    // "ExtensionSettings" com status OK e o JSON contendo "blocked_permissions": ["proxy"]; depois, instalar (ou
    // criar, modo desenvolvedor) uma extensão com permissão "proxy" e confirmar que é bloqueada/desativada.
    //
    // LIMITE HONESTO: (1) bloqueia também extensões LEGÍTIMAS que usem a permissão "proxy" (alternadores de proxy
    // de trabalho, por exemplo); (2) só vale para Chrome/Edge/Brave — Firefox/Opera/Vivaldi ficam de fora;
    // (3) extensão carregada fora da política (modo desenvolvedor, "carregar sem compactação") pode escapar em
    // alguns navegadores; (4) a VPN/proxy como PROGRAMA não passa por aqui (ver ProcessBlocker).
    private const string ExtensionSettingsValueName = "ExtensionSettings";
    private const string ProxyPermission = "proxy";
    private const string PolicyMarkersKeyPath = @"SOFTWARE\SharkBlocker\PolicyMarkers";

    private static string MarkerName(string policyKeyPath) =>
        "ExtensionSettingsProxy_" + policyKeyPath.Replace('\\', '_');

    private static bool ContainsProxy(JsonArray perms) =>
        perms.Any(n => n is JsonValue v && v.TryGetValue<string>(out var t) &&
                       string.Equals(t, ProxyPermission, StringComparison.Ordinal));

    /// <summary>Lê o JSON de ExtensionSettings. null = não dá para usar com segurança (tipo errado ou JSON inválido).</summary>
    private static bool TryReadExtensionSettings(RegistryKey policyKey, out JsonObject root, out bool existed)
    {
        root = new JsonObject();
        existed = false;
        var raw = policyKey.GetValue(ExtensionSettingsValueName);
        if (raw == null) return true;                       // não existe: começa de um objeto vazio
        existed = true;
        if (raw is not string text) return false;           // tipo inesperado (não REG_SZ): não mexe
        if (string.IsNullOrWhiteSpace(text)) return true;   // vazio: não há nada a preservar
        try
        {
            if (JsonNode.Parse(text) is JsonObject parsed) { root = parsed; return true; }
        }
        catch (Exception) { } // JsonException, chave duplicada etc.: tratado como "não legível"
        return false;                                       // JSON inválido ou não é objeto: não mexe
    }

    private static void EnsureProxyPermissionBlocked(RegistryKey policyKey, string policyKeyPath)
    {
        try
        {
            if (!TryReadExtensionSettings(policyKey, out var root, out _))
            {
                Logger.Log($"ExtensionSettings de {policyKeyPath} existe mas não é um JSON de objeto legível — NÃO alterado " +
                           "(não vou sobrescrever a política existente). Bloqueio de extensões com permissão 'proxy' fica inativo ali.");
                return;
            }

            // "*" (todas as extensões) -> "blocked_permissions" (lista). Qualquer forma inesperada = não mexe.
            JsonObject star;
            if (root["*"] == null) { star = new JsonObject(); root["*"] = star; }
            else if (root["*"] is JsonObject existingStar) star = existingStar;
            else { Logger.Log($"ExtensionSettings de {policyKeyPath}: a entrada \"*\" não é um objeto — NÃO alterado."); return; }

            JsonArray perms;
            if (star["blocked_permissions"] == null) { perms = new JsonArray(); star["blocked_permissions"] = perms; }
            else if (star["blocked_permissions"] is JsonArray existingPerms) perms = existingPerms;
            else { Logger.Log($"ExtensionSettings de {policyKeyPath}: \"blocked_permissions\" não é uma lista — NÃO alterado."); return; }

            if (ContainsProxy(perms)) return; // já está (nosso ou de um administrador): idempotente, não regrava

            perms.Add(ProxyPermission);
            policyKey.SetValue(ExtensionSettingsValueName, root.ToJsonString(), RegistryValueKind.String);
            SetMarker(policyKeyPath, true); // fomos NÓS que acrescentamos: só então podemos remover depois
            Logger.Log($"ExtensionSettings de {policyKeyPath}: acrescentado \"*\".blocked_permissions=[\"proxy\"] (mesclado com o que já existia).");
        }
        catch (Exception ex)
        {
            Logger.LogException($"BrowserPolicyManager.EnsureProxyPermissionBlocked ({policyKeyPath})", ex);
        }
    }

    /// <summary>Tira só o "proxy" que NÓS acrescentamos (marcador). Limpa o que ficar vazio; apaga o valor se sobrar {}.</summary>
    private static void RemoveProxyPermissionBlock(RegistryKey? policyKey, string policyKeyPath)
    {
        try
        {
            if (!MarkerIsSet(policyKeyPath)) return; // não fomos nós (ou nunca gravamos): não toca

            if (policyKey != null && TryReadExtensionSettings(policyKey, out var root, out var existed) && existed &&
                root["*"] is JsonObject star && star["blocked_permissions"] is JsonArray perms)
            {
                for (var i = perms.Count - 1; i >= 0; i--)
                {
                    if (perms[i] is JsonValue v && v.TryGetValue<string>(out var t) &&
                        string.Equals(t, ProxyPermission, StringComparison.Ordinal))
                        perms.RemoveAt(i);
                }
                if (perms.Count == 0) star.Remove("blocked_permissions");
                if (star.Count == 0) root.Remove("*");

                if (root.Count == 0) policyKey.DeleteValue(ExtensionSettingsValueName, throwOnMissingValue: false);
                else policyKey.SetValue(ExtensionSettingsValueName, root.ToJsonString(), RegistryValueKind.String);
                Logger.Log($"ExtensionSettings de {policyKeyPath}: removido o \"proxy\" que este app tinha acrescentado.");
            }

            SetMarker(policyKeyPath, false);
        }
        catch (Exception ex)
        {
            Logger.LogException($"BrowserPolicyManager.RemoveProxyPermissionBlock ({policyKeyPath})", ex);
        }
    }

    private static bool MarkerIsSet(string policyKeyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyMarkersKeyPath);
            return key?.GetValue(MarkerName(policyKeyPath)) is int v && v == 1;
        }
        catch { return false; } // não deu para verificar = não removemos nada
    }

    private static void SetMarker(string policyKeyPath, bool on)
    {
        try
        {
            if (on)
            {
                using var key = Registry.LocalMachine.CreateSubKey(PolicyMarkersKeyPath);
                if (key != null) SetIfDifferent(key, MarkerName(policyKeyPath), 1, RegistryValueKind.DWord);
            }
            else
            {
                using (var key = Registry.LocalMachine.OpenSubKey(PolicyMarkersKeyPath, writable: true))
                    key?.DeleteValue(MarkerName(policyKeyPath), throwOnMissingValue: false);

                // Sem lixo: se a chave de marcadores ficou vazia, apaga a chave.
                using (var check = Registry.LocalMachine.OpenSubKey(PolicyMarkersKeyPath))
                {
                    if (check != null && check.ValueCount == 0 && check.SubKeyCount == 0)
                    {
                        check.Close();
                        Registry.LocalMachine.DeleteSubKey(PolicyMarkersKeyPath, throwOnMissingSubKey: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException($"BrowserPolicyManager.SetMarker ({policyKeyPath})", ex);
        }
    }

    /// <summary>
    /// Só grava se o valor realmente mudou. O Watchdog chama isto a cada 5s; regravar
    /// o mesmo valor toda vez faz o navegador reler as políticas à toa.
    /// </summary>
    private static void SetIfDifferent(RegistryKey key, string name, object value, RegistryValueKind kind)
    {
        var current = key.GetValue(name);
        if (current != null && current.Equals(value)) return;
        key.SetValue(name, value, kind);
    }

    // IDs que a versão antiga gravava em ExtensionInstallBlocklist durante o bloqueio.
    private static readonly string[] LegacyBlocklistIds =
    {
        "einpaelgookohagofgnnkcfjbhekhlfb", "dbjoeabdnnblimcccbhhepmfl5t6z5fa",
        "meeefljbkbaihkekjlngnlceebhampij", "oofiananboodjbbmdelgbaombpkkkpok",
        "ajnfmcehhhkbhjkngbfcclpaghekfoib", "legokcekfbaahbibfflnfbgpnohjhbni",
        "iggpbpdimokdpoabdbomphajidpekdck", "gibniaodpofblbdoebengnpibmilkhm",
        "khpnogfejfpkfehjmaipppjlkggiipop",
    };

    /// <summary>
    /// Desfaz o que versões antigas deixaram no Registro: a lista de extensões
    /// bloqueadas ("*" ou os IDs acima) e a árvore bogus HKLM\HKEY_CURRENT_USER.
    /// Só apaga quando o conteúdo é EXATAMENTE o que o app antigo gravava — uma
    /// lista de bloqueio sua, com outros IDs, não é tocada.
    /// </summary>
    public static void CleanUpLegacyArtifacts()
    {
        foreach (var keyPath in ChromiumPolicyKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                if (key == null) continue;

                if (key.GetValue("ExtensionInstallBlocklist") is string[] blocklist &&
                    blocklist.Length > 0 &&
                    blocklist.All(id => id == "*" || LegacyBlocklistIds.Contains(id)))
                {
                    key.DeleteValue("ExtensionInstallBlocklist", throwOnMissingValue: false);
                    Logger.Log($"Removida a lista antiga ExtensionInstallBlocklist de {keyPath} (bloqueava todas as extensões).");
                }

                if (key.GetValue("ExtensionInstallAllowlist") is string[] allowlist && allowlist.Length == 0)
                {
                    key.DeleteValue("ExtensionInstallAllowlist", throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.CleanUpLegacyArtifacts ({keyPath})", ex);
            }
        }

        try
        {
            var onlySoftware = false;
            using (var bogus = Registry.LocalMachine.OpenSubKey("HKEY_CURRENT_USER"))
            {
                if (bogus != null)
                {
                    onlySoftware = bogus.GetSubKeyNames()
                        .All(n => n.Equals("Software", StringComparison.OrdinalIgnoreCase));
                }
            }

            if (onlySoftware)
            {
                Registry.LocalMachine.DeleteSubKeyTree("HKEY_CURRENT_USER", throwOnMissingSubKey: false);
                Logger.Log("Removida a chave bogus HKLM\\HKEY_CURRENT_USER criada por uma versão antiga.");
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("BrowserPolicyManager.CleanUpLegacyArtifacts (chave bogus)", ex);
        }
    }

    public static void RemovePermanentChromiumPolicies()
    {
        var names = new[]
        {
            "DnsOverHttpsMode", "BuiltInDnsClientEnabled",
            "ForceGoogleSafeSearch", "ForceYouTubeRestrict", "SafeSitesFilterBehavior",
            "ForceBingSafeSearch", "BrowserGuestModeEnabled"
        };
        foreach (var keyPath in ChromiumPolicyKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                foreach (var name in names)
                    key?.DeleteValue(name, throwOnMissingValue: false);

                // Auditoria U2: só o "proxy" que nós acrescentamos em ExtensionSettings (o resto é preservado).
                RemoveProxyPermissionBlock(key, keyPath);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.RemovePermanentChromiumPolicies ({keyPath})", ex);
            }
        }

        // Auditoria U5: tira o DoH-off do Opera e do Vivaldi (as chaves ficam; podem ter outras políticas).
        foreach (var keyPath in DohOnlyPolicyKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true);
                key?.DeleteValue("DnsOverHttpsMode", throwOnMissingValue: false);
                key?.DeleteValue("BuiltInDnsClientEnabled", throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Logger.LogException($"BrowserPolicyManager.RemovePermanentChromiumPolicies ({keyPath})", ex);
            }
        }

        ApplyFirefoxDoh(enabled: true, locked: false);
    }

    private static void ApplyFirefoxDoh(bool enabled, bool locked)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS");
            key?.SetValue("Enabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            key?.SetValue("Locked", locked ? 1 : 0, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Logger.LogException("BrowserPolicyManager.ApplyFirefoxDoh", ex);
        }
    }
}
