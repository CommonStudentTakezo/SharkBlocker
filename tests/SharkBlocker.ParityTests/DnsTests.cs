using SharkBlocker.Services;
using Xunit;

namespace SharkBlocker.ParityTests;

/// <summary>Parte pura do filtro de DNS local (DnsFilterCore): leitura da pergunta, NXDOMAIN e lista de bloqueio.</summary>
public class DnsTests
{
    // Consulta A de "www.Example.com" (ID 0x1234, RD=1), gerada à mão.
    private static readonly byte[] Query = Convert.FromHexString(
        "12340100000100000000000003777777074578616d706c6503636f6d0000010001");

    // A resposta NXDOMAIN esperada para ela.
    private static readonly byte[] Expected = Convert.FromHexString(
        "12348183000100000000000003777777074578616d706c6503636f6d0000010001");

    [Fact]
    public void LeAPergunta_EEmMinusculas()
    {
        Assert.True(DnsFilterCore.TryParseQuestion(Query, Query.Length, out var name, out var end));
        Assert.Equal("www.example.com", name);
        Assert.Equal(Query.Length, end);
    }

    [Fact]
    public void Nxdomain_TemOsBytesCertos()
    {
        DnsFilterCore.TryParseQuestion(Query, Query.Length, out _, out var end);
        Assert.Equal(Expected, DnsFilterCore.BuildNxDomain(Query, end));
    }

    [Fact]
    public void Nxdomain_IgnoraRespostasAdicionaisDaConsulta()
    {
        // Consulta com um registro extra (EDNS) depois da pergunta: a resposta leva só a pergunta.
        var withExtra = new byte[Query.Length + 11];
        Array.Copy(Query, withExtra, Query.Length);
        withExtra[11] = 1; // ARCOUNT = 1
        Assert.True(DnsFilterCore.TryParseQuestion(withExtra, withExtra.Length, out _, out var end));
        var resp = DnsFilterCore.BuildNxDomain(withExtra, end);
        Assert.Equal(Query.Length, resp.Length);
        Assert.Equal(0, resp[11]);
        Assert.Equal(3, resp[3] & 0x0F);
    }

    [Fact]
    public void PacotesInvalidos_SaoRecusados()
    {
        Assert.False(DnsFilterCore.TryParseQuestion(Query, 10, out _, out _));                   // curto demais
        Assert.False(DnsFilterCore.TryParseQuestion(Query, Query.Length - 3, out _, out _));    // sem QTYPE/QCLASS completos

        var response = (byte[])Query.Clone();
        response[2] |= 0x80;                                                                      // QR=1: é resposta
        Assert.False(DnsFilterCore.TryParseQuestion(response, response.Length, out _, out _));

        var pointer = (byte[])Query.Clone();
        pointer[12] = 0xC0;                                                                       // compressão na pergunta
        Assert.False(DnsFilterCore.TryParseQuestion(pointer, pointer.Length, out _, out _));

        var noQuestion = (byte[])Query.Clone();
        noQuestion[5] = 0;                                                                        // QDCOUNT = 0
        Assert.False(DnsFilterCore.TryParseQuestion(noQuestion, noQuestion.Length, out _, out _));
    }

    [Fact]
    public void BuildQuery_VoltaAserLida()
    {
        var q = DnsFilterCore.BuildQuery("cloudflare.com");
        Assert.True(DnsFilterCore.TryParseQuestion(q, q.Length, out var name, out var end));
        Assert.Equal("cloudflare.com", name);
        Assert.Equal(q.Length, end);
    }

    [Fact]
    public void Bloqueio_CobreSubdominiosMasNaoNomesParecidos()
    {
        var set = DnsFilterCore.BuildDomainSet(new[] { "https://www.Example.com/pagina", "outro.net" });
        Assert.True(DnsFilterCore.IsBlocked("example.com", set));
        Assert.True(DnsFilterCore.IsBlocked("a.b.example.com", set));
        Assert.True(DnsFilterCore.IsBlocked("EXAMPLE.COM.", set));
        Assert.False(DnsFilterCore.IsBlocked("notexample.com", set));
        Assert.False(DnsFilterCore.IsBlocked("example.org", set));
        Assert.False(DnsFilterCore.IsBlocked("com", set));
    }

    [Fact]
    public void NomesCanario_SempreBloqueados_MesmoSemLista()
    {
        Assert.True(DnsFilterCore.IsBlocked("use-application-dns.net", null));
        Assert.True(DnsFilterCore.IsBlocked("mask.icloud.com", new HashSet<string>()));
        Assert.False(DnsFilterCore.IsBlocked("icloud.com", null));
    }
}
