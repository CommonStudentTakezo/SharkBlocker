using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SharkBlocker.Services;

/// <summary>
/// Sincronização "oficial" (primitivas nativas do Windows, não arquivos) entre os três papéis do
/// SharkBlocker: o App (SharkBlocker.exe), o Watchdog (serviço do Windows) e o "Guard" — a vigília
/// que o próprio App mantém sobre o Watchdog (ver <see cref="ProcessGuardian"/>). Dois mecanismos:
///
///  1) MUTEX NOMEADO GLOBAL ("Global\..." — visível entre a sessão 0, onde o serviço roda, e a
///     sessão do usuário, onde o App roda). Cada lado SEGURA um mutex só seu enquanto está de pé;
///     nunca libera "na mão" fora da saída. O outro lado pergunta "ele está vivo?" com
///     <see cref="Mutex.OpenExisting(string)"/>: o Windows mantém o objeto do mutex vivo só enquanto
///     alguém tem um handle aberto pra ele — quando o processo dono morre (de qualquer jeito,
///     inclusive "Finalizar Tarefa"), o handle fecha sozinho e o objeto desaparece. OpenExisting
///     então falha, e sabemos que o processo morreu, sem precisar enumerar processos
///     (Process.GetProcessesByName) nem rodar um "sc query"/"tasklist" a cada verificação — bem
///     mais barato e instantâneo.
///
///  2) NAMED PIPE local (\\.\pipe\SharkBlocker_WatchdogStatus), servido só pelo Watchdog. Cobre o
///     que o mutex sozinho não cobre: o processo pode estar DE PÉ mas TRAVADO (laço infinito,
///     deadlock) — o mutex continuaria "vivo" porque o handle ainda está aberto, mas o pipe não
///     responde dentro do tempo limite. Protocolo é um PING/PONG textual bem simples, de propósito
///     (sem dependências externas, fácil de auditar).
/// </summary>
public static class ProcessSync
{
    private const string WatchdogMutexName = "Global\\SharkBlocker_WatchdogAlive";
    private const string AppMutexName = "Global\\SharkBlocker_AppAlive";
    internal const string StatusPipeName = "SharkBlocker_WatchdogStatus";

    private static Mutex? _watchdogHeld;
    private static Mutex? _appHeld;

    // ---------------------------------------------------------------- Mutex: "eu estou vivo" ----

    /// <summary>Chamado UMA vez pelo Watchdog ao iniciar. O mutex fica aberto até o processo sair.</summary>
    public static void HoldWatchdogAlive() => _watchdogHeld = Hold(WatchdogMutexName);

    /// <summary>Chamado UMA vez pelo App (processo principal, já garantido único por Outro mutex de instância única).</summary>
    public static void HoldAppAlive() => _appHeld = Hold(AppMutexName);

    private static Mutex Hold(string name)
    {
        // DACL explícita: o App roda como o usuário logado e o Watchdog roda como SYSTEM na sessão
        // 0 — sem conceder acesso a "todos", um lado não consegue nem abrir o mutex do outro pra
        // checar presença (o padrão do .NET só dá acesso total ao criador).
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            MutexRights.FullControl, AccessControlType.Allow));

        var mutex = MutexAcl.Create(true, name, out _, security);
        return mutex;
    }

    /// <summary>Libera o mutex do App de propósito (saída normal). Não é obrigatório chamar — o Windows fecha o handle sozinho quando o processo sai — mas deixa o objeto "limpo" um instante antes.</summary>
    public static void ReleaseAppAlive()
    {
        try { _appHeld?.ReleaseMutex(); } catch { /* nada a fazer se já não é dono */ }
        try { _appHeld?.Dispose(); } catch { }
        _appHeld = null;
    }

    /// <summary>Equivalente ao de cima, chamado pelo Watchdog em StopAsync (parada controlada do serviço).</summary>
    public static void ReleaseWatchdogAlive()
    {
        try { _watchdogHeld?.ReleaseMutex(); } catch { /* nada a fazer se já não é dono */ }
        try { _watchdogHeld?.Dispose(); } catch { }
        _watchdogHeld = null;
    }

    public static bool IsWatchdogAlive() => Exists(WatchdogMutexName);

    private static bool Exists(string name)
    {
        try
        {
            using var m = Mutex.OpenExisting(name);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false; // ninguém segura esse mutex agora: o dono morreu (ou nunca abriu)
        }
        catch (Exception ex)
        {
            Logger.LogException("ProcessSync.Exists(" + name + ")", ex);
            return false; // em dúvida, trata como "não confirmado vivo" — mais seguro
        }
    }

    // ---------------------------------------------------------- Named Pipe: status do Watchdog ----

    private static CancellationTokenSource? _pipeCts;
    private static Func<string>? _statusProvider;

    /// <summary>Servidor do pipe de status — chamado só pelo Watchdog. <paramref name="statusJsonProvider"/> é consultado a cada PING recebido.</summary>
    public static void StartStatusPipeServer(Func<string> statusJsonProvider)
    {
        _statusProvider = statusJsonProvider;
        _pipeCts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoopAsync(_pipeCts.Token));
    }

    public static void StopStatusPipeServer()
    {
        try { _pipeCts?.Cancel(); } catch { }
    }

    // ---- criação do pipe com DACL "Everyone" via P/Invoke direto -------------------------------
    //
    // Por que P/Invoke e não o pacote NuGet System.IO.Pipes.AccessControl: ele parou de publicar
    // versões estáveis depois da 5.0.0 (o restore falha pedindo >= 8.0.0 com NU1102). Chamar
    // CreateNamedPipe diretamente evita essa dependência frágil — é mais código, mas só usa APIs
    // do Win32 que não têm versão (advapi32.dll/kernel32.dll já estão em toda instalação do
    // Windows) e o resultado final (um SafePipeHandle válido) é entregue a um NamedPipeServerStream
    // normal, então o resto do código (ler/escrever/Dispose) não muda nada.

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadmodeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeUnlimitedInstances = 255;
    private const int SddlRevision1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor, int stringSDRevision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    // Retorna IntPtr (não SafePipeHandle direto): a marshalação automática de SafeHandle como
    // retorno de P/Invoke exige um construtor sem parâmetros que SafePipeHandle não tem — então o
    // handle cru é embrulhado manualmente logo abaixo, em CreateServerStream.
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateNamedPipeW(
        string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeOut, ref SECURITY_ATTRIBUTES securityAttributes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static NamedPipeServerStream CreateServerStream()
    {
        // "D:(A;;GA;;;WD)" = DACL com uma única regra: Allow, Generic All, para WD (Everyone/World).
        // É o mesmo acesso que PipeAccessRights.ReadWrite + AccessControlType.Allow dava antes, só
        // que escrito como SDDL em vez de objetos MutexAccessRule/PipeAccessRule.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:(A;;GA;;;WD)", SddlRevision1, out var sd, IntPtr.Zero))
            throw new IOException("ConvertStringSecurityDescriptorToSecurityDescriptor falhou: " + Marshal.GetLastWin32Error());

        try
        {
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd, bInheritHandle = 0 };
            var rawHandle = CreateNamedPipeW(
                @"\\.\pipe\" + StatusPipeName,
                PipeAccessDuplex | FileFlagOverlapped,
                PipeTypeByte | PipeReadmodeByte | PipeWait,
                PipeUnlimitedInstances,
                outBufferSize: 4096, inBufferSize: 4096, defaultTimeOut: 0,
                securityAttributes: ref sa);

            if (rawHandle == new IntPtr(-1)) // INVALID_HANDLE_VALUE
                throw new IOException("CreateNamedPipe falhou: " + Marshal.GetLastWin32Error());

            var handle = new SafePipeHandle(rawHandle, ownsHandle: true);
            return new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, handle);
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    private static async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream();
                await server.WaitForConnectionAsync(ct);
                var accepted = server;
                server = null; // dono passa a ser a tarefa do cliente
                _ = HandleClientAsync(accepted, ct); // não bloqueia o próximo accept
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                Logger.LogException("ProcessSync.AcceptLoopAsync", ex);
                server?.Dispose();
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private static async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using (server)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3)); // nunca deixa um cliente travado segurando a instância do pipe

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(cts.Token);
                if (line == "PING")
                {
                    await writer.WriteLineAsync((_statusProvider?.Invoke() ?? "{}").AsMemory(), cts.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // cliente sumiu/não respeitou o protocolo: ignora, o servidor segue aceitando outros
            }
            catch (Exception ex)
            {
                Logger.LogException("ProcessSync.HandleClientAsync", ex);
            }
        }
    }

    /// <summary>Cliente do pipe de status — usado pelo App/Guard. Retorna null se o Watchdog não respondeu a tempo (tratado como "não confirmado vivo").</summary>
    public static async Task<string?> PingWatchdogAsync(int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", StatusPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(cts.Token);

            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);

            await writer.WriteLineAsync("PING".AsMemory(), cts.Token);
            return await reader.ReadLineAsync(cts.Token);
        }
        catch
        {
            return null; // pipe indisponível/timeout = mesmo tratamento: "Watchdog não confirmado"
        }
    }
}
