using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace SharkBlocker.Services;

/// <summary>
/// Registra o Watchdog.exe como um Serviço do Windows (em vez de tarefa agendada),
/// o que traz uma vantagem importante: usando "sc failure", configuramos o Service
/// Control Manager para reiniciar o serviço automaticamente se o processo morrer
/// de forma inesperada (ex: alguém dá "Finalizar Tarefa" nele). Isso acontece em
/// segundos, sem depender do app principal estar aberto.
///
/// IMPORTANTE — limite honesto: um Administrador local sempre pode rodar
/// "sc stop SharkBlockerWatchdog" ou parar pelo services.msc com privilégio de
/// admin. Isso NÃO conta como "falha" para o Service Control Manager, então a
/// recuperação automática não dispara nesse caso — é uma parada "limpa". Por isso,
/// no app, a ação de parar/desinstalar só é exposta atrás da tela de senha
/// (ver PasswordManager), então mesmo sabendo o comando, é preciso saber a senha
/// definida por você mesmo. Ainda assim, tecnicamente, alguém com conhecimento de
/// Windows e acesso de admin consegue reverter isso rodando os comandos "sc" na mão
/// — não existe proteção de software 100% à prova disso.
///
/// MODO SEGURO: por padrão o Windows NÃO carrega serviços de terceiros no modo seguro, então sem
/// registro específico o Watchdog simplesmente não sobe e nada reverte edições no hosts. O mecanismo
/// documentado (o mesmo que antivírus usam) é uma chave com o nome do serviço em
/// HKLM\SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal e \Network, com o valor padrão "Service"
/// — ver <see cref="EnsureSafeBootRegistration"/>.
/// </summary>
public static class ServiceInstaller
{
    private const string ServiceName = "SharkBlockerWatchdog";

    // Chaves do modo seguro (Minimal = "Modo Seguro"; Network = "Modo Seguro com Rede").
    private const string SafeBootMinimalKey = @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\" + ServiceName;
    private const string SafeBootNetworkKey = @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\" + ServiceName;
    private const string SafeBootDefaultValue = "Service";
    private const string ServiceRegistryKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;

    // ---- Negar "Parar" a Administradores (auditoria U4) ----
    // Descritor padrão de um serviço (SYSTEM e Administradores com controle; usuários só leem).
    private const string DefaultServiceSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    // O mesmo, com uma NEGAÇÃO de Parar/Pausar (WP, DT) para Administradores na frente. Continua podendo
    // iniciar, consultar, reconfigurar, apagar e REGRAVAR o descritor (é assim que pausa/atualização/
    // desinstalação, atrás de senha, voltam ao padrão antes de parar). Aviso: o token do SYSTEM também
    // contém o grupo Administradores, então nem o SYSTEM consegue "parar" por SCM — nada do app precisa
    // disso (a recuperação automática do SCM e a tarefa de reavivamento só INICIAM o serviço).
    private const string StopDeniedServiceSddl =
        "D:(D;;WPDT;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";
    private const int ServiceStopRight = 0x0020; // SERVICE_STOP
    private static bool _stopProtectionCheckWarned;

    // GetSystemMetrics(SM_CLEANBOOT): 0 = inicialização normal, 1 = modo seguro, 2 = modo seguro com rede.
    private const int SM_CLEANBOOT = 67;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static bool Install(string watchdogExePath)
    {
        RemoveLegacyService();
        RunSc($"create {ServiceName} binPath= \"{watchdogExePath}\" start= auto DisplayName= \"SharkBlocker Watchdog\"");

        // Reinicia automaticamente após 1s, em até 3 falhas consecutivas, resetando o contador após 1 dia.
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/1000/restart/1000/restart/1000");
        RunSc($"failureflag {ServiceName} 1");

        // Sem isto o serviço não sobe no modo seguro (ver comentário da classe).
        EnsureSafeBootRegistration();

        RunSc($"start {ServiceName}");
        EnsureStopProtection();
        AppStartupManager.RegisterReviveTask();

        return IsInstalled();
    }

    /// <summary>Remove o serviço registrado pelo nome antigo do app, para não rodarem dois Watchdogs.</summary>
    private static void RemoveLegacyService()
    {
        const string legacy = "ColdTurkeyCloneWatchdog";

        var psi = new ProcessStartInfo("sc", $"query {legacy}") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
        using var q = Process.Start(psi);
        var output = q?.StandardOutput.ReadToEnd() ?? "";
        q?.WaitForExit();
        if (output.Contains("1060")) return; // não existe — nada a limpar

        RunSc($"stop {legacy}");
        RunSc($"delete {legacy}");
    }

    /// <summary>Inicia o serviço (usado pelo app se o Watchdog for parado à força).</summary>
    public static void Start() => RunSc($"start {ServiceName}");

    /// <summary>
    /// Nega "Parar" a Administradores no serviço (sc stop, services.msc, Stop-Service). Idempotente: lê o
    /// descritor direto do Registro (sem criar processo) e só chama "sc sdset" se a negação sumiu.
    /// LIMITE HONESTO: é atrito, não barreira — um administrador ainda pode regravar o descritor
    /// (sc sdset) e parar o serviço; a tarefa de reavivamento (1 min) e a detecção de encerramento
    /// forçado continuam sendo a rede de segurança.
    /// </summary>
    public static void EnsureStopProtection()
    {
        try
        {
            if (HasStopDenyForAdmins()) return;
            RunSc($"sdset {ServiceName} \"{StopDeniedServiceSddl}\"");
            Logger.Log("Serviço: negação de \"Parar\" para Administradores (re)aplicada.");
        }
        catch (Exception ex)
        {
            Logger.LogException("ServiceInstaller.EnsureStopProtection", ex);
        }
    }

    /// <summary>
    /// Volta ao descritor padrão (sem a negação). TEM que rodar antes de qualquer parada legítima do serviço
    /// (desinstalar, pausar, atualizar) — todas já passaram pela senha.
    /// </summary>
    public static void ClearStopProtection() =>
        RunSc($"sdset {ServiceName} \"{DefaultServiceSddl}\"");

    private static bool HasStopDenyForAdmins()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceRegistryKey + @"\Security");
            // Sem valor "Security" = serviço com o descritor padrão (sem negação).
            if (key?.GetValue("Security") is not byte[] bytes) return false;

            var sd = new System.Security.AccessControl.RawSecurityDescriptor(bytes, 0);
            if (sd.DiscretionaryAcl == null) return false;

            var admins = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (var ace in sd.DiscretionaryAcl)
            {
                if (ace is System.Security.AccessControl.CommonAce common &&
                    common.AceQualifier == System.Security.AccessControl.AceQualifier.AccessDenied &&
                    common.SecurityIdentifier == admins &&
                    (common.AccessMask & ServiceStopRight) != 0)
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Não deu para checar: assume que está certo (não chama o sc.exe a cada ciclo à toa).
            if (!_stopProtectionCheckWarned)
            {
                _stopProtectionCheckWarned = true;
                Logger.LogException("ServiceInstaller.HasStopDenyForAdmins", ex);
            }
            return true;
        }
    }

    /// <summary>
    /// Reaplica a política de recuperação do SCM (reinício automático em até 3 falhas, com o
    /// contador zerando após 1 dia sem falha) — a mesma config feita em <see cref="Install"/>.
    /// Chamado PERIODICAMENTE pelo próprio Watchdog (EnforcementWorker), não só uma vez na
    /// instalação: alguém com acesso de administrador pode reverter isso na aba "Recuperação" do
    /// services.msc ou com "sc failure ... actions=" vazio, o que desligaria o auto-restart sem
    /// desinstalar nada. Reaplicar a cada ciclo é idempotente e barato (duas chamadas a sc.exe),
    /// e garante que a política volta sozinha pouco depois de ser alterada.
    /// </summary>
    public static void EnsureFailureActionsConfigured()
    {
        RunSc($"failure {ServiceName} reset= 86400 actions= restart/1000/restart/1000/restart/1000");
        RunSc($"failureflag {ServiceName} 1");
    }

    /// <summary>
    /// Modo de inicialização atual do Windows: 0 = normal, 1 = modo seguro, 2 = modo seguro com rede
    /// (<c>GetSystemMetrics(SM_CLEANBOOT)</c>). Se a consulta falhar por qualquer motivo devolve 0
    /// (o valor não é usado para liberar nada: só serve para registrar em log e aplicar o bloqueio
    /// sem esperar o primeiro ciclo).
    /// </summary>
    public static int GetCleanBootMode()
    {
        try { return GetSystemMetrics(SM_CLEANBOOT); }
        catch (Exception ex)
        {
            Logger.LogException("ServiceInstaller.GetCleanBootMode", ex);
            return 0;
        }
    }

    /// <summary>
    /// Garante que o serviço está registrado para iniciar no modo seguro (e no modo seguro com rede):
    /// grava o valor padrão "Service" em ...\Control\SafeBoot\Minimal\SharkBlockerWatchdog e em
    /// ...\SafeBoot\Network\SharkBlockerWatchdog. Sem essas chaves o Windows não carrega o serviço no
    /// modo seguro e o hosts poderia ser editado sem ninguém reverter.
    ///
    /// IDEMPOTENTE (padrão SetIfDifferent): só grava se a chave não existe ou o valor difere, então
    /// pode ser chamado a cada ciclo de ~60 s pelo Watchdog (EnforcementWorker), no mesmo ponto do
    /// <see cref="EnsureFailureActionsConfigured"/>. Assim, apagar as chaves na mão não desliga a
    /// proteção em silêncio: elas voltam sozinhas (e o fato vai para o log) quando o Watchdog
    /// rodar de novo.
    ///
    /// Só registra se o serviço existe e não está marcado para exclusão — evita deixar chaves órfãs
    /// quando o Watchdog roda solto (teste manual) ou durante a desinstalação.
    ///
    /// LIMITE HONESTO: um Administrador pode apagar as chaves e reiniciar em modo seguro ANTES do
    /// próximo ciclo (até ~60 s), ou editar o Registro offline (outro sistema, ambiente de
    /// recuperação). Isto fecha o caminho casual, não o de quem tem acesso físico e tempo.
    /// </summary>
    public static void EnsureSafeBootRegistration()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

            using (var svcKey = hklm.OpenSubKey(ServiceRegistryKey, writable: false))
            {
                // Serviço inexistente, ou já marcado para exclusão ("sc delete" em andamento): não registra.
                if (svcKey == null || svcKey.GetValue("DeleteFlag") != null) return;
            }

            SetSafeBootKeyIfDifferent(hklm, SafeBootMinimalKey);
            SetSafeBootKeyIfDifferent(hklm, SafeBootNetworkKey);
        }
        catch (Exception ex)
        {
            // Falha ao gravar não derruba o ciclo: tenta de novo no próximo.
            Logger.LogException("ServiceInstaller.EnsureSafeBootRegistration", ex);
        }
    }

    private static void SetSafeBootKeyIfDifferent(RegistryKey hklm, string subKeyPath)
    {
        try
        {
            using (var existing = hklm.OpenSubKey(subKeyPath, writable: false))
            {
                // Valor padrão = nome vazio. GetValueKind só é chamado se o valor existe.
                if (existing != null &&
                    existing.GetValue("") is string current &&
                    string.Equals(current, SafeBootDefaultValue, StringComparison.Ordinal) &&
                    existing.GetValueKind("") == RegistryValueKind.String)
                {
                    return; // já está certo: não regrava
                }
            }

            using var key = hklm.CreateSubKey(subKeyPath, writable: true);
            key.SetValue("", SafeBootDefaultValue, RegistryValueKind.String);
            Logger.Log($"Registro do modo seguro (re)gravado: HKLM\\{subKeyPath}");
        }
        catch (Exception ex)
        {
            Logger.LogException($"ServiceInstaller.SetSafeBootKeyIfDifferent({subKeyPath})", ex);
        }
    }

    /// <summary>
    /// Remove as duas chaves do modo seguro. Só é chamado pela DESINSTALAÇÃO do serviço (depois da
    /// senha). A pausa de proteções (protection-pause.ps1 / instalador) NÃO chama isto: ela só
    /// desabilita e para o serviço, que continua registrado.
    /// </summary>
    private static void RemoveSafeBootRegistration()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var path in new[] { SafeBootMinimalKey, SafeBootNetworkKey })
            {
                try { hklm.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
                catch (Exception ex) { Logger.LogException($"ServiceInstaller.RemoveSafeBootRegistration({path})", ex); }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("ServiceInstaller.RemoveSafeBootRegistration", ex);
        }
    }

    /// <summary>Só deve ser chamado depois que o app confirmar a senha do usuário.</summary>
    public static void Uninstall()
    {
        // Primeiro apaga a tarefa de reanimação — senão ela religaria o serviço no meio da remoção.
        AppStartupManager.UnregisterReviveTask();
        ClearStopProtection(); // senão a negação de "Parar" (U4) impediria o próprio sc stop abaixo
        RunSc($"stop {ServiceName}");

        // "sc stop" só SINALIZA a parada — não garante que o processo já morreu.
        // Espera até ~5s por uma confirmação real antes de seguir em frente.
        // (O Watchdog devolve o DNS ao normal ao parar, o que leva alguns segundos: espera até ~30 s, não 5 s.)
        for (var i = 0; i < 60 && IsRunning(); i++)
        {
            Thread.Sleep(500);
        }

        RunSc($"delete {ServiceName}");

        // Rede de segurança: mata QUALQUER Watchdog.exe ainda vivo, mesmo os que
        // já não pertencem mais a nenhum serviço registrado (órfãos de
        // reinstalações anteriores que "sc stop" sozinho não consegue mais
        // alcançar). É isso que evita múltiplos Watchdog.exe brigando pelo
        // mesmo arquivo hosts ao mesmo tempo.
        KillAllWatchdogProcesses();

        // Por último: com o serviço apagado e nenhum Watchdog vivo, ninguém recria as chaves.
        RemoveSafeBootRegistration();
    }

    /// <summary>
    /// Mata todo processo chamado "Watchdog", de qualquer origem/instalação.
    /// Chamado antes de (re)instalar o serviço, pra garantir que nunca existe
    /// mais de uma instância rodando ao mesmo tempo.
    /// </summary>
    public static void KillAllWatchdogProcesses()
    {
        var killed = 0;
        foreach (var proc in Process.GetProcessesByName("Watchdog"))
        {
            try
            {
                Logger.Log($"Encerrando processo Watchdog.exe órfão/antigo (PID {proc.Id}).");
                proc.Kill(entireProcessTree: true);
                killed++;
                proc.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Logger.LogException("ServiceInstaller.KillAllWatchdogProcesses", ex);
            }
            finally
            {
                proc.Dispose();
            }
        }

        // Matar o Watchdog no meio da parada pode deixar o DNS do Windows apontando para o filtro sem ninguém atendendo.
        if (killed > 0) DnsFilterController.RepairIfOrphaned("Watchdog encerrado à força");
    }

    public static bool IsInstalled()
    {
        var psi = new ProcessStartInfo("sc", $"query {ServiceName}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();
        return !output.Contains("1060"); // 1060 = serviço não existe
    }

    public static bool IsRunning()
    {
        var psi = new ProcessStartInfo("sc", $"query {ServiceName}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        };
        using var p = Process.Start(psi);
        var output = p?.StandardOutput.ReadToEnd() ?? "";
        p?.WaitForExit();
        return output.Contains("RUNNING");
    }

    /// <summary>Lê o caminho do executável configurado no serviço registrado (ou null se não achar).</summary>
    public static string? GetConfiguredBinPath()
    {
        // CAUSA (log de 2026-11-19): a leitura abaixo procura "BINARY_PATH_NAME" na saída do "sc qc", mas no Windows em português o rótulo é
        // traduzido ("NOME_DO_CAMINHO_BINÁRIO") e ainda sai em página de código OEM. A busca nunca achava nada, "alreadyGood" era sempre falso e
        // TODA abertura do app matava o Watchdog e recriava o serviço (deixando o DNS/hosts sem guarda e o serviço "marcado para exclusão").
        // O registro não depende de idioma: HKLM\SYSTEM\CurrentControlSet\Services\<serviço>\ImagePath.
        var fromRegistry = ParseImagePath(ReadImagePathFromRegistry());
        if (fromRegistry != null) return fromRegistry;

        var psi = new ProcessStartInfo("sc", $"qc {ServiceName}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true  // 🔧 BUG FIX #2: Capturar stderr também
        };
        using var p = Process.Start(psi);
        
        // 🔧 BUG FIX #2: Verificar se processo iniciou
        if (p == null)
        {
            Logger.Log("ERRO: Process.Start() retornou null ao executar sc qc");
            return null;
        }
        
        var output = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        // 🔧 BUG FIX #2: Log se falhou
        if (p.ExitCode != 0)
        {
            Logger.Log($"sc qc saiu com código {p.ExitCode}: {stderr}");
            return null;
        }

        // 🔧 BUG FIX #2: Usar Regex robusto em vez de Split('\n') frágil
        var match = System.Text.RegularExpressions.Regex.Match(
            output,
            @"BINARY_PATH_NAME\s*:\s*""?([^""\n\r]+)""?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ReadImagePathFromRegistry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + ServiceName);
            return key?.GetValue("ImagePath") as string;
        }
        catch { return null; }
    }

    /// <summary>"C:\a b\Watchdog.exe" --x  ou  C:\a\Watchdog.exe --x  ->  caminho do executável (sem aspas nem argumentos). Puro, testável.</summary>
    internal static string? ParseImagePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? s[..(exe + 4)] : s;
    }

    private static void RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            
            // 🔧 BUG FIX #1: Verificar se Process.Start() retornou null
            if (p == null)
            {
                Logger.Log($"ERRO CRÍTICO: Process.Start() retornou null ao executar: sc {arguments}");
                return;
            }
            
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Logger.Log($"sc {arguments} saiu com código {p.ExitCode}. stdout: {stdout} | stderr: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("ServiceInstaller.RunSc", ex);
        }
    }
}
