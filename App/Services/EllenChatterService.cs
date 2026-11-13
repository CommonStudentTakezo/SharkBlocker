namespace SharkBlocker.Services;

/// <summary>
/// Falas espontâneas da Ellen no widget da tela: de tempos em tempos ela "puxa assunto" com uma
/// observação de vigia, uma piada seca, uma pergunta retórica ou um empurrãozinho de motivação.
///
/// Cadência (ajuste as constantes abaixo): a primeira fala vem uns minutos depois de o app abrir
/// (para a pessoa perceber que ela existe), as seguintes a cada 25–45 minutos, com sorteio.
/// As falas saem de um "saco" embaralhado: nenhuma se repete até todas terem aparecido.
/// Também existem falas que dependem do contexto (bloqueio ativo, madrugada).
/// Tom: sonolenta, seca, protetora, sem culpa nem bronca.
/// </summary>
public static class EllenChatterService
{
    public const int FirstMinMinutes = 8;
    public const int FirstMaxMinutes = 15;
    public const int MinMinutes = 25;
    public const int MaxMinutes = 45;

    /// <summary>Tempo até a próxima fala (sorteado dentro da faixa).</summary>
    public static TimeSpan NextDelay(bool first)
    {
        var minutes = first
            ? Random.Shared.Next(FirstMinMinutes, FirstMaxMinutes + 1)
            : Random.Shared.Next(MinMinutes, MaxMinutes + 1);
        return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(Random.Shared.Next(0, 60));
    }

    private static readonly EllenQuote[] General =
    {
        // vigiando
        new("Só passando para avisar: continuo vigiando. Pode seguir seu dia.", "smirk"),
        new("Não estou olhando de propósito. É que eu sou muito boa nisso.", "smirk"),
        new("Dizem que tubarão nunca para de nadar. Eu nunca paro de vigiar. Combina.", "smirk"),
        new("Ainda aqui. Ainda de olho. Nada de novo, e é assim que eu gosto.", "calm"),
        new("Relatório da vigia: tudo sob controle. Tente não estragar a média.", "smirk"),
        new("Sinto cheiro de procrastinação. Ou é só o meu doce.", "sweet"),
        new("Se eu piscar e você aprontar, eu fico sabendo do mesmo jeito.", "smirk"),

        // piadas secas
        new("Minha vida é simples: vigio, descanso, como doce. Três de três.", "sweet"),
        new("Se tédio fosse proibido, metade da internet estaria bloqueada. Eu ficaria sem emprego.", "smirk"),
        new("Já bebeu água hoje? Ninguém virou lenda sem se hidratar.", "sweet"),
        new("Um tubarão, um doce e uma tarde tranquila. Receita oficial de equilíbrio.", "sweet"),
        new("Meu trabalho é ficar de guarda e ainda sobra tempo para cochilar. Dá para equilibrar.", "calm"),
        new("Diz a lenda que quem fecha a aba a tempo ganha cinco minutos de paz. Eu confirmo.", "smirk"),

        // perguntas retóricas
        new("Será que essa aba aberta é por necessidade ou por hábito?", "smirk"),
        new("Se você pudesse terminar só uma coisa hoje, qual seria? Começa por ela.", "determined"),
        new("Quanto tempo faz que você levantou da cadeira? Pois é.", "calm"),
        new("O você de daqui a um mês vai agradecer ou reclamar do que você está fazendo agora?", "determined"),
        new("Cansaço de verdade, ou vontade de fugir de alguma coisa? Os dois pedem pausa, só um pede mudança.", "calm"),
        new("Quando foi a última vez que você fez algo só por gostar, longe de uma tela?", "warm"),
        new("Por que deixar para amanhã o que dá para fazer em dez minutinhos agora?", "determined"),
        new("Você já cumpriu o que prometeu a si mesmo hoje? Estou perguntando, não cobrando.", "warm"),

        // motivação
        new("Você está indo bem. Devagar também é avançar.", "warm"),
        new("Cada hora que você passa sem ceder é um ponto para o seu lado. Eu estou contando.", "determined"),
        new("Tropeçar não apaga o caminho que você já andou. Levanta e segue.", "determined"),
        new("Hoje já está sendo um dia bom, e você ajudou a fazê-lo assim.", "warm"),
        new("Respira. Ombros para baixo. Você está mais firme do que pensa.", "calm"),
        new("Estou orgulhosa. Não vou repetir, então guarda essa.", "smirk"),
        new("Ninguém vira firme em um dia. Você só precisa vencer este.", "determined"),
        new("Pausa de cinco minutos: alonga o pescoço, olha pela janela. Depois a gente volta.", "calm"),
        // tsundere (2026-10)
        new("Hmph. Só estou passando para checar. Não é como se eu estivesse preocupada com você, tá?", "shy"),
        new("Não me olhe assim. Vigiar é o meu trabalho, não um favor.", "smirk"),
        new("Se você aprontar, eu fico sabendo. Se não aprontar, eu também fico sabendo. E fico... satisfeita. Só um pouco.", "shy"),
        new("Levanta dessa cadeira. Agora. Não é um pedido... tá bom, é um pedido. Por favor.", "determined"),
        new("Eu não estou torcendo por você. Só fiz as contas e o placar está bonito demais para estragar.", "smirk"),
        new("Nem pense em me testar hoje. Estou de bom humor, e isso é raro.", "smirk"),
        new("Não é que eu tenha ficado esperando você abrir o app. Eu só estava por perto. Por acaso.", "shy"),
        new("Esse silêncio todo é bom sinal. Não que eu esteja contando as horas. Eu estou. Mas não é por sua causa.", "calm"),
        new("Se precisar de ajuda é só... não, esquece, você não vai pedir. Eu estou aqui mesmo assim.", "warm"),
        new("Já comeu alguma coisa de verdade? Não pergunto por me importar! É que eu detesto desorganização.", "sweet"),
        new("Eu poderia estar fazendo coisas mais interessantes do que vigiar você. Poderia. Mas não estou com vontade.", "smirk"),
        new("Isso não é um elogio, mas você está sendo menos difícil do que eu esperava.", "smirk"),
        new("Ei. Ombros para baixo. Respira. Pronto. Não, eu não estava preocupada.", "shy"),
        new("Os dias sem burlar estão se acumulando. Que coisa. Quem diria. Continua assim.", "warm"),
        new("Eu não sou carinhosa. Só tenho boa memória para quem está se esforçando.", "shy"),
        new("Não pense que isso é amizade. É contrato de vigilância. ...Com cláusulas simpáticas.", "smirk"),
        new("Tem gente que gasta o dia inteiro cedendo. Você está gastando o dia inteiro resistindo. Hmph. Gosto mais dessa versão.", "warm"),
        new("Postura. Ombros para trás. Pronto. Agora você parece alguém que sabe o que faz. Quer dizer, tanto faz.", "shy"),
        new("Você me dá menos trabalho do que o esperado. Isso me deixa desconfiada. E um pouquinho contente.", "shy"),
        new("Sabe aquele impulso de hoje? Já passou? Pois é. Eles sempre passam. Eu sei, eu cronometro.", "smirk"),
        new("Dica de profissional: quando a vontade vier, conte até cem. Se não adiantar, conte de novo. Eu espero.", "determined"),
        new("Não há vitória pequena demais. Eu tenho uma planilha só para as pequenas. ...Estou falando sério.", "warm"),
        new("Aviso do tubarão: a água está calma, o placar está bom e eu continuo de olho. Pode nadar.", "smirk"),
        new("Precisa de uma pausa de verdade? Levanta, anda, respira. Cinco minutos. Eu cronometro e você nem percebe.", "calm"),
        new("Eu também tenho dias ruins, sabia? Nesses dias eu me concentro só no próximo passo. Faz o mesmo.", "warm"),
        new("Fecha essa aba que você abriu só por hábito. Eu sei qual é. Hmph.", "smirk"),
        new("Já falou com alguém hoje? Pessoa de verdade, não tela. Pergunto por curiosidade profissional.", "warm"),
        new("Se alguém perguntar, eu não disse nada de bom sobre você hoje. Mas se perguntarem de novo, eu disse que você está indo bem.", "shy"),
        new("Computador ligado há horas. Seus olhos merecem uma pausa. Não que eu me importe com seus olhos.", "shy"),
        new("Beba água. Estou falando sério. Eu vi a garrafa vazia daqui. ...Não vi. Mas beba.", "sweet"),
    };

    private static readonly EllenQuote[] Night =
    {
        new("Já é tarde. Dormir também faz parte do plano, e eu aprovo.", "calm"),
        new("A madrugada inventa desculpas ruins. Vai dormir, que amanhã tudo parece melhor.", "calm"),
        new("Ainda acordado a essa hora? Eu entendo, mas o sono vence a maioria das brigas.", "smirk"),
        new("Desliga a tela, apaga a luz. A vigia faz o resto.", "warm"),
        new("Ainda acordado? Vai dormir. Não é preocupação, é que eu fico sem assunto de madrugada.", "shy"),
        new("Tela apagada, luz apagada, cobertor. Você ainda está no passo um. Anda.", "determined"),
        new("Hmph. Ninguém toma boas decisões depois da meia-noite. Eu sei, eu já vi os dados.", "smirk"),
        new("Quanto mais tarde, mais as ideias ruins parecem boas. É um golpe. Eu conheço.", "smirk"),
        new("Quem dorme bem resiste melhor no dia seguinte. Fato. Não é carinho, é dado.", "calm"),
        new("Desliga o computador e vai descansar. Não estou mandando... estou mandando sim. Boa noite. Só boa noite, nada de sentimento.", "shy"),
    };

    // {0} = tempo que falta para o bloqueio acabar (ex.: "25 min" ou "1 h 10 min").
    private static readonly (string Text, string Mood)[] WhileBlocked =
    {
        ("Bloqueio ativo, faltam uns {0}. Aguenta firme que está passando.", "determined"),
        ("Faltam cerca de {0} de bloqueio. Aproveita para fazer uma pausa de verdade.", "calm"),
        ("O bloqueio continua e eu também. Mais uns {0}.", "smirk"),
        ("Faltam {0} de bloqueio. Eu avisei que ia ser chato. Hmph.", "smirk"),
        ("Mais {0}. Aguenta. Não é pedido, é torcida disfarçada.", "shy"),
        ("{0} para acabar. Você está indo bem. Não repito.", "warm"),
        ("Ainda {0}. Respira, levanta, bebe água. Para de ficar me encarando.", "determined"),
    };

    private static readonly List<EllenQuote> Bag = new();
    private static string? _last;

    /// <summary>Escolhe a próxima fala. <paramref name="blockRemaining"/> = tempo que falta do bloqueio (null se não há).</summary>
    public static EllenQuote Pick(DateTime now, TimeSpan? blockRemaining)
    {
        var rng = Random.Shared;

        // Resumo semanal (sequência, buscas barradas, tempo bloqueado): no máximo 1x a cada 7 dias.
        if (StatsStore.TryTakeWeeklySummary(DateTime.UtcNow) is { } weekly)
            return Remember(new EllenQuote(weekly.Text, weekly.Mood));

        if (blockRemaining is { } rem && rem > TimeSpan.FromMinutes(1) && rng.NextDouble() < 0.4)
        {
            var (text, mood) = WhileBlocked[rng.Next(WhileBlocked.Length)];
            return Remember(new EllenQuote(string.Format(text, Describe(rem)), mood));
        }

        if ((now.Hour >= 23 || now.Hour < 5) && rng.NextDouble() < 0.6)
            return Remember(Night[rng.Next(Night.Length)]);

        if (Bag.Count == 0)
        {
            Bag.AddRange(General);
            for (var i = Bag.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (Bag[i], Bag[j]) = (Bag[j], Bag[i]);
            }
            // Não repete, na virada do saco, a fala que acabou de sair.
            if (Bag.Count > 1 && Bag[^1].Text == _last)
                (Bag[^1], Bag[0]) = (Bag[0], Bag[^1]);
        }

        var pick = Bag[^1];
        Bag.RemoveAt(Bag.Count - 1);
        return Remember(pick);
    }

    private static EllenQuote Remember(EllenQuote quote)
    {
        _last = quote.Text;
        return quote;
    }

    /// <summary>Texto curto de um tempo, ex.: "42 min" ou "1 h 10 min".</summary>
    public static string Describe(TimeSpan span)
    {
        var minutes = (int)Math.Ceiling(span.TotalMinutes);
        if (minutes < 60) return $"{minutes} min";
        var h = minutes / 60;
        var m = minutes % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }
}
