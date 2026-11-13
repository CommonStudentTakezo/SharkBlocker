namespace SharkBlocker.Services;

public enum PasswordCheck
{
    /// <summary>Senha correta.</summary>
    Ok,
    /// <summary>Senha incorreta.</summary>
    Wrong,
    /// <summary>Muitas tentativas erradas: aguardando o fim da espera.</summary>
    LockedOut,
    /// <summary>Ainda nao existe senha administrativa (a acao e RECUSADA, nunca liberada).</summary>
    NoPassword
}

/// <summary>
/// Ponto UNICO de verificacao da senha administrativa. Toda acao que desativa uma regra, remove uma
/// protecao, fecha o app ou desinstala passa por aqui (UninstallFlow.TryAuthorizeRemoval,
/// TryPerformProtectedUninstall e a troca de senha). Garante:
///
///  - A senha e OBRIGATORIA: sem senha definida a acao e recusada (fail-closed). Antes, quem nunca
///    definiu uma senha passava direto.
///  - Limite de tentativas: erros seguidos geram esperas crescentes (3 erros = 30 s, 5 = 2 min,
///    7 = 10 min, 10 ou mais = 30 min). O contador fica no Registro (HKLM), assinado com HMAC,
///    entao fechar e reabrir o app NAO zera a espera.
///  - Atualizacao do hash: um hash antigo (menos iteracoes) e regravado no padrao atual assim que a
///    senha correta e digitada.
///
/// Limite honesto: e uma barreira de atrito contra o impulso e contra tentativa e erro, nao contra
/// um Administrador que mexa direto no Registro/arquivos (isso vale para o projeto inteiro).
/// </summary>
public static class PasswordGate
{
    /// <summary>Tempo que ainda falta de espera por excesso de tentativas (zero = liberado).</summary>
    public static TimeSpan RemainingLockout()
    {
        var (_, until) = StateManager.GetPasswordThrottle();
        return until.HasValue && until.Value > DateTime.UtcNow ? until.Value - DateTime.UtcNow : TimeSpan.Zero;
    }

    public static PasswordCheck Verify(string? input, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;

        var state = StateManager.Load();
        if (!state.HasPassword) return PasswordCheck.NoPassword;

        wait = RemainingLockout();
        if (wait > TimeSpan.Zero) return PasswordCheck.LockedOut;

        if (PasswordManager.Verify(input ?? "", state.PasswordHash))
        {
            StateManager.SetPasswordThrottle(0, null);

            if (PasswordManager.NeedsRehash(state.PasswordHash))
            {
                try
                {
                    var fresh = StateManager.Load();
                    fresh.PasswordHash = PasswordManager.Hash(input!);
                    StateManager.Save(fresh);
                    Logger.Log("PasswordGate: hash da senha atualizado para o padrão atual de iterações.");
                }
                catch (Exception ex) { Logger.LogException("PasswordGate (atualizar hash)", ex); }
            }
            return PasswordCheck.Ok;
        }

        var (failures, _) = StateManager.GetPasswordThrottle();
        failures++;
        var delay = DelayFor(failures);
        StateManager.SetPasswordThrottle(failures, delay > TimeSpan.Zero ? DateTime.UtcNow + delay : null);
        Logger.Log($"Senha administrativa incorreta (erro nº {failures} seguido)" +
                   (delay > TimeSpan.Zero ? $" — nova tentativa só em {Describe(delay)}." : "."));
        wait = delay;
        return PasswordCheck.Wrong;
    }

    private static TimeSpan DelayFor(int failures) => failures switch
    {
        < 3 => TimeSpan.Zero,
        < 5 => TimeSpan.FromSeconds(30),
        < 7 => TimeSpan.FromMinutes(2),
        < 10 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(30)
    };

    public static string Describe(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{Math.Ceiling(t.TotalMinutes):0} min" : $"{Math.Max(1, Math.Ceiling(t.TotalSeconds)):0} s";
}
