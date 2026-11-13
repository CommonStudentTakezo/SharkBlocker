namespace SharkBlocker.Dialogs;

/// <summary>
/// Frases mostradas na tela de trava. Uma é sorteada a cada trava (e é a mesma em todos os
/// monitores daquela trava). Edite à vontade: é só uma lista de textos.
/// </summary>
public static class MotivationalQuotes
{
    // Frases originais, no tom da Ellen Joe (sonolenta, direta, fã de doces, eficiente quando precisa).
    private static readonly string[] Quotes =
    {
        "Trabalho é trabalho. Mas descansar direito também faz parte do serviço.",
        "A vontade é como fome de madrugada: parece enorme e passa em alguns minutos.",
        "Respira. Eu fico de vigia, você não precisa decidir nada agora.",
        "Um docinho e um copo d'água resolvem mais impulsos do que parece.",
        "Tubarão não precisa correr atrás de tudo. Escolhe o que importa e vai.",
        "Você não está perdendo nada. Está ganhando o seu tempo de volta.",
        "Cada \"não\" de hoje deixa o de amanhã mais leve.",
        "O impulso é uma onda: sobe, chega ao pico e vai embora. Fica na areia até ela passar.",
        "Quem você quer ser daqui a um ano está sendo decidido agora.",
        "Disciplina é escolher o que você mais quer em vez do que você quer agora.",
        "Recomeçar não é fracassar. Desistir de tentar é que seria.",
        "Levanta, alonga, olha pela janela. Em poucos minutos a vontade perde a força.",
        "Você já superou dias difíceis antes. Este também passa.",
        "O seu eu do futuro agradece cada impulso que você deixa passar.",
        "Não precisa ser perfeito. Precisa ser mais um dia no caminho certo.",
        "O desconforto de agora é o preço da liberdade de amanhã.",
        "Ninguém está olhando, e é justamente aí que o seu caráter se constrói.",
        "Um deslize não apaga o progresso. Levanta e segue.",
        "Troque o impulso por um passo: caminhe um pouco, mude de ambiente.",
        "Foco é dizer não a muita coisa para dizer sim ao que importa.",
        "Estes minutos são um presente: use para pensar no que realmente importa.",
        "Você é mais forte do que esse impulso.",
        "Pequenas vitórias diárias constroem grandes mudanças.",
        "Quando bater a vontade, lembre por que você começou.",
        "Quem controla o próprio tempo controla a própria vida.",
        "Você não precisa de motivação, precisa de um próximo passo pequeno. Qual é o seu?",
        "A pessoa que você quer ser já está do outro lado desta espera.",
        "Cuidar de si mesmo também é saber parar.",
        "Ligue para alguém querido ou escreva o que está sentindo. Pôr para fora tira o peso.",
        "Não é sobre nunca tropeçar. É sobre não ficar no chão.",
        // tsundere (2026-10)
        "Respira. Eu não estou aqui para te julgar... só para te impedir. É diferente.",
        "Essa vontade passa. Eu sei porque já vi passar. Várias vezes. Só aguenta uns minutos.",
        "Não é castigo. É... uma pausa obrigatória. Com a minha supervisão. De graça.",
        "Você já passou por dias difíceis antes. Eu não vou deixar este te ganhar. Não por você. Pelo meu placar.",
        "Eu não estou preocupada. Só não quero o trabalho de recomeçar a contagem.",
        "Fica aí. Respira fundo. Eu seguro a porta.",
        "A vontade é uma onda: sobe, chega ao pico e vai embora. Eu fico de boia ao seu lado. Só por esse tempo.",
        "Pode ficar com raiva de mim. Eu aguento. Mas depois você vai me agradecer. Eu vou fingir que não ouvi.",
        "Se doer um pouco agora, é porque está funcionando. Hmph. Não me peça para suavizar.",
        "A vontade é uma visita. Dá água, não dá cadeira. Ela vai embora.",
        "Se estiver difícil, tudo bem. Dificuldade não é derrota. Derrota é desistir de tentar, e você ainda está aqui.",
        "Bebe um copo de água. Sério. Agora. A vontade diminui, eu juro. ...Não que eu faça promessas à toa.",
        "Se a mente inventar uma desculpa boa, desconfia. As melhores desculpas são as mais bem escritas.",
        "Eu não vou te dar um discurso. Só vou ficar aqui até a vontade cansar de esperar.",
        "Já fez algo bom por si hoje? Se não fez, faz quando a trava acabar: uma caminhada, um banho, um telefonema. Eu anoto.",
    };

    // Primeira tela da trava (Ellen brava): manda sair, ir aprender algo e não pesquisar essas coisas.
    private static readonly string[] AngryQuotes =
    {
        "Sai daqui. Vai estudar, aprender alguma coisa que preste e para de pesquisar essas coisas.",
        "Sério? De novo? Fecha isso, levanta e vai fazer algo útil. Pesquisar isso aí não vai rolar.",
        "Nem tenta. Vai ler, aprender algo novo, sla, qualquer coisa melhor do que isso.",
        "Eu tô de olho. Larga isso, vai aprender alguma coisa e esquece essa pesquisa.",
        "Chega. Sai do computador, vai usar a cabeça pra algo bom e não volta pra procurar essas coisas.",
        "Sai daqui. Vai estudar. Não é que eu esteja preocupada com você: eu detesto ver desperdício.",
        "Hmph! De novo? Eu vi. Levanta, anda um pouco e esquece isso.",
        "Nem tenta me enganar. Vai ler algo, fazer algo útil e para de procurar essas coisas.",
        "Eu estou brava. Não com você... tá, com você. Mas só um pouco.",
        "Peguei. E não adianta pedir desculpa para mim: peça para o seu eu de amanhã.",
    };

    public static string Pick() => SharkBlocker.Services.ShuffleBag.Next("lockQuotes", Quotes);

    public static string PickAngry() => SharkBlocker.Services.ShuffleBag.Next("lockAngry", AngryQuotes);
}
