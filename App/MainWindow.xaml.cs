using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SharkBlocker.Dialogs;
using SharkBlocker.Models;
using SharkBlocker.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using MessageBox = SharkBlocker.Dialogs.ThemedMessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace SharkBlocker;

public partial class MainWindow : Window
{
    private BlockState _state = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _scheduleTicks; // reserva do agendador: confere a agenda a cada ~10 ticks (o Watchdog já faz a cada 5 s)
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _healthKnown;
    private bool _healthWasOk = true;
    private bool _checkingHealth;

    // Guarda do serviço: se o Watchdog for parado à força (ou travar sem responder), o app trava a
    // tela e o reinicia. Ver ProcessGuardian (usa Mutex + Named Pipe — ProcessSync) e, do outro
    // lado, EnforcementWorker.MonitorAppAsync no Watchdog (vigia o App da mesma forma).
    private readonly ProcessGuardian _guard = new();
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private bool _restrictionsAppliedByApp;
    private bool _reallyClosing;
    private bool _trayHintShown;

    /// <summary>true quando o Windows abriu o app no login (--minimized): fica na bandeja, sem janela.</summary>
    public bool StartedMinimized { get; set; }
    private bool _busy;               // operação longa em andamento (importar lista / aplicar bloqueio)
    private bool _refreshingStatus;
    private int _killRunning;         // 1 enquanto uma varredura de processos está rodando em segundo plano
    private DateTime _lastKillUtc = DateTime.MinValue;
    private System.Windows.Forms.NotifyIcon? _trayIcon;

    private static readonly string[] KnownBypassBrowserPaths =
    {
        "tor browser", "duckduckgo", "epic privacy browser", "ungoogled chromium"
    };

    // Sugestão para a lista do bloqueio temporizado (varrida no texto da página).
    private static readonly string[] AdultContentKeywords = AdultKeywords.SuggestedForLockList;

    // Domínio válido (já em minúsculas). Usado ao importar listas.
    private static readonly Regex DomainRegex =
        new(@"^[a-z0-9][a-z0-9.-]*\.[a-z]{2,}$", RegexOptions.Compiled);

    private string InstallRootPath => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    private string ExtensionFolderPath => Path.Combine(InstallRootPath, "BrowserExtension");

    // CORRIGIDO: o caminho guardado em WatchdogExePathOverride (escolhido uma vez, "localizar
    // manualmente") VENCIA o Watchdog instalado ao lado do app. Resultado: depois de reinstalar/atualizar,
    // o app reinstalava o serviço apontando para um Watchdog.exe ANTIGO (ex.: pasta bin\Release em
    // Downloads) e o código novo do Watchdog nunca rodava. Agora o Watchdog irmão (<instalação>\Watchdog)
    // sempre vale; o override só é usado quando o irmão não existe.
    private string WatchdogExePath
    {
        get
        {
            var sibling = Path.Combine(InstallRootPath, "Watchdog", "Watchdog.exe");
            if (File.Exists(sibling)) return sibling;
            return (!string.IsNullOrEmpty(_state.WatchdogExePathOverride) && File.Exists(_state.WatchdogExePathOverride))
                ? _state.WatchdogExePathOverride!
                : sibling;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        _state = StateManager.Load();

        // Senha administrativa obrigatória: sempre que a janela aparecer e ainda não houver senha,
        // pede para definir (se iniciou minimizado na bandeja, pede quando a pessoa abrir a janela).
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
                Dispatcher.BeginInvoke(new Action(PromptPasswordSetupIfNeeded), DispatcherPriority.ApplicationIdle);
        };

        SitesBox.Text = string.Join(Environment.NewLine, _state.BlockedWebsites);
        KeywordsBox.Text = string.Join(Environment.NewLine, _state.BlockedKeywords);
        AppsBox.Text = string.Join(Environment.NewLine, _state.BlockedProcesses);
        PathKeywordsBox.Text = string.Join(Environment.NewLine, _state.BlockedProcessPathKeywords);
        UpdateImportedListLabel();
        UpdatePermanentButton();
        UpdateOptionalVpnButton();
        // O modelo carrega em segundo plano: atualiza o texto "Modelo: ..." quando o estado mudar.
        Action imageStatusHandler = () => Dispatcher.BeginInvoke(new Action(UpdateImageDetectorButton));
        NsfwClassifier.StatusChanged += imageStatusHandler;
        Closed += (_, _) => NsfwClassifier.StatusChanged -= imageStatusHandler;
        UpdateImageDetectorButton();
        UpdateVirtualizationButton();
        UpdateLocalDnsButton();
        UpdateIncognitoAlwaysButton();
        UpdateWebArchivesButton();
        UpdateMinutesHint();

        UpdateCounts();
        SitesBox.TextChanged += (_, _) => UpdateCounts();
        KeywordsBox.TextChanged += (_, _) => UpdateCounts();
        AppsBox.TextChanged += (_, _) => UpdateCounts();

        SetupTrayIcon();

        // CORRIGIDO: o "X" da janela só esconde (vai pra bandeja) e cancelava QUALQUER fechamento,
        // inclusive o do Windows ao desligar/reiniciar/sair da conta — o app ficava segurando o
        // desligamento ("este aplicativo está impedindo o desligamento"). Agora, quando o Windows
        // avisa que a sessão vai acabar, a janela deixa fechar de verdade.
        // A marca de "saída normal" avisa o Watchdog de que este fechamento não é um encerramento
        // forçado (Gerenciador de Tarefas), que dispara a trava de tela.
        Application.Current.SessionEnding += (_, _) =>
        {
            _reallyClosing = true;
            AppPresence.MarkCleanExit();
        };
        Application.Current.Exit += (_, _) =>
        {
            AppPresence.MarkCleanExit();
            ProcessSync.ReleaseAppAlive();
            _guard.Dispose();
            _trayIcon?.Dispose();
        };

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };

        _timer.Tick += Timer_Tick;
        _timer.Start();

        _healthTimer.Tick += async (_, _) =>
        {
            await RefreshHealthAsync();
            await RefreshStatsAsync();
        };
        _healthTimer.Start();

        _guard.ForcedStopHandled += () => Logger.Log("ProcessGuardian: reação ao Watchdog encerrado à força concluída.");
        _guard.Start();
        UpdateUiForLockState();

        // CORRIGIDO: a parte lenta (instalar o serviço, consultar `sc`/`schtasks`, perguntas da
        // primeira abertura) rodava AQUI, antes da janela aparecer — o app parecia travado ao abrir.
        // Agora a janela abre primeiro e isso acontece logo depois, sem congelar a interface.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Startup));
    }

    private async void Startup()
    {
        try
        {
            // Camada de DESKTOP: vigia a barra de endereço do navegador, sem depender da extensão.
            // Liga primeiro, pra não haver brecha enquanto alguma pergunta está na tela.
            BrowserUrlWatcher.Start();
            ImageGuard.Start();

            // Aberto sozinho no login: sem janela, então avisa na bandeja que está rodando
            // (senão parece que o app não iniciou com o PC).
            if (StartedMinimized && _trayIcon != null)
            {
                try
                {
                    _trayIcon.ShowBalloonTip(4000, "SharkBlocker iniciado",
                        "Estou na bandeja, de plantão. Clique no ícone para abrir a janela.",
                        System.Windows.Forms.ToolTipIcon.Info);
                }
                catch { /* o aviso é só cortesia */ }
            }

            await EnsureServiceInstalledAsync();
            await RefreshHealthAsync();
            await OfferPermanentProtectionsOnFirstRunAsync();
            await RefreshStatusAsync();
            await RefreshHealthAsync();
            _guard.Arm();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.Startup", ex);
        }
    }

    /// <summary>Chamado pelo App quando alguém abre o programa de novo: traz a janela escondida de volta.</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ---------- Avisos rápidos e estado "ocupado" ----------

    /// <summary>
    /// Mensagem curta no rodapé, que some sozinha. CORRIGIDO: antes "Listas salvas.", "Importando..."
    /// etc. eram escritas na faixa de status, que é reescrita a cada segundo pelo timer — a
    /// mensagem sumia quase na hora.
    /// </summary>
    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        UiMotion.FadeSlideIn(ToastBorder, fromY: 12, durationMs: 220);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Troca de aba: o conteúdo novo aparece com um fade e um deslize curtinho.</summary>
    private void MainTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        // O evento também "borbulha" de controles dentro das abas; só interessa o da própria TabControl.
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;

        if (MainTabs.SelectedContent is UIElement content)
            UiMotion.FadeSlideIn(content, fromY: 8, durationMs: 220);
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Mouse.OverrideCursor = busy ? System.Windows.Input.Cursors.Wait : null;
        UpdateUiForLockState();
    }

    private void UpdatePermanentButton()
    {
        PermanentProtectionsButton.Content = _state.PermanentProtectionsEnabled
            ? "Desativar proteções permanentes"
            : "Ativar proteções permanentes";
    }

    // ---------- Opção: VPNs de uso comum (Tailscale, ZeroTier...) — auditoria U2 ----------

    private void UpdateOptionalVpnButton()
    {
        OptionalVpnButton.Content = _state.BlockOptionalVpnTools
            ? "Desativar bloqueio de Tailscale, ZeroTier e similares"
            : "Ativar bloqueio de Tailscale, ZeroTier e similares";
    }

    private void OptionalVpnButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            if (!_state.BlockOptionalVpnTools)
            {
                // Ligar só RESTRINGE mais: sem senha.
                _state.BlockOptionalVpnTools = true;
                StateManager.Save(_state);
                Logger.Log("Opção 'VPNs de uso comum' LIGADA (Tailscale, ZeroTier, Hamachi, ngrok, cloudflared serão encerrados durante o bloqueio).");
            }
            else
            {
                // Desligar AFROUXA: trava de tela de 30 min + senha + confirmações, como nas outras remoções.
                if (!UninstallFlow.TryAuthorizeRemoval(this, "Desativar o bloqueio de VPNs de uso comum")) return;

                _state.BlockOptionalVpnTools = false;
                StateManager.Save(_state);
                Logger.Log("Opção 'VPNs de uso comum' DESLIGADA (com senha).");
            }

            UpdateOptionalVpnButton();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.OptionalVpnButton_Click", ex);
            MessageBox.Show($"Erro ao alterar a opção: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Opção: detector de imagens (ImageGuard) ----------

    private void UpdateImageDetectorButton()
    {
        ImageDetectorButton.Content = _state.ImageDetectorEnabled
            ? "Desativar detector de imagens"
            : "Ativar detector de imagens";
        ImageDetectorStatusText.Text = "Modelo: " + ImageGuard.StatusText();
    }

    private void ImageDetectorButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            if (!_state.ImageDetectorEnabled)
            {
                // Ligar só RESTRINGE mais: sem senha.
                _state.ImageDetectorEnabled = true;
                StateManager.Save(_state);
                Logger.Log("Opção 'Detector de imagens' LIGADA.");
            }
            else
            {
                // Desligar AFROUXA: trava de tela de 30 min + senha + confirmações, como nas outras remoções.
                if (!UninstallFlow.TryAuthorizeRemoval(this, "Desativar o detector de imagens")) return;

                _state.ImageDetectorEnabled = false;
                StateManager.Save(_state);
                Logger.Log("Opção 'Detector de imagens' DESLIGADA (com senha).");
            }

            UpdateImageDetectorButton();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.ImageDetectorButton_Click", ex);
            MessageBox.Show($"Erro ao alterar a opção: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Opção: máquinas virtuais, WSL e Sandbox — auditoria U8 / backlog #11 ----------

    private void UpdateVirtualizationButton()
    {
        VirtualizationButton.Content = _state.BlockVirtualization
            ? "Desativar bloqueio de máquinas virtuais, WSL e Sandbox"
            : "Ativar bloqueio de máquinas virtuais, WSL e Sandbox";
    }

    private void VirtualizationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            if (!_state.BlockVirtualization)
            {
                // Ligar só RESTRINGE mais: sem senha.
                _state.BlockVirtualization = true;
                StateManager.Save(_state);
                Logger.Log("Opção 'Máquinas virtuais, WSL e Sandbox' LIGADA (serão encerrados durante o bloqueio).");
            }
            else
            {
                // Desligar AFROUXA: trava de tela de 30 min + senha + confirmações, como nas outras remoções.
                if (!UninstallFlow.TryAuthorizeRemoval(this, "Desativar o bloqueio de máquinas virtuais, WSL e Sandbox")) return;

                _state.BlockVirtualization = false;
                StateManager.Save(_state);
                Logger.Log("Opção 'Máquinas virtuais, WSL e Sandbox' DESLIGADA (com senha).");
            }

            UpdateVirtualizationButton();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.VirtualizationButton_Click", ex);
            MessageBox.Show($"Erro ao alterar a opção: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Opção: filtro de DNS local — backlog #9 ----------

    private void UpdateLocalDnsButton()
    {
        LocalDnsButton.Content = _state.LocalDnsFilterEnabled
            ? "Desativar filtro de DNS local"
            : "Ativar filtro de DNS local";
    }

    private void LocalDnsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            if (!_state.LocalDnsFilterEnabled)
            {
                var answer = MessageBox.Show(
                    "Ligar o filtro de DNS local?\n\n" +
                    "O DNS do PC inteiro passa a ser respondido pelo Watchdog enquanto houver bloqueio ativo ou " +
                    "proteções permanentes, e conexões de DNS por outros caminhos (DNS-sobre-HTTPS, DNS de terceiros) " +
                    "ficam bloqueadas no firewall.\n\n" +
                    "Se o filtro falhar, ele se desfaz sozinho. Se a internet parar de funcionar por causa dele, " +
                    "rode o emergency-fix-hosts.bat como Administrador.",
                    "Filtro de DNS local", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;

                // Ligar só RESTRINGE mais: sem senha.
                _state.LocalDnsFilterEnabled = true;
                StateManager.Save(_state);
                Logger.Log("Opção 'Filtro de DNS local' LIGADA (o Watchdog assume o DNS no próximo ciclo, se houver bloqueio ou proteções permanentes).");
            }
            else
            {
                // Desligar AFROUXA: trava de tela de 30 min + senha + confirmações, como nas outras remoções.
                if (!UninstallFlow.TryAuthorizeRemoval(this, "Desativar o filtro de DNS local")) return;

                _state.LocalDnsFilterEnabled = false;
                StateManager.Save(_state);
                Logger.Log("Opção 'Filtro de DNS local' DESLIGADA (com senha); o Watchdog devolve o DNS ao normal no próximo ciclo.");
            }

            UpdateLocalDnsButton();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.LocalDnsButton_Click", ex);
            MessageBox.Show($"Erro ao alterar a opção: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Opção: anônimo sempre bloqueado — auditoria U5 ----------

    private void UpdateIncognitoAlwaysButton()
    {
        IncognitoAlwaysButton.Content = _state.BlockIncognitoAlways
            ? "Desativar bloqueio permanente do modo anônimo"
            : "Ativar bloqueio permanente do modo anônimo";
    }

    private void IncognitoAlwaysButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            if (!_state.BlockIncognitoAlways)
            {
                // Ligar só RESTRINGE mais: sem senha.
                _state.BlockIncognitoAlways = true;
                StateManager.Save(_state);
                if (_state.PermanentProtectionsEnabled)
                {
                    BrowserPolicyManager.ApplyRestrictions(); // o Watchdog também reafirma
                    Logger.Log("Opção 'anônimo sempre bloqueado' LIGADA: modo anônimo/privado desativado nos navegadores.");
                }
                else
                {
                    Logger.Log("Opção 'anônimo sempre bloqueado' LIGADA, mas só vale com as proteções permanentes ligadas.");
                    MessageBox.Show("A opção está ligada, mas só tem efeito com as proteções permanentes ATIVADAS.\n\n" +
                                    "Ative as proteções permanentes (botão acima) para o modo anônimo ficar bloqueado o tempo todo.",
                        "Anônimo sempre bloqueado", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                // Desligar AFROUXA: trava de tela de 30 min + senha + confirmações, como nas outras remoções.
                if (!UninstallFlow.TryAuthorizeRemoval(this, "Desativar o bloqueio permanente do modo anônimo")) return;

                _state.BlockIncognitoAlways = false;
                StateManager.Save(_state);
                // Durante um bloqueio temporizado o bloqueio do anônimo continua (acaba com o bloqueio).
                if (!_state.IsLocked) BrowserPolicyManager.RemoveRestrictions();
                Logger.Log("Opção 'anônimo sempre bloqueado' DESLIGADA (com senha).");
            }

            UpdateIncognitoAlwaysButton();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.IncognitoAlwaysButton_Click", ex);
            MessageBox.Show($"Erro ao alterar a opção: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Opção: arquivos da web (Wayback Machine, archive.today...) — auditoria U7 ----------

    private void UpdateWebArchivesButton()
    {
        WebArchivesButton.Content = _state.BlockWebArchives
            ? "Desativar bloqueio de arquivos da web"
            : "Ativar bloqueio de arquivos da web";
    }

    private void WebArchivesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            if (!_state.BlockWebArchives)
            {
                // Ligar só RESTRINGE mais: sem senha. A extensão lê a opção em /state (a cada ~30 s).
                _state.BlockWebArchives = true;
                StateManager.Save(_state);
                Logger.Log("Opção 'arquivos da web' LIGADA (Wayback Machine, archive.today e similares barrados durante o bloqueio).");
            }
            else
            {
                // Desligar AFROUXA: trava de tela de 30 min + senha + confirmações, como nas outras remoções.
                if (!UninstallFlow.TryAuthorizeRemoval(this, "Desativar o bloqueio de arquivos da web")) return;

                _state.BlockWebArchives = false;
                StateManager.Save(_state);
                Logger.Log("Opção 'arquivos da web' DESLIGADA (com senha).");
            }

            UpdateWebArchivesButton();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.WebArchivesButton_Click", ex);
            MessageBox.Show($"Erro ao alterar a opção: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Primeira abertura: oferecer as proteções permanentes ----------

    /// <summary>
    /// SafeSearch forçado e extensões de bloqueio vêm dentro das "proteções
    /// permanentes" — que antes só ligavam se a pessoa achasse e clicasse no
    /// botão (por isso "não forçava" nada na prática). Agora o app oferece, UMA
    /// vez, na primeira abertura. Não liga sozinho de propósito: as proteções
    /// também travam a pasta de instalação contra exclusão e só desligam com senha
    /// + confirmações, o que pegaria de surpresa quem está só testando uma build.
    /// </summary>
    private async Task OfferPermanentProtectionsOnFirstRunAsync()
    {
        if (_state.PermanentProtectionsEnabled || _state.PermanentProtectionsOptOut) return;

        var answer = MessageBox.Show(
            "Ativar agora as proteções permanentes?\n\n" +
            "• SafeSearch forçado (Google, Bing, YouTube — no arquivo hosts e nas políticas do navegador)\n" +
            "• Extensão de bloqueio instalada automaticamente no Chrome/Edge/Brave, com bloqueio de " +
            "palavras-chave de conteúdo adulto\n" +
            "• DNS-sobre-HTTPS desligado nos navegadores\n" +
            "• Pasta do programa protegida contra exclusão\n\n" +
            "Depois de ativadas, só dá para desativar com a senha e as confirmações. " +
            "Se você escolher \"Não\", o app não pergunta de novo (dá para ativar na aba Proteções).",
            "Ativar proteções permanentes?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

        if (answer != MessageBoxResult.Yes)
        {
            _state.PermanentProtectionsOptOut = true;
            StateManager.Save(_state);
            return;
        }

        try
        {
            var installRoot = InstallRootPath;
            var extensionFolder = ExtensionFolderPath;
            SetBusy(true);
            try
            {
                await Task.Run(() => PermanentProtections.Enable(installRoot, extensionFolder));
            }
            finally
            {
                SetBusy(false);
            }

            _state.PermanentProtectionsEnabled = true;
            _state.PermanentProtectionsOptOut = false;
            StateManager.Save(_state);
            UpdatePermanentButton();
            Logger.Log("Proteções permanentes ativadas na primeira abertura (pelo prompt).");
            ShowToast("Proteções permanentes ativadas.");
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.OfferPermanentProtectionsOnFirstRun", ex);
            MessageBox.Show($"Não consegui ativar as proteções permanentes: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Serviço / status ----------

    private async Task EnsureServiceInstalledAsync()
    {
        if (!File.Exists(WatchdogExePath))
        {
            Logger.Log($"Watchdog.exe não encontrado em: {WatchdogExePath}");

            var choice = MessageBox.Show(
                "Não encontrei o Watchdog.exe no caminho esperado:\n\n" + WatchdogExePath + "\n\n" +
                "Isso é normal se você está rodando direto de uma pasta de build/publish (os caminhos do .NET " +
                "variam bastante: Debug/Release, self-contained, etc). Depois de instalado via Setup.exe isso se " +
                "resolve sozinho — mas se você quiser testar agora sem instalar, posso te deixar apontar onde está " +
                "o Watchdog.exe manualmente.\n\n" +
                "Quer localizar o arquivo agora?",
                "Serviço não encontrado", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (choice != MessageBoxResult.Yes) return;
            if (!LocateWatchdogManually()) return;
        }

        await InstallServiceAtAsync(WatchdogExePath);
    }

    private bool LocateWatchdogManually()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecione o Watchdog.exe",
            Filter = "Watchdog.exe|Watchdog.exe|Executáveis (*.exe)|*.exe",
            FileName = "Watchdog.exe"
        };
        if (dialog.ShowDialog() != true) return false;

        _state.WatchdogExePathOverride = dialog.FileName;
        StateManager.Save(_state);
        Logger.Log($"Watchdog.exe localizado manualmente em: {dialog.FileName}");
        return true;
    }

    private async void LocateWatchdogButton_Click(object sender, RoutedEventArgs e)
    {
        if (!LocateWatchdogManually()) return;
        await InstallServiceAtAsync(WatchdogExePath);
        await RefreshStatusAsync();
    }

    private async Task InstallServiceAtAsync(string watchdogExePath)
    {
        if (!File.Exists(watchdogExePath)) return;

        var appExePath = Path.Combine(AppContext.BaseDirectory, "SharkBlocker.exe");

        // CORRIGIDO: tudo aqui chama `sc`/`schtasks` e espera processos terminarem (até alguns
        // segundos). Na thread da interface isso congelava a janela; agora roda em segundo plano.
        SetBusy(true);
        bool ok;
        try
        {
            ok = await Task.Run(() => InstallServiceCore(watchdogExePath, appExePath));
        }
        finally
        {
            SetBusy(false);
        }

        _guard.Arm();

        // O Watchdog usa isto para recriar a tarefa de abertura automática se alguém a apagar.
        var autoUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        if (_state.AutoStartExePath != appExePath || _state.AutoStartUser != autoUser)
        {
            _state.AutoStartExePath = appExePath;
            _state.AutoStartUser = autoUser;
            StateManager.Save(_state);
        }

        if (!ok)
        {
            MessageBox.Show(
                "Encontrei o Watchdog.exe, mas a instalação do serviço falhou. Detalhes em:\n" + Logger.LogFilePath,
                "Falha ao instalar serviço", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Roda fora da thread da interface. Devolve false só se a instalação do serviço falhou.</summary>
    private static bool InstallServiceCore(string watchdogExePath, string appExePath)
    {
        var ok = true;

        var alreadyGood = ServiceInstaller.IsInstalled()
            && ServiceInstaller.IsRunning()
            && string.Equals(ServiceInstaller.GetConfiguredBinPath(), watchdogExePath, StringComparison.OrdinalIgnoreCase);

        if (alreadyGood)
        {
            // Já está tudo certo — não mexe em nada. Evitar reinstalar à toa
            // importa porque parar+recriar o serviço cria um intervalo de
            // alguns segundos sem ninguém reforçando o hosts.
            Logger.Log("Serviço já instalado e rodando com o binário correto — nada a fazer.");
        }
        else
        {
            // Sempre limpa qualquer Watchdog.exe órfão primeiro — inclusive os
            // que o Windows já não associa mais a nenhum serviço registrado (é
            // o que causava dois Watchdogs rodando ao mesmo tempo e brigando
            // pelo hosts).
            ServiceInstaller.KillAllWatchdogProcesses();

            // Com os órfãos mortos, reseta o ACL do hosts — pode ter ficado com
            // entradas conflitantes de quando havia mais de um processo
            // mexendo nele ao mesmo tempo.
            HostsFileManager.ResetAcl();

            if (ServiceInstaller.IsInstalled())
            {
                ServiceInstaller.Uninstall();
            }

            ok = ServiceInstaller.Install(watchdogExePath);
            Logger.Log(ok ? $"Serviço instalado com sucesso apontando pra {watchdogExePath}."
                          : "Falha ao instalar o serviço — ver log do sc acima.");
        }

        // Sempre (re)registra a abertura automática: é idempotente (/F) e garante que tarefas
        // criadas por versões antigas ganhem o "--minimized" (abrir direto na bandeja, sem
        // saltar a janela toda vez que o Windows liga).
        AppStartupManager.RegisterAutoStart(appExePath);

        return ok;
    }

    /// <summary>A prova de verdade de que a extensão está carregada: ela falou com o serviço há pouco?</summary>
    private static string DescribeExtensionContact()
    {
        var last = LocalStateServer.ReadLastExtensionContactUtc();
        if (last == null)
            return "Extensão no navegador: NUNCA contatou o serviço ✘ — ela não está carregada em nenhum navegador. " +
                   "Use o botão \"Instalar extensão manualmente\" (aba Status → Ferramentas de diagnóstico).";

        var age = DateTime.UtcNow - last.Value;
        return age.TotalMinutes <= 2
            ? $"Extensão no navegador: ATIVA ✔ (último contato há {Math.Max(0, (int)age.TotalSeconds)}s)"
            : $"Extensão no navegador: sem contato há {(int)age.TotalMinutes} min ✘ (navegador fechado ou extensão removida)";
    }

    private void InstallExtensionManuallyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExtensionFolderPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.InstallExtensionManuallyButton_Click (explorer)", ex);
        }

        MessageBox.Show(
            "Abri a pasta da extensão. No navegador:\n\n" +
            "1. Digite chrome://extensions (no Edge: edge://extensions) na barra de endereço.\n" +
            "2. Ligue \"Modo do desenvolvedor\".\n" +
            "3. Clique em \"Carregar sem compactação\" e escolha a pasta BrowserExtension que acabei de abrir.\n\n" +
            "Depois volte aqui e clique em \"Atualizar status\": deve aparecer \"Extensão no navegador: ATIVA\".\n\n" +
            "Atenção: assim a extensão funciona, mas NÃO fica protegida contra remoção. A instalação automática " +
            "e não removível exige publicar a extensão nas lojas (veja o README).",
            "Instalar extensão manualmente", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void RefreshStatusButton_Click(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async void TestHostsButton_Click(object sender, RoutedEventArgs e)
    {
        TestHostsButton.IsEnabled = false;
        try
        {
            var (success, message) = await Task.Run(HostsFileManager.SelfTest);
            Logger.Log($"Autoteste do hosts: {message}");
            MessageBox.Show(message, success ? "Autoteste OK" : "Autoteste FALHOU",
                MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.TestHostsButton_Click", ex);
            MessageBox.Show($"O autoteste não conseguiu rodar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TestHostsButton.IsEnabled = true;
        }
    }

    private void VerifyCrxButton_Click(object sender, RoutedEventArgs e)
    {
        var crxPath = Path.Combine(ExtensionFolderPath, "extension.crx");
        var (ok, message) = ExtensionInstaller.VerifyExistingCrx(crxPath);
        Logger.Log($"Verificação manual do .crx: {message}");
        MessageBox.Show(message, ok ? "Verificação OK" : "Verificação FALHOU",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    private async void CleanupOrphansButton_Click(object sender, RoutedEventArgs e)
    {
        CleanupOrphansButton.IsEnabled = false;
        try
        {
            var before = Process.GetProcessesByName("Watchdog").Length;
            _guard.Arm();
            await Task.Run(() =>
            {
                ServiceInstaller.KillAllWatchdogProcesses();
                HostsFileManager.ResetAcl();
            });

            MessageBox.Show(
                $"{before} processo(s) \"Watchdog\" encontrados e encerrados. ACL do hosts resetado.\n\n" +
                "Recomendo fechar e abrir o app de novo agora, pra ele reinstalar um único Watchdog limpo.",
                "Limpeza concluída", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.CleanupOrphansButton_Click", ex);
            MessageBox.Show($"Não consegui concluir a limpeza: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CleanupOrphansButton.IsEnabled = true;
        }

        await RefreshStatusAsync();
    }

    private void OpenLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Logger.LogFilePath)!);
            if (!File.Exists(Logger.LogFilePath))
                File.WriteAllText(Logger.LogFilePath, "(log vazio até agora)\n");

            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Logger.LogFilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não consegui abrir o log: {ex.Message}\n\nCaminho: {Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// CORRIGIDO: o status consultava `sc`, `schtasks`, o Registro e o AppLocker NA THREAD DA
    /// INTERFACE (cada um abre um processo e espera). Agora a coleta roda em segundo plano e só o
    /// resultado volta para a tela.
    /// </summary>
    /// <summary>Atualiza o cartão da aba Agenda (status e lista de horários).</summary>
    private void RefreshScheduleCard()
    {
        try
        {
            var state = StateManager.Load();
            ScheduleStatusText.Text = BlockScheduler.StatusText(state, DateTime.Now);

            var schedules = state.Schedules ?? new List<BlockSchedule>();
            ScheduleListText.Text = schedules.Count == 0
                ? "Nenhum horário configurado ainda."
                : string.Join("\n", schedules.Select(s => "• " + BlockScheduler.Describe(s)));

            EditScheduleButton.Content = state.SchedulesConfigured
                ? "Alterar agenda (trava a tela por 30 min)"
                : "Configurar agenda";
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.RefreshScheduleCard", ex);
        }
    }

    private void EditScheduleButton_Click(object sender, RoutedEventArgs e) => OpenScheduleEditor(null);

    /// <summary>Abre o editor de agenda já com o horário sugerido (backlog #6). Vale a mesma regra de qualquer mudança de agenda.</summary>
    private void SuggestScheduleButton_Click(object sender, RoutedEventArgs e) => OpenScheduleEditor(_suggestedSchedule);

    private void OpenScheduleEditor(BlockSchedule? suggested)
    {
        try
        {
            var state = StateManager.Load();

            // Depois da primeira configuração, qualquer mudança passa por trava de tela de 30 min + senha.
            if (state.SchedulesConfigured)
            {
                var confirm = MessageBox.Show(
                    "Para alterar a agenda, a tela será travada por 30 minutos e depois a senha será pedida. Continuar?",
                    "Alterar agenda", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (confirm != MessageBoxResult.Yes) return;
                if (!UninstallFlow.TryAuthorizeScheduleChange(this)) return;
            }

            var initial = (StateManager.Load().Schedules ?? new List<BlockSchedule>()).ToList();
            if (suggested != null) initial.Add(suggested); // a sugestão entra como uma linha nova; só vale se a pessoa salvar
            var editor = new ScheduleEditorWindow(initial) { Owner = this };
            if (editor.ShowDialog() != true) return;

            BlockScheduler.SaveSchedules(editor.Result);
            _state = StateManager.Load();
            RefreshScheduleCard();
            UpdateUiForLockState();
            ShowToast("Agenda salva.");
            _ = RefreshStatsAsync(); // a sugestão some se o horário agora já está coberto

            var s = _state;
            var listed = s.BlockedWebsites.Count + s.ImportedWebsites.Count + s.BlockedKeywords.Count +
                         s.BlockedProcesses.Count + s.BlockedProcessPathKeywords.Count;
            if (editor.Result.Count > 0 && listed == 0)
            {
                MessageBox.Show(
                    "As listas de bloqueio salvas estão vazias, então os horários vão ligar o bloqueio sem nada para bloquear. " +
                    "Preencha as listas na aba Bloqueio e clique em Salvar listas.",
                    "Listas vazias", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.EditScheduleButton_Click", ex);
            MessageBox.Show("Não consegui abrir ou salvar a agenda agora. Detalhes no log de diagnóstico.",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Atualiza o cartão "Sequência e estatísticas" da aba Status (lê o stats.json).</summary>
    private async Task RefreshStatsAsync()
    {
        RefreshScheduleCard(); // aproveita o mesmo gancho (abertura, "Atualizar status" e a cada minuto)

        try
        {
            var s = await Task.Run(() => StatsStore.Snapshot(DateTime.UtcNow));
            var streak = StatsStore.FormatStreak(s.StreakDays);
            StreakValueText.Text = streak;
            StreakCaptionText.Text = s.StreakDays >= 1 ? "sem tentar burlar" : "sem tentar burlar (recomeçou há pouco)";
            StatsDetailText.Text =
                $"Recorde: {StatsStore.FormatStreak(s.BestStreakDays)}\n" +
                (s.TotalDays30 > 0
                    ? $"Dias limpos (últimos 30): {s.CleanDays30} de {s.TotalDays30} ({s.CleanDays30 * 100 / s.TotalDays30}%)\n"
                    : "") +
                $"Buscas barradas (últimos 7 dias): {s.SearchesWeek}\n" +
                $"Tentativas de burlar (últimos 7 dias): {s.BypassWeek}\n" +
                $"Tempo bloqueado (últimos 7 dias): {StatsStore.FormatDuration(s.BlockedSecondsWeek)}\n" +
                $"Tempo bloqueado (total): {StatsStore.FormatDuration(s.BlockedSecondsTotal)}\n" +
                $"Contando desde {s.SinceUtc.ToLocalTime():dd/MM/yyyy}";
            RenderRiskMap(s);
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.RefreshStatsAsync", ex);
        }
    }

    // ---------- Mapa de horários de risco (backlog #6) ----------

    private BlockSchedule? _suggestedSchedule;

    private void RenderRiskMap(StatsSummary s)
    {
        _suggestedSchedule = null;
        SuggestScheduleButton.Visibility = Visibility.Collapsed;

        var hist = s.RiskHours;
        var total = hist?.Sum() ?? 0;
        if (hist == null || total < 5)
        {
            RiskMapText.Text = "";
            RiskAxisText.Text = "";
            RiskSuggestionText.Text = total == 0
                ? "Sem ocorrências registradas até agora."
                : $"Ainda sem dados suficientes ({total} ocorrência(s)). Com pelo menos 5, eu sugiro um horário.";
            return;
        }

        var (bars, axis) = StatsStore.FormatRiskMap(hist);
        RiskMapText.Text = bars;
        RiskAxisText.Text = axis;

        if (s.RiskSuggestion is not { } w)
        {
            RiskSuggestionText.Text = $"{total} ocorrências no histórico guardado (até 60 dias), espalhadas pelo dia: sem um horário claro para sugerir.";
            return;
        }

        var span = $"{w.StartHour:00}:00 às {w.EndHourExclusive:00}:00";
        var pct = w.Covered * 100 / w.Total;
        if (IsAlreadyScheduled(w))
        {
            RiskSuggestionText.Text = $"As ocorrências se concentram entre {span} ({w.Covered} de {w.Total}, {pct}%), e esse horário já está coberto pela sua agenda.";
            return;
        }

        RiskSuggestionText.Text =
            $"As ocorrências se concentram entre {span}: {w.Covered} de {w.Total} ({pct}%) no histórico guardado (até 60 dias). " +
            "Um bloqueio agendado nesse horário cobre onde elas mais acontecem. O editor abre já preenchido; só vale se você salvar.";
        _suggestedSchedule = new BlockSchedule
        {
            StartTime = new TimeSpan(w.StartHour, 0, 0),
            EndTime = new TimeSpan(w.EndHourExclusive, 0, 0)
        };
        SuggestScheduleButton.Content = $"Criar agenda sugerida ({span})";
        SuggestScheduleButton.Visibility = Visibility.Visible;
    }

    /// <summary>true se TODAS as horas da janela já estão dentro de horários ativos que valem todos os dias da semana.</summary>
    private static bool IsAlreadyScheduled(RiskWindow w)
    {
        var schedules = StateManager.Load().Schedules ?? new List<BlockSchedule>();
        var length = (w.EndHourExclusive - w.StartHour + 24) % 24;
        if (length == 0) length = 24;

        for (var i = 0; i < length; i++)
        {
            var hour = (w.StartHour + i) % 24;
            var covered = schedules.Any(sc => sc.Enabled && sc.Days.Distinct().Count() == 7 && CoversHour(sc, hour));
            if (!covered) return false;
        }
        return true;
    }

    private static bool CoversHour(BlockSchedule sc, int hour)
    {
        var start = (int)sc.StartTime.TotalMinutes;
        var end = (int)sc.EndTime.TotalMinutes;
        if (start == end) return false;
        var hStart = hour * 60;
        var hEnd = hStart + 60;
        return start < end
            ? hStart >= start && hEnd <= end
            : hStart >= start || hEnd <= end; // atravessa a meia-noite
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshingStatus) return;
        _refreshingStatus = true;
        RefreshStatusButton.IsEnabled = false;
        ServiceStatusText.Text = "Verificando...";

        try
        {
            await RefreshStatsAsync();
            var installRoot = InstallRootPath;
            var permanentEnabled = _state.PermanentProtectionsEnabled;
            var (service, protections) = await Task.Run(() => BuildStatusTexts(installRoot, permanentEnabled));
            ServiceStatusText.Text = service;
            ProtectionsStatusText.Text = protections;
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.RefreshStatusAsync", ex);
            ServiceStatusText.Text = "Não consegui ler o status agora. Tente \"Atualizar status\" de novo (detalhes no log).";
        }
        finally
        {
            _refreshingStatus = false;
            RefreshStatusButton.IsEnabled = true;
        }
    }

    private static (string Service, string Protections) BuildStatusTexts(string installRoot, bool permanentEnabled)
    {
        var extensionFolder = Path.Combine(installRoot, "BrowserExtension");

        var installed = ServiceInstaller.IsInstalled();
        var running = installed && ServiceInstaller.IsRunning();

        var service = installed
            ? (running ? "Serviço de bloqueio: instalado e rodando ✔" : "Serviço de bloqueio: instalado, mas PARADO ✘ (clique em Atualizar de novo em alguns segundos — ele se autorrecupera)")
            : "Serviço de bloqueio: NÃO instalado ✘ — feche e abra o app como Administrador";
        service += $"\nApp abre sozinho no login: {(AppStartupManager.IsRegistered() ? "sim ✔" : "não")}";

        var crxExists = File.Exists(Path.Combine(extensionFolder, "extension.crx"));
        var appLockerLine = AppLockerManager.IsSupported() ? "disponível" : "indisponível nesta edição do Windows";

        var policyStatus = ExtensionInstaller.GetPolicyStatus();
        var policyLine = string.Join(" | ", policyStatus.Select(kv => $"{kv.Key}: {kv.Value}"));

        var protections =
            (permanentEnabled ? "Proteções permanentes: ATIVAS ✔" : "Proteções permanentes: desativadas") +
            $"\nExtensão (.crx local gerado automaticamente): {(crxExists ? "sim ✔" : "ainda não — ative as proteções permanentes")}" +
            $"\nPolítica de instalação forçada: {policyLine}" +
            $"\n{DescribeExtensionContact()}" +
            $"\n{ExtensionInstaller.DescribeInstallMode(installRoot)}" +
            $"\nAppLocker: {appLockerLine}";

        return (service, protections);
    }

    // ---------- Saída protegida ----------

    /// <summary>
    /// Botão de urgência: a pessoa pede a trava de tela de 30 min quando sente que vai ceder. Sem senha (só
    /// reforça a proteção, não afrouxa nada), pede uma confirmação para evitar clique sem querer, e NÃO é punição:
    /// o motivo "urgency" não entra nas estatísticas, então a sequência não zera.
    /// </summary>
    private void StartUrgency()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (ScreenLock.IsShowing) return;

                var answer = MessageBox.Show(
                    "Travar a tela por 30 minutos agora?\n\n" +
                    "Não pede senha e não dá para cancelar depois de começar. " +
                    "Não conta como tentativa de burlar: a sua sequência continua.",
                    "Preciso de uma pausa", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;

                var until = StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, ScreenLock.ReasonUrgency);
                var (headline, message) = ScreenLock.TextFor(StateManager.Load().ScreenLockReason);
                ScreenLock.ShowUntil(until, headline, message);
            }
            catch (Exception ex)
            {
                Logger.LogException("MainWindow.StartUrgency", ex);
            }
        }));
    }

    private void UrgencyButton_Click(object sender, RoutedEventArgs e) => StartUrgency();

    /// <summary>
    /// Sair de verdade (menu da bandeja) é uma tentativa de fechar o app: trava de tela de 30 min,
    /// depois senha (se houver) e a sequência de confirmações. Durante um bloqueio temporizado
    /// não existe saída: só a trava de tela.
    /// </summary>
    private void RequestExit()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_state.IsLocked)
            {
                var until = StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, AppPresence.ReasonClosed);
                var (headline, message) = ScreenLock.TextFor(StateManager.Load().ScreenLockReason);
                ScreenLock.ShowUntil(until, headline, message);
                return;
            }

            if (!UninstallFlow.TryAuthorizeRemoval(this, "Fechar o SharkBlocker")) return;

            _reallyClosing = true;
            AppPresence.MarkCleanExit();
            _trayIcon?.Dispose();
            Application.Current.Shutdown();
        }));
    }

    // ---------- Saúde das proteções (checagem a cada 60 s) ----------

    /// <summary>
    /// SEGURANÇA: um bloqueador só protege se estiver inteiro. A cada minuto confere (em segundo
    /// plano) se o serviço roda, se a abertura automática existe e — com as proteções permanentes
    /// ligadas e um navegador aberto — se a extensão está dando sinal de vida. O resultado aparece
    /// no cabeçalho, e uma PIORA (de "tudo certo" para "atenção") gera um aviso na hora.
    /// </summary>
    private async Task RefreshHealthAsync()
    {
        if (_checkingHealth) return;
        _checkingHealth = true;
        try
        {
            var permanentEnabled = _state.PermanentProtectionsEnabled;
            var (ok, text) = await Task.Run(() => CheckHealth(permanentEnabled));

            HealthText.Text = (ok ? "● " : "▲ ") + text;
            HealthText.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "ColorSuccess" : "ColorWarning");

            if (_healthKnown && _healthWasOk && !ok)
            {
                ShowToast("Atenção: " + text);
                try
                {
                    _trayIcon?.ShowBalloonTip(5000, "SharkBlocker: atenção", text, System.Windows.Forms.ToolTipIcon.Warning);
                }
                catch { /* o aviso é só cortesia */ }
            }

            _healthKnown = true;
            _healthWasOk = ok;
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.RefreshHealthAsync", ex);
        }
        finally
        {
            _checkingHealth = false;
        }
    }

    private static (bool Ok, string Text) CheckHealth(bool permanentEnabled)
    {
        var problems = new List<string>();

        if (!ServiceInstaller.IsInstalled()) problems.Add("serviço de bloqueio não instalado");
        else if (!ServiceInstaller.IsRunning()) problems.Add("serviço de bloqueio parado");

        if (!AppStartupManager.IsRegistered()) problems.Add("não abre sozinho no login");

        if (permanentEnabled)
        {
            // Sem navegador aberto não existe sinal da extensão — isso não é problema.
            var browserOpen = new[] { "chrome", "msedge", "brave" }.Any(n =>
            {
                var found = Process.GetProcessesByName(n);
                var any = found.Length > 0;
                foreach (var p in found) p.Dispose();
                return any;
            });

            if (browserOpen)
            {
                var last = LocalStateServer.ReadLastExtensionContactUtc();
                if (last == null || (DateTime.UtcNow - last.Value).TotalMinutes > 5)
                    problems.Add("extensão do navegador sem sinal");
            }
        }

        return problems.Count == 0
            ? (true, "Tudo certo: serviço e proteções ativos")
            : (false, string.Join("; ", problems));
    }

    // ---------- Backup das listas ----------

    private sealed record ListsBackup(
        List<string>? Sites, List<string>? Keywords, List<string>? Apps, List<string>? PathKeywords);

    private void ExportListsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Exportar listas",
            Filter = "Listas do SharkBlocker (*.json)|*.json",
            FileName = "sharkblocker-listas.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var backup = new ListsBackup(
                SplitLines(SitesBox.Text), SplitLines(KeywordsBox.Text),
                SplitLines(AppsBox.Text), SplitLines(PathKeywordsBox.Text));
            File.WriteAllText(dialog.FileName,
                JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
            ShowToast("Listas exportadas para " + Path.GetFileName(dialog.FileName) + ".");
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.ExportListsButton_Click", ex);
            MessageBox.Show($"Não consegui exportar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportListsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("As listas não podem ser alteradas enquanto o bloqueio estiver ativo.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Importar listas",
            Filter = "Listas do SharkBlocker (*.json)|*.json|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var backup = JsonSerializer.Deserialize<ListsBackup>(File.ReadAllText(dialog.FileName));
            if (backup == null) throw new InvalidDataException("arquivo vazio");

            List<string> Merge(string current, List<string>? incoming) =>
                SplitLines(current).Concat(incoming ?? new List<string>())
                    .Select(l => l.Trim()).Where(l => l.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            SitesBox.Text = string.Join(Environment.NewLine, Merge(SitesBox.Text, backup.Sites));
            KeywordsBox.Text = string.Join(Environment.NewLine, Merge(KeywordsBox.Text, backup.Keywords));
            AppsBox.Text = string.Join(Environment.NewLine, Merge(AppsBox.Text, backup.Apps));
            PathKeywordsBox.Text = string.Join(Environment.NewLine, Merge(PathKeywordsBox.Text, backup.PathKeywords));

            ShowToast("Listas carregadas nas caixas. Clique em \"Salvar listas\" para guardar.");
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.ImportListsButton_Click", ex);
            MessageBox.Show($"Esse arquivo não parece ser um backup de listas do SharkBlocker ({ex.Message}).",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Importar lista grande (assíncrono + fora da caixa de texto) ----------

    private async void ImportListButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_state.IsLocked)
        {
            MessageBox.Show("As listas não podem ser alteradas enquanto o bloqueio estiver ativo.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Selecione uma lista (formato hosts, ex: StevenBlack/hosts, ou um domínio por linha)",
            Filter = "Arquivos de texto (*.txt)|*.txt|Todos os arquivos (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        SetBusy(true);
        ShowToast("Importando... (arquivos grandes podem levar alguns segundos)");

        try
        {
            // Faz o parsing em uma thread separada — listas com dezenas de
            // milhares de linhas (ex: porn.txt de blocklists públicas) travavam
            // a interface (e podiam até "crashar" o app) quando processadas na
            // thread da UI, ainda mais quando o resultado ia parar numa TextBox.
            var domains = await Task.Run(() => ParseHostsFormatFile(dialog.FileName));

            if (domains.Count == 0)
            {
                MessageBox.Show(
                    "Nenhum domínio reconhecido nesse arquivo.\n\n" +
                    "Formatos aceitos: \"0.0.0.0 dominio.com\" (formato hosts) ou um domínio por linha.",
                    "Nada importado", MessageBoxButton.OK, MessageBoxImage.Warning);
                ToastText.Text = "Nada foi importado.";
                return;
            }

            // Trocar a lista importada por outra que NÃO contém tudo o que a atual tem é uma remoção.
            if (_state.ImportedWebsites.Count > 0 &&
                _state.ImportedWebsites.Except(domains, StringComparer.OrdinalIgnoreCase).Any())
            {
                Mouse.OverrideCursor = null;
                if (!UninstallFlow.TryAuthorizeRemoval(this, "Substituir a lista importada",
                        new[] { _state.ImportedListLabel ?? "lista importada" }))
                {
                    ShowToast("Importação cancelada: a lista atual continua ativa.");
                    return;
                }
            }

            // Listas grandes NÃO vão para a caixa de texto (é isso que travava
            // a interface) — ficam guardadas à parte e só um resumo é mostrado.
            _state.ImportedWebsites = domains;
            _state.ImportedListLabel = $"{Path.GetFileName(dialog.FileName)} ({domains.Count:N0} domínios)";
            StateManager.Save(_state);
            UpdateImportedListLabel();

            ShowToast($"{domains.Count:N0} domínio(s) importados de \"{Path.GetFileName(dialog.FileName)}\".");
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.ImportListButton_Click", ex);
            MessageBox.Show($"Falha ao importar: {ex.Message}", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RemoveImportedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("Não é possível alterar listas durante o bloqueio.", "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_state.ImportedWebsites.Count > 0 &&
            !UninstallFlow.TryAuthorizeRemoval(this, "Remover a lista importada",
                new[] { _state.ImportedListLabel ?? "lista importada" }))
        {
            ShowToast("Remoção cancelada: a lista importada continua ativa.");
            return;
        }

        _state.ImportedWebsites = new List<string>();
        _state.ImportedListLabel = null;
        StateManager.Save(_state);
        UpdateImportedListLabel();
        ShowToast("Lista importada removida.");
    }

    private void UpdateImportedListLabel()
    {
        ImportedListLabelText.Text = string.IsNullOrEmpty(_state.ImportedListLabel)
            ? "Nenhuma lista importada."
            : $"Lista importada ativa: {_state.ImportedListLabel}";
    }

    /// <summary>
    /// Aceita o formato hosts ("0.0.0.0 dominio.com", inclusive vários domínios na mesma linha e
    /// comentário no fim da linha) e também listas com UM domínio por linha. CORRIGIDO: antes só
    /// valia a linha EXATA "0.0.0.0 dominio.com" — linhas com "# comentário" no final, ou listas
    /// de domínio puro, eram descartadas em silêncio.
    /// </summary>
    private static List<string> ParseHostsFormatFile(string path)
    {
        var result = new List<string>(capacity: 4096);
        var separators = new[] { ' ', '\t' };

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine;
            var hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;

            var tokens = line.Split(separators, StringSplitOptions.RemoveEmptyEntries);

            int first;
            if (tokens.Length >= 2 && (tokens[0] == "0.0.0.0" || tokens[0] == "127.0.0.1"))
                first = 1;                       // formato hosts
            else if (tokens.Length == 1)
                first = 0;                       // um domínio por linha
            else
                continue;

            for (var i = first; i < tokens.Length; i++)
            {
                var domain = tokens[i].ToLowerInvariant();
                if (domain == "localhost" || domain.EndsWith(".local")) continue;
                if (DomainRegex.IsMatch(domain)) result.Add(domain);
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void SuggestBrowsersButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = SplitLines(PathKeywordsBox.Text);
        var merged = existing.Concat(KnownBypassBrowserPaths).Distinct(StringComparer.OrdinalIgnoreCase);
        PathKeywordsBox.Text = string.Join(Environment.NewLine, merged);
    }

    private void SuggestAdultKeywordsButton_Click(object sender, RoutedEventArgs e)
    {
        var existing = SplitLines(KeywordsBox.Text);
        var merged = existing.Concat(AdultContentKeywords).Distinct(StringComparer.OrdinalIgnoreCase);
        KeywordsBox.Text = string.Join(Environment.NewLine, merged);
    }

    // ---------- Minutos do bloqueio ----------

    private void PresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string minutes)
        {
            MinutesBox.Text = minutes;
        }
    }

    private void MinutesBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Só dígitos. (Colar texto com letras passa por aqui sem ser barrado; por isso
        // TryReadMinutes também valida na hora de iniciar.)
        e.Handled = !e.Text.All(char.IsDigit);
    }

    private void MinutesBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // Este evento dispara durante o InitializeComponent, quando os controles abaixo ainda
        // não foram criados.
        if (MinutesHintText == null) return;
        UpdateMinutesHint();
    }

    private bool TryReadMinutes(out int minutes) =>
        int.TryParse(MinutesBox.Text, out minutes) && minutes >= 1 && minutes <= 99999;

    private void UpdateMinutesHint()
    {
        MinutesHintText.Text = TryReadMinutes(out var minutes)
            ? "= " + DescribeDuration(TimeSpan.FromMinutes(minutes))
            : "digite um número de 1 a 99999";
    }

    private static string DescribeDuration(TimeSpan time)
    {
        var parts = new List<string>();
        var days = (int)time.TotalDays;
        if (days > 0) parts.Add(days == 1 ? "1 dia" : $"{days} dias");
        if (time.Hours > 0) parts.Add(time.Hours == 1 ? "1 hora" : $"{time.Hours} horas");
        if (time.Minutes > 0 || parts.Count == 0) parts.Add(time.Minutes == 1 ? "1 minuto" : $"{time.Minutes} minutos");

        return parts.Count == 1
            ? parts[0]
            : string.Join(", ", parts.Take(parts.Count - 1)) + " e " + parts[^1];
    }

    // ---------- Salvar / Lock ----------

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("As listas não podem ser alteradas enquanto o bloqueio estiver ativo.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (sites, invalidSites) = NormalizeSites(SitesBox.Text);
        var newKeywords = SplitLines(KeywordsBox.Text);
        var newApps = SplitLines(AppsBox.Text);
        var newPaths = SplitLines(PathKeywordsBox.Text);

        // Tirar algo que já estava bloqueado = trava de 30 min + senha + confirmações.
        if (!EnsureRemovalAuthorized(sites, newKeywords, newApps, newPaths)) return;

        _state.BlockedWebsites = sites;
        _state.BlockedKeywords = newKeywords;
        _state.BlockedProcesses = newApps;
        _state.BlockedProcessPathKeywords = newPaths;
        StateManager.Save(_state);

        // A caixa passa a mostrar o que de fato foi guardado (domínios limpos). As linhas que não
        // são domínio continuam lá, para a pessoa corrigir.
        SitesBox.Text = string.Join(Environment.NewLine, sites.Concat(invalidSites));

        ShowToast(invalidSites.Count == 0
            ? "Listas salvas."
            : $"Listas salvas, mas {invalidSites.Count} linha(s) de sites não parecem um domínio e foram ignoradas.");
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_state.IsLocked)
        {
            MessageBox.Show("Já existe um bloqueio ativo. Espere o tempo acabar — não há como cancelar antes.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryReadMinutes(out var minutes))
        {
            MessageBox.Show("Digite um número de minutos válido (de 1 a 99999).", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var (sites, invalidSites) = NormalizeSites(SitesBox.Text);
        if (invalidSites.Count > 0)
        {
            MessageBox.Show(
                "Estas linhas da lista de sites não parecem um domínio válido:\n\n• " +
                string.Join("\n• ", invalidSites.Take(10)) + (invalidSites.Count > 10 ? "\n• ..." : "") +
                "\n\nCorrija ou apague antes de iniciar (depois do bloqueio ligado não dá para editar).",
                "Sites inválidos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SitesBox.Text = string.Join(Environment.NewLine, sites);

        var keywords = SplitLines(KeywordsBox.Text);
        var apps = SplitLines(AppsBox.Text);
        var paths = SplitLines(PathKeywordsBox.Text);

        if (!EnsureRemovalAuthorized(sites, keywords, apps, paths)) return;

        if (sites.Count == 0 && _state.ImportedWebsites.Count == 0 && keywords.Count == 0 && apps.Count == 0 && paths.Count == 0)
        {
            var empty = MessageBox.Show(
                "Nenhum site, palavra-chave ou programa está listado. O bloqueio ficaria ligado (e sem cancelar) " +
                "sem bloquear nada.\n\nIniciar mesmo assim?",
                "Listas vazias", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (empty != MessageBoxResult.Yes) return;
        }

        var pretty = DescribeDuration(TimeSpan.FromMinutes(minutes));

        var confirm = MessageBox.Show(
            $"Isso vai travar tudo que está listado por {pretty}. NÃO existe forma de cancelar pela interface antes do tempo acabar. Continuar?",
            "Confirmar bloqueio", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        // Um zero a mais no campo vira um bloqueio de dias, sem volta. Pede uma segunda confirmação
        // (com "Não" como padrão) quando passa de 24 horas.
        if (minutes > 24 * 60)
        {
            var longConfirm = MessageBox.Show(
                $"Atenção: {pretty} é mais de um dia inteiro, e não dá pra cancelar.\n\nÉ isso mesmo que você quer?",
                "Bloqueio muito longo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (longConfirm != MessageBoxResult.Yes) return;
        }

        _state.BlockedWebsites = sites;
        _state.BlockedKeywords = keywords;
        _state.BlockedProcesses = apps;
        _state.BlockedProcessPathKeywords = paths;
        _state.LockedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
        StateManager.Save(_state);
        EllenEventService.NoteBlockStarted(TimeSpan.FromMinutes(minutes)); // duração entra na fala de parabéns

        // Aplicar milhares de domínios no hosts demora; em segundo plano a janela não congela.
        var websites = _state.BlockedWebsites.Concat(_state.ImportedWebsites).ToList(); // digitados primeiro (ver MaxExpandedDomains)
        var subdomainPrefixes = _state.HostsSubdomainPrefixes;
        var processes = _state.BlockedProcesses.ToList();
        var pathKeywords = _state.BlockedProcessPathKeywords.ToList();

        string? error = null;
        SetBusy(true);
        ShowToast("Aplicando o bloqueio...");
        try
        {
            await Task.Run(() =>
            {
                HostsFileManager.ApplyBlockedSites(websites, subdomainPrefixes);
                HostsFileManager.Protect();
                ProcessBlocker.KillBlockedProcesses(processes, pathKeywords);
                NativeVpnGuard.DisconnectActiveVpnConnections("app"); // VPN nativa do Windows (auditoria U2)
                BrowserPolicyManager.ApplyRestrictions();
            });
            _restrictionsAppliedByApp = true;
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.LockButton_Click", ex);
            error = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }

        if (error != null)
        {
            MessageBox.Show(
                "O bloqueio foi marcado como ativo (o timer está rodando e não pode ser cancelado), mas houve um " +
                "erro ao aplicar o bloqueio de sites/apps:\n\n" + error + "\n\n" +
                "Detalhes completos em:\n" + Logger.LogFilePath + "\n\n" +
                "Isso pode ser causado por antivírus bloqueando alterações no arquivo hosts — confira o " +
                "Histórico de proteção em Windows Security.",
                "Erro ao aplicar bloqueio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            ShowToast($"Bloqueio iniciado por {pretty}. A Ellen assume daqui.");
        }

        UpdateUiForLockState();
    }

    // ---------- Proteções permanentes ----------

    private async void PermanentProtectionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            var installRoot = InstallRootPath;
            var extensionFolder = ExtensionFolderPath;

            if (!_state.PermanentProtectionsEnabled)
            {
                SetBusy(true);
                try
                {
                    await Task.Run(() => PermanentProtections.Enable(installRoot, extensionFolder));
                }
                finally
                {
                    SetBusy(false);
                }

                _state.PermanentProtectionsEnabled = true;
                _state.PermanentProtectionsOptOut = false;
                StateManager.Save(_state);
                MessageBox.Show(
                    "Ativado: SafeSearch, DoH desligado, política de instalação automática da extensão no " +
                    "Chrome/Edge/Brave e pasta protegida contra exclusão. Confira na aba Status se a extensão " +
                    "será aceita pelo navegador neste computador.\n\n" +
                    "Se o navegador já estava aberto, pode ser preciso fechar e abrir de novo (ou esperar alguns " +
                    "minutos) pra ele reler as políticas e a extensão aparecer em chrome://extensions.",
                    "Proteções permanentes ativadas", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // Trava de tela de 30 min + senha (se houver) + sequência de confirmações.
                if (!UninstallFlow.TryAuthorizeRemoval(this, "Desativar proteções permanentes"))
                {
                    return;
                }

                SetBusy(true);
                try
                {
                    await Task.Run(() => PermanentProtections.Disable(installRoot));
                }
                finally
                {
                    SetBusy(false);
                }

                _state.PermanentProtectionsEnabled = false;
                _state.PermanentProtectionsOptOut = true;
                StateManager.Save(_state);
            }

            UpdatePermanentButton();
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.PermanentProtectionsButton_Click", ex);
            MessageBox.Show($"Erro ao aplicar proteções permanentes: {ex.Message}\n\nDetalhes em:\n{Logger.LogFilePath}",
                "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyAppLockerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppLockerManager.IsSupported())
        {
            MessageBox.Show(
                "O AppLocker só existe no Windows 11 Pro, Enterprise ou Education — não está disponível " +
                "nesta edição do Windows (Home). Isso é uma limitação do Windows, não do app.",
                "Não suportado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pathKeywords = SplitLines(PathKeywordsBox.Text).Select(k => $"*{k}*");
        var ok = AppLockerManager.Apply(pathKeywords);
        MessageBox.Show(ok
            ? "Regras do AppLocker aplicadas. Pode levar um instante pro Windows aplicar de fato — " +
              "reinicie o computador se os apps ainda abrirem normalmente depois de alguns minutos."
            : $"Falha ao aplicar as regras. Detalhes em:\n{Logger.LogFilePath}",
            "AppLocker", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
    }

    // ---------- Senha / desinstalação ----------

    private void SetPasswordButton_Click(object sender, RoutedEventArgs e) => ChangeOrSetPassword();

    private bool _passwordSetupPrompting;

    /// <summary>
    /// SENHA OBRIGATÓRIA: enquanto não houver senha administrativa, pede para definir toda vez que a
    /// janela aparece. Se a pessoa cancelar, nada é liberado: sem senha, fechar o app, remover itens,
    /// desativar proteções e desinstalar são RECUSADOS (ver UninstallFlow).
    /// </summary>
    private void PromptPasswordSetupIfNeeded()
    {
        if (_passwordSetupPrompting || !IsVisible || StateManager.Load().HasPassword) return;
        _passwordSetupPrompting = true;
        try
        {
            MessageBox.Show(
                "O SharkBlocker exige uma senha administrativa. Ela será pedida para fechar o app, remover itens, " +
                "desativar proteções e desinstalar.\n\nDefina uma senha agora (mínimo 8 caracteres).",
                "Defina uma senha", MessageBoxButton.OK, MessageBoxImage.Information);

            if (!ChangeOrSetPassword())
            {
                MessageBox.Show(
                    "Sem senha definida, não é possível fechar o SharkBlocker, remover itens, desativar proteções nem desinstalar. " +
                    "Use o botão \"Definir senha\" quando quiser.",
                    "Senha pendente", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _passwordSetupPrompting = false; }
    }

    /// <returns>true se a senha foi definida/alterada.</returns>
    private bool ChangeOrSetPassword()
    {
        // CORRIGIDO: antes dava pra trocar a senha com o bloqueio ativo,
        // o que anulava a proteção do timer. Agora não dá mais.
        if (_state.IsLocked)
        {
            MessageBox.Show("Não é possível alterar a senha enquanto o bloqueio estiver ativo.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (StateManager.Load().HasPassword)
        {
            var remaining = PasswordGate.RemainingLockout();
            if (remaining > TimeSpan.Zero)
            {
                MessageBox.Show($"Muitas tentativas incorretas. Nova tentativa liberada em {PasswordGate.Describe(remaining)}.",
                    "Aguarde", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var current = AskPassword("Digite a senha atual:");
            if (current == null) return false;
            var check = PasswordGate.Verify(current, out var wait);
            if (check != PasswordCheck.Ok)
            {
                MessageBox.Show(check == PasswordCheck.LockedOut
                        ? $"Muitas tentativas incorretas. Nova tentativa liberada em {PasswordGate.Describe(wait)}."
                        : "Senha atual incorreta." + (wait > TimeSpan.Zero ? $"\nNova tentativa liberada em {PasswordGate.Describe(wait)}." : ""),
                    "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        var newPassword = AskPassword($"Digite a nova senha (mínimo {PasswordManager.MinLength} caracteres):");
        if (newPassword == null) return false; // cancelou
        var problem = PasswordManager.ValidateNewPassword(newPassword);
        if (problem != null)
        {
            MessageBox.Show(problem, "Senha fraca", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        var confirmPassword = AskPassword("Confirme a nova senha:");
        if (confirmPassword == null) return false; // cancelou
        if (newPassword != confirmPassword)
        {
            MessageBox.Show("As senhas não coincidem.", "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        _state = StateManager.Load();
        _state.PasswordHash = PasswordManager.Hash(newPassword);
        StateManager.Save(_state);
        MessageBox.Show("Senha definida.", "Pronto", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsLocked)
        {
            MessageBox.Show("Não é possível desinstalar enquanto o bloqueio estiver ativo. Espere o tempo acabar.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!UninstallFlow.TryPerformProtectedUninstall(this)) return;

        MessageBox.Show("Serviço removido, hosts/políticas/pasta restaurados.\n\n" +
            "Se instalou pelo Setup, vá em \"Adicionar ou Remover Programas\" e remova normalmente " +
            "(a checagem de senha já foi feita). Se copiou os arquivos manualmente, já pode apagar a pasta.",
            "Desinstalado", MessageBoxButton.OK, MessageBoxImage.Information);

        _reallyClosing = true;
        AppPresence.MarkCleanExit();
        _trayIcon?.Dispose();
        Application.Current.Shutdown();
    }

    private string? AskPassword(string prompt)
    {
        var dialog = new PasswordDialog(prompt) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.EnteredPassword : null;
    }

    // ---------- Bandeja do sistema ----------
    // O app em si é só um painel de controle — quem garante o bloqueio rodando
    // é o SERVIÇO do Windows (Watchdog), não esta janela. Ainda assim, fechar a
    // janela sem querer causava confusão ("parece que parou de funcionar").
    // Agora o "X" minimiza pra bandeja; só "Sair" pelo menu da bandeja fecha de
    // verdade (o serviço continua rodando de qualquer jeito, mesmo com o app
    // fechado por completo — isso aqui é só clareza de interface).

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "SharkBlocker · Ellen is watching"
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Abrir o SharkBlocker", null, (_, _) => ShowFromTray());
        menu.Items.Add("Preciso de uma pausa agora (trava 30 min)", null, (_, _) => StartUrgency());
        menu.Items.Add("Mostrar/esconder a frase na tela", null, (_, _) => DesktopQuoteWindow.Toggle());
        var silenceItem = new System.Windows.Forms.ToolStripMenuItem("Modo Silêncio da Ellen (1 hora)");
        silenceItem.Click += (_, _) => DesktopQuoteWindow.SetSilence(!EllenSilenceService.IsActive);
        menu.Items.Add(silenceItem);
        var compactItem = new System.Windows.Forms.ToolStripMenuItem("Widget compacto (só o avatar)");
        compactItem.Click += (_, _) => DesktopQuoteWindow.ToggleCompact();
        menu.Items.Add(compactItem);
        menu.Items.Add("Widget: voltar ao canto padrão", null, (_, _) => DesktopQuoteWindow.ResetPosition());
        // O texto acompanha o estado: durante o silêncio vira "Encerrar" com o tempo que falta.
        menu.Opening += (_, _) =>
        {
            silenceItem.Text = EllenSilenceService.Remaining is { } left
                ? $"Encerrar o Modo Silêncio (faltam {EllenChatterService.Describe(left)})"
                : "Modo Silêncio da Ellen (1 hora)";
            compactItem.Checked = DesktopQuoteWindow.IsCompact;
        };
        menu.Items.Add("Sair (trava a tela e pede senha)", null, (_, _) => RequestExit());
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    /// <summary>Ícone da bandeja = o tubarão do app (se der algo errado, cai no escudo padrão).</summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/sharkblocker.ico", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info != null)
            {
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.LoadTrayIcon", ex);
        }
        return System.Drawing.SystemIcons.Shield;
    }

    // CORRIGIDO: "Finalizar tarefa" no Gerenciador de Tarefas manda um WM_CLOSE para a janela. O app
    // tratava isso como o "X": escondia a janela e mostrava "foi para a bandeja" — sem travar a
    // tela — e logo depois o processo era morto sem ninguém reagir. Agora distinguimos:
    //  - "X" / Alt+F4 / menu do sistema chegam como WM_SYSCOMMAND(SC_CLOSE) ANTES do WM_CLOSE;
    //  - um WM_CLOSE "solto" (sem isso antes) veio de fora (Gerenciador de Tarefas, EndTask...).
    // ---- Barra de título própria: os botões chamam SystemCommands (mandam o mesmo WM_SYSCOMMAND
    // do botão nativo), então o "X" continua contando como fechamento do usuário (ver WindowHook).
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        // Janela sem moldura nativa estoura a borda da tela ao maximizar: compensa com margem.
        OuterGrid.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaximizeButton.Tag = maximized ? "\uE923" : "\uE922";
    }

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_CLOSE = 0xF060;
    private bool _userCloseRequested;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        (System.Windows.PresentationSource.FromVisual(this) as System.Windows.Interop.HwndSource)?.AddHook(WindowHook);
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && ((int)(wParam.ToInt64() & 0xFFF0)) == SC_CLOSE)
            _userCloseRequested = true;
        return IntPtr.Zero;
    }

    private static readonly string[] ProcessManagerNames =
        { "Taskmgr", "procexp", "procexp64", "ProcessHacker", "SystemInformer" };

    private static bool IsProcessManagerRunning()
    {
        try
        {
            foreach (var name in ProcessManagerNames)
            {
                var procs = Process.GetProcessesByName(name);
                var any = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (any) return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("MainWindow.IsProcessManagerRunning", ex);
            return true; // na dúvida, trata como tentativa de encerrar (lado seguro)
        }
        return false;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyClosing) return;
        e.Cancel = true;
        var fromUser = _userCloseRequested;
        _userCloseRequested = false;
        Hide();

        // Só é "encerramento forçado" se houver um gerenciador de processos aberto (quem manda o
        // WM_CLOSE do "Finalizar tarefa"). Outros WM_CLOSE (miniatura da barra de tarefas,
        // instalador fechando o app) só levam a janela para a bandeja, sem penalidade.
        if (!fromUser && IsProcessManagerRunning())
        {
            // Tentativa de encerrar o app por fora: trava a tela agora (o app continua vivo).
            // Se o processo for morto em seguida, o Watchdog o reabre e a trava continua valendo.
            Logger.Log("Pedido de fechamento externo (Gerenciador de Tarefas ou similar) — travando a tela por 30 min.");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    var until = StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, AppPresence.ReasonClosed);
                    if (ScreenLock.IsShowing) return;
                    var (headline, message) = ScreenLock.TextFor(StateManager.Load().ScreenLockReason);
                    ScreenLock.ShowUntil(until, headline, message);
                }
                catch (Exception ex)
                {
                    Logger.LogException("MainWindow.Window_Closing (trava por fechamento externo)", ex);
                }
            }), DispatcherPriority.Background);
            return;
        }

        // Avisa UMA vez que o programa continua na bandeja (senão parece que fechou).
        if (!_trayHintShown && _trayIcon != null)
        {
            _trayHintShown = true;
            try
            {
                _trayIcon.ShowBalloonTip(4000, "SharkBlocker continua de plantão",
                    "A janela foi para a bandeja. O bloqueio segue funcionando. Sair de vez trava a tela e pede senha.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { /* o aviso é só cortesia */ }
        }
    }

    // ---------- Timer ----------

    private void Timer_Tick(object? sender, EventArgs e)
    {
        try
        {
            AppPresence.Heartbeat();
            _state = StateManager.Load();

            // Bloqueio agendado (reserva, caso o Watchdog esteja fora): liga/estende o bloqueio se estamos numa janela.
            if (++_scheduleTicks >= 10)
            {
                _scheduleTicks = 0;
                if (BlockScheduler.EnsureActive()) _state = StateManager.Load();
            }

            // Ellen comemora quando um bloqueio termina por tempo (não quando é removido antes, com senha).
            if (EllenEventService.OnTick(_state, DateTime.UtcNow) is { } cheer)
                DesktopQuoteWindow.React(cheer);

            // Trava de tela pedida de fora (extensão reportou busca inapropriada via Watchdog).
            if (_state.IsScreenLockActive && !ScreenLock.IsShowing)
            {
                var (headline, message) = ScreenLock.TextFor(_state.ScreenLockReason);
                ScreenLock.ShowUntil(_state.ScreenLockUntilUtc!.Value, headline, message);
            }

            // Bloqueio AGENDADO em andamento: a tela fica travada até o fim do bloqueio (a trava refaz-se se cair).
            if (!ScreenLock.IsShowing && _state.IsLocked)
            {
                ScreenLock.ShowScheduledBlockIfDue(_state);
                _state = StateManager.Load();
            }

            if (_state.IsLocked)
            {
                KillBlockedProcessesInBackground();
            }
            else if (_state.LockedUntilUtc.HasValue || _restrictionsAppliedByApp)
            {
                HostsFileManager.Unprotect();
                HostsFileManager.ClearBlockedSites();
                if (_state.PermanentProtectionsEnabled)
                {
                    HostsFileManager.ApplySafeSearchEntries(SafeSearchDomains.GetHostsEntries());
                    HostsFileManager.Protect();
                }
                // U5: com "anônimo sempre bloqueado" (e proteções permanentes) o bloqueio do anônimo fica.
                if (!BrowserPolicyManager.IncognitoBlockedAlways(_state)) BrowserPolicyManager.RemoveRestrictions();
                _restrictionsAppliedByApp = false;
                _state.LockedUntilUtc = null;
                StateManager.Save(_state);
            }

            UpdateUiForLockState();
        }
        catch (Exception ex)
        {
            // Loga sem popup — isso roda a cada segundo, um popup por ciclo seria
            // insuportável. O log de diagnóstico continua registrando o problema.
            Logger.LogException("MainWindow.Timer_Tick", ex);
        }
    }

    /// <summary>
    /// CORRIGIDO: a varredura de processos rodava na thread da interface A CADA SEGUNDO
    /// (listar todos os processos e ler o caminho de cada um custa centenas de ms), o que
    /// deixava a janela travando durante o bloqueio. Agora roda em segundo plano, no máximo
    /// uma por vez e a cada ~3 s. O Watchdog (a cada 5 s) e o vigia em tempo real (WMI)
    /// continuam sendo a defesa principal.
    /// </summary>
    private void KillBlockedProcessesInBackground()
    {
        if ((DateTime.UtcNow - _lastKillUtc).TotalSeconds < 3) return;
        if (Interlocked.Exchange(ref _killRunning, 1) != 0) return;

        _lastKillUtc = DateTime.UtcNow;
        var processes = _state.BlockedProcesses.ToList();
        var pathKeywords = _state.BlockedProcessPathKeywords.ToList();

        _ = Task.Run(() =>
        {
            try
            {
                ProcessBlocker.KillBlockedProcesses(processes, pathKeywords);
                NativeVpnGuard.DisconnectActiveVpnConnections("app"); // VPN nativa do Windows (auditoria U2)
            }
            catch (Exception ex)
            {
                Logger.LogException("MainWindow.KillBlockedProcessesInBackground", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _killRunning, 0);
            }
        });
    }

    private void UpdateUiForLockState()
    {
        var locked = _state.IsLocked;
        var editable = !locked && !_busy;

        SitesBox.IsEnabled = !locked;
        KeywordsBox.IsEnabled = !locked;
        AppsBox.IsEnabled = !locked;
        PathKeywordsBox.IsEnabled = !locked;
        MinutesBox.IsEnabled = editable;
        LockButton.IsEnabled = editable;
        SaveButton.IsEnabled = editable;
        UninstallButton.IsEnabled = editable;
        ImportListButton.IsEnabled = editable;
        ImportListsButton.IsEnabled = editable;
        RemoveImportedButton.IsEnabled = editable;
        SuggestBrowsersButton.IsEnabled = editable;
        SuggestAdultKeywordsButton.IsEnabled = editable;
        SetPasswordButton.IsEnabled = editable;
        PermanentProtectionsButton.IsEnabled = !_busy;

        if (locked)
        {
            var until = _state.LockedUntilUtc!.Value;
            var remaining = until - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

            var untilLocal = until.ToLocalTime();
            var untilText = untilLocal.Date == DateTime.Today
                ? untilLocal.ToString("HH:mm:ss")
                : untilLocal.ToString("dd/MM HH:mm");

            // CORRIGIDO: o formato "hh" do TimeSpan mostra só as horas do dia (0–23) e esconde os
            // dias — um bloqueio de 2 dias aparecia como "00:..". Agora conta o total de horas.
            var countdown = $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";

            if (_trayIcon != null) _trayIcon.Text = $"SharkBlocker · bloqueado, faltam {countdown}";
            StatusIcon.Visibility = Visibility.Visible;
            StatusSharkIcon.Visibility = Visibility.Collapsed;
            StatusText.Text = $"BLOQUEADO até {untilText} (faltam {countdown}). Sem opção de cancelar.";
            StatusBanner.Background = (System.Windows.Media.Brush)FindResource("ColorPrimaryDark");
            StatusText.Foreground = System.Windows.Media.Brushes.White;
        }
        else
        {
            if (_trayIcon != null) _trayIcon.Text = "SharkBlocker · Ellen is watching";
            StatusIcon.Visibility = Visibility.Collapsed;
            StatusSharkIcon.Visibility = Visibility.Visible;
            StatusText.Text = _state.PermanentProtectionsEnabled
                ? "Sem bloqueio ativo. Proteções permanentes ligadas."
                : "Sem bloqueio ativo.";
            StatusBanner.Background = (System.Windows.Media.Brush)FindResource("ColorSurfaceAlt");
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("ColorTextPrimary");
        }
    }

    /// <summary>Lista o que estava salvo como bloqueado e SUMIU das listas novas (remoção ou troca).</summary>
    private List<string> FindRemovals(List<string> sites, List<string> keywords, List<string> apps, List<string> paths)
    {
        var removed = new List<string>();

        void Check(IEnumerable<string>? saved, IEnumerable<string> now, string label, Func<string, string> key)
        {
            var nowKeys = new HashSet<string>(now.Select(key), StringComparer.OrdinalIgnoreCase);
            foreach (var item in saved ?? Enumerable.Empty<string>())
            {
                if (!nowKeys.Contains(key(item))) removed.Add($"{label}: {item}");
            }
        }

        string Plain(string s) => s.Trim().ToLowerInvariant();
        Check(_state.BlockedWebsites, sites, "site", s => HostsFileManager.NormalizeDomain(s) ?? Plain(s));
        Check(_state.BlockedKeywords, keywords, "palavra", Plain);
        Check(_state.BlockedProcesses, apps, "programa", Plain);
        Check(_state.BlockedProcessPathKeywords, paths, "caminho", Plain);
        return removed;
    }

    /// <summary>
    /// Tentar REMOVER (ou trocar) algo que já está bloqueado dispara a trava de tela de 30 min e
    /// só prossegue com senha e confirmações. Se não passar, as caixas voltam ao que estava salvo.
    /// </summary>
    private bool EnsureRemovalAuthorized(List<string> sites, List<string> keywords, List<string> apps, List<string> paths)
    {
        var removed = FindRemovals(sites, keywords, apps, paths);
        if (removed.Count == 0) return true;

        var ok = UninstallFlow.TryAuthorizeRemoval(this, "Remover itens bloqueados", removed);
        if (!ok)
        {
            RestoreBoxesFromState();
            ShowToast("Remoção cancelada: nada foi removido.");
        }
        return ok;
    }

    private void RestoreBoxesFromState()
    {
        SitesBox.Text = string.Join(Environment.NewLine, _state.BlockedWebsites);
        KeywordsBox.Text = string.Join(Environment.NewLine, _state.BlockedKeywords);
        AppsBox.Text = string.Join(Environment.NewLine, _state.BlockedProcesses);
        PathKeywordsBox.Text = string.Join(Environment.NewLine, _state.BlockedProcessPathKeywords);
    }

    /// <summary>Separa o que é domínio válido (já normalizado e sem repetição) do que não é.</summary>
    private static (List<string> Valid, List<string> Invalid) NormalizeSites(string text)
    {
        var valid = new List<string>();
        var invalid = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in SplitLines(text))
        {
            var domain = HostsFileManager.NormalizeDomain(raw);
            if (domain == null) invalid.Add(raw);
            else if (seen.Add(domain)) valid.Add(domain);
        }
        return (valid, invalid);
    }

    private void UpdateCounts()
    {
        SitesHeader.Text = $"Sites bloqueados ({SplitLines(SitesBox.Text).Count})";
        KeywordsHeader.Text = $"Palavras-chave bloqueadas ({SplitLines(KeywordsBox.Text).Count})";
        AppsHeader.Text = $"Programas bloqueados ({SplitLines(AppsBox.Text).Count})";
    }

    private static List<string> SplitLines(string text) =>
        text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
}
