using System.Windows;
using SharkBlocker.Dialogs;
using SharkBlocker.Services;
using Application = System.Windows.Application;
using MessageBox = SharkBlocker.Dialogs.ThemedMessageBox;

namespace SharkBlocker;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex; // mantido vivo durante toda a execução
    private static EventWaitHandle? _showWindowEvent; // "mostre a janela" vindo de uma segunda instância
    private const string ShowWindowEventName = "Local\\SharkBlocker_ShowWindow";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Diálogos e janelas auxiliares (senha, trava de tela) abrem e fecham antes
        // da janela principal existir. Com o modo padrão (OnLastWindowClose), fechar
        // a primeira dessas janelas derrubaria o app inteiro. Aqui o app só termina
        // quando alguém chama Shutdown() de propósito (já é o que o resto do código faz).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Rede de segurança: qualquer exceção não tratada em qualquer lugar da
        // interface (não só nos pontos que já têm try/catch específico) cai
        // aqui em vez de derrubar o app inteiro sem explicação nenhuma.
        // Falha interna fatal NÃO é "fechado à força": marca saída normal para o Watchdog não punir
        // com a trava de tela (ele só reabre o app).
        AppDomain.CurrentDomain.UnhandledException += (_, _) => AppPresence.MarkCleanExit();

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.LogException("DispatcherUnhandledException (não tratada em nenhum outro lugar)", args.Exception);
            MessageBox.Show(
                "Ocorreu um erro inesperado:\n\n" + args.Exception.Message +
                "\n\nDetalhes completos em:\n" + Logger.LogFilePath +
                "\n\nO app vai tentar continuar rodando normalmente.",
                "Erro inesperado", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Teste da trava de tela, sem mexer em nada do app:
        //   SharkBlocker.exe --test-lock 20     (trava por 20 segundos; padrão 15)
        // Serve só pra conferir que a janela, o teclado e o Gerenciador de Tarefas
        // se comportam, sem precisar esperar 5 minutos de verdade.
        var testLockIndex = Array.IndexOf(e.Args, "--test-lock");
        if (testLockIndex >= 0)
        {
            var seconds = 15;
            if (testLockIndex + 1 < e.Args.Length && int.TryParse(e.Args[testLockIndex + 1], out var parsed))
            {
                seconds = Math.Clamp(parsed, 5, 120);
            }

            ScreenLock.ShowUntil(DateTime.UtcNow.AddSeconds(seconds), "TESTE DA TRAVA DE TELA",
                $"Isto é só um teste de {seconds} segundos. Nada foi desinstalado.");
            Shutdown(0);
            return;
        }

        // Trava de tela de 1 h pedida pelo remover-modo-teste.ps1 (compromisso antes de remover o app do celular):
        //   SharkBlocker.exe --lock-removal 60     (minutos; padrão 60; entre 1 e 240)
        // Só grava a trava no estado (assinado) e sai: quem mostra a tela é o app que já roda (Timer_Tick) ou, se ele não estiver
        // aberto, o Watchdog, que o reabre quando há trava ativa. Saída 0 = a trava está gravada e vale; 1 = não deu (sem admin etc.).
        // Sem janela de propósito: o script o chama oculto e uma caixa de mensagem o deixaria esperando.
        var lockRemovalIndex = Array.IndexOf(e.Args, "--lock-removal");
        if (lockRemovalIndex >= 0)
        {
            var minutes = 60;
            if (lockRemovalIndex + 1 < e.Args.Length && int.TryParse(e.Args[lockRemovalIndex + 1], out var parsedMinutes))
                minutes = Math.Clamp(parsedMinutes, 1, 240);

            var ok = false;
            try
            {
                if (ElevationHelper.IsAdministrator())
                {
                    var until = StateManager.StartOrExtendScreenLock(TimeSpan.FromMinutes(minutes), ScreenLock.ReasonRemovalWait);
                    // Confere no estado lido de volta (a gravação é assinada; se falhou, não afirma que travou).
                    var check = StateManager.Load();
                    ok = check.IsScreenLockActive && check.ScreenLockUntilUtc!.Value >= DateTime.UtcNow.AddMinutes(minutes - 1);
                    Logger.Log($"--lock-removal {minutes}: trava gravada até {until.ToLocalTime():HH:mm} (conferida: {ok}).");
                }
                else
                {
                    Logger.Log("--lock-removal: sem permissão de administrador; nada foi feito.");
                }
            }
            catch (Exception ex) { Logger.LogException("App.OnStartup (--lock-removal)", ex); }

            Shutdown(ok ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--uninstall"))
        {
            var authorized = UninstallFlow.TryPerformProtectedUninstall(null);
            Shutdown(authorized ? 0 : 1);
            return;
        }

        // Só uma janela de controle por vez: o Watchdog pode pedir ao Agendador de Tarefas para
        // abrir o app (pra mostrar a trava de tela), e ele não pode virar duas instâncias.
        var startMinimized = e.Args.Contains("--minimized");

        _singleInstanceMutex = new Mutex(true, "Local\\SharkBlocker_AppSingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // CORRIGIDO: abrir o programa de novo (atalho/duplo clique) com ele escondido na
            // bandeja não fazia NADA, parecendo quebrado. Agora avisa a instância que já roda
            // para mostrar a janela. (Quando é só o Agendador/Watchdog, não mostra nada.)
            if (!startMinimized)
            {
                try
                {
                    using var signal = EventWaitHandle.OpenExisting(ShowWindowEventName);
                    signal.Set();
                }
                catch { /* a outra instância ainda está subindo — sem problema */ }
            }
            Shutdown(0);
            return;
        }


        // Segura o mutex "Global\SharkBlocker_AppAlive" pelo resto da execução — é como o Watchdog
        // (e, no sentido oposto, o Guard dentro deste mesmo App) confirmam presença um do outro sem
        // enumerar processos. Ver ProcessSync.
        ProcessSync.HoldAppAlive();

        // Rede de segurança: se um Watchdog foi morto no meio da restauração do DNS (reinstalação), o Windows pode ficar apontando
        // para o filtro sem ninguém atendendo. O app roda como administrador: conserta em segundo plano. Ver DnsFilterController.
        _ = Task.Run(() => DnsFilterController.RepairIfOrphaned("abertura do app"));

        _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        var waiter = new Thread(() =>
        {
            while (_showWindowEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.ShowFromTray()));
            }
        })
        { IsBackground = true, Name = "ShowWindowWaiter" };
        waiter.Start();

        // Praticamente TODA proteção deste app (hosts, registro, serviço) exige
        // admin. Antes, quando faltava permissão, o app tentava mesmo assim e
        // ignorava o erro silenciosamente — parecendo que "não fazia nada".
        // Agora ele recusa a abrir sem elevação, com uma explicação clara.
        if (!ElevationHelper.IsAdministrator())
        {
            MessageBox.Show(
                "Este app precisa ser executado como Administrador para funcionar " +
                "(ele edita o arquivo hosts, o Registro e instala um serviço do Windows).\n\n" +
                "Feche esta janela, clique com o botão direito no SharkBlocker.exe e escolha " +
                "\"Executar como administrador\".\n\n" +
                "Se você rodou pelo Visual Studio/dotnet run, abra o Visual Studio (ou o terminal) " +
                "como administrador também — rodar o .exe direto sem isso NÃO ativa a elevação " +
                "automaticamente mesmo com o manifesto pedindo admin.",
                "Precisa ser Administrador", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // REDE DE SEGURANÇA (não depende do Watchdog): se a execução anterior do app sumiu sem saída
        // normal (batimento recente, nenhuma marca de saída) e ninguém puniu até agora — o Watchdog
        // também foi encerrado, não voltou ou voltou tarde —, quem pune é esta instância ao abrir.
        // Tem que ser lido ANTES de MarkStarted(), que reinicia o batimento. Se o Watchdog já tratou
        // o incidente, ele apagou o batimento (ClearAlive) e isto dá false.
        // Fica DEPOIS da checagem de administrador: uma abertura sem elevação sai logo em seguida e
        // não pode nem apagar o sinal da execução anterior nem deixar um batimento falso.
        var previousRunWasKilled = AppPresence.WasAliveSinceBoot(TimeSpan.FromMinutes(10));

        // Apaga qualquer marca de "saída normal" antiga e já começa a bater o coração (Watchdog).
        AppPresence.MarkStarted();

        if (previousRunWasKilled && AppPresence.IsPlannedRestart(TimeSpan.FromMinutes(10)))
        {
            Logger.Log("A execução anterior do app foi encerrada por um reinício PLANEJADO (instalação/atualização) — sem trava.");
            previousRunWasKilled = false;
        }

        if (previousRunWasKilled)
        {
            Logger.Log("A execução anterior do app terminou sem saída normal e ninguém puniu — travando a tela por 30 min (rede de segurança do app).");
            try { StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, AppPresence.ReasonClosed); }
            catch (Exception ex) { Logger.LogException("App.OnStartup (trava por fechamento forçado anterior)", ex); }
        }

        // Se a pessoa pediu pra desinstalar e o app foi fechado/reiniciado no meio
        // da espera de 30 minutos, a trava volta só pelo tempo que faltava. Se não
        // há espera ativa, garante que nada ficou "preso" de uma trava anterior
        // que tenha sido interrompida (ex.: queda de energia).
        // Lista padrão de sites bloqueados: juntada uma vez por versão (ver DefaultSites).
        DefaultSites.EnsureMerged();

        var startupState = StateManager.Load();
        if (startupState.IsScreenLockActive)
        {
            var (headline, message) = ScreenLock.TextFor(startupState.ScreenLockReason);
            ScreenLock.ShowUntil(startupState.ScreenLockUntilUtc!.Value, headline, message);
        }

        // Dentro de um horário de bloqueio agendado (inclusive logo após ligar o PC), a tela volta a travar
        // até o fim do bloqueio. EnsureActive garante o LockedUntilUtc caso o Watchdog ainda não tenha agido.
        try
        {
            BlockScheduler.EnsureActive();
            ScreenLock.ShowScheduledBlockIfDue(StateManager.Load());
        }
        catch (Exception ex) { Logger.LogException("App.OnStartup (trava do bloqueio agendado)", ex); }

        LockdownPolicies.RestoreTaskManager();

        // Definir MainWindow explicitamente: a primeira janela criada no processo vira a
        // "principal" automaticamente, e aqui isso seria uma janela da trava de tela já fechada.
        var main = new MainWindow();
        MainWindow = main;
        main.StartedMinimized = startMinimized;
        if (!startMinimized) main.Show();

        // Frase do Dia da Ellen no canto inferior esquerdo da tela (também quando o app abre na bandeja).
        DesktopQuoteWindow.EnsureShown();
    }
}
