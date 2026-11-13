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
/// pelo <see cref="NsfwClassifier"/> (modelo no PC). Regras (iguais às do Android):
///  - qualquer pontuação acima do limite: minimiza as janelas (Win+M) e TRAVA a tela na hora, em escada: 1ª vez 5 min,
///    2ª 15 min, da 3ª em diante 30 min (motivo "image"; a contagem zera após 24 h sem detecções);
///  - varredura a cada 2 s: a trava vem ~2 a 3 s depois de a imagem aparecer.
/// Limites (sensibilidade): Cautelosa 0,90 · Padrão 0,80 · Sensível 0,65.
///
/// LIMITES HONESTOS
///  - Precisa do modelo (Assets\nsfw\model.onnx). Sem ele, "Sem modelo" e nada é vigiado.
///  - Janelas com proteção contra captura (alguns players/DRM) saem pretas: o detector não vê nada nelas.
///  - A imagem pode ficar visível 1 a 3 s antes da trava.
///  - Modelos treinados em fotos reais erram em imagens geradas por IA e em ilustração/anime; há falsos positivos.
///  - Não fecha a janela (ao contrário do vigia de navegador): só mostra a área de trabalho. Se a imagem voltar depois
///    da trava, ela pode travar de novo.
/// Privacidade: a captura é lida na memória, reduzida e descartada; só a PONTUAÇÃO vai para o log.
/// Roda no App (sessão do usuário). O Watchdog não vê a área de trabalho e NÃO compila este arquivo.
/// </summary>
public static class ImageGuard
{
    private const int PollMilliseconds = 2000;
    private const int GraceSeconds = 45;

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

    public static float Threshold(int sensitivity) => sensitivity switch
    {
        1 => 0.90f,
        3 => 0.65f,
        _ => 0.80f,
    };

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
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                if (errorLogs++ < 5) Logger.LogException("ImageGuard.Tick", ex);
            }
            Thread.Sleep(PollMilliseconds);
        }
    }

    private static void Tick()
    {
        var state = StateManager.Load();
        if (!state.ImageDetectorEnabled) return;
        if (!state.PermanentProtectionsEnabled && !state.IsLocked) return;
        if (state.IsScreenLockActive || ScreenLock.IsShowing) return;

        NsfwClassifier.Init();
        if (NsfwClassifier.CurrentStatus != NsfwClassifier.Status.Ready) return;

        // Nunca olha o próprio SharkBlocker (telas de trava, janelas do app).
        var hwnd = GetForegroundWindow();
        if (hwnd != IntPtr.Zero)
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if ((int)pid == Environment.ProcessId) return;
        }

        // Não olha a área de trabalho/barra de tarefas (o papel de parede nunca pode virar laço de travas).
        if (hwnd != IntPtr.Zero)
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            var c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return;
        }

        var best = 0f;
        foreach (var screen in Screen.AllScreens)
        {
            var score = ScoreScreen(screen.Bounds);
            if (score.HasValue && score.Value > best) best = score.Value;
        }

        var thr = Threshold(state.ImageSensitivity);
        if (best >= thr)
        {
            Logger.Log($"Imagem: pontuação {best:0.00} (limite {thr:0.00})");
            // CARÊNCIA: nos 45 s depois de uma trava acabar, só minimiza (não trava de novo), para dar tempo de fechar
            // o que ficou aberto. Sem isso, uma imagem que ficou na tela travaria em laço de 30 em 30 min.
            var grace = state.ScreenLockUntilUtc.HasValue && DateTime.UtcNow < state.ScreenLockUntilUtc.Value.AddSeconds(GraceSeconds);
            OnHit(best, grace);
        }
    }

    private static float? ScoreScreen(Rectangle bounds)
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

    private static void OnHit(float score, bool grace)
    {
        // Qualquer detecção acima do limite minimiza as janelas e TRAVA (escada 5 → 15 → 30 min, ver
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
