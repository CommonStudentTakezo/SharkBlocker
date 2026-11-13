using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using SharkBlocker.Models;

namespace SharkBlocker.Services;

/// <summary>
/// Expõe SOMENTE leitura, em http://127.0.0.1:57411/state, o estado atual de bloqueio,
/// para que a extensão de navegador (que não tem acesso ao sistema de arquivos)
/// possa saber quais palavras-chave e domínios bloquear, e se o lock está ativo.
///
/// Escuta apenas em loopback (127.0.0.1) — não é acessível pela rede/internet.
///
/// Dois endpoints:
///  - GET  /state  — leitura do estado. Quando a chamada traz "?ext=1" (é a nossa extensão),
///    grava um "sinal de vida" em disco; é isso que a aba Status mostra como "extensão ativa".
///  - POST /event  — a extensão avisa que bloqueou algo inapropriado; o app inicia a trava de
///    tela de 30 min. Só é aceito com o cabeçalho Origin exatamente igual ao da nossa extensão
///    (um site comum não consegue forjar o Origin pelo navegador, então uma página qualquer
///    não consegue disparar a trava).
/// </summary>
public class LocalStateServer : IDisposable
{
    private const string Prefix = "http://127.0.0.1:57411/";
    private readonly HttpListener _listener = new();
    private readonly Func<BlockState> _loadState;
    private CancellationTokenSource? _cts;

    public LocalStateServer(Func<BlockState> loadState)
    {
        _loadState = loadState;
        _listener.Prefixes.Add(Prefix);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = HandleRequestAsync(ctx);
            }
            catch when (token.IsCancellationRequested)
            {
                break;
            }
            catch { /* segue escutando mesmo se uma requisição falhar */ }
        }
    }

    private static readonly string HeartbeatFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SharkBlocker", "extension-heartbeat.txt");

    private static DateTime _lastHeartbeatWrite = DateTime.MinValue;

    /// <summary>Quando a extensão falou com o serviço pela última vez (null se nunca).</summary>
    public static DateTime? ReadLastExtensionContactUtc()
    {
        try
        {
            if (File.Exists(HeartbeatFile) && long.TryParse(AtomicFile.ReadAllText(HeartbeatFile).Trim(), out var ticks))
                return new DateTime(ticks, DateTimeKind.Utc);
        }
        catch { }
        return null;
    }

    private static void WriteHeartbeat()
    {
        if ((DateTime.UtcNow - _lastHeartbeatWrite).TotalSeconds < 5) return;
        _lastHeartbeatWrite = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HeartbeatFile)!);
            AtomicFile.WriteSmall(HeartbeatFile, DateTime.UtcNow.Ticks.ToString());
        }
        catch { }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        // Só a nossa extensão precisa ler isto. Com "*", qualquer site aberto no navegador
        // poderia consultar a lista de bloqueio. Navegadores Chromium mandam o Origin
        // "chrome-extension://<id>" nas chamadas feitas pelo service worker.
        var origin = ctx.Request.Headers["Origin"];
        if (origin == $"chrome-extension://{ExtensionInstaller.ExtensionId}")
        {
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", origin);
        }

        if (ctx.Request.Url?.AbsolutePath == "/event" && ctx.Request.HttpMethod == "POST")
        {
            HandleEvent(ctx, origin);
            ctx.Response.Close();
            return;
        }

        if (ctx.Request.Url?.AbsolutePath == "/state" && ctx.Request.HttpMethod == "GET")
        {
            if (ctx.Request.Url.Query.Contains("ext=1")) WriteHeartbeat();
            var state = _loadState();
            var payload = new
            {
                locked = state.IsLocked,
                lockedUntilUtc = state.LockedUntilUtc,
                blockedKeywords = state.BlockedKeywords,
                blockedWebsites = state.BlockedWebsites,
                permanentProtectionsEnabled = state.PermanentProtectionsEnabled,
                blockWebArchives = state.BlockWebArchives // U7
            };
            var json = JsonSerializer.Serialize(payload);
            var bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }
        else
        {
            ctx.Response.StatusCode = 404;
        }

        ctx.Response.Close();
    }

    private void HandleEvent(HttpListenerContext ctx, string? origin)
    {
        if (origin != $"chrome-extension://{ExtensionInstaller.ExtensionId}")
        {
            ctx.Response.StatusCode = 403;
            return;
        }

        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "inappropriate")
            {
                // Sem proteções permanentes E sem bloqueio ativo (desligadas de propósito, com
                // senha) = sem penalidade. Com bloqueio ativo a trava vale: é o que faz o termo
                // proibido da sua lista travar a tela.
                var currentState = _loadState();
                if (!currentState.PermanentProtectionsEnabled && !currentState.IsLocked)
                {
                    ctx.Response.StatusCode = 204;
                    return;
                }

                var until = StateManager.StartScreenLockIfNotActive(StateManager.ScreenLockPenalty, "search");
                Logger.Log($"Extensão reportou bloqueio com trava (adulto/proxy/termo proibido) — trava de tela até {until:o}.");
                ctx.Response.StatusCode = 202;
                return;
            }

            ctx.Response.StatusCode = 400;
        }
        catch (Exception ex)
        {
            Logger.LogException("LocalStateServer.HandleEvent", ex);
            ctx.Response.StatusCode = 500;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener.Stop();
        _listener.Close();
    }
}
