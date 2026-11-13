using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SharkBlocker.Services;

/// <summary>
/// Leitura/escrita SEGURA dos arquivos de configuração e dados (state.json e marcadores), pensada
/// para dois processos (App e Watchdog) mexendo na mesma pasta o tempo todo e para ferramentas de
/// terceiros (antivírus, backup, sincronizadores, editores) que abrem arquivos sem avisar.
///
///  - LEITURA: abre com FileAccess.Read + FileShare.Read | FileShare.Delete. Outros podem LER ao
///    mesmo tempo, ninguém consegue ESCREVER dentro do arquivo enquanto ele é lido, e a troca
///    atômica (rename) feita pelo escritor não é bloqueada.
///  - ESCRITA: grava num temporário exclusivo (FileShare.None), força ao disco (Flush(true)),
///    VALIDA o que foi gravado e só então troca pelo arquivo final (File.Replace/Move). Quem lê
///    vê sempre o arquivo antigo completo ou o novo completo — nunca um meio-termo.
///  - TENTATIVAS: violações de compartilhamento (outro programa com o arquivo aberto por instantes)
///    são repetidas algumas vezes com espera curta antes de falhar.
///  - BACKUP: a versão atual só vira ".bak" se ela própria for VÁLIDA (antes o File.Replace copiava
///    qualquer coisa, inclusive um arquivo já corrompido, por cima da última cópia boa). Além do
///    .bak, mantém instantâneos datados em "backups\" (no máximo 10, no máximo 1 a cada 10 min).
///  - RECUPERAÇÃO: se o arquivo sumiu ou está ilegível, restaura automaticamente do .bak ou do
///    instantâneo válido mais recente; o arquivo ruim é guardado como ".corrupt-*" (até 3) para análise.
/// </summary>
public static class AtomicFile
{
    private const int MaxAttempts = 6;
    private const int MaxSnapshots = 10;
    private const int MaxQuarantined = 3;
    private static readonly TimeSpan SnapshotEvery = TimeSpan.FromMinutes(10);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Lê o arquivo inteiro sem atrapalhar quem escreve (FileShare.Read | Delete) e com novas tentativas.</summary>
    public static string ReadAllText(string path) => Retry(() =>
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(fs, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    });

    /// <summary>Escrita atômica simples (marcadores pequenos): temporário + troca. Sem backup.</summary>
    public static void WriteSmall(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = TempName(path);
        try
        {
            WriteTemp(tmp, content);
            Swap(tmp, path);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// Escrita atômica de arquivo CRÍTICO. <paramref name="isValid"/> é usado para (1) conferir o que
    /// acabou de ser gravado antes de trocar e (2) decidir se a versão atual merece virar .bak.
    /// </summary>
    public static void WriteAllTextAtomic(string path, string content, Func<string, bool>? isValid = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = TempName(path);
        try
        {
            WriteTemp(tmp, content);

            if (isValid != null && !isValid(ReadAllText(tmp)))
                throw new IOException($"O conteudo gravado em {Path.GetFileName(path)} nao passou na validacao; o arquivo atual foi mantido.");

            PromoteCurrentToBackup(path, isValid);
            Swap(tmp, path);
        }
        finally { TryDelete(tmp); }

        try { SnapshotIfDue(path); }
        catch (Exception ex) { Logger.LogException($"AtomicFile.SnapshotIfDue ({Path.GetFileName(path)})", ex); }
    }

    /// <summary>
    /// Restaura <paramref name="path"/> a partir do .bak ou do instantâneo VÁLIDO mais recente.
    /// Devolve true (e o caminho usado em <paramref name="source"/>) se conseguiu.
    /// </summary>
    public static bool TryRecover(string path, Func<string, bool> isValid, out string? source)
    {
        source = null;
        foreach (var candidate in Candidates(path))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var text = ReadAllText(candidate);
                if (!isValid(text)) continue;

                Quarantine(path);
                var tmp = TempName(path);
                try
                {
                    WriteTemp(tmp, text);
                    Swap(tmp, path);
                }
                finally { TryDelete(tmp); }

                source = candidate;
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogException($"AtomicFile.TryRecover (candidato {Path.GetFileName(candidate)})", ex);
            }
        }
        return false;
    }

    // ------------------------------------------------------------------ internos

    private static string TempName(string path) => $"{path}.{Environment.ProcessId}.tmp";

    private static string BackupsDir(string path) => Path.Combine(Path.GetDirectoryName(path)!, "backups");

    private static void WriteTemp(string tmp, string content) => Retry(() =>
    {
        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        var bytes = Utf8NoBom.GetBytes(content);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(true); // força ao disco: queda de energia logo depois não deixa o arquivo vazio
        return 0;
    });

    private static void Swap(string tmp, string path) => Retry(() =>
    {
        if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        else File.Move(tmp, path);
        return 0;
    });

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temporario: o proximo ciclo sobrescreve */ }
    }

    private static void PromoteCurrentToBackup(string path, Func<string, bool>? isValid)
    {
        try
        {
            if (!File.Exists(path)) return;
            var current = ReadAllText(path);
            if (isValid != null && !isValid(current))
            {
                Logger.Log($"AtomicFile: {Path.GetFileName(path)} atual esta invalido — a ultima copia de seguranca boa (.bak) foi preservada.");
                return;
            }

            var bak = path + ".bak";
            var tmp = TempName(bak);
            try
            {
                WriteTemp(tmp, current);
                Swap(tmp, bak);
            }
            finally { TryDelete(tmp); }
        }
        catch (Exception ex)
        {
            Logger.LogException($"AtomicFile.PromoteCurrentToBackup ({Path.GetFileName(path)})", ex);
        }
    }

    private static void SnapshotIfDue(string path)
    {
        var dir = BackupsDir(path);
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var existing = Directory.GetFiles(dir, name + ".*" + ext).OrderByDescending(f => f, StringComparer.Ordinal).ToList();

        if (existing.Count > 0 && DateTime.UtcNow - File.GetLastWriteTimeUtc(existing[0]) < SnapshotEvery) return;

        var snap = Path.Combine(dir, $"{name}.{DateTime.UtcNow:yyyyMMdd-HHmmss}{ext}");
        File.Copy(path, snap, overwrite: true);

        foreach (var old in existing.Skip(MaxSnapshots - 1))
        {
            try { File.Delete(old); } catch { }
        }
    }

    private static List<string> Candidates(string path)
    {
        var list = new List<string> { path + ".bak" };
        try
        {
            var dir = BackupsDir(path);
            if (Directory.Exists(dir))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var ext = Path.GetExtension(path);
                list.AddRange(Directory.GetFiles(dir, name + ".*" + ext).OrderByDescending(f => f, StringComparer.Ordinal));
            }
        }
        catch (Exception ex) { Logger.LogException("AtomicFile.Candidates", ex); }
        return list;
    }

    private static void Quarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.Move(path, $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}", overwrite: true);

            var dir = Path.GetDirectoryName(path)!;
            var old = Directory.GetFiles(dir, Path.GetFileName(path) + ".corrupt-*").OrderByDescending(f => f, StringComparer.Ordinal).Skip(MaxQuarantined);
            foreach (var f in old) { try { File.Delete(f); } catch { } }
        }
        catch (Exception ex) { Logger.LogException("AtomicFile.Quarantine", ex); }
    }

    private static T Retry<T>(Func<T> op)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return op(); }
            catch (Exception ex) when (attempt < MaxAttempts &&
                                       ((ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException) ||
                                        ex is UnauthorizedAccessException))
            {
                Thread.Sleep(25 * attempt); // outro programa com o arquivo aberto por instantes (antivirus, backup, o outro processo)
            }
        }
    }
}

/// <summary>
/// Permissões da pasta de dados (C:\ProgramData\SharkBlocker).
///
/// Por que ProgramData e não AppData: a diretriz do Windows é AppData para dados POR USUÁRIO e
/// ProgramData para dados da MÁQUINA compartilhados entre contas. Aqui o Watchdog roda como SYSTEM
/// (o AppData dele é outro perfil) e o app como Administrador — os dois precisam do MESMO estado,
/// então o lugar certo é ProgramData.
///
/// O padrão de ProgramData deixa usuários comuns criarem/alterarem arquivos nas subpastas. Aqui a
/// pasta segue o princípio do menor privilégio: SYSTEM e Administradores com Controle Total, Usuários
/// só leitura/execução, SEM herdar as permissões de ProgramData. Idempotente: só reaplica se estiver diferente.
/// </summary>
public static class DataDirectorySecurity
{
    private static bool _attempted;

    public static void Ensure(string dir)
    {
        if (_attempted) return;   // uma tentativa por processo (também evita recursão via Logger)
        _attempted = true;
        try
        {
            Directory.CreateDirectory(dir);
            var info = new DirectoryInfo(dir);
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

            if (IsConfigured(info.GetAccessControl(), system, admins)) return;

            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false); // nao herda as regras de ProgramData
            security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(security);
            Logger.Log("Permissoes da pasta de dados ajustadas: SYSTEM e Administradores = controle total; Usuarios = somente leitura.");
        }
        catch (Exception ex)
        {
            Logger.LogException("DataDirectorySecurity.Ensure", ex);
        }
    }

    private static bool IsConfigured(DirectorySecurity sec, SecurityIdentifier system, SecurityIdentifier admins)
    {
        if (!sec.AreAccessRulesProtected) return false;
        var rules = sec.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow).ToList();

        bool HasFull(SecurityIdentifier sid) =>
            rules.Any(r => r.IdentityReference.Equals(sid) && (r.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
        if (!HasFull(system) || !HasFull(admins)) return false;

        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes |
                                       FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete |
                                       FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
                                       FileSystemRights.TakeOwnership;
        // ninguem alem de SYSTEM/Administradores pode ter qualquer direito de escrita
        return !rules.Any(r => !r.IdentityReference.Equals(system) && !r.IdentityReference.Equals(admins) && (r.FileSystemRights & write) != 0);
    }
}
