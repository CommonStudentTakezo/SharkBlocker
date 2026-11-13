using System.IO;

namespace SharkBlocker.Services;

/// <summary>
/// Marca de "este app foi fechado de forma NORMAL" (saída autorizada pela bandeja, fim de sessão do
/// Windows, falha interna). O Watchdog (serviço) vigia o processo do app: se ele some SEM essa
/// marca, foi encerrado à força (Gerenciador de Tarefas, taskkill...) e a tela é travada por 30 min.
/// Compartilhado entre o App e o Watchdog.
/// </summary>
public static class AppPresence
{
    /// <summary>Motivos de trava de tela gravados no estado (lidos por ScreenLock.TextFor).</summary>
    public const string ReasonClosed = "closed";
    public const string ReasonTamper = "tamper";

    private static readonly string FlagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SharkBlocker", "clean-exit.flag");

    private static readonly string AliveFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SharkBlocker", "app-alive.flag");

    private static readonly string WatchdogFlagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SharkBlocker", "watchdog-running.flag");

    private static volatile bool _exiting;
    private static DateTime _lastBeatUtc = DateTime.MinValue;

    /// <summary>
    /// Batimento do app (no máximo a cada ~4 s). Serve para o caso em que o app E o Watchdog são
    /// encerrados juntos: o Watchdog que reinicia vê que o app estava vivo há segundos, sem a marca
    /// de saída normal, e trata como fechamento forçado.
    /// </summary>
    public static void Heartbeat()
    {
        if (_exiting || (DateTime.UtcNow - _lastBeatUtc).TotalSeconds < 4) return;
        _lastBeatUtc = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AliveFile)!);
            AtomicFile.WriteSmall(AliveFile, DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* só um sinal auxiliar */ }
    }

    /// <summary>
    /// O app batia o coração DEPOIS da inicialização atual do Windows, há no máximo
    /// <paramref name="maxAge"/>, e nunca registrou saída normal (o arquivo some na saída normal)?
    /// Usado quando o Watchdog (re)inicia: se o app não está mais rodando mas estava vivo, foi morto
    /// à força — mesmo que o serviço só volte bem depois (reinício da recuperação, tarefa de
    /// reanimação). Batimentos de antes do boot (queda de energia, desligamento) são ignorados.
    /// </summary>
    public static bool WasAliveSinceBoot(TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(AliveFile)) return false;
            if (!long.TryParse(AtomicFile.ReadAllText(AliveFile).Trim(), out var ticks)) return false;
            var when = new DateTime(ticks, DateTimeKind.Utc);
            var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
            return when >= boot && DateTime.UtcNow - when <= maxAge;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Chamado quando uma instância do app sobe. Apaga uma marca de "saída normal" antiga que tenha
    /// sobrado, para ela nunca ser confundida com o fechamento desta nova execução.
    /// </summary>
    public static void MarkStarted()
    {
        _exiting = false;
        try { File.Delete(FlagFile); } catch { /* sem problema */ }
        _lastBeatUtc = DateTime.MinValue;
        Heartbeat();
    }

    public static void MarkCleanExit()
    {
        _exiting = true;
        try { File.Delete(AliveFile); } catch { /* sem problema */ }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FlagFile)!);
            AtomicFile.WriteSmall(FlagFile, DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* sem a marca, no pior caso o Watchdog trata como fechamento forçado */ }
    }

    public static bool WasCleanExitRecent(TimeSpan window)
    {
        try
        {
            if (!File.Exists(FlagFile)) return false;
            if (!long.TryParse(AtomicFile.ReadAllText(FlagFile).Trim(), out var ticks)) return false;
            var when = new DateTime(ticks, DateTimeKind.Utc);
            return DateTime.UtcNow - when <= window && when <= DateTime.UtcNow.AddSeconds(5);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Chamado pelo Watchdog logo ao iniciar. Devolve true se a instância ANTERIOR dele morreu sem
    /// parada normal (o marcador continua no disco, gravado depois do boot atual): foi encerrada à
    /// força (Gerenciador de Tarefas, taskkill) e o Windows a reiniciou. Depois grava o marcador
    /// desta instância. Marcador de antes do boot (queda de energia) é ignorado.
    /// </summary>
    public static bool MarkWatchdogStarted()
    {
        var previousKilled = false;
        try
        {
            if (File.Exists(WatchdogFlagFile) &&
                long.TryParse(AtomicFile.ReadAllText(WatchdogFlagFile).Trim(), out var ticks))
            {
                var when = new DateTime(ticks, DateTimeKind.Utc);
                var boot = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
                previousKilled = when >= boot;
            }
        }
        catch { /* na dúvida, não pune */ }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WatchdogFlagFile)!);
            AtomicFile.WriteSmall(WatchdogFlagFile, DateTime.UtcNow.Ticks.ToString());
        }
        catch { /* só um sinal */ }
        return previousKilled;
    }

    private static readonly string PlannedFlagFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SharkBlocker", "planned-restart.flag");

    /// <summary>
    /// "Reinício planejado": gravado pelos scripts de instalação/atualização (quick-install.ps1,
    /// protection-pause.ps1) quando eles encerram o app e o serviço DE PROPÓSITO. Enquanto estiver
    /// fresco, Watchdog, Guard e app reabrem o app normalmente mas NÃO travam a tela. Vale só por
    /// <paramref name="maxAge"/> (renovado a cada etapa do script) e nunca é apagado por quem o lê,
    /// porque vários componentes o consultam.
    /// </summary>
    public static bool IsPlannedRestart(TimeSpan maxAge)
    {
        try
        {
            if (!File.Exists(PlannedFlagFile)) return false;
            if (!long.TryParse(AtomicFile.ReadAllText(PlannedFlagFile).Trim(), out var ticks)) return false;
            var when = new DateTime(ticks, DateTimeKind.Utc);
            var age = DateTime.UtcNow - when;
            return age >= TimeSpan.FromMinutes(-1) && age <= maxAge;
        }
        catch { return false; }
    }

    /// <summary>Parada NORMAL do Watchdog (sc stop, desligamento do Windows): apaga o marcador.</summary>
    public static void MarkWatchdogStopped()
    {
        try { File.Delete(WatchdogFlagFile); } catch { /* sem problema */ }
    }

    /// <summary>Apaga o batimento: o Watchdog já tratou este fechamento forçado e não deve puni-lo de novo.</summary>
    public static void ClearAlive()
    {
        try { File.Delete(AliveFile); } catch { /* sem problema */ }
    }

    public static void Clear()
    {
        try { File.Delete(FlagFile); } catch { /* a marca vence sozinha em segundos */ }
    }
}
