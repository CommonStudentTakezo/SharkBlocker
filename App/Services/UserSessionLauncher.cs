using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SharkBlocker.Services;

/// <summary>
/// Abre um programa na sessão do usuário que está na frente do PC, a partir de um serviço (SYSTEM,
/// sessão 0). É o plano B do Watchdog para reabrir o app quando o Agendador de Tarefas falha.
/// Usa o token ELEVADO do usuário (o app exige administrador), sem prompt de UAC.
/// </summary>
public static class UserSessionLauncher
{
    private const int TokenLinkedToken = 19;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State; // WTS_CONNECTSTATE_CLASS: 0 = Active
    }

    private const int WtsActive = 0;
    public const int ErrorElevationRequired = 740; // a conta não tem token elevado (usuário padrão)
    public const int ErrorNoToken = 1008;          // nenhuma conta logada nessa sessão (tela de login)

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? application, string commandLine,
        IntPtr procAttrs, IntPtr threadAttrs, bool inheritHandles, uint flags, IntPtr env, string? currentDir,
        ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Sessões de usuário em estado "Ativa" (console, troca rápida de usuário, Área de Trabalho Remota),
    /// sem a sessão 0 (serviços). A sessão da tela de login também aparece como ativa; nela o
    /// WTSQueryUserToken falha com <see cref="ErrorNoToken"/> e quem chama trata isso em silêncio.
    /// Devolve lista vazia se a enumeração falhar (quem chama usa o caminho antigo, só o console).
    /// </summary>
    public static List<uint> GetActiveUserSessions()
    {
        var result = new List<uint>();
        var buffer = IntPtr.Zero;
        try
        {
            // WTS_CURRENT_SERVER_HANDLE = NULL; versão do pedido = 1.
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out buffer, out var count))
            {
                Logger.Log($"UserSessionLauncher: WTSEnumerateSessions falhou (erro {Marshal.GetLastWin32Error()}).");
                return result;
            }

            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(buffer, i * size));
                if (info.State == WtsActive && info.SessionId != 0) result.Add(info.SessionId);
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("UserSessionLauncher.GetActiveUserSessions", ex);
            result.Clear();
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
        return result;
    }

    /// <summary>Abre o programa na sessão do console (a de quem está na frente do PC).</summary>
    public static bool Launch(string exePath, string arguments)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == 0xFFFFFFFF)
        {
            Logger.Log("UserSessionLauncher: nenhuma sessão de usuário ativa no console.");
            return false;
        }
        return LaunchInSession(session, exePath, arguments, out _);
    }

    /// <summary>
    /// Abre o programa na sessão indicada, com o token do usuário daquela sessão (elevado, se a conta
    /// for administradora). <paramref name="win32Error"/> traz o código do Windows em caso de falha:
    /// 740 = conta sem elevação (usuário padrão; o app exige administrador), 1008 = ninguém logado.
    /// </summary>
    public static bool LaunchInSession(uint session, string exePath, string arguments, out int win32Error)
    {
        win32Error = 0;
        IntPtr userToken = IntPtr.Zero, linked = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(session, out userToken))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken");

            // Admin com UAC: o token da sessão é o "limitado"; o elevado vem como token vinculado.
            var tokenToUse = userToken;
            if (GetTokenInformation(userToken, TokenLinkedToken, out linked, IntPtr.Size, out _) && linked != IntPtr.Zero)
                tokenToUse = linked;

            if (!DuplicateTokenEx(tokenToUse, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");

            if (!CreateEnvironmentBlock(out env, primary, false)) env = IntPtr.Zero;

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "winsta0\\default" };
            var cmd = $"\"{exePath}\" {arguments}";
            var dir = System.IO.Path.GetDirectoryName(exePath);

            if (!CreateProcessAsUser(primary, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, env, dir, ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");

            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            Logger.Log($"UserSessionLauncher: app aberto direto na sessão {session} (PID {pi.dwProcessId}).");
            return true;
        }
        catch (Win32Exception wex)
        {
            win32Error = wex.NativeErrorCode;
            // Esperados e frequentes: sem stack trace (o chamador espaça as tentativas).
            if (win32Error == ErrorNoToken)
                Logger.Log($"UserSessionLauncher: sessão {session} sem usuário logado (tela de login).");
            else if (win32Error == ErrorElevationRequired)
                Logger.Log($"UserSessionLauncher: sessão {session} — a conta não tem privilégio de administrador e o app exige elevação (erro 740).");
            else
                Logger.LogException("UserSessionLauncher.LaunchInSession", wex);
            return false;
        }
        catch (Exception ex)
        {
            win32Error = -1;
            Logger.LogException("UserSessionLauncher.LaunchInSession", ex);
            return false;
        }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (linked != IntPtr.Zero) CloseHandle(linked);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }
}
