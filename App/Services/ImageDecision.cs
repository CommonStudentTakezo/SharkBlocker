namespace SharkBlocker.Services;

/// <summary>
/// Pontuações de UMA captura de tela, já separadas por tipo (ver <see cref="NsfwClassifier"/>):
///  - <see cref="Explicit"/>: soma das classes "explícitas" (porn + hentai; hentai cobre anime/mangá adulto);
///  - <see cref="Sexy"/>: soma das classes "sugestivas" (sexy: biquíni, roupa sugestiva, mas também close de rosto e muito tom de pele);
///  - <see cref="Raw"/>: saída completa do modelo (só para o log; são números, nunca imagem).
/// </summary>
public sealed record ImageScores(float Explicit, float Sexy, float[] Raw)
{
    /// <summary>Explícito + sugestivo (equivale à pontuação única usada antes da rodada de 2026-11-14).</summary>
    public float Total => Math.Clamp(Explicit + Sexy, 0f, 1f);
}

/// <summary>Nível de uma captura, do menor para o maior.</summary>
public enum ImageLevel
{
    /// <summary>Nada a fazer.</summary>
    Clean = 0,
    /// <summary>Só "sugestivo" (biquíni/roupa sugestiva): exige o maior número de capturas seguidas.</summary>
    Suggestive = 1,
    /// <summary>Explícito acima do limite: exige confirmação em 2 capturas seguidas.</summary>
    Explicit = 2,
    /// <summary>Explícito muito alto: trava na hora, sem esperar confirmação.</summary>
    Immediate = 3,
}

/// <summary>Limites de uma sensibilidade (ver <see cref="ImageDecision.Limits"/>).</summary>
public readonly record struct ImageLimits(float Explicit, float Combined, float Immediate);

/// <summary>
/// Limites da segunda opinião (NudeNet), por sensibilidade (ver <see cref="ImageDecision.NudeLimitsFor"/>):
/// <see cref="Corroborate"/> = parte exposta que CONFIRMA o classificador; <see cref="NudeOnly"/> = parte exposta forte o bastante
/// para agir mesmo com o classificador abaixo do limite; <see cref="BodyMin"/> = confiança mínima para contar "há corpo na tela".
/// </summary>
public readonly record struct NudeLimits(float Corroborate, float NudeOnly, float BodyMin);

/// <summary>
/// Regra de decisão do detector de imagens, SEM WinForms/WPF/ONNX (por isso pode ser testada em xUnit).
/// Só o App usa; o Watchdog não compila este arquivo.
///
/// Por que existe (rodada 2026-11-14): a regra antiga era "soma(hentai+porn+sexy) >= 0,80 em UMA captura trava". A classe "sexy"
/// do modelo dispara com close de rosto, tom de pele em destaque e moda; um único quadro ruim (um vídeo de xadrez com o
/// apresentador em close) já travava a tela por 5 a 30 min. Agora:
///  - explícito (porn + hentai) acima do limite  -> precisa de 2 capturas seguidas;
///  - explícito muito alto (>= Immediate)         -> trava na hora;
///  - só sugestivo (explícito + sexy >= Combined) -> precisa de 3 capturas seguidas e de um limite bem mais alto, para pegar
///    biquíni/roupa sugestiva sem pegar um rosto em close.
/// Com a segunda opinião (NudeNet, opcional) o nível passa por <see cref="Refine"/>: o "só sugestivo" sem nenhuma parte do corpo
/// na tela é vetado (close de rosto), e parte exposta confirmada sobe o nível.
/// </summary>
public static class ImageDecision
{
    /// <summary>Sensibilidade 1 = menos rígido, 2 = padrão, 3 = mais rígido (valor fora disso vira padrão).</summary>
    public static ImageLimits Limits(int sensitivity) => sensitivity switch
    {
        1 => new ImageLimits(Explicit: 0.92f, Combined: 0.97f, Immediate: 0.98f),
        3 => new ImageLimits(Explicit: 0.70f, Combined: 0.85f, Immediate: 0.92f),
        _ => new ImageLimits(Explicit: 0.85f, Combined: 0.93f, Immediate: 0.96f),
    };

    /// <summary>O NudeNet só roda (e custa CPU) quando o classificador já deu pontuação total a partir daqui.</summary>
    public const float NudeNetGate = 0.30f;

    /// <summary>Limites da segunda opinião; mesma convenção de sensibilidade de <see cref="Limits"/>.</summary>
    public static NudeLimits NudeLimitsFor(int sensitivity) => sensitivity switch
    {
        // Corroborate subiu em 2026-11-15: um vídeo de cachorro deu porn 0,90 no classificador e "seio exposto" 0,38 no NudeNet; com
        // Corroborate 0,30 (Mais rígido) isso virava Imediato e travava na hora. Confirmar exige uma leitura mais firme do NudeNet.
        1 => new NudeLimits(Corroborate: 0.65f, NudeOnly: 0.70f, BodyMin: 0.30f),
        3 => new NudeLimits(Corroborate: 0.45f, NudeOnly: 0.50f, BodyMin: 0.30f),
        _ => new NudeLimits(Corroborate: 0.55f, NudeOnly: 0.60f, BodyMin: 0.30f),
    };

    /// <summary>
    /// Combina o nível do classificador com a segunda opinião (<paramref name="nude"/> = null: sem NudeNet, nada muda).
    ///  - Imediato continua imediato (rede de segurança: o NudeNet é fraco em anime/hentai, não pode vetar);
    ///  - Explícito + parte exposta confirmada -> Imediato (os dois modelos concordam); sem confirmação continua Explícito
    ///    (não veta: pode ser anime);
    ///  - Sugestivo (só "sexy"): parte exposta confirmada -> Explícito; só corpo coberto/barriga (biquíni) -> continua Sugestivo;
    ///    NENHUMA parte do corpo (só rosto, paisagem, fundo cor de pele) -> Limpo (veto: é o caso do close de rosto);
    ///  - Limpo: parte exposta forte (>= NudeOnly) com o classificador já em pontuação total >= <see cref="NudeNetGate"/> -> Explícito.
    /// </summary>
    public static ImageLevel Refine(ImageLevel level, ImageScores s, NudeNetResult? nude, NudeLimits limits)
    {
        if (nude == null || level == ImageLevel.Immediate) return level;

        switch (level)
        {
            case ImageLevel.Explicit:
                return nude.Exposed >= limits.Corroborate ? ImageLevel.Immediate : ImageLevel.Explicit;
            case ImageLevel.Suggestive:
                if (nude.Exposed >= limits.Corroborate) return ImageLevel.Explicit;
                return nude.Body >= limits.BodyMin ? ImageLevel.Suggestive : ImageLevel.Clean;
            default:
                return nude.Exposed >= limits.NudeOnly && s.Total >= NudeNetGate ? ImageLevel.Explicit : ImageLevel.Clean;
        }
    }

    public static ImageLevel Evaluate(ImageScores s, ImageLimits limits)
    {
        if (s.Explicit >= limits.Immediate) return ImageLevel.Immediate;
        if (s.Explicit >= limits.Explicit) return ImageLevel.Explicit;
        if (s.Total >= limits.Combined) return ImageLevel.Suggestive;
        return ImageLevel.Clean;
    }

    /// <summary>Capturas seguidas (contando a atual) necessárias para travar com a captura atual neste nível.</summary>
    public static int ScansRequired(ImageLevel level) => level switch
    {
        ImageLevel.Immediate => 1,
        ImageLevel.Explicit => 2,
        ImageLevel.Suggestive => 3,
        _ => int.MaxValue,
    };

    /// <summary>
    /// <paramref name="streak"/> = quantas capturas seguidas (contando a atual) ficaram em nível acima de Clean.
    /// Uma captura Explicit logo depois de uma Suggestive conta como sequência: o que importa é a captura atual.
    /// </summary>
    public static bool Confirmed(int streak, ImageLevel level) => level != ImageLevel.Clean && streak >= ScansRequired(level);

    private static readonly string[] FiveClassNames = { "drawings", "hentai", "neutral", "porn", "sexy" };

    /// <summary>Texto da segunda opinião para o log ("NudeNet exposto 0,00 corpo 0,00 [FACE_FEMALE 0,87]" ou "NudeNet sem resposta").</summary>
    public static string DescribeNude(NudeNetResult? n) =>
        n == null ? "NudeNet n/d" : $"NudeNet exposto {n.Exposed:0.00} corpo {n.Body:0.00}" + (n.Summary.Length > 0 ? $" [{n.Summary}]" : "");

    /// <summary>Texto só com números para o log: "drawings 0,01 hentai 0,02 ..." (nunca imagem nem conteúdo).</summary>
    public static string Describe(ImageScores s)
    {
        var parts = new List<string>(s.Raw.Length);
        for (var i = 0; i < s.Raw.Length; i++)
        {
            var name = s.Raw.Length == FiveClassNames.Length ? FiveClassNames[i] : "c" + i;
            parts.Add($"{name} {s.Raw[i]:0.00}");
        }
        return string.Join(" ", parts);
    }
}
