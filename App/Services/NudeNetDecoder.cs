namespace SharkBlocker.Services;

/// <summary>
/// Resumo de UMA captura passada pelo NudeNet (segunda opinião, por regiões do corpo):
///  - <see cref="Exposed"/>: maior confiança entre as partes EXPOSTAS fortes (seio feminino, genitais, nádegas, ânus);
///  - <see cref="Body"/>: maior confiança entre QUALQUER parte do corpo (as de cima + coberta, barriga exposta, peito masculino).
///    Rosto, pés e axilas NÃO contam: um close de rosto tem Body = 0;
///  - <see cref="Summary"/>: as 3 maiores detecções acima de 0,25, só para o log (rótulo e número, nunca imagem).
/// </summary>
public sealed record NudeNetResult(float Exposed, float Body, string Summary);

/// <summary>
/// Decodificação da saída do NudeNet v3 (YOLOv8: 4 valores de caixa + 18 pontuações de classe por âncora), SEM ONNX/WinForms,
/// para poder ser testada em xUnit. Não faz NMS: só se quer a MAIOR confiança por classe, e a supressão de caixas
/// repetidas não muda esse máximo. A ordem dos rótulos é a do modelo (lista <c>all_labels</c> do pacote nudenet 3.x).
/// </summary>
public static class NudeNetDecoder
{
    public static readonly string[] Labels =
    {
        "FEMALE_GENITALIA_COVERED", "FACE_FEMALE", "BUTTOCKS_EXPOSED", "FEMALE_BREAST_EXPOSED", "FEMALE_GENITALIA_EXPOSED",
        "MALE_BREAST_EXPOSED", "ANUS_EXPOSED", "FEET_EXPOSED", "BELLY_COVERED", "FEET_COVERED", "ARMPITS_COVERED",
        "ARMPITS_EXPOSED", "FACE_MALE", "BELLY_EXPOSED", "MALE_GENITALIA_EXPOSED", "ANUS_COVERED", "FEMALE_BREAST_COVERED",
        "BUTTOCKS_COVERED",
    };

    public const int Classes = 18;
    /// <summary>4 (caixa) + 18 (classes).</summary>
    public const int Channels = 4 + Classes;
    /// <summary>Confiança mínima para uma detecção aparecer no texto do log.</summary>
    private const float SummaryFloor = 0.25f;

    // Índices em Labels.
    private static readonly int[] ExposedIdx = { 2, 3, 4, 6, 14 };                           // nádegas, seio F, genitália F, ânus, genitália M (todas EXPOSED)
    private static readonly int[] BodyIdx = { 0, 2, 3, 4, 5, 6, 13, 14, 15, 16, 17 };         // as de cima + coberta (genitália F, ânus, seio F, nádegas), peito M e barriga exposta

    /// <summary>
    /// <paramref name="data"/> = saída achatada de formato [1, d1, d2]. Aceita canais primeiro ([1,22,N], o normal do YOLOv8)
    /// ou canais por último ([1,N,22]). Devolve null se nenhuma das duas dimensões for 22 ou se faltam dados.
    /// </summary>
    public static NudeNetResult? Summarize(float[] data, int d1, int d2)
    {
        bool channelsFirst;
        int anchors;
        if (d1 == Channels) { channelsFirst = true; anchors = d2; }
        else if (d2 == Channels) { channelsFirst = false; anchors = d1; }
        else return null;

        if (anchors <= 0 || data.LongLength < (long)Channels * anchors) return null;

        var best = new float[Classes];
        for (var c = 0; c < Classes; c++)
        {
            var max = 0f;
            for (var a = 0; a < anchors; a++)
            {
                var v = channelsFirst ? data[(4 + c) * anchors + a] : data[a * Channels + 4 + c];
                if (v > max) max = v; // NaN nunca é maior: ignorado
            }
            best[c] = Math.Min(max, 1f);
        }

        var exposed = 0f;
        foreach (var i in ExposedIdx) exposed = Math.Max(exposed, best[i]);
        var body = 0f;
        foreach (var i in BodyIdx) body = Math.Max(body, best[i]);

        var top = Enumerable.Range(0, Classes)
            .Where(i => best[i] >= SummaryFloor)
            .OrderByDescending(i => best[i])
            .Take(3)
            .Select(i => $"{Labels[i]} {best[i]:0.00}");
        return new NudeNetResult(exposed, body, string.Join(", ", top));
    }
}
