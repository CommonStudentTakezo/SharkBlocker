using SharkBlocker.Services;
using Xunit;

namespace SharkBlocker.ParityTests;

/// <summary>Regra de decisão do detector de imagens (ImageDecision): só números, sem imagem nem modelo.</summary>
public class ImageDecisionTests
{
    private static ImageScores S(float explicitScore, float sexy) =>
        new(explicitScore, sexy, new[] { 0f, explicitScore, 0f, 0f, sexy });

    private static ImageLevel Level(float explicitScore, float sexy, int sensitivity = 2) =>
        ImageDecision.Evaluate(S(explicitScore, sexy), ImageDecision.Limits(sensitivity));

    [Fact]
    public void CloseDeRostoComSexyAlto_NaoTravaNoPadrao()
    {
        // O caso do vídeo de xadrez: a soma antiga (0,80) travava; agora "sexy" sozinho precisa de 0,93.
        Assert.Equal(ImageLevel.Clean, Level(0.02f, 0.84f));
        Assert.Equal(ImageLevel.Clean, Level(0.05f, 0.87f));
    }

    [Fact]
    public void BiquiniComSexyMuitoAlto_ViraSugestivo()
    {
        Assert.Equal(ImageLevel.Suggestive, Level(0.01f, 0.95f));
    }

    [Fact]
    public void ExplicitoAcimaDoLimite_ViraExplicito_EMuitoAlto_ViraImediato()
    {
        Assert.Equal(ImageLevel.Explicit, Level(0.88f, 0.05f));
        Assert.Equal(ImageLevel.Immediate, Level(0.97f, 0.01f));
    }

    [Fact]
    public void ExplicitoAbaixoDoLimite_ComSexyBaixo_NaoTrava()
    {
        Assert.Equal(ImageLevel.Clean, Level(0.60f, 0.10f));
    }

    [Fact]
    public void SomaDeExplicitoESexy_PodeFecharOLimiteSugestivo()
    {
        Assert.Equal(ImageLevel.Suggestive, Level(0.50f, 0.45f)); // 0,95 >= 0,93 e explícito < 0,85
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LimitesSaoCrescentes_ImediatoNuncaMenorQueExplicito(int sens)
    {
        var l = ImageDecision.Limits(sens);
        Assert.True(l.Explicit < l.Combined);
        Assert.True(l.Explicit < l.Immediate);
    }

    [Fact]
    public void MaisRigidoPegaMais_MenosRigidoPegaMenos()
    {
        // Mesma captura, três rigores.
        Assert.Equal(ImageLevel.Clean, Level(0.75f, 0.05f, sensitivity: 1));
        Assert.Equal(ImageLevel.Clean, Level(0.75f, 0.05f, sensitivity: 2));
        Assert.Equal(ImageLevel.Explicit, Level(0.75f, 0.05f, sensitivity: 3));
    }

    [Fact]
    public void SensibilidadeInvalida_ViraPadrao()
    {
        Assert.Equal(ImageDecision.Limits(2), ImageDecision.Limits(0));
        Assert.Equal(ImageDecision.Limits(2), ImageDecision.Limits(99));
    }

    [Fact]
    public void Confirmacao_ImediatoTravaEm1_ExplicitoEm2_SugestivoEm3()
    {
        Assert.True(ImageDecision.Confirmed(1, ImageLevel.Immediate));
        Assert.False(ImageDecision.Confirmed(1, ImageLevel.Explicit));
        Assert.True(ImageDecision.Confirmed(2, ImageLevel.Explicit));
        Assert.False(ImageDecision.Confirmed(2, ImageLevel.Suggestive));
        Assert.True(ImageDecision.Confirmed(3, ImageLevel.Suggestive));
        Assert.False(ImageDecision.Confirmed(99, ImageLevel.Clean));
    }

    [Fact]
    public void ExplicitoDepoisDeSugestivo_CompletaASequencia()
    {
        // Duas capturas seguidas suspeitas (sugestiva, depois explícita): a atual é Explicit e basta streak 2.
        Assert.True(ImageDecision.Confirmed(2, ImageLevel.Explicit));
    }

    [Fact]
    public void Describe_UsaNomesDasCincoClasses_ESoNumeros()
    {
        var text = ImageDecision.Describe(new ImageScores(0.1f, 0.2f, new[] { 0.01f, 0.02f, 0.67f, 0.08f, 0.22f }));
        Assert.Contains("hentai", text);
        Assert.Contains("sexy", text);
        Assert.DoesNotContain("/", text);
    }

    [Fact]
    public void Total_FicaLimitadoA1()
    {
        Assert.Equal(1f, new ImageScores(0.8f, 0.7f, Array.Empty<float>()).Total);
    }
}
