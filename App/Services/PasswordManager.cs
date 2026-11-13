using System.Security.Cryptography;

namespace SharkBlocker.Services;

/// <summary>
/// Cria e verifica hashes de senha (PBKDF2-SHA256) para proteger ações críticas:
/// desinstalar, parar o serviço de bloqueio, ou apagar as listas de bloqueio.
/// Nunca guardamos a senha em texto puro.
/// </summary>
public static class PasswordManager
{
    // OWASP (2023) recomenda 600.000 iteracoes para PBKDF2-HMAC-SHA256. Hashes antigos (100.000)
    // continuam validos: a contagem vem gravada no proprio hash, e PasswordGate regrava no formato
    // novo depois de uma verificacao bem-sucedida (NeedsRehash).
    private const int Iterations = 600_000;
    public const int MinLength = 8;

    private static readonly HashSet<string> CommonPasswords = new(StringComparer.OrdinalIgnoreCase)
    {
        "12345678", "123456789", "1234567890", "87654321", "11111111", "00000000", "password", "password1",
        "senha123", "senha1234", "senhasenha", "qwertyui", "qwerty123", "abcd1234", "abc12345", "admin123"
    };

    /// <summary>Politica minima para uma senha NOVA. Devolve a mensagem do problema, ou null se estiver ok.</summary>
    public static string? ValidateNewPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return "A senha não pode ser vazia.";
        if (password.Length < MinLength) return $"A senha precisa ter pelo menos {MinLength} caracteres.";
        if (password.Distinct().Count() < 4) return "Use uma senha com mais variedade de caracteres.";
        if (CommonPasswords.Contains(password)) return "Essa senha é muito comum. Escolha outra.";
        return null;
    }

    /// <summary>true se o hash guardado usa menos iteracoes que o padrao atual.</summary>
    public static bool NeedsRehash(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('.');
        return parts.Length == 3 && int.TryParse(parts[0], out var it) && it < Iterations;
    }
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        try
        {
            var parts = stored.Split('.');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], out var iterations)) return false;

            var salt = Convert.FromBase64String(parts[1]);
            var expectedHash = Convert.FromBase64String(parts[2]);

            var actualHash = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expectedHash.Length);
            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (Exception ex)
        {
            // Hash armazenado corrompido/malformado — trata como senha errada
            // em vez de propagar a exceção (isso não devia derrubar o app).
            Logger.LogException("PasswordManager.Verify (hash armazenado malformado)", ex);
            return false;
        }
    }
}
