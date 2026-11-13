using System.IO;

namespace SharkBlocker.Services;

public static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SharkBlocker", "app.log");

    public static string LogFilePath => LogPath;

    // Identifica qual processo está logando (App.exe ou Watchdog.exe) e o PID,
    // pra dar pra distinguir no log quem fez o quê quando os dois rodam juntos.
    private static readonly string ProcessTag =
        $"{System.Diagnostics.Process.GetCurrentProcess().ProcessName}:{Environment.ProcessId}";

    // CORRIGIDO: o log crescia sem limite (o Watchdog escreve a cada 5 s durante um bloqueio).
    // Passando de 2 MB, o arquivo atual vira app.log.1 (substituindo o anterior) e recomeça.
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes)
                File.Move(LogPath, LogPath + ".1", overwrite: true);
        }
        catch { /* outro processo (App/Watchdog) pode estar girando ao mesmo tempo */ }
    }

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                DataDirectorySecurity.Ensure(Path.GetDirectoryName(LogPath)!);
                RotateIfNeeded();
                // App e Watchdog logam no MESMO arquivo ao mesmo tempo: abre em modo Append com
                // FileShare.ReadWrite | Delete (o File.AppendAllText usava FileShare.Read, e a segunda
                // escrita simultanea falhava e se perdia; sem Delete a rotacao tambem era bloqueada).
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{ProcessTag}] {message}{Environment.NewLine}";
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        fs.Write(bytes, 0, bytes.Length);
                        break;
                    }
                    catch (IOException) when (attempt < 4) { Thread.Sleep(20 * attempt); }
                }
            }
        }
        catch { }
    }

    public static void LogException(string context, Exception ex)
    {
        var msg = $"ERRO em {context}: {ex.GetType().Name}: {ex.Message}";
        // Registra também as exceções internas (ex.: TypeInitializationException esconde o motivo real).
        var inner = ex.InnerException;
        for (var depth = 0; inner != null && depth < 4; depth++, inner = inner.InnerException)
            msg += $" <= {inner.GetType().Name}: {inner.Message}";
        Log(msg);
    }
}
