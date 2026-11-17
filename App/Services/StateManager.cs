using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SharkBlocker.Models;

namespace SharkBlocker.Services;

/// <summary>
/// Salva/lê o estado em dois lugares independentes — JSON (ProgramData) e
/// Registro (HKLM) — e usa o MAIOR horário de término válido entre os dois.
/// Cada valor é acompanhado de uma assinatura HMAC-SHA256 calculada com uma
/// chave embutida no binário. Isso significa que só "editar o número" no JSON
/// ou no Registro na mão não funciona mais para encurtar o bloqueio: sem
/// recalcular a assinatura corretamente (o que exige extrair a chave do
/// executável via engenharia reversa), aquele valor editado é tratado como
/// inválido e descartado — e a outra cópia (ainda íntegra) prevalece.
///
/// Limite honesto: isso NÃO é criptografia inquebrável — é ofuscação. Alguém
/// com conhecimento de engenharia reversa e tempo disponível pode extrair a
/// chave do binário compilado. O objetivo é impedir a edição trivial (abrir o
/// JSON/regedit e mudar um número), não resistir a um ataque sofisticado.
///
/// Sincronização: assim como o hosts, o state.json é lido/escrito por DOIS
/// processos (App e Watchdog) o tempo todo — usa o mesmo padrão de Mutex
/// nomeado pra evitar uma escrita corromper/apagar a outra.
///
/// Resiliência: se o JSON estiver corrompido por qualquer motivo (escrita
/// truncada por queda de energia, por exemplo), Load() não derruba o app —
/// loga o erro e recomeça com um estado novo, em vez de propagar a exceção
/// pra cima (isso já causou pelo menos um risco real de crash na abertura
/// do app, antes desta correção).
/// </summary>
public static class StateManager
{
    /// <summary>Duração da trava de tela punitiva / de autorização (a MESMA no App e no Watchdog).</summary>
    public static readonly TimeSpan ScreenLockPenalty = TimeSpan.FromMinutes(30);

    private static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SharkBlocker");

    private static readonly string StateFile = Path.Combine(DataDir, "state.json");

    // Migração do nome antigo do app: traz a pasta de dados (senha, listas, estado) e o registro
    // da trava para o nome novo, sem sobrescrever nada que já exista.
    static StateManager()
    {
        // Pasta de dados com permissao de menor privilegio (SYSTEM/Administradores escrevem,
        // Usuarios so leem). Ver DataDirectorySecurity.
        DataDirectorySecurity.Ensure(DataDir);

        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ColdTurkeyClone");
            if (Directory.Exists(legacyDir))
            {
                Directory.CreateDirectory(DataDir);
                foreach (var file in Directory.GetFiles(legacyDir))
                {
                    if (file.EndsWith(".flag", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(DataDir, Path.GetFileName(file));
                    if (!File.Exists(target)) File.Copy(file, target);
                }
            }

            using var oldKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\ColdTurkeyClone");
            if (oldKey != null)
            {
                using var newKey = Registry.LocalMachine.CreateSubKey(RegKeyPath);
                foreach (var name in oldKey.GetValueNames())
                {
                    if (newKey!.GetValue(name) == null)
                        newKey.SetValue(name, oldKey.GetValue(name)!, oldKey.GetValueKind(name));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager (migração do nome antigo)", ex);
        }
    }

    private const string RegKeyPath = @"SOFTWARE\SharkBlocker";
    private const string RegTicksValue = "LockedUntilUtcTicks";
    private const string RegSigValue = "LockSignature";

    // Trava de tela (desinstalar / busca inapropriada). Mesma ideia do lock acima, mas
    // com um "propósito" próprio dentro do HMAC — assim uma assinatura de um
    // não serve para forjar o outro.
    private const string RegScreenLockTicksValue = "ScreenLockTicks";
    private const string RegScreenLockSigValue = "ScreenLockSignature";
    private const string ScreenLockPurpose = "screen-lock";

    // Chave embutida no binário. Troque por bytes aleatórios seus antes de compilar
    // (isso NÃO é uma chave secreta de verdade — é ofuscação, não criptografia).
    private static readonly byte[] SigningKey =
        Convert.FromBase64String("k3F9zQpL8mR2vXo4TgYbN7hWc1JeSdAq6UiZtHfP0rMlKn5B");

    private static readonly Mutex StateMutex = new(false, "Global\\SharkBlocker_StateFileMutex");

    /// <summary>Mesmo mutex global do estado, para outros arquivos de dados (ex.: StatsStore) usarem sem criar outro.</summary>
    internal static T RunLocked<T>(Func<T> action) => WithStateLock(action);

    private static T WithStateLock<T>(Func<T> action)
    {
        var acquired = false;
        try
        {
            acquired = StateMutex.WaitOne(TimeSpan.FromSeconds(10));
            return action();
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
            return action();
        }
        finally
        {
            if (acquired)
            {
                try { StateMutex.ReleaseMutex(); } catch { }
            }
        }
    }

    public static BlockState Load() => WithStateLock(() =>
    {
        Directory.CreateDirectory(DataDir);

        // CORRIGIDO: state.json corrompido fazia o app recomeçar do ZERO — o que apagava a senha
        // (HasPassword virava false: desinstalar sem senha), as listas e as proteções permanentes.
        // Agora tenta o state.json e, se falhar, a cópia de segurança (state.json.bak) mantida
        // a cada gravação. Só recomeça do zero se as duas estiverem ilegíveis.
        BlockState state;
        if (File.Exists(StateFile) && TryReadStateCached(StateFile, out var main))
        {
            state = main!;
        }
        else if (AtomicFile.TryRecover(StateFile, IsValidStateJson, out var recoveredFrom) &&
                 TryReadState(StateFile, out var healed))
        {
            // AUTO-REPARO: restaura o state.json a partir da última cópia VÁLIDA (.bak ou instantâneo
            // datado em backups\) e já o regrava — antes só LIA o .bak e deixava o arquivo ruim no lugar.
            Logger.Log($"StateManager.Load: state.json ausente/ilegível — restaurado automaticamente de {Path.GetFileName(recoveredFrom!)}.");
            _cachedState = null;
            state = healed!;
        }
        else
        {
            state = new BlockState();
        }

        // SENHA: o hash tambem fica espelhado no Registro, assinado. Se o state.json perder o hash
        // (apagado/editado/restaurado de uma copia antiga), vale o espelho — assim apagar o campo
        // "PasswordHash" do JSON NAO desliga a senha. Mudar a senha grava o espelho ANTES do JSON.
        var mirroredHash = ReadPasswordHashMirror();
        if (mirroredHash != null && !string.Equals(state.PasswordHash, mirroredHash, StringComparison.Ordinal))
        {
            Logger.Log("StateManager.Load: o hash da senha no state.json estava ausente ou diferente do espelho assinado — usando o espelho.");
            state.PasswordHash = mirroredHash;
        }

        DateTime? jsonValid = (state.LockedUntilUtc.HasValue && Verify(state.LockedUntilUtc.Value.Ticks, state.LockSignature))
            ? state.LockedUntilUtc
            : null;

        var (regTicks, regSig) = ReadRegistryLock();
        DateTime? regValid = (regTicks.HasValue && Verify(regTicks.Value, regSig))
            ? new DateTime(regTicks.Value, DateTimeKind.Utc)
            : null;

        DateTime? finalUntil;
        if (jsonValid.HasValue && regValid.HasValue) finalUntil = jsonValid.Value > regValid.Value ? jsonValid : regValid;
        else finalUntil = jsonValid ?? regValid;

        state.LockedUntilUtc = finalUntil;
        state.LockSignature = finalUntil.HasValue ? Sign(finalUntil.Value.Ticks) : null;

        // Trava de tela: mesmo critério (maior valor com assinatura
        // válida entre JSON e Registro; valor adulterado é descartado).
        DateTime? screenLockJsonValid =
            (state.ScreenLockUntilUtc.HasValue &&
             VerifyPurpose(ScreenLockPurpose, state.ScreenLockUntilUtc.Value.Ticks, state.ScreenLockSignature))
                ? state.ScreenLockUntilUtc
                : null;

        var (slTicks, slSig) = ReadRegistryScreenLock();
        DateTime? screenLockRegValid =
            (slTicks.HasValue && VerifyPurpose(ScreenLockPurpose, slTicks.Value, slSig))
                ? new DateTime(slTicks.Value, DateTimeKind.Utc)
                : null;

        var screenLockFinal = MaxOf(screenLockJsonValid, screenLockRegValid);
        state.ScreenLockUntilUtc = screenLockFinal;
        state.ScreenLockSignature = screenLockFinal.HasValue
            ? SignPurpose(ScreenLockPurpose, screenLockFinal.Value.Ticks)
            : null;

        return state;
    });

    // EFICIÊNCIA: Load() é chamado várias vezes por segundo (janela, vigia de navegador, servidor
    // local, Watchdog). Com uma lista importada de dezenas de milhares de domínios, reler e
    // desserializar o JSON toda vez era caro. Agora o arquivo só é reanalisado quando muda
    // (tamanho ou data de gravação); senão devolve uma cópia rasa do último resultado.
    private static BlockState? _cachedState;
    private static string? _cachedPath;
    private static long _cachedLength;
    private static DateTime _cachedWriteUtc;

    private static bool TryReadStateCached(string path, out BlockState? state)
    {
        try
        {
            var info = new FileInfo(path);
            if (_cachedState != null && _cachedPath == path &&
                info.Length == _cachedLength && info.LastWriteTimeUtc == _cachedWriteUtc)
            {
                state = _cachedState.ShallowCopy();
                return true;
            }

            if (!TryReadState(path, out var parsed)) { state = null; return false; }

            _cachedState = parsed;
            _cachedPath = path;
            _cachedLength = info.Length;
            _cachedWriteUtc = info.LastWriteTimeUtc;
            state = parsed!.ShallowCopy();
            return true;
        }
        catch
        {
            _cachedState = null;
            return TryReadState(path, out state);
        }
    }

    private static bool TryReadState(string path, out BlockState? state)
    {
        try
        {
            state = JsonSerializer.Deserialize<BlockState>(AtomicFile.ReadAllText(path));
            return state != null;
        }
        catch (Exception ex)
        {
            Logger.LogException($"StateManager.Load ({Path.GetFileName(path)} ilegível)", ex);
            state = null;
            return false;
        }
    }

    /// <summary>Um state.json "bom" é um JSON que vira um BlockState (usado para validar escritas, .bak e restaurações).</summary>
    private static bool IsValidStateJson(string json)
    {
        try { return !string.IsNullOrWhiteSpace(json) && JsonSerializer.Deserialize<BlockState>(json) != null; }
        catch { return false; }
    }

    private static DateTime? MaxOf(DateTime? a, DateTime? b)
    {
        if (a.HasValue && b.HasValue) return a.Value > b.Value ? a : b;
        return a ?? b;
    }

    /// <summary>
    /// Salva o estado. O trava de tela NUNCA é apagado/encurtado
    /// por aqui: App e Watchdog salvam objetos "de memória" que podem estar
    /// ligeiramente desatualizados, e um Save antigo não pode desfazer um
    /// cooldown recém-iniciado pelo outro processo. Para iniciar ou encerrar
    /// o cooldown de verdade, use <see cref="SetScreenLock"/>.
    /// </summary>
    public static void Save(BlockState state) => SaveCore(state, overwriteScreenLock: false);

    /// <summary>
    /// Inicia uma trava de tela de <paramref name="duration"/> — a MENOS que já exista uma
    /// em andamento (aí devolve a que já existe, sem esticar nem reiniciar). Atômico entre
    /// App e Watchdog.
    /// </summary>
    public static DateTime StartScreenLockIfNotActive(TimeSpan duration, string reason) => WithStateLock(() =>
    {
        // Estatísticas (sequência, buscas barradas): registra a tentativa mesmo que já haja trava em andamento.
        // Best-effort e com dedup de 60 s; nunca lança.
        StatsStore.Record(reason);

        var state = Load();
        if (state.IsScreenLockActive) return state.ScreenLockUntilUtc!.Value;

        var until = DateTime.UtcNow + duration;
        state.ScreenLockUntilUtc = until;
        state.ScreenLockReason = reason;
        SaveCore(state, overwriteScreenLock: true);
        return until;
    });

    /// <summary>
    /// Trava de tela de duração FIXA pedida pela própria pessoa (compromisso de 1 h do remover-modo-teste.ps1). Se já houver trava
    /// que dura MAIS que esta, mantém; se dura menos, ESTENDE até agora + <paramref name="duration"/> (sem mudar o motivo). Nunca
    /// encurta uma trava existente. Não entra nas estatísticas.
    /// </summary>
    public static DateTime StartOrExtendScreenLock(TimeSpan duration, string reason) => WithStateLock(() =>
    {
        var state = Load();
        var until = DateTime.UtcNow + duration;
        if (state.IsScreenLockActive)
        {
            if (state.ScreenLockUntilUtc!.Value >= until) return state.ScreenLockUntilUtc.Value;
        }
        else
        {
            state.ScreenLockReason = reason;
        }
        state.ScreenLockUntilUtc = until;
        SaveCore(state, overwriteScreenLock: true);
        return until;
    });

    /// <summary>
    /// Trava por IMAGEM com punição em escada, de forma atômica: 1ª vez 5 min, 2ª 15 min, da 3ª em diante 30 min
    /// (<see cref="ScreenLockPenalty"/>). A contagem fica no estado (reabrir o app não zera) e recomeça depois de 24 h sem
    /// nenhuma trava por imagem. Se já houver trava em andamento, só registra a estatística e não conta como nova.
    /// </summary>
    public static DateTime StartImageScreenLock(string reason) => WithStateLock(() =>
    {
        var state = Load();
        if (state.IsScreenLockActive) return StartScreenLockIfNotActive(ScreenLockPenalty, reason);

        var now = DateTime.UtcNow;
        if (!state.ImageStrikeUtc.HasValue || now - state.ImageStrikeUtc.Value > TimeSpan.FromHours(24))
            state.ImageStrikes = 0;
        state.ImageStrikes++;
        state.ImageStrikeUtc = now;
        SaveCore(state, overwriteScreenLock: false);

        var duration = state.ImageStrikes switch
        {
            1 => TimeSpan.FromMinutes(5),
            2 => TimeSpan.FromMinutes(15),
            _ => ScreenLockPenalty,
        };
        return StartScreenLockIfNotActive(duration, reason);
    });

    /// <summary>Define (ou, com null, encerra) a trava de tela, de forma atômica.</summary>
    public static void SetScreenLock(DateTime? untilUtc) => WithStateLock(() =>
    {
        var state = Load();
        state.ScreenLockUntilUtc = untilUtc;
        if (!untilUtc.HasValue) state.ScreenLockReason = null;
        SaveCore(state, overwriteScreenLock: true);
        return true;
    });

    private static void SaveCore(BlockState state, bool overwriteScreenLock) => WithStateLock(() =>
    {
        try
        {
            Directory.CreateDirectory(DataDir);

            state.LockSignature = state.LockedUntilUtc.HasValue ? Sign(state.LockedUntilUtc.Value.Ticks) : null;

            if (!overwriteScreenLock)
            {
                // Preserva o cooldown já gravado (o maior entre o do objeto e o do disco/Registro).
                var persisted = Load().ScreenLockUntilUtc;
                state.ScreenLockUntilUtc = MaxOf(state.ScreenLockUntilUtc, persisted);
            }

            if (state.ScreenLockUntilUtc.HasValue && state.ScreenLockUntilUtc.Value <= DateTime.UtcNow)
            {
                state.ScreenLockUntilUtc = null; // já expirou — não precisa guardar
            }

            state.ScreenLockSignature = state.ScreenLockUntilUtc.HasValue
                ? SignPurpose(ScreenLockPurpose, state.ScreenLockUntilUtc.Value.Ticks)
                : null;

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

            // Espelho da senha PRIMEIRO: se a escrita do JSON falhar, o Registro ja tem o hash novo.
            if (!string.IsNullOrEmpty(state.PasswordHash)) WritePasswordHashMirror(state.PasswordHash!);

            // Escrita atômica e validada (temporário exclusivo -> disco -> valida -> troca). A versão
            // anterior só vira .bak se ela própria for válida, e instantâneos datados ficam em backups\.
            // Ver AtomicFile (FileShare.Read | Delete na leitura, novas tentativas em conflito).
            AtomicFile.WriteAllTextAtomic(StateFile, json, IsValidStateJson);

            if (state.LockedUntilUtc.HasValue)
                WriteRegistryLock(state.LockedUntilUtc.Value, state.LockSignature!);
            else
                ClearRegistryLock();

            if (state.ScreenLockUntilUtc.HasValue)
                WriteRegistryScreenLock(state.ScreenLockUntilUtc.Value, state.ScreenLockSignature!);
            else
                ClearRegistryScreenLock();
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.Save", ex);
        }

        return true;
    });

    private static string Sign(long ticks)
    {
        var hash = HMACSHA256.HashData(SigningKey, BitConverter.GetBytes(ticks));
        return Convert.ToBase64String(hash);
    }

    private static bool Verify(long ticks, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        try
        {
            var expected = HMACSHA256.HashData(SigningKey, BitConverter.GetBytes(ticks));
            var actual = Convert.FromBase64String(signature);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private static string SignPurpose(string purpose, long ticks)
    {
        var data = Encoding.UTF8.GetBytes($"{purpose}|{ticks}");
        return Convert.ToBase64String(HMACSHA256.HashData(SigningKey, data));
    }

    private static bool VerifyPurpose(string purpose, long ticks, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        try
        {
            var expected = HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes($"{purpose}|{ticks}"));
            var actual = Convert.FromBase64String(signature);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    private static (long? ticks, string? sig) ReadRegistryScreenLock()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath);
            var ticksVal = key?.GetValue(RegScreenLockTicksValue) as string;
            var sigVal = key?.GetValue(RegScreenLockSigValue) as string;
            if (ticksVal != null && long.TryParse(ticksVal, out var ticks))
                return (ticks, sigVal);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.ReadRegistryScreenLock", ex);
        }
        return (null, null);
    }

    private static void WriteRegistryScreenLock(DateTime untilUtc, string signature)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKeyPath);
            key?.SetValue(RegScreenLockTicksValue, untilUtc.Ticks.ToString(), RegistryValueKind.String);
            key?.SetValue(RegScreenLockSigValue, signature, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.WriteRegistryScreenLock", ex);
        }
    }

    // ---------- Senha: espelho assinado do hash e limitador de tentativas ----------

    private const string RegPwdHashValue = "PasswordHashMirror";
    private const string RegPwdHashSigValue = "PasswordHashMirrorSig";
    private const string RegPwdCountValue = "PasswordFailCount";
    private const string RegPwdUntilValue = "PasswordLockUntilTicks";
    private const string RegPwdThrottleSigValue = "PasswordThrottleSig";
    private const string PwdHashPurpose = "password-hash";
    private const string PwdThrottlePurpose = "password-throttle";

    private static string SignText(string purpose, string text) =>
        Convert.ToBase64String(HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes($"{purpose}|{text}")));

    private static bool VerifyText(string purpose, string text, string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        try
        {
            var expected = HMACSHA256.HashData(SigningKey, Encoding.UTF8.GetBytes($"{purpose}|{text}"));
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(signature));
        }
        catch { return false; }
    }

    private static string? ReadPasswordHashMirror()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath);
            var hash = key?.GetValue(RegPwdHashValue) as string;
            var sig = key?.GetValue(RegPwdHashSigValue) as string;
            return !string.IsNullOrEmpty(hash) && VerifyText(PwdHashPurpose, hash, sig) ? hash : null;
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.ReadPasswordHashMirror", ex);
            return null;
        }
    }

    private static void WritePasswordHashMirror(string hash)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKeyPath);
            key?.SetValue(RegPwdHashValue, hash, RegistryValueKind.String);
            key?.SetValue(RegPwdHashSigValue, SignText(PwdHashPurpose, hash), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.WritePasswordHashMirror", ex);
        }
    }

    /// <summary>Erros de senha seguidos e ate quando a tentativa esta suspensa. Valores adulterados (assinatura invalida) = pior caso, por 30 min.</summary>
    public static (int Failures, DateTime? LockedUntilUtc) GetPasswordThrottle()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath);
            if (key == null) return (0, null);
            var countText = key.GetValue(RegPwdCountValue) as string;
            var untilText = key.GetValue(RegPwdUntilValue) as string;
            var sig = key.GetValue(RegPwdThrottleSigValue) as string;
            if (countText == null && untilText == null && sig == null) return (0, null);

            if (!int.TryParse(countText, out var count) || !long.TryParse(untilText ?? "0", out var ticks) ||
                !VerifyText(PwdThrottlePurpose, $"{countText}|{untilText}", sig))
            {
                Logger.Log("StateManager: contador de tentativas de senha adulterado — aplicando espera máxima.");
                var until = DateTime.UtcNow.AddMinutes(30);
                SetPasswordThrottle(10, until); // regrava assinado: a espera vence sozinha, sem bloqueio eterno
                return (10, until);
            }
            return (count, ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : null);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.GetPasswordThrottle", ex);
            return (0, null);
        }
    }

    public static void SetPasswordThrottle(int failures, DateTime? lockedUntilUtc)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKeyPath);
            if (key == null) return;
            if (failures <= 0)
            {
                key.DeleteValue(RegPwdCountValue, false);
                key.DeleteValue(RegPwdUntilValue, false);
                key.DeleteValue(RegPwdThrottleSigValue, false);
                return;
            }
            var count = failures.ToString();
            var until = (lockedUntilUtc?.Ticks ?? 0).ToString();
            key.SetValue(RegPwdCountValue, count, RegistryValueKind.String);
            key.SetValue(RegPwdUntilValue, until, RegistryValueKind.String);
            key.SetValue(RegPwdThrottleSigValue, SignText(PwdThrottlePurpose, $"{count}|{until}"), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.SetPasswordThrottle", ex);
        }
    }

    private static void ClearRegistryScreenLock()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath, writable: true);
            key?.DeleteValue(RegScreenLockTicksValue, throwOnMissingValue: false);
            key?.DeleteValue(RegScreenLockSigValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.ClearRegistryScreenLock", ex);
        }
    }

    private static (long? ticks, string? sig) ReadRegistryLock()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath);
            var ticksVal = key?.GetValue(RegTicksValue) as string;
            var sigVal = key?.GetValue(RegSigValue) as string;
            if (ticksVal != null && long.TryParse(ticksVal, out var ticks))
                return (ticks, sigVal);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.ReadRegistryLock", ex);
        }
        return (null, null);
    }

    private static void WriteRegistryLock(DateTime untilUtc, string signature)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(RegKeyPath);
            key?.SetValue(RegTicksValue, untilUtc.Ticks.ToString(), RegistryValueKind.String);
            key?.SetValue(RegSigValue, signature, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.WriteRegistryLock", ex);
        }
    }

    private static void ClearRegistryLock()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegKeyPath, writable: true);
            key?.DeleteValue(RegTicksValue, throwOnMissingValue: false);
            key?.DeleteValue(RegSigValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.LogException("StateManager.ClearRegistryLock", ex);
        }
    }
}
