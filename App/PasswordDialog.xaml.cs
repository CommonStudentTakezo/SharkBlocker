using System.Windows;
using System.Windows.Input;

namespace SharkBlocker;

public partial class PasswordDialog : Window
{
    public string EnteredPassword { get; private set; } = "";

    public PasswordDialog(string prompt)
    {
        InitializeComponent();
        PromptText.Text = prompt;

        // CORRIGIDO: chamar Focus() dentro do construtor não funciona de forma confiável
        // (a janela ainda nem existe na tela), então a pessoa precisava clicar no campo.
        // Agora o foco vai pro campo da senha quando a janela termina de carregar.
        Loaded += (_, _) =>
        {
            Activate();
            PasswordInput.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        EnteredPassword = PasswordInput.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch { /* o clique pode terminar antes do arrasto começar */ }
    }
}
