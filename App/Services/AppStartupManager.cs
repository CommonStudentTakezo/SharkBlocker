using System.Diagnostics;
using System.IO;

namespace SharkBlocker.Services;

/// <summary>
/// Registra o SharkBlocker.exe (a janela principal, não o Watchdog) pra
/// abrir sozinho no login do Windows, via Agendador de Tarefas com
/// "/RL HIGHEST" (privilégio máximo). Isso é importante: um atalho comum na
/// pasta de Inicializar, ou uma chave de Registro "Run", dispararia um
/// prompt do UAC TODA VEZ que o Windows liga, porque o app exige admin. Uma
/// tarefa agendada configurada com privilégio máximo já nasce autorizada,
/// então abre direto, sem prompt.
///
/// Importante: isso é só conveniência de interface (abrir o painel sozinho).
/// A proteção de verdade (hosts, bloqueio, SafeSearch) já roda mesmo sem
/// isso, porque é o Watchdog — um Serviço do Windows — quem faz esse
/// trabalho, e serviços já iniciam sozinhos no boot independente do login de
/// qualquer usuário (ver ServiceInstaller.cs).
/// </summary>
public static class AppStartupManager
{
    private const string TaskName = "SharkBlockerApp";

    public static void RegisterAutoStart(string appExePath, string? userName = null)
    {
        // CORRIGIDO: uma tarefa criada só com "schtasks /Create /SC ONLOGON" nasce com as
        // configurações padrão do Agendador, que têm duas armadilhas:
        //   1) "só iniciar se estiver na tomada" — em notebook na bateria o app NÃO abre no login;
        //   2) "parar se rodar por mais de 3 dias" — o app era encerrado depois de 72 h ligado.
        // Por isso a tarefa agora é criada por um XML, com as duas desligadas. "--minimized" abre
        // direto na bandeja (sem pular a janela toda vez que o Windows liga).
        var xmlPath = Path.Combine(Path.GetTempPath(), "sharkblocker-task.xml");
        // Tarefa do nome antigo: só mexe se existir (senão o log enchia de erro a cada abertura).
        if (QueryTask("ColdTurkeyCloneApp")) RunSchtasks("/Delete /TN \"ColdTurkeyCloneApp\" /F");
        try
        {
            // U4: a tarefa vale para QUALQUER conta (grupo Users), não só para quem instalou. O parâmetro
            // userName continua existindo só por compatibilidade (o Watchdog ainda o repassa).
            var xml = BuildTaskXml(appExePath);
            File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode); // UTF-16, como o XML declara

            var code = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (code == 0) return;

            Logger.Log("AppStartupManager: criação por XML falhou — tentando o modo simples (sem as configurações de bateria).");
        }
        catch (Exception ex)
        {
            Logger.LogException("AppStartupManager.RegisterAutoStart (XML)", ex);
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* temporário */ }
        }

        // Plano B: o modo antigo, que funciona mas herda os padrões do Agendador.
        RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{appExePath}\\\" --minimized\" /SC ONLOGON /RL HIGHEST /F");
    }

    /// <summary>SID do grupo local "Users" (S-1-5-32-545): toda conta de usuário pertence a ele.</summary>
    private const string UsersGroupSid = "S-1-5-32-545";

    /// <summary>
    /// true se a tarefa JÁ registrada tem o formato novo (grupo Users). Tarefas criadas por versões
    /// anteriores eram presas ao usuário que instalou; o Watchdog usa isto para recriá-las.
    /// Qualquer dúvida (falha ao consultar) = true, para não recriar a tarefa à toa a cada minuto.
    /// </summary>
    public static bool IsRegisteredForAllUsers()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\" /XML")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return true;
            var xml = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0 || string.IsNullOrWhiteSpace(xml)) return true;
            return xml.Contains(UsersGroupSid, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Logger.LogException("AppStartupManager.IsRegisteredForAllUsers", ex);
            return true;
        }
    }

    private static string BuildTaskXml(string appExePath)
    {
        string E(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        var dir = Path.GetDirectoryName(appExePath) ?? "";
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Abre o SharkBlocker ao entrar no Windows.</Description></RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT10S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <GroupId>{UsersGroupSid}</GroupId>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{E(appExePath)}</Command>
      <Arguments>--minimized</Arguments>
      <WorkingDirectory>{E(dir)}</WorkingDirectory>
    </Exec>
  </Actions>
</Task>";
    }

    /// <summary>
    /// Pede ao Agendador de Tarefas para executar a tarefa do app AGORA. Funciona a partir do
    /// Watchdog (conta SYSTEM, sessão 0, sem tela): a tarefa roda na sessão do usuário logado,
    /// então a trava de tela aparece na área de trabalho de verdade.
    /// </summary>
    public static bool RunNow() => RunSchtasks($"/Run /TN \"{TaskName}\"") == 0;

    public static void UnregisterAutoStart()
    {
        RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
    }

    // ---------- Tarefa de reanimação do serviço ----------
    // "sc stop" / "Parar" na aba Serviços é uma parada LIMPA: o Windows NÃO reinicia o serviço (só
    // reinicia quando ele MORRE). Esta tarefa (SYSTEM, a cada 1 min) roda "sc start": se o serviço
    // estiver parado, volta em até 1 min e, ao voltar, trata o app morto como encerramento forçado.
    // Com o serviço já rodando, "sc start" só devolve um erro inofensivo.
    private const string ReviveTaskName = "SharkBlockerWatchdogRevive";
    private const string ServiceNameForRevive = "SharkBlockerWatchdog";

    public static void RegisterReviveTask()
    {
        var sc = Path.Combine(Environment.SystemDirectory, "sc.exe");
        RunSchtasks($"/Create /TN \"{ReviveTaskName}\" /SC MINUTE /MO 1 /RU SYSTEM /RL HIGHEST " +
                    $"/TR \"\\\"{sc}\\\" start {ServiceNameForRevive}\" /F");
    }

    public static void EnsureReviveTask()
    {
        try { if (!QueryTask(ReviveTaskName)) RegisterReviveTask(); }
        catch (Exception ex) { Logger.LogException("AppStartupManager.EnsureReviveTask", ex); }
    }

    public static void UnregisterReviveTask() => RunSchtasks($"/Delete /TN \"{ReviveTaskName}\" /F");

    public static bool IsRegistered() => QueryTask(TaskName);

    private static bool QueryTask(string name)
    {
        var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{name}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit();
        return p?.ExitCode == 0;
    }

    private static int RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", arguments)
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
                Logger.Log($"schtasks {arguments} saiu com código {p.ExitCode}. stdout: {stdout} | stderr: {stderr}");
            }
            return p?.ExitCode ?? -1;
        }
        catch (Exception ex)
        {
            Logger.LogException("AppStartupManager.RunSchtasks", ex);
            return -1;
        }
    }
}
