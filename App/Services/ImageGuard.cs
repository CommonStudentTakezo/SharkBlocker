using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SharkBlocker.Dialogs;

namespace SharkBlocker.Services;

/// <summary>
/// DETECTOR DE IMAGENS do PC (camada extra, além do filtro de endereço/texto). A cada ~2 s, com bloqueio ativo ou
/// proteções permanentes ligadas, fora de uma trava e com a opção ligada, captura cada tela, reduz a imagem e passa
/// pelo <see cref="NsfwClassifier"/> (modelo no PC). A decisão está em <see cref="ImageDecision"/> (rodada 2026-11-14,
/// para cortar falsos positivos):
///  - EXPLÍCITO (porn + hentai) acima do limite em 2 capturas seguidas: minimiza as janelas (Win+M) e TRAVA a tela;
///  - explícito muito alto (>= limite "imediato"): trava na hora, sem esperar confirmação;
///  - SUGESTIVO (biquíni/roupa sugestiva: explícito + sexy acima de um limite bem mais alto) em 3 capturas seguidas;
///  - captura suspeita NÃO minimiza nada: o app só refaz a varredura logo em seguida (~0,7 s) para confirmar;
///  - a trava é em escada: 1ª vez 5 min, 2ª 15 min, da 3ª em diante 30 min (motivo "image"; zera após 24 h sem detecções).
/// Limites por sensibilidade (explícito · sugestivo · imediato): Menos rígido 0,92·0,97·0,98 · Padrão 0,85·0,93·0,96 ·
/// Mais rígido 0,70·0,85·0,92 (ver <see cref="ImageDecision.Limits"/>).
///
/// LIMITES HONESTOS
///  - Precisa do modelo (Assets\nsfw\model.onnx). Sem ele, "Sem modelo" e nada é vigiado.
///  - Janelas com proteção contra captura (alguns players/DRM) saem pretas: o detector não vê nada nelas.
///  - A imagem pode ficar visível 2 a 4 s antes da trava (confirmação em capturas seguidas).
///  - A classe "sexy" do modelo também reage a rosto em close e a muito tom de pele; por isso o sugestivo tem limite alto e
///    exige 3 capturas. Isso reduz, mas não elimina, falsos positivos (e deixa passar biquíni de pontuação mais baixa).
///  - Modelos treinados em fotos reais erram em imagens geradas por IA e em ilustração/anime; há falsos positivos.
///  - Não fecha a janela (ao contrário do vigia de navegador): só mostra a área de trabalho. Se a imagem voltar depois
///    da trava, ela pode travar de novo.
/// Privacidade: a captura é lida na memória, reduzida e descartada; só NÚMEROS (pontuação por classe) vão para o log.
/// Roda no App (sessão do usuário). O Watchdog não vê a área de trabalho e NÃO compila este arquivo.
/// </summary>
public static class ImageGuard
{
    private const int PollMilliseconds = 2000;
    /// <summary>Intervalo da varredura de CONFIRMAÇÃO (depois de uma captura suspeita): confirma rápido sem minimizar nada.</summary>
    private const int ConfirmMilliseconds = 700;
    private const int GraceSeconds = 45;
    /// <summary>Capturas abaixo do limite, mas com pontuação alta, vão para o log (no máx. 1 a cada 10 s) para calibrar.</summary>
    private const float CalibrationLogFloor = 0.50f;
    private static readonly TimeSpan CalibrationLogEvery = TimeSpan.FromSeconds(10);

    /// <summary>Quantas capturas seguidas (contando a última) ficaram em nível acima de Clean. Só a thread do ImageGuard mexe.</summary>
    private static int _streak;
    private static DateTime _lastCalibrationLogUtc = DateTime.MinValue;

    private static readonly object StartGate = new();
    private static Thread? _thread;
    private static volatile bool _stop;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_LWIN = 0x5B;
    private const byte VK_M = 0x4D;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void Start()
    {
        lock (StartGate)
        {
            if (_thread != null) return;
            _stop = false;
            _thread = new Thread(Loop) { IsBackground = true, Name = "ImageGuard" };
            _thread.Start();
            // Carrega o modelo FORA da thread da interface (uma falha no ONNX nunca pode impedir o app de abrir).
            ThreadPool.QueueUserWorkItem(_ => { try { NsfwClassifier.Init(); } catch (Exception ex) { Logger.LogException("ImageGuard.Init", ex); } });
        }
    }

    public static void Stop() => _stop = true;

    /// <summary>Texto de estado para a tela e o diagnóstico.</summary>
    public static string StatusText()
    {
        return NsfwClassifier.CurrentStatus switch
        {
            NsfwClassifier.Status.Ready => "Modelo carregado e pronto.",
            NsfwClassifier.Status.NoModel => "Sem modelo: " + NsfwClassifier.Detail,
            NsfwClassifier.Status.BadModel => "Modelo incompatível: " + NsfwClassifier.Detail,
            _ => "Ainda não verificado.",
        };
    }

    private static void Loop()
    {
        Logger.Log("ImageGuard iniciado (detector de imagens; só age com a opção ligada e o modelo presente).");
        var errorLogs = 0;
        while (!_stop)
        {
            var pending = false;
            try
            {
                pending = Tick();
            }
            catch (Exception ex)
            {
                _streak = 0;
                if (errorLogs++ < 5) Logger.LogException("ImageGuard.Tick", ex);
            }
            // Com uma captura suspeita aguardando confirmação, refaz a varredura logo (sem minimizar nada nesse meio tempo).
            Thread.Sleep(pending ? ConfirmMilliseconds : PollMilliseconds);
        }
    }

    /// <summary>Uma varredura. Devolve true se há uma captura suspeita aguardando confirmação (o laço então varre mais rápido).</summary>
    private static bool Tick()
    {
        var state = StateManager.Load();
        if (!state.ImageDetectorEnabled) return Skip();
        if (!state.PermanentProtectionsEnabled && !state.IsLocked) return Skip();
        if (state.IsScreenLockActive || ScreenLock.IsShowing) return Skip();

        NsfwClassifier.Init();
        if (NsfwClassifier.CurrentStatus != NsfwClassifier.Status.Ready) return Skip();

        // Nunca olha o próprio SharkBlocker (telas de trava, janelas do app).
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if ((int)pid == Environment.ProcessId) return Skip();
        }

        // Não olha a área de trabalho/barra de tarefas (o papel de parede nunca pode virar laço de travas).
        if (hwnd != IntPtr.Zero)
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            var c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return Skip();
        }

        var limits = ImageDecision.Limits(state.ImageSensitivity);

        // Vale o monitor de nível mais alto (em empate, o de maior pontuação total).
        ImageScores? worst = null;
        var level = ImageLevel.Clean;
        foreach (var screen in Screen.AllScreens)
        {
            var scores = ScoreScreen(screen.Bounds);
            if (scores == null) continue;
            var l = ImageDecision.Evaluate(scores, limits);
            if (worst == null || l > level || (l == level && scores.Total > worst.Total))
            {
                worst = scores;
                level = l;
            }
        }

        if (worst == null) return Skip();

        if (level == ImageLevel.Clean)
        {
            _streak = 0;
            LogCalibrationIfHigh(worst, limits);
            return false;
        }

        _streak++;
        var required = ImageDecision.ScansRequired(level);
        if (!ImageDecision.Confirmed(_streak, level))
        {
            Logger.Log($"Imagem: suspeita {level} ({_streak}/{required}) explícito {worst.Explicit:0.00} sugestivo {worst.Sexy:0.00} | {ImageDecision.Describe(worst)}");
            return true;
        }

        Logger.Log($"Imagem: CONFIRMADA {level} ({_streak} capturas) explícito {worst.Explicit:0.00} sugestivo {worst.Sexy:0.00} (limites {limits.Explicit:0.00}/{limits.Combined:0.00}/{limits.Immediate:0.00}) | {ImageDecision.Describe(worst)}");
        _streak = 0;
        // CARÊNCIA: nos 45 s depois de uma trava acabar, só minimiza (não trava de novo), para dar tempo de fechar
        // o que ficou aberto. Sem isso, uma imagem que ficou na tela travaria em laço de 30 em 30 min.
        var grace = state.ScreenLockUntilUtc.HasValue && DateTime.UtcNow < state.ScreenLockUntilUtc.Value.AddSeconds(GraceSeconds);
        OnHit(grace);
        return false;
    }

    /// <summary>Saída antecipada da varredura (opção desligada, trava ativa, foco no próprio app...): a confirmação em andamento se perde.</summary>
    private static bool Skip()
    {
        _streak = 0;
        return false;
    }

    /// <summary>
    /// Registra (no máx. 1 vez a cada 10 s) capturas que NÃO travaram mas tiveram pontuação alta, só com números. Serve para
    /// calibrar os limites com casos reais (ex.: um vídeo com close de rosto) sem precisar de imagem.
    /// </summary>
    private static void LogCalibrationIfHigh(ImageScores s, ImageLimits limits)
    {
        if (s.Total < CalibrationLogFloor) return;
        var now = DateTime.UtcNow;
        if (now - _lastCalibrationLogUtc < CalibrationLogEvery) return;
        _lastCalibrationLogUtc = now;
        Logger.Log($"Imagem: abaixo do limite, explícito {s.Explicit:0.00} sugestivo {s.Sexy:0.00} (limites {limits.Explicit:0.00}/{limits.Combined:0.00}/{limits.Immediate:0.00}) | {ImageDecision.Describe(s)}");
    }

    private static ImageScores? ScoreScreen(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        try
        {
            using var bmp = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            }
            return NsfwClassifier.Score(bmp);
        }
        catch
        {
            // Tela segura (UAC, bloqueio do Windows) ou captura recusada: segue sem travar.
            return null;
        }
    }

    private static void OnHit(bool grace)
    {
        // Detecção CONFIRMADA (ver Tick) minimiza as janelas e TRAVA (escada 5 → 15 → 30 min, ver
        // StateManager.StartImageScreenLock). Na carência (logo depois de uma trava acabar) só minimiza.
        ShowDesktop();
        if (grace) return;

        try
        {
            StateManager.StartImageScreenLock(ScreenLock.ReasonImage);
        }
        catch (Exception ex)
        {
            Logger.LogException("ImageGuard.OnHit (trava)", ex);
        }
    }

    /// <summary>Win+M (minimizar tudo; NÃO alterna como o Win+D): tira a imagem da tela sem fechar nada.</summary>
    private static void ShowDesktop()
    {
        try
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_M, 0, 0, UIntPtr.Zero);
            keybd_event(VK_M, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            Logger.LogException("ImageGuard.ShowDesktop", ex);
        }
    }
}
