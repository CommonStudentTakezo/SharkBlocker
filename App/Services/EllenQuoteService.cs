namespace SharkBlocker.Services;

/// <summary>Uma frase da Ellen. <see cref="Mood"/> escolhe a expressão (arquivo Assets\ellen-{mood}.png).</summary>
public sealed record EllenQuote(string Text, string Mood);

/// <summary>
/// "Frase do Dia" da Ellen.
///
/// Como a frase é escolhida:
///  - Modo diário (padrão): a frase só depende da DATA. Abrir o app dez vezes no mesmo dia mostra a
///    mesma frase, e à meia-noite ela troca. As frases são embaralhadas em ciclos, então nenhuma
///    se repete até todas terem aparecido (e a última de um ciclo nunca é a primeira do seguinte).
///    Não grava nada em disco, então não há estado para corromper.
///  - Modo "a cada abertura": mude <see cref="NewQuoteOnEveryLaunch"/> para true e cada vez que o
///    app abrir sai uma frase diferente (a mesma durante toda a execução, a menos que a pessoa
///    peça "outra frase").
///
/// Expressões (mood): calm, determined, sweet, warm, smirk. Se o PNG da expressão não existir,
/// a interface usa lockscreen-mascot.png (ver ELLEN-ASSETS.md na raiz do projeto).
/// </summary>
public static class EllenQuoteService
{
    /// <summary>false = uma frase por dia (sempre a mesma na data). true (atual) = frase sorteada a cada abertura do programa.</summary>
    public static readonly bool NewQuoteOnEveryLaunch = true;

    public static readonly string[] Moods = { "calm", "determined", "sweet", "warm", "smirk", "shy" };

    // Frases originais, no tom da Ellen: sonolenta, direta, fã de doces, protetora sem ser dramática.
    // Foco em apoio e recomeço, sem culpa. Edite à vontade: é só uma lista.
    private static readonly EllenQuote[] Quotes =
    {
        // calm: calma, sem pressa
        new("Sem pressa. Hoje o objetivo é só chegar ao fim do dia inteiro.", "calm"),
        new("Respira fundo. A vontade passa, e eu continuo de olho enquanto isso.", "calm"),
        new("Dia tranquilo também é vitória. Nem tudo precisa ser épico.", "calm"),
        new("Descansar faz parte do trabalho. Dorme bem hoje, amanhã você agradece.", "calm"),
        new("Você não precisa resolver a vida inteira agora. Só a próxima hora.", "calm"),
        new("Silêncio, uma bebida quente e cinco minutos sem tela. Funciona mais do que parece.", "calm"),
        new("A maré sobe e desce. Você só precisa ficar firme enquanto ela passa.", "calm"),
        new("Cansaço é diferente de fraqueza. Descanse, mas não desista.", "calm"),
        new("Hoje pode ser um dia lento. Lento ainda é para a frente.", "calm"),
        new("O impulso é uma onda: sobe, chega ao pico e vai embora. Surfa sem cair.", "calm"),

        // determined: foco e disciplina
        new("Foco. Escolhe uma coisa importante e termina ela primeiro.", "determined"),
        new("Você já passou por dias piores do que este e continuou aqui. Isso conta.", "determined"),
        new("Disciplina é cumprir o combinado mesmo sem vontade. Eu sei que você consegue.", "determined"),
        new("Cada não que você diz ao impulso treina você para o próximo.", "determined"),
        new("Meta de hoje: cumprir o que você prometeu a si mesmo. Só isso.", "determined"),
        new("Não negocie com a vontade. Levanta, anda um pouco, volta com a cabeça clara.", "determined"),
        new("O seu tempo é o seu bem mais valioso. Gaste em algo que você respeite amanhã.", "determined"),
        new("Um passo pequeno ainda é um passo. Qual é o seu agora?", "determined"),
        new("Quem insiste em recomeçar já está na frente de quem desistiu.", "determined"),
        new("Constância vale mais que intensidade. Um pouco todo dia vence muito de vez em quando.", "determined"),

        // sweet: pausas, doces, autocuidado
        new("Bateu a vontade? Um docinho e um copo d'água. Sério, ajuda.", "sweet"),
        new("Ninguém aguenta o dia inteiro sem uma pausa doce. Faz a sua, do jeito que te faz bem.", "sweet"),
        new("Uma sobremesa de vez em quando é um bom prêmio por um dia bem cumprido.", "sweet"),
        new("Cuide de você como cuidaria de algo que ama. Comece comendo direito.", "sweet"),
        new("Pausa para um lanche. Estômago cheio, cabeça mais calma.", "sweet"),
        new("Pequenos prazeres saudáveis são o melhor reforço. Escolhe um e aproveita sem culpa.", "sweet"),
        new("Água, alongar, janela aberta. Três minutos que mudam o humor do resto da tarde.", "sweet"),

        // warm: acolhimento
        new("Estou do seu lado nessa. Você não precisa fingir que está tudo bem.", "warm"),
        new("Falar com alguém de confiança tira um peso enorme. Considera mandar aquela mensagem.", "warm"),
        new("Tropeçou? Tudo bem. O que importa é que você levantou e voltou.", "warm"),
        new("Você está fazendo mais do que imagina. Vai com calma consigo mesmo.", "warm"),
        new("Não se compare com ninguém. Compare com quem você era quando começou.", "warm"),
        new("Cada dia em que você escolhe cuidar de si é uma pequena prova de coragem.", "warm"),
        new("Pedir ajuda é força, não fraqueza. A porta está aberta.", "warm"),
        new("Seja gentil com você hoje. Quem se trata bem aguenta mais a estrada.", "warm"),
        new("Eu não vou te julgar. Vou só continuar de vigia, quantas vezes for preciso.", "warm"),
        new("Seu valor não depende de uma sequência perfeita de dias.", "warm"),
        new("Recomeçar não apaga o que você já construiu.", "warm"),
        new("A vergonha não ajuda a mudar. A curiosidade e a paciência ajudam.", "warm"),

        // smirk: leve, um pouco irônica
        new("Tentando me enganar? Eu fico acordada mais tempo do que você imagina.", "smirk"),
        new("Tubarão que para de nadar afunda. Continue se movendo, devagar que seja.", "smirk"),
        new("Eu vigio, você segue. Divisão de trabalho justa.", "smirk"),
        new("Dormir cedo e acordar com a cabeça limpa: o melhor truque que existe.", "smirk"),
        new("Dá para ser sério e ainda assim ter um dia leve. Experimenta.", "smirk"),
        new("O combinado era cumprir, não ser perfeito. Você está dentro do prazo.", "smirk"),
        new("Com o celular na mão sem saber por quê? Deixa ele longe por dez minutos.", "smirk"),
        new("Quem você quer ser daqui a um ano agradece cada escolha de hoje.", "smirk"),
        new("Liberdade não é fazer tudo o que dá vontade. É poder escolher.", "smirk"),
        new("Pergunta rápida: isso me aproxima ou me afasta de quem eu quero ser?", "smirk"),

        // tsundere (2026-10)
        new("Hmph. Dia tranquilo, placar limpo. Não que eu esteja satisfeita.", "calm"),
        new("Respira. Eu não estou preocupada, só estou aqui. É diferente.", "calm"),
        new("Hoje o plano é simples: chegar ao fim do dia inteiro. Eu fico de olho. Não por você.", "calm"),
        new("Sem pressa. Mas também sem desculpa. Tá, só um pouco de desculpa. Pouco.", "calm"),
        new("Durma bem hoje. Não é cuidado, é logística: gente descansada dá menos trabalho.", "calm"),
        new("Água, janela aberta, cinco minutos longe da tela. Não estou mandando... estou, sim.", "calm"),
        new("Quieta, observando. É o meu estado natural e eu gosto dele.", "calm"),
        new("Silêncio no log é a minha música favorita. Continua assim.", "calm"),
        new("Foco. Escolhe uma coisa e termina. E não me olha com essa cara, estou ajudando.", "determined"),
        new("Não negocie com a vontade. Eu já tentei e ela é péssima de conversa.", "determined"),
        new("Cada \"não\" que você diz ao impulso me poupa uma bronca. Então, por favor, continue dizendo.", "determined"),
        new("Cumpre o combinado. Eu não vou pedir de novo. ...Tá, vou pedir de novo. Mas é a última.", "determined"),
        new("Disciplina não é bonita, é útil. Seja útil hoje.", "determined"),
        new("Meta do dia: não me dar trabalho. Parece pouco, mas é ambicioso.", "determined"),
        new("Levanta, anda um pouco, volta com a cabeça no lugar. Isso é uma ordem, não um conselho.", "determined"),
        new("Você é capaz. Falei. Não vou repetir, então presta atenção.", "determined"),
        new("Água e um docinho. Não é carinho, é manutenção do sistema.", "sweet"),
        new("Pausa para o lanche. Eu escolho o doce, você escolhe o horário. Divisão justa.", "sweet"),
        new("Comeu direito hoje? Não pergunto por me importar! É que gente com fome toma decisões ruins.", "sweet"),
        new("Se for comer algo doce, divide comigo. ...Brincadeira. Quase.", "sweet"),
        new("Um chá, um doce e uma tarde sem tela. Receita perfeita, aprovada por mim.", "sweet"),
        new("Hmph. Já bebeu água? Bebe. Agora. Estou olhando.", "sweet"),
        new("Eu não ligo se você tropeçou. Ligo se você ficou no chão. Levanta.", "warm"),
        new("Você está indo bem, tá? Falei uma vez. Não vou repetir.", "warm"),
        new("Eu estou do seu lado. ...Quer dizer, do lado de cá da tela, vigiando. É parecido.", "warm"),
        new("Não precisa fingir que está tudo bem. Comigo não precisa. Mas conta só se quiser.", "warm"),
        new("Se quiser falar com alguém de confiança, fala. Eu não vou ficar triste. Nem com ciúme. Nem um pouco.", "warm"),
        new("Seu valor não depende de uma sequência perfeita. Isso eu sei, e você devia saber também.", "warm"),
        new("Estou orgulhosa. Esquece que eu disse isso.", "warm"),
        new("Recomeçar não é vergonha. Vergonha seria eu ter que te lembrar. ...Brincadeira. Recomeça.", "warm"),
        new("Hmph. Eu vigio, você segue. Divisão de trabalho justa. Não reclame.", "smirk"),
        new("Cuidado: eu mordo. Sou tubarão, lembra?", "smirk"),
        new("Esse jeito de quem vai aprontar não me engana. Já vi esse filme.", "smirk"),
        new("Se eu fosse você, deixava o celular longe por dez minutos. Mas eu sou eu, então eu deixo por você.", "smirk"),
        new("Não estou torcendo por você. Só não quero ver o placar estragado.", "smirk"),
        new("O combinado era cumprir, não ser perfeito. Você está dentro do prazo. Não se acostume.", "smirk"),
        new("Quem você quer ser daqui a um ano agradece cada escolha de hoje. Eu também. Quer dizer, tanto faz.", "smirk"),
        new("N-não é como se eu tivesse esperado você abrir o app. Eu só estava por perto.", "shy"),
        new("Não me olha assim! Eu só estou fazendo meu trabalho. Meu trabalho. Mais nada.", "shy"),
        new("Eu disse que não estava preocupada. Não estava. ...Só um pouquinho.", "shy"),
        new("Para de cumprir o combinado tão direitinho. Isso me deixa sem graça.", "shy"),
        new("Obrigada por hoje. ...Esquece. Não falei nada.", "shy"),
        new("Se você disser que eu sou legal, eu vou fingir que não ouvi. E vou lembrar para sempre.", "shy"),
        new("Eu não sou carinhosa! É só que eu... me acostumei com você por aqui.", "shy"),
        new("Tá, tá, eu gostei do dia de hoje. Foi... bom. Não conta pra ninguém.", "shy"),
    };

    private static EllenQuote? _launchQuote;

    /// <summary>A frase "oficial" do momento, conforme o modo configurado.</summary>
    public static EllenQuote Current(DateTime? now = null)
    {
        if (NewQuoteOnEveryLaunch)
            return _launchQuote ??= ShuffleBag.Next("quote", Quotes);

        return ForDate(DateOnly.FromDateTime(now ?? DateTime.Now));
    }

    /// <summary>Frase do dia para uma data (determinística: mesma data, mesma frase).</summary>
    public static EllenQuote ForDate(DateOnly date)
    {
        var count = Quotes.Length;
        var day = date.DayNumber;
        var cycle = day / count;
        var position = day % count;

        var order = ShuffledOrder(cycle, count);
        if (position == 0 && cycle > 0)
        {
            // Evita repetir, na virada de ciclo, a frase que acabou de sair no dia anterior.
            var previous = ShuffledOrder(cycle - 1, count)[count - 1];
            if (order[0] == previous)
                (order[0], order[1]) = (order[1], order[0]);
        }

        return Quotes[order[position]];
    }

    /// <summary>Uma frase aleatória diferente da atual (botão "outra frase").</summary>
    public static EllenQuote Another(string? currentText)
    {
        for (var i = 0; i < 8; i++)
        {
            var pick = ShuffleBag.Next("quote", Quotes);
            if (!string.Equals(pick.Text, currentText, StringComparison.Ordinal)) return pick;
        }
        return Quotes[0];
    }

    /// <summary>Embaralhamento estável (Fisher-Yates com semente fixa pelo número do ciclo).</summary>
    private static int[] ShuffledOrder(int cycle, int count)
    {
        var order = new int[count];
        for (var i = 0; i < count; i++) order[i] = i;

        var rng = new Random(unchecked(cycle * 7919 + 17));
        for (var i = count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }
}
