using SharkBlocker.Services;
using Xunit;

namespace SharkBlocker.ParityTests;

/// <summary>Cálculo dos "dias limpos" (StatsStore.CleanDays). Independe do fuso: usa sempre meio-dia local.</summary>
public class StatsTests
{
    private static readonly DateTime Today = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Local);

    private static DateTime Ago(int days) => Today.AddDays(-days).ToUniversalTime();

    [Fact]
    public void SemTentativas_TodosOsDiasDesdeOInicioSaoLimpos()
    {
        var (clean, total) = StatsStore.CleanDays(Array.Empty<DateTime>(), Ago(10), Today.ToUniversalTime());
        Assert.Equal((11, 11), (clean, total));
    }

    [Fact]
    public void JanelaLimitadaA30Dias_UmaTentativaHojeSujaSoHoje()
    {
        var (clean, total) = StatsStore.CleanDays(new[] { Ago(0) }, Ago(100), Today.ToUniversalTime());
        Assert.Equal((29, 30), (clean, total));
    }

    [Fact]
    public void VariasTentativasNoMesmoDia_ContamUmaVez()
    {
        var (clean, total) = StatsStore.CleanDays(new[] { Ago(3), Ago(3).AddHours(1), Ago(3).AddHours(-2) }, Ago(100), Today.ToUniversalTime());
        Assert.Equal((29, 30), (clean, total));
    }

    [Fact]
    public void TentativaForaDaJanela_Ignorada()
    {
        var (clean, total) = StatsStore.CleanDays(new[] { Ago(40) }, Ago(100), Today.ToUniversalTime());
        Assert.Equal((30, 30), (clean, total));
    }

    [Fact]
    public void PrimeiroDia_UmDiaSoELimpoAteTentar()
    {
        Assert.Equal((1, 1), StatsStore.CleanDays(Array.Empty<DateTime>(), Ago(0), Today.ToUniversalTime()));
        Assert.Equal((0, 1), StatsStore.CleanDays(new[] { Ago(0) }, Ago(0), Today.ToUniversalTime()));
    }

    [Fact]
    public void TentativasEmDiasDiferentes_SomamDiasSujos()
    {
        var (clean, total) = StatsStore.CleanDays(new[] { Ago(0), Ago(5), Ago(20) }, Ago(100), Today.ToUniversalTime());
        Assert.Equal((27, 30), (clean, total));
    }

    // ---- Mapa de horários de risco (backlog #6) ----

    private static int[] Hist(params (int Hour, int Count)[] items)
    {
        var h = new int[24];
        foreach (var (hour, count) in items) h[hour] = count;
        return h;
    }

    [Fact]
    public void Risco_ComPoucosEventos_NaoSugere()
    {
        Assert.Null(StatsStore.SuggestWindow(Hist((3, 2), (4, 2))));          // 4 ocorrências (< 5)
        Assert.Null(StatsStore.SuggestWindow(new int[24]));
    }

    [Fact]
    public void Risco_AtravessaAMeiaNoite()
    {
        var w = StatsStore.SuggestWindow(Hist((23, 4), (0, 5), (1, 3), (14, 1)));
        Assert.Equal(new RiskWindow(23, 2, 12, 13), w);
    }

    [Fact]
    public void Risco_ConcentradoEmUmaHora_GeraJanelaDeDuasHoras()
    {
        Assert.Equal(new RiskWindow(20, 22, 6, 6), StatsStore.SuggestWindow(Hist((21, 6))));
    }

    [Fact]
    public void Risco_EstendeAteSeisHoras()
    {
        var w = StatsStore.SuggestWindow(Hist((0, 3), (1, 3), (2, 3), (3, 3), (4, 3), (5, 3), (6, 3), (12, 2)));
        Assert.Equal(new RiskWindow(0, 6, 18, 23), w);
    }

    [Fact]
    public void Risco_EspalhadoPeloDia_NaoSugere()
    {
        var h = new int[24];
        Array.Fill(h, 1);
        Assert.Null(StatsStore.SuggestWindow(h));
    }

    [Fact]
    public void Risco_HistogramaUsaAHoraLocal()
    {
        var h = StatsStore.HourHistogram(new[] { Ago(0), Ago(1), Ago(2) });
        Assert.Equal(3, h.Sum());
        Assert.Equal(3, h[Today.Hour]); // meio-dia local em dias diferentes
    }

    [Fact]
    public void Risco_MapaTemVinteEQuatroBarras()
    {
        var (bars, axis) = StatsStore.FormatRiskMap(Hist((23, 4), (0, 2)));
        Assert.Equal(24, bars.Length);
        Assert.Equal('█', bars[23]);
        Assert.Equal('▄', bars[0]);
        Assert.StartsWith("0", axis);
    }
}
