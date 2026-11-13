using System.Windows;
using SharkBlocker.Dialogs;

namespace SharkBlocker;

/// <summary>
/// Depois que a senha já foi confirmada, mostra uma sequência longa de
/// confirmações — cada uma com uma pergunta DIFERENTE — antes de deixar uma
/// ação crítica (desinstalar, desativar proteções permanentes) prosseguir.
/// Qualquer "Não" cancela tudo na hora. Inspirado no StayFocusd: a senha
/// impede QUEM não deveria mexer; isso aqui aumenta o atrito pra você mesmo,
/// no momento de impulso, mesmo sabendo a senha — clicar "Sim" umas 100
/// vezes seguidas, cada vez numa pergunta diferente, é tedioso o bastante
/// pra dar tempo do impulso passar.
///
/// 10 aberturas x 10 perguntas = 100 combinações distintas, embaralhadas a
/// cada tentativa (pra não virar decoreba de clicar sem ler). Tudo numa janela só
/// (<see cref="GauntletWindow"/>), com contador e barra de progresso.
/// </summary>
public static class ConfirmationGauntlet
{
    private static readonly string[] Openers =
    {
        "Antes de continuar:",
        "Pensa bem:",
        "Uma pergunta rápida:",
        "Sério mesmo?",
        "Ok, mas antes disso:",
        "Última chance de reconsiderar:",
        "Vamos com calma:",
        "Uma coisa de cada vez:",
        "Espera um segundo:",
        "Antes de qualquer coisa:",
    };

    private static readonly string[] Questions =
    {
        "você tem certeza que quer continuar com isso agora?",
        "o motivo que te fez configurar essa proteção ainda existe?",
        "como você vai se sentir sobre essa decisão daqui a uma hora?",
        "essa é uma decisão pensada, ou um impulso do momento?",
        "você já resistiu até aqui — vale a pena desistir bem agora?",
        "essa é a versão de você que quer decidir isso, ou é o impulso falando?",
        "vale a pena trocar esse progresso por um momento de facilidade?",
        "você vai se orgulhar dessa decisão amanhã de manhã?",
        "isso resolve o problema de verdade, ou só adia ele?",
        "quer mesmo abrir mão da proteção que você mesmo escolheu ter?",
    };

    /// <returns>true se o usuário confirmou TODAS as perguntas; false se cancelou em qualquer uma delas.</returns>
    public static bool Run(Window? owner, string actionLabel)
    {
        var combinations = new List<string>();
        foreach (var opener in Openers)
            foreach (var question in Questions)
                combinations.Add($"{opener} {question}");

        // Embaralha pra não virar um padrão decorável de cliques.
        var rng = Random.Shared;
        for (var i = combinations.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (combinations[i], combinations[j]) = (combinations[j], combinations[i]);
        }

        // Uma janela só, com contador e barra de progresso (antes eram 100 caixas de mensagem
        // abrindo e fechando). O "Não" é o botão padrão: Enter ou Esc cancelam tudo.
        var window = new GauntletWindow(actionLabel, combinations);

        // Só usa a janela dona se ela está de fato visível; no fluxo "--uninstall" (chamado pelo
        // desinstalador do Windows) não existe janela principal e o diálogo abre no centro da tela.
        var ownerUsable = owner != null && owner.IsVisible && owner.IsLoaded &&
                          owner.WindowState != WindowState.Minimized;
        if (ownerUsable)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Topmost = true;
            window.ShowInTaskbar = true;
        }

        window.ShowDialog();
        return window.Completed;
    }
}
