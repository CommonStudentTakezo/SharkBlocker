using System.Windows;
using SharkBlocker.Dialogs;
using SharkBlocker.Services;
using MessageBox = SharkBlocker.Dialogs.ThemedMessageBox;

namespace SharkBlocker;

/// <summary>
/// Lógica única de "desinstalação protegida", usada em dois lugares:
///  1) Pelo botão "Desinstalar" dentro do próprio app.
///  2) Pelo desinstalador gerado pelo Inno Setup, que chama
///     "SharkBlocker.exe --uninstall" ANTES de apagar qualquer arquivo
///     (ver Installer.iss, InitializeUninstall). Se essa chamada retornar
///     código de saída diferente de 0, o Setup aborta a desinstalação
///     inteira — isso fecha a brecha que existiria se o desinstalador
///     padrão do Windows pudesse remover tudo sem pedir senha.
///
/// Ordem do fluxo (cada etapa só começa se a anterior passou):
///  1) Bloqueio temporizado ativo?  -> recusa.
///  2) TRAVA DE TELA (sempre, mesmo sem senha definida) de 30 minutos, assim que a pessoa pede pra desinstalar —
///     tempo pro impulso passar ANTES de qualquer decisão. Vale TODA vez. O fim fica gravado
///     (assinado), então fechar o app ou reiniciar o PC não zera a espera.
///  3) Senha (se houver).
///  4) Decisão final: a sequência de confirmações (ConfirmationGauntlet).
///  5) Limpeza de verdade.
/// </summary>
public static class UninstallFlow
{
    /// <summary>
    /// Autorização para REMOVER algo que protege (item de uma lista de bloqueio, as proteções
    /// permanentes). Vale TODA vez que a pessoa tenta: trava de tela de 30 minutos assim que pede,
    /// depois senha (se houver) e a sequência de confirmações. Só devolve true se passou por tudo.
    /// </summary>
    public static bool TryAuthorizeRemoval(Window? owner, string actionLabel, IReadOnlyList<string>? details = null)
    {
        var state = StateManager.Load();
        Logger.Log($"Tentativa de remoção: {actionLabel}" +
                   (details is { Count: > 0 } ? " — " + string.Join("; ", details.Take(20)) : ""));

        // A trava de tela de 30 minutos vem PRIMEIRO, assim que a pessoa pede — mesmo que ainda não
        // exista senha. (Antes, sem senha, o app só avisava e nem travava.)
        var lockUntil = StateManager.StartScreenLockIfNotActive(ScreenLock.PenaltyDuration, ScreenLock.ReasonRemoval);
        var (headline, message) = ScreenLock.TextFor(StateManager.Load().ScreenLockReason);
        ScreenLock.ShowUntil(lockUntil, headline, message);

        // SENHA OBRIGATÓRIA: sem senha definida a ação é RECUSADA (depois da trava).
        if (!state.HasPassword)
        {
            ShowPasswordRequiredNotice();
            return false;
        }

        if (!AskAndVerifyPassword(owner, $"Digite a senha para confirmar: {actionLabel}")) return false;

        return ConfirmationGauntlet.Run(owner, actionLabel);
    }

    /// <summary>
    /// Autorização para ALTERAR a agenda de bloqueios depois da primeira configuração: trava de tela de
    /// 30 minutos assim que a pessoa pede, depois a senha (obrigatória, fail-closed). Sem a sequência de
    /// confirmações. Só devolve true se passou por tudo.
    /// </summary>
    public static bool TryAuthorizeScheduleChange(Window? owner)
    {
        Logger.Log("Pedido para alterar os bloqueios agendados — trava de tela de 30 min.");

        var lockUntil = StateManager.StartScreenLockIfNotActive(ScreenLock.PenaltyDuration, ScreenLock.ReasonSchedule);
        var (headline, message) = ScreenLock.TextFor(StateManager.Load().ScreenLockReason);
        ScreenLock.ShowUntil(lockUntil, headline, message);

        if (!StateManager.Load().HasPassword)
        {
            ShowPasswordRequiredNotice();
            return false;
        }

        return AskAndVerifyPassword(owner, "Digite a senha para alterar os bloqueios agendados:");
    }

    private static void ShowPasswordRequiredNotice() =>
        MessageBox.Show(
            "Nenhuma senha administrativa foi definida. Por segurança, sem senha não é possível fechar o SharkBlocker, " +
            "remover itens, desativar proteções nem desinstalar.\n\nDefina uma senha no botão \"Definir senha\" e tente de novo.",
            "Senha obrigatória", MessageBoxButton.OK, MessageBoxImage.Information);

    /// <summary>
    /// Pede a senha e confere pelo ponto único (<see cref="PasswordGate"/>): limite de tentativas com
    /// espera crescente, sem liberar nada se a senha não existir. true = senha correta.
    /// </summary>
    private static bool AskAndVerifyPassword(Window? owner, string prompt)
    {
        var remaining = PasswordGate.RemainingLockout();
        if (remaining > TimeSpan.Zero)
        {
            MessageBox.Show($"Muitas tentativas incorretas. Nova tentativa liberada em {PasswordGate.Describe(remaining)}.",
                "Aguarde", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var dialog = new PasswordDialog(prompt);
        if (owner != null && owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dialog.ShowDialog() != true) return false;

        switch (PasswordGate.Verify(dialog.EnteredPassword, out var wait))
        {
            case PasswordCheck.Ok:
                return true;
            case PasswordCheck.NoPassword:
                ShowPasswordRequiredNotice();
                return false;
            case PasswordCheck.LockedOut:
                MessageBox.Show($"Muitas tentativas incorretas. Nova tentativa liberada em {PasswordGate.Describe(wait)}.",
                    "Aguarde", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            default:
                MessageBox.Show("Senha incorreta. Nada foi feito." +
                                (wait > TimeSpan.Zero ? $"\nNova tentativa liberada em {PasswordGate.Describe(wait)}." : ""),
                    "Erro", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
        }
    }

    /// <returns>true se autorizado e a limpeza (hosts/serviço/políticas) foi concluída.</returns>
    public static bool TryPerformProtectedUninstall(Window? owner)
    {
        var state = StateManager.Load();

        if (state.IsLocked)
        {
            MessageBox.Show(
                "Não é possível desinstalar enquanto o bloqueio estiver ativo. Espere o tempo acabar.",
                "Bloqueado", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Trava de tela de 30 minutos ANTES de qualquer decisão — TODA vez que a pessoa
        // pede pra desinstalar, tenha senha definida ou não. Se já há uma trava em andamento
        // (app reaberto no meio dela, ou trava por busca inapropriada), reaproveita o horário
        // que já estava gravado.
        var lockUntil = StateManager.StartScreenLockIfNotActive(ScreenLock.PenaltyDuration, ScreenLock.ReasonUninstall);
        var (headline, message) = ScreenLock.TextFor(StateManager.Load().ScreenLockReason);
        ScreenLock.ShowUntil(lockUntil, headline, message);

        // SENHA OBRIGATÓRIA: sem senha definida não há desinstalação (checado depois da trava).
        if (!StateManager.Load().HasPassword)
        {
            ShowPasswordRequiredNotice();
            return false;
        }

        if (!AskAndVerifyPassword(owner, "Digite a senha para confirmar a desinstalação:")) return false;

        // Senha certa não é o fim da história: uma sequência longa de
        // confirmações variadas vem depois, pra aumentar o atrito contra o
        // impulso do momento mesmo de quem sabe a senha (pedido explícito:
        // "é fácil demais desinstalar e só colocar a senha").
        if (!ConfirmationGauntlet.Run(owner, "Desinstalar"))
        {
            return false;
        }

        // Cada etapa da limpeza roda isolada: se uma falhar (ex.: antivírus segurando o hosts,
        // chave do Registro sem permissão), as outras continuam e o problema é mostrado no
        // final. Antes, uma exceção no meio deixava tudo pela metade e derrubava o app.
        var failed = new List<string>();
        void Step(string name, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                failed.Add(name);
                Logger.LogException($"UninstallFlow ({name})", ex);
            }
        }

        Step("remover o serviço", ServiceInstaller.Uninstall);
        Step("remover a abertura automática", AppStartupManager.UnregisterAutoStart);
        Step("liberar o arquivo hosts", HostsFileManager.Unprotect);
        Step("limpar sites bloqueados do hosts", HostsFileManager.ClearBlockedSites);
        Step("limpar o SafeSearch do hosts", HostsFileManager.ClearSafeSearchEntries);
        Step("remover restrições do navegador", BrowserPolicyManager.RemoveRestrictions);
        Step("remover regras de firewall do bloqueio", () => FirewallBlocker.RemoveAll(deep: true));
        Step("restaurar o DNS do Windows e as regras de DNS", DnsFilterController.RestoreSystemDns);
        Step("remover políticas permanentes do navegador", BrowserPolicyManager.RemovePermanentChromiumPolicies);
        Step("desinstalar a extensão", ExtensionInstaller.Uninstall);

        var installRoot = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(AppContext.BaseDirectory, ".."));
        Step("liberar a pasta de instalação", () => FolderProtection.Unprotect(installRoot));

        if (failed.Count > 0)
        {
            MessageBox.Show(
                "A desinstalação foi autorizada, mas estas etapas da limpeza falharam:\n\n• " +
                string.Join("\n• ", failed) +
                "\n\nDetalhes em:\n" + Logger.LogFilePath +
                "\n\nRode o app como Administrador e tente de novo, ou reverta essas partes à mão.",
                "Limpeza incompleta", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return true;
    }
}
