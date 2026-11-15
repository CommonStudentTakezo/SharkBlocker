using SharkBlocker.Services;
using Xunit;

namespace SharkBlocker.ParityTests;

/// <summary>Regras puras do diário (sem arquivo, sem janela) e o novo tipo "recaída" das estatísticas.</summary>
public class JournalTests
{
    private static JournalEntry E(string trigger = "", int intensity = 0, bool relapse = false, int daysAgo = 0, string activity = "") => new()
    {
        Trigger = trigger, Intensity = intensity, WasRelapse = relapse, Activity = activity,
        Utc = DateTime.UtcNow.AddDays(-daysAgo),
    };

    [Fact]
    public void Normalize_AparaLimitaEFixaValores()
    {
        var n = RelapseJournal.Normalize(new JournalEntry
        {
            Trigger = "  Tédio  ", Activity = new string('a', 900), WhatWouldHelp = "  sair  ", Intensity = 9, Source = "qualquer",
        });
        Assert.Equal("Tédio", n.Trigger);
        Assert.Equal(RelapseJournal.MaxTextLength, n.Activity.Length);
        Assert.Equal("sair", n.WhatWouldHelp);
        Assert.Equal(5, n.Intensity);
        Assert.Equal(RelapseJournal.SourceManual, n.Source);
        Assert.NotEqual(Guid.Empty, n.Id);
    }

    [Fact]
    public void Normalize_IntensidadeNegativaViraZero_ENuloViraVazio()
    {
        var n = RelapseJournal.Normalize(new JournalEntry { Intensity = -3, Trigger = null!, Activity = null!, WhatWouldHelp = null! });
        Assert.Equal(0, n.Intensity);
        Assert.Equal("", n.Trigger);
        Assert.Equal("", n.Activity);
    }

    [Fact]
    public void Normalize_NaoAlteraAOriginal()
    {
        var original = new JournalEntry { Trigger = "  x  " };
        _ = RelapseJournal.Normalize(original);
        Assert.Equal("  x  ", original.Trigger);
    }

    [Fact]
    public void IsWorthSaving_VazioNaoVale_ExcetoRecaida()
    {
        Assert.False(RelapseJournal.IsWorthSaving(RelapseJournal.Normalize(E())));
        Assert.True(RelapseJournal.IsWorthSaving(RelapseJournal.Normalize(E(relapse: true))));
        Assert.True(RelapseJournal.IsWorthSaving(RelapseJournal.Normalize(E(trigger: "Tédio"))));
        Assert.True(RelapseJournal.IsWorthSaving(RelapseJournal.Normalize(E(intensity: 2))));
        Assert.True(RelapseJournal.IsWorthSaving(RelapseJournal.Normalize(E(activity: "assistindo"))));
    }

    [Fact]
    public void Summarize_ContaTotaisGatilhoMaisComumEMedia()
    {
        var list = new[]
        {
            E("Tédio", 4, relapse: true, daysAgo: 1),
            E("tédio", 2, relapse: false, daysAgo: 2),
            E("Estresse", 5, relapse: true, daysAgo: 40),
            E("", 0, relapse: false, daysAgo: 3),
        };
        var s = RelapseJournal.Summarize(list, DateTime.UtcNow);
        Assert.Equal(4, s.Total);
        Assert.Equal(2, s.Relapses);
        Assert.Equal(3, s.Last30Days);
        Assert.Equal(2, s.TopTriggerCount);
        Assert.Equal("tédio", s.TopTrigger, ignoreCase: true);
        Assert.Equal((4 + 2 + 5) / 3.0, s.AverageIntensity, 3);
    }

    [Fact]
    public void Summarize_Vazio_NaoQuebra()
    {
        var s = RelapseJournal.Summarize(Array.Empty<JournalEntry>(), DateTime.UtcNow);
        Assert.Equal(0, s.Total);
        Assert.Null(s.TopTrigger);
        Assert.Equal(0, s.AverageIntensity);
    }

    [Fact]
    public void OneLine_MostraDataGatilhoVontadeETipo()
    {
        var line = RelapseJournal.OneLine(E("Tédio", 4, relapse: true));
        Assert.Contains("Tédio", line);
        Assert.Contains("vontade 4", line);
        Assert.Contains("recaída", line);
        Assert.Contains("só anotou", RelapseJournal.OneLine(E("Tédio")));
    }

    [Fact]
    public void Gatilhos_NaoTemRepetidos()
    {
        Assert.Equal(RelapseJournal.Triggers.Length, RelapseJournal.Triggers.Distinct().Count());
        Assert.Contains("Outro", RelapseJournal.Triggers);
    }

    [Fact]
    public void Estatisticas_RecaidaTemTipoProprio()
    {
        Assert.Equal(StatsStore.KindRelapse, StatsStore.KindFor(StatsStore.ReasonRelapse));
        // Os outros tipos continuam como antes.
        Assert.Equal(StatsStore.KindSearch, StatsStore.KindFor("search"));
        Assert.Equal(StatsStore.KindBypass, StatsStore.KindFor("uninstall"));
        Assert.Null(StatsStore.KindFor("urgency"));
    }
}
