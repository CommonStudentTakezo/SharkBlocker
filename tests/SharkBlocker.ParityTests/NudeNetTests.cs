using SharkBlocker.Services;
using Xunit;

namespace SharkBlocker.ParityTests;

/// <summary>Segunda opinião (NudeNet): decodificação da saída YOLO e regra combinada. Só números, sem modelo nem imagem.</summary>
public class NudeNetTests
{
    private const int C = NudeNetDecoder.Channels; // 22
    private static int Idx(string label) => Array.IndexOf(NudeNetDecoder.Labels, label);

    /// <summary>Monta uma saída [1,22,anchors] (canais primeiro) com as confianças dadas na âncora 3 (resto zero).</summary>
    private static float[] ChannelsFirst(int anchors, params (string label, float conf)[] hits)
    {
        var d = new float[C * anchors];
        foreach (var (label, conf) in hits) d[(4 + Idx(label)) * anchors + 3] = conf;
        return d;
    }

    /// <summary>Mesma coisa no formato [1,anchors,22] (canais por último).</summary>
    private static float[] ChannelsLast(int anchors, params (string label, float conf)[] hits)
    {
        var d = new float[C * anchors];
        foreach (var (label, conf) in hits) d[3 * C + 4 + Idx(label)] = conf;
        return d;
    }

    [Fact]
    public void Rotulos_SaoDezoito_NaOrdemDoModelo()
    {
        Assert.Equal(18, NudeNetDecoder.Labels.Length);
        Assert.Equal("FEMALE_GENITALIA_COVERED", NudeNetDecoder.Labels[0]);
        Assert.Equal("FACE_FEMALE", NudeNetDecoder.Labels[1]);
        Assert.Equal("BUTTOCKS_COVERED", NudeNetDecoder.Labels[17]);
        Assert.Equal(18, NudeNetDecoder.Labels.Distinct().Count());
    }

    [Fact]
    public void CloseDeRosto_NaoTemCorpoNemExposto()
    {
        var r = NudeNetDecoder.Summarize(ChannelsFirst(2100, ("FACE_MALE", 0.93f), ("FACE_FEMALE", 0.40f), ("FEET_EXPOSED", 0.5f), ("ARMPITS_EXPOSED", 0.6f)), C, 2100);
        Assert.NotNull(r);
        Assert.Equal(0f, r!.Exposed);
        Assert.Equal(0f, r.Body);
        Assert.Contains("FACE_MALE", r.Summary);
    }

    [Fact]
    public void SeioExposto_ContaComoExpostoEComoCorpo()
    {
        var r = NudeNetDecoder.Summarize(ChannelsFirst(2100, ("FEMALE_BREAST_EXPOSED", 0.71f)), C, 2100)!;
        Assert.Equal(0.71f, r.Exposed);
        Assert.Equal(0.71f, r.Body);
    }

    [Fact]
    public void BiquiniCoberto_ContaComoCorpoMasNaoComoExposto()
    {
        var r = NudeNetDecoder.Summarize(ChannelsFirst(2100, ("FEMALE_BREAST_COVERED", 0.66f), ("BELLY_EXPOSED", 0.5f)), C, 2100)!;
        Assert.Equal(0f, r.Exposed);
        Assert.Equal(0.66f, r.Body);
    }

    [Fact]
    public void MesmoResultado_NosDoisFormatosDeSaida()
    {
        var hits = new[] { ("BUTTOCKS_EXPOSED", 0.58f), ("FACE_FEMALE", 0.9f) };
        var a = NudeNetDecoder.Summarize(ChannelsFirst(2100, hits), C, 2100)!;
        var b = NudeNetDecoder.Summarize(ChannelsLast(2100, hits), 2100, C)!;
        Assert.Equal(a.Exposed, b.Exposed);
        Assert.Equal(a.Body, b.Body);
        Assert.Equal(a.Summary, b.Summary);
    }

    [Fact]
    public void FormatoInvalido_OuPoucosDados_DevolveNull()
    {
        Assert.Null(NudeNetDecoder.Summarize(new float[100], 10, 10));          // nenhuma dimensão é 22
        Assert.Null(NudeNetDecoder.Summarize(new float[10], C, 2100));          // faltam dados
        Assert.Null(NudeNetDecoder.Summarize(Array.Empty<float>(), C, 0));      // sem âncoras
    }

    [Fact]
    public void NaN_EhIgnorado_ConfiancaFicaLimitadaA1()
    {
        var d = ChannelsFirst(50, ("FEMALE_BREAST_EXPOSED", 1.7f));
        d[(4 + Idx("ANUS_EXPOSED")) * 50 + 1] = float.NaN;
        var r = NudeNetDecoder.Summarize(d, C, 50)!;
        Assert.Equal(1f, r.Exposed);
    }

    // ---- Regra combinada ----

    private static readonly ImageScores G = new(0f, 0f, new float[5]);
    private static ImageScores GTotal(float total) => new(0f, total, new float[5]);
    private static NudeNetResult N(float exposed, float body) => new(exposed, body, "");
    private static readonly NudeLimits L = ImageDecision.NudeLimitsFor(2);

    [Fact]
    public void SemNudeNet_NadaMuda()
    {
        foreach (var level in Enum.GetValues<ImageLevel>())
            Assert.Equal(level, ImageDecision.Refine(level, G, null, L));
    }

    [Fact]
    public void Sugestivo_SemNenhumCorpo_EhVetado_CloseDeRosto()
    {
        Assert.Equal(ImageLevel.Clean, ImageDecision.Refine(ImageLevel.Suggestive, GTotal(0.95f), N(0f, 0f), L));
    }

    [Fact]
    public void Sugestivo_ComCorpoCoberto_ContinuaSugestivo_Biquini()
    {
        Assert.Equal(ImageLevel.Suggestive, ImageDecision.Refine(ImageLevel.Suggestive, GTotal(0.95f), N(0f, 0.55f), L));
    }

    [Fact]
    public void Sugestivo_ComParteExposta_SobeParaExplicito()
    {
        Assert.Equal(ImageLevel.Explicit, ImageDecision.Refine(ImageLevel.Suggestive, GTotal(0.95f), N(0.60f, 0.60f), L));
    }

    [Fact]
    public void Explicito_NaoEhVetado_PodeSerAnime_MasConfirmadoViraImediato()
    {
        Assert.Equal(ImageLevel.Explicit, ImageDecision.Refine(ImageLevel.Explicit, GTotal(0.9f), N(0f, 0f), L));
        Assert.Equal(ImageLevel.Immediate, ImageDecision.Refine(ImageLevel.Explicit, GTotal(0.9f), N(0.60f, 0.60f), L));
    }

    [Fact]
    public void Imediato_Continua_MesmoSemNudeNet()
    {
        Assert.Equal(ImageLevel.Immediate, ImageDecision.Refine(ImageLevel.Immediate, GTotal(0.99f), N(0f, 0f), L));
    }

    [Fact]
    public void Limpo_ComParteExpostaForte_ESuspeitaDoClassificador_ViraExplicito()
    {
        Assert.Equal(ImageLevel.Explicit, ImageDecision.Refine(ImageLevel.Clean, GTotal(0.35f), N(0.65f, 0.65f), L));
    }

    [Fact]
    public void Limpo_ParteExpostaForte_MasClassificadorQuietoAbaixoDoPortao_ContinuaLimpo()
    {
        Assert.Equal(ImageLevel.Clean, ImageDecision.Refine(ImageLevel.Clean, GTotal(0.10f), N(0.90f, 0.90f), L));
    }

    [Fact]
    public void Limpo_ParteExpostaFraca_ContinuaLimpo()
    {
        Assert.Equal(ImageLevel.Clean, ImageDecision.Refine(ImageLevel.Clean, GTotal(0.5f), N(0.45f, 0.45f), L));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LimitesDaSegundaOpiniao_CorroborarNuncaMaiorQueSozinho(int sens)
    {
        var l = ImageDecision.NudeLimitsFor(sens);
        Assert.True(l.Corroborate < l.NudeOnly);
        Assert.True(l.BodyMin > 0f);
    }

    // Caso real (log de 2026-11-15): rosto de cachorro em close: porn 0,90 no classificador e "seio exposto" 0,38 no NudeNet.
    private static readonly ImageScores DogFace = new(0.91f, 0f, new[] { 0f, 0.01f, 0.08f, 0.90f, 0f });
    private static readonly ImageScores Anime = new(0.91f, 0f, new[] { 0f, 0.90f, 0.08f, 0.01f, 0f });

    [Fact]
    public void RostoDeCachorro_NaoTravaNoPadrao_NemNoMenosRigido()
    {
        foreach (var sens in new[] { 1, 2 })
            Assert.Equal(ImageLevel.Clean, ImageDecision.Refine(ImageLevel.Explicit, DogFace, N(0.38f, 0.38f), ImageDecision.NudeLimitsFor(sens)));
    }

    [Fact]
    public void RostoDeCachorro_NoMaisRigido_VirariaSugestivoNuncaImediato()
    {
        Assert.Equal(ImageLevel.Suggestive, ImageDecision.Refine(ImageLevel.Explicit, DogFace, N(0.38f, 0.38f), ImageDecision.NudeLimitsFor(3)));
    }

    [Fact]
    public void FotoExplicita_LeituraIntermediariaDoNudeNet_ViraSugestivo()
    {
        Assert.Equal(ImageLevel.Suggestive, ImageDecision.Refine(ImageLevel.Explicit, DogFace, N(0.45f, 0.45f), L));
    }

    [Fact]
    public void AnimeExplicito_SemConfirmacaoDoNudeNet_ContinuaExplicito()
    {
        foreach (var sens in new[] { 1, 2, 3 })
            Assert.Equal(ImageLevel.Explicit, ImageDecision.Refine(ImageLevel.Explicit, Anime, N(0f, 0f), ImageDecision.NudeLimitsFor(sens)));
    }

    [Fact]
    public void MaisRigido_AgeComConfiancaMenor()
    {
        // Mesma leitura (exposto 0,50): só o rigor mais alto aceita agir sem o classificador acima do limite.
        Assert.Equal(ImageLevel.Clean, ImageDecision.Refine(ImageLevel.Clean, GTotal(0.4f), N(0.50f, 0.50f), ImageDecision.NudeLimitsFor(2)));
        Assert.Equal(ImageLevel.Explicit, ImageDecision.Refine(ImageLevel.Clean, GTotal(0.4f), N(0.50f, 0.50f), ImageDecision.NudeLimitsFor(3)));
    }

    [Fact]
    public void DescribeNude_SemResposta_EComResposta()
    {
        Assert.Equal("NudeNet n/d", ImageDecision.DescribeNude(null));
        Assert.Contains("exposto 0", ImageDecision.DescribeNude(new NudeNetResult(0.5f, 0.6f, "FACE_MALE 0.90")));
    }
}
