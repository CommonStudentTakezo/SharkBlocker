namespace SharkBlocker.Services;

/// <summary>
/// Sugestões para a lista de palavras-chave do bloqueio temporizado (essa lista é varrida
/// no TEXTO da página, e só enquanto há bloqueio ativo).
///
/// Os termos que valem o tempo todo (URL e buscas) ficam na própria extensão, em
/// BrowserExtension/adult-terms.js — é a ÚNICA fonte dessa lista, pra funcionar mesmo que
/// o app/serviço esteja fora do ar.
/// </summary>
public static class AdultKeywords
{
    public static readonly string[] SuggestedForLockList =
    {
        "erótica", "erotica", "romance erótico", "livros eróticos",
        "conteúdo adulto", "adult content", "explicit content",
        "hentai", "nsfw",
    };
}
