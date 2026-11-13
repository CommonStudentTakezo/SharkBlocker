using System.Text.Json;
using System.Windows.Threading;

namespace SharkBlocker.Services;

/// <summary>
/// O "Guard": a vigília que o App mantém sobre o Watchdog (serviço do Windows), complementando o
/// que o Watchdog já faz na outra direção (vigiar o App — ver EnforcementWorker.MonitorAppAsync).
///
/// Checagem em duas camadas, a cada tick:
///  1) RÁPIDA — <see cref="ProcessSync.IsWatchdogAlive"/> (mutex nomeado): o processo ainda existe?
///  2) CONFIRMAÇÃO — <see cref="ProcessSync.PingWatchdogAsync"/> (named pipe): ele está, além de
///     existir, respondendo? Um serviço travado (laço/deadlock) passa na camada 1 mas falha na 2.
///
/// Três formas de concluir que o Watchdog foi encerrado à força (todas = trava de 30 min):
///  a) o processo SUMIU (mutex ausente) por pelo menos <see cref="ProcessGoneSeconds"/> s;
///  b) o processo existe mas NÃO RESPONDE ao pipe por <see cref="UnresponsiveSeconds"/> s;
///  c) o serviço VOLTOU COM OUTRO PID (o PID vem no "pong"). CORRIGIDO: o Windows reinicia o
///     serviço em 5 s (sc failure), antes de qualquer janela de ausência longa — então matar só o
///     Watchdog pelo Gerenciador de Tarefas nunca era percebido: ele já estava de volta quando o
///     Guard ia agir. A troca de PID é a prova de que o processo anterior morreu.
///
/// Isto é deliberadamente só a CAMADA DE DETECÇÃO/REAÇÃO — quem decide o que fazer (travar, logar,
/// reiniciar) continua chamando os mesmos serviços de sempre (StateManager, ServiceInstaller).
/// </summary>
public sealed class ProcessGuardian : IDisposable
{
    /// <summary>Processo sumiu (mutex ausente): inequívoco, só espera um tick para não reagir a um soluço.</summary>
    private const int ProcessGoneSeconds = 3;

    /// <summary>Processo de pé mas sem responder ao pipe: pode ser CPU/pipe ocupado, então exige mais tempo.</summary>
    private const int UnresponsiveSeconds = 15;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private DateTime _armedAtUtc = DateTime.MaxValue; // só vale depois da inicialização
    private DateTime? _missingSinceUtc;
    private bool _everConfirmedAlive;
    private int _lastWatchdogPid; // PID visto no último "pong" (0 = sem referência ainda)
    private bool _busy;

    /// <summary>Disparado quando o Guard conclui que o Watchdog foi encerrado à força e já reagiu (trava + reinício). Só para log/toast opcional da UI.</summary>
    public event Action? ForcedStopHandled;

    public void Start()
    {
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
    }

    /// <summary>
    /// Dá um tempo de folga antes de voltar a vigiar (o serviço pode estar sendo reinstalado de
    /// propósito — ex.: logo após EnsureServiceInstalledAsync). Também esquece o PID de referência:
    /// uma reinstalação intencional troca o PID e NÃO pode ser punida.
    /// </summary>
    public void Arm()
    {
        _missingSinceUtc = null;
        _lastWatchdogPid = 0;
        _armedAtUtc = DateTime.UtcNow.AddSeconds(45);
    }

    private async Task TickAsync()
    {
        if (_busy || DateTime.UtcNow < _armedAtUtc) return;
        _busy = true;
        try
        {
            var processAlive = await Task.Run(ProcessSync.IsWatchdogAlive);
            string? pong = processAlive ? await ProcessSync.PingWatchdogAsync() : null;

            if (pong != null)
            {
                _everConfirmedAlive = true;
                _missingSinceUtc = null;

                var pid = ParsePid(pong);
                if (pid > 0)
                {
                    var previous = _lastWatchdogPid;
                    _lastWatchdogPid = pid;
                    if (previous != 0 && previous != pid && AppPresence.IsPlannedRestart(TimeSpan.FromMinutes(10)))
                    {
                        Logger.Log($"O serviço (Watchdog) trocou de PID ({previous} -> {pid}) por um reinício PLANEJADO (instalação/atualização) — sem trava.");
                    }
                    else if (previous != 0 && previous != pid)
                    {
                        // O serviço já foi reiniciado pelo Windows: não precisa reiniciar, só punir.
                        Logger.Log($"O SERVIÇO (Watchdog) VOLTOU COM OUTRO PID ({previous} -> {pid}): o processo anterior foi encerrado à força — travando a tela por 30 min.");
                        StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, AppPresence.ReasonClosed);
                        ForcedStopHandled?.Invoke();
                    }
                }
                return;
            }

            // Nunca vimos o Watchdog responder nesta sessão (ex.: serviço ainda não terminou de
            // instalar) — não pune um serviço que simplesmente ainda não subiu.
            if (!_everConfirmedAlive)
            {
                _missingSinceUtc = null;
                return;
            }

            // Exige ausência SUSTENTADA antes de agir — evita reagir a um ping perdido isolado
            // (CPU ocupada, pipe momentaneamente sem instância livre). Processo sumido é inequívoco,
            // então a espera é curta; processo de pé mas mudo espera mais.
            if (AppPresence.IsPlannedRestart(TimeSpan.FromMinutes(10)))
            {
                _missingSinceUtc = null; // instalação/atualização em andamento: o serviço some de propósito
                return;
            }

            _missingSinceUtc ??= DateTime.UtcNow;
            var needed = processAlive ? UnresponsiveSeconds : ProcessGoneSeconds;
            if ((DateTime.UtcNow - _missingSinceUtc.Value).TotalSeconds < needed) return;
            _missingSinceUtc = null;

            if (!await Task.Run(ServiceInstaller.IsInstalled)) return; // foi desinstalado de propósito

            Logger.Log("O SERVIÇO (Watchdog) FOI ENCERRADO À FORÇA OU PAROU DE RESPONDER — travando a tela por 30 min e reiniciando o serviço.");
            StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, AppPresence.ReasonClosed);
            await Task.Run(ServiceInstaller.Start);
            Arm();
            ForcedStopHandled?.Invoke();
        }
        catch (Exception ex)
        {
            Logger.LogException("ProcessGuardian.TickAsync", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Lê o "pid" do JSON de status do Watchdog (EnforcementWorker.BuildStatusJson). 0 se não der.</summary>
    private static int ParsePid(string statusJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            return doc.RootElement.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pid) ? pid : 0;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
