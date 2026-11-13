using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SharkBlocker.Services;

/// <summary>
/// Backlog #9 — parte PURA do filtro de DNS local (sem rede, testável): ler a pergunta de um pacote DNS,
/// montar a resposta NXDOMAIN e decidir se um nome é bloqueado.
/// </summary>
public static class DnsFilterCore
{
    /// <summary>
    /// Nomes que SEMPRE recebem NXDOMAIN: sinais oficiais para desligar DNS-sobre-HTTPS automático do Firefox
    /// (use-application-dns.net) e o Relay Privado do iCloud (mask*.icloud.com).
    /// </summary>
    public static readonly string[] CanaryDomains =
    {
        "use-application-dns.net", "mask.icloud.com", "mask-h2.icloud.com"
    };

    /// <summary>
    /// Lê a 1ª pergunta de uma CONSULTA DNS. false se o pacote for curto, for uma resposta (QR=1), não tiver
    /// pergunta ou usar compressão de nome na pergunta (inválido). <paramref name="questionEnd"/> = posição logo após
    /// o QCLASS (fim da seção de pergunta).
    /// </summary>
    public static bool TryParseQuestion(byte[] packet, int length, out string name, out int questionEnd)
    {
        name = "";
        questionEnd = 0;
        if (packet == null || length < 17 || length > packet.Length) return false; // 12 (cabeçalho) + 1 (raiz) + 4
        if ((packet[2] & 0x80) != 0) return false;                                  // é resposta, não consulta
        var qdCount = (packet[4] << 8) | packet[5];
        if (qdCount < 1) return false;

        var sb = new StringBuilder();
        var pos = 12;
        while (true)
        {
            if (pos >= length) return false;
            int len = packet[pos++];
            if (len == 0) break;
            if ((len & 0xC0) != 0) return false;   // ponteiro de compressão / rótulo estendido: não vale na pergunta
            if (pos + len > length) return false;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(packet, pos, len));
            pos += len;
            if (sb.Length > 253) return false;
        }
        if (pos + 4 > length) return false;

        name = sb.ToString().ToLowerInvariant();
        questionEnd = pos + 4;
        return true;
    }

    /// <summary>Resposta NXDOMAIN (RCODE 3) para a consulta: mesmo ID, só a 1ª pergunta, sem respostas/EDNS.</summary>
    public static byte[] BuildNxDomain(byte[] query, int questionEnd)
    {
        var resp = new byte[questionEnd];
        Array.Copy(query, resp, questionEnd);
        resp[2] = (byte)(0x80 | (query[2] & 0x79)); // QR=1; mantém opcode e RD; AA e TC zerados
        resp[3] = 0x83;                              // RA=1, RCODE=3 (NXDOMAIN)
        resp[4] = 0; resp[5] = 1;                    // QDCOUNT = 1
        resp[6] = 0; resp[7] = 0;                    // ANCOUNT
        resp[8] = 0; resp[9] = 0;                    // NSCOUNT
        resp[10] = 0; resp[11] = 0;                  // ARCOUNT
        return resp;
    }

    /// <summary>Consulta DNS (tipo A, classe IN) para o nome, usada no autoteste.</summary>
    public static byte[] BuildQuery(string name, ushort id = 0x5342)
    {
        var ms = new MemoryStream();
        ms.WriteByte((byte)(id >> 8)); ms.WriteByte((byte)id);
        ms.WriteByte(0x01); ms.WriteByte(0x00);              // RD
        ms.WriteByte(0); ms.WriteByte(1);                    // QDCOUNT
        for (var i = 0; i < 6; i++) ms.WriteByte(0);         // AN, NS, AR
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.WriteByte(0);
        ms.WriteByte(0); ms.WriteByte(1);                    // QTYPE A
        ms.WriteByte(0); ms.WriteByte(1);                    // QCLASS IN
        return ms.ToArray();
    }

    /// <summary>
    /// Liberações fixas: nomes que NÃO são bloqueados só porque um domínio-PAI está na lista (youtube.com bloqueado,
    /// music.youtube.com liberado). Se a lista tiver o próprio nome liberado (ou um abaixo dele), vale a lista.
    /// Espelhos: background.js (CTC_ALWAYS_ALLOWED_HOSTS), UrlPolicy.cs e UrlPolicy.kt.
    /// </summary>
    public static readonly string[] AlwaysAllowedHosts = { "music.youtube.com" };

    /// <summary>true se o nome (ou qualquer domínio-pai dele) está na lista, ou é um dos nomes "canário".</summary>
    public static bool IsBlocked(string name, ISet<string>? domains)
    {
        if (string.IsNullOrEmpty(name)) return false;
        name = name.TrimEnd('.').ToLowerInvariant();
        foreach (var canary in CanaryDomains)
            if (name == canary) return true;
        if (domains == null || domains.Count == 0) return false;

        string? stopAt = null;
        foreach (var a in AlwaysAllowedHosts)
            if (name == a || name.EndsWith("." + a, StringComparison.Ordinal)) { stopAt = a; break; }

        var s = name;
        while (true)
        {
            if (domains.Contains(s)) return true;
            if (stopAt != null && s == stopAt) return false; // não sobe para o domínio-pai da liberação
            var dot = s.IndexOf('.');
            if (dot < 0) return false;
            s = s[(dot + 1)..];
        }
    }

    /// <summary>Monta o conjunto de domínios bloqueados (mesma normalização do arquivo hosts).</summary>
    public static HashSet<string> BuildDomainSet(IEnumerable<string>? domains)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (domains == null) return set;
        foreach (var raw in domains)
        {
            var d = HostsFileManager.NormalizeDomain(raw);
            if (d != null) set.Add(d);
        }
        return set;
    }
}

/// <summary>
/// Backlog #9 — servidor DNS local (UDP e TCP, porta 53, só em 127.0.0.1 e ::1).
///  - nome na lista de bloqueio (ou um nome "canário"): responde NXDOMAIN;
///  - o resto: encaminha ao Cloudflare Família (1.1.1.3 / 1.0.0.3), que também recusa sites adultos e malware.
///
/// Sem WPF: é compilado também pelo Watchdog. Quem decide QUANDO ligar e mexer no DNS do Windows é o
/// <see cref="DnsFilterController"/>. Aqui só há o servidor. Nunca lança nas rotinas de atendimento.
/// </summary>
public sealed class LocalDnsFilter : IDisposable
{
    public static readonly IPAddress[] Upstreams = { IPAddress.Parse("1.1.1.3"), IPAddress.Parse("1.0.0.3") };

    private const int UpstreamTimeoutMs = 2500;
    private const int TcpIdleTimeoutMs = 5000;

    private readonly CancellationTokenSource _cts = new();
    private readonly List<UdpClient> _udp = new();
    private readonly List<TcpListener> _tcp = new();
    private volatile HashSet<string> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private long _queries;
    private long _blockedCount;
    private long _upstreamFailures;
    private bool _disposed;

    /// <summary>true se também conseguiu escutar em ::1 (aí o DNS do Windows pode apontar para ::1 também).</summary>
    public bool ListensOnIPv6 { get; private set; }

    public long Queries => Interlocked.Read(ref _queries);
    public long BlockedCount => Interlocked.Read(ref _blockedCount);

    /// <summary>Consultas que o servidor de cima não respondeu (as duas tentativas falharam): diagnóstico.</summary>
    public long UpstreamFailures => Interlocked.Read(ref _upstreamFailures);

    public void SetBlockedDomains(HashSet<string> domains) => _blocked = domains;

    /// <summary>Começa a escutar. Lança se a porta 53 de 127.0.0.1 estiver ocupada (então nada é alterado no Windows).</summary>
    public void Start()
    {
        StartOn(IPAddress.Loopback);
        try
        {
            StartOn(IPAddress.IPv6Loopback);
            ListensOnIPv6 = true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Filtro de DNS: sem escuta em ::1 ({ex.Message}); só IPv4.");
        }
    }

    private void StartOn(IPAddress address)
    {
        UdpClient? udp = null;
        TcpListener? tcp = null;
        try
        {
            udp = new UdpClient(new IPEndPoint(address, 53));
            try
            {
                // Windows: ICMP "porta inalcançável" de um cliente que sumiu faria o Receive lançar exceção.
                udp.Client.IOControl((IOControlCode)(-1744830452), new byte[] { 0, 0, 0, 0 }, null);
            }
            catch { /* só um ajuste de robustez */ }

            tcp = new TcpListener(address, 53);
            tcp.Start();
        }
        catch
        {
            udp?.Dispose();
            try { tcp?.Stop(); } catch { }
            throw;
        }

        _udp.Add(udp);
        _tcp.Add(tcp);
        _ = Task.Run(() => UdpLoopAsync(udp));
        _ = Task.Run(() => TcpLoopAsync(tcp));
    }

    // ---------------------------------------------------------------- decisão

    /// <summary>Resposta para uma consulta (NXDOMAIN local ou a do servidor de cima). null = descartar.</summary>
    private async Task<byte[]?> ResolveAsync(byte[] query, int length, bool overTcp)
    {
        if (!DnsFilterCore.TryParseQuestion(query, length, out var name, out var questionEnd)) return null;
        Interlocked.Increment(ref _queries);

        if (DnsFilterCore.IsBlocked(name, _blocked))
        {
            Interlocked.Increment(ref _blockedCount);
            return DnsFilterCore.BuildNxDomain(query, questionEnd);
        }

        var raw = length == query.Length ? query : query[..length];
        var reply = overTcp ? await ForwardTcpAsync(raw) : await ForwardUdpAsync(raw);
        if (reply == null && !_cts.IsCancellationRequested) Interlocked.Increment(ref _upstreamFailures);
        return reply;
    }

    // ---------------------------------------------------------------- UDP

    private async Task UdpLoopAsync(UdpClient udp)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(50, CancellationToken.None); continue; }
            catch (Exception ex) { Logger.LogException("LocalDnsFilter.UdpLoop", ex); await Task.Delay(200, CancellationToken.None); continue; }

            _ = Task.Run(async () =>
            {
                try
                {
                    var reply = await ResolveAsync(received.Buffer, received.Buffer.Length, overTcp: false);
                    if (reply != null && !ct.IsCancellationRequested)
                        await udp.SendAsync(reply, reply.Length, received.RemoteEndPoint);
                }
                catch (ObjectDisposedException) { }
                catch (Exception ex) { Logger.LogException("LocalDnsFilter.UdpHandle", ex); }
            });
        }
    }

    private async Task<byte[]?> ForwardUdpAsync(byte[] query)
    {
        foreach (var upstream in Upstreams)
        {
            try
            {
                using var sock = new Socket(upstream.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(UpstreamTimeoutMs);

                await sock.ConnectAsync(new IPEndPoint(upstream, 53), cts.Token);
                await sock.SendAsync(query, SocketFlags.None, cts.Token);

                var buffer = new byte[4096];
                // Ignora respostas com ID diferente (lixo ou resposta atrasada de outra consulta).
                while (true)
                {
                    var n = await sock.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
                    if (n >= 12 && buffer[0] == query[0] && buffer[1] == query[1]) return buffer[..n];
                }
            }
            catch (OperationCanceledException) { if (_cts.IsCancellationRequested) return null; }
            catch (Exception) { /* tenta o próximo servidor */ }
        }
        return null;
    }

    // ---------------------------------------------------------------- TCP

    private async Task TcpLoopAsync(TcpListener listener)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Logger.LogException("LocalDnsFilter.TcpLoop", ex); await Task.Delay(200, CancellationToken.None); continue; }

            _ = Task.Run(() => HandleTcpClientAsync(client));
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var ct = _cts.Token;
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TcpIdleTimeoutMs);

                    var header = new byte[2];
                    if (!await ReadExactAsync(stream, header, 2, idle.Token)) return;
                    var len = (header[0] << 8) | header[1];
                    if (len < 12) return;
                    var msg = new byte[len];
                    if (!await ReadExactAsync(stream, msg, len, idle.Token)) return;

                    var reply = await ResolveAsync(msg, len, overTcp: true);
                    if (reply == null) return;

                    var frame = new byte[reply.Length + 2];
                    frame[0] = (byte)(reply.Length >> 8);
                    frame[1] = (byte)reply.Length;
                    Array.Copy(reply, 0, frame, 2, reply.Length);
                    await stream.WriteAsync(frame, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
        catch (Exception ex) { Logger.LogException("LocalDnsFilter.TcpHandle", ex); }
    }

    private async Task<byte[]?> ForwardTcpAsync(byte[] query)
    {
        foreach (var upstream in Upstreams)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(UpstreamTimeoutMs + 1500);

                using var client = new TcpClient(upstream.AddressFamily);
                await client.ConnectAsync(upstream, 53, cts.Token);
                var stream = client.GetStream();

                var frame = new byte[query.Length + 2];
                frame[0] = (byte)(query.Length >> 8);
                frame[1] = (byte)query.Length;
                Array.Copy(query, 0, frame, 2, query.Length);
                await stream.WriteAsync(frame, cts.Token);

                var header = new byte[2];
                if (!await ReadExactAsync(stream, header, 2, cts.Token)) continue;
                var len = (header[0] << 8) | header[1];
                if (len < 12) continue;
                var resp = new byte[len];
                if (!await ReadExactAsync(stream, resp, len, cts.Token)) continue;
                return resp;
            }
            catch (OperationCanceledException) { if (_cts.IsCancellationRequested) return null; }
            catch (Exception) { /* tenta o próximo servidor */ }
        }
        return null;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    // ---------------------------------------------------------------- autoteste

    /// <summary>
    /// Pergunta <paramref name="name"/> (tipo A) ao próprio filtro em 127.0.0.1:53. true se houve resposta com
    /// RCODE 0 dentro do prazo, ou seja: o filtro escuta E o servidor de cima responde.
    /// </summary>
    public static bool Probe(string name, int timeoutMs = 3000)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = timeoutMs;
            udp.Client.SendTimeout = timeoutMs;
            var query = DnsFilterCore.BuildQuery(name);
            udp.Send(query, query.Length, new IPEndPoint(IPAddress.Loopback, 53));

            var remote = new IPEndPoint(IPAddress.Any, 0);
            var reply = udp.Receive(ref remote);
            return reply.Length >= 12 && reply[0] == query[0] && reply[1] == query[1] && (reply[3] & 0x0F) == 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        foreach (var u in _udp) { try { u.Dispose(); } catch { } }
        foreach (var t in _tcp) { try { t.Stop(); } catch { } }
        _udp.Clear();
        _tcp.Clear();
        try { _cts.Dispose(); } catch { }
    }
}
