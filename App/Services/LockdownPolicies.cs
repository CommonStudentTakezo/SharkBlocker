using Microsoft.Win32;

namespace SharkBlocker.Services;

/// <summary>
/// Desativa o Gerenciador de Tarefas só enquanto a trava de tela da
/// desinstalação está ativa. Motivo: Ctrl+Alt+Del é a "Sequência de Atenção
/// Segura" do Windows e NÃO pode ser interceptada por nenhum programa (nem
/// por hook de teclado) — então, sem isso, bastaria Ctrl+Alt+Del > Gerenciador
/// de Tarefas > Finalizar tarefa para derrubar a trava.
///
/// Segurança contra "ficar preso": só mexemos no valor se ELE ainda não
/// estava ligado por outra política; deixamos uma marca em HKCU dizendo que o
/// valor é nosso; e o app chama <see cref="RestoreTaskManager"/> ao terminar a
/// trava E em toda abertura (cobre queda de energia/crash no meio da trava).
///
/// Limite: usa HKCU do usuário que está rodando o app. Se você elevou com a
/// conta de OUTRO administrador (UAC pedindo usuário/senha), o HKCU é o dele e
/// o Gerenciador de Tarefas do usuário logado não é afetado.
/// </summary>
public static class LockdownPolicies
{
    private const string SystemPolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string TaskMgrValue = "DisableTaskMgr";

    private const string MarkerKey = @"Software\SharkBlocker";
    private const string MarkerValue = "TaskMgrDisabledByUs";

    public static void DisableTaskManager()
    {
        try
        {
            using var sys = Registry.CurrentUser.CreateSubKey(SystemPolicyKey);
            if (sys == null) return;

            if (sys.GetValue(TaskMgrValue) is int current && current == 1)
            {
                // Já estava desativado por outra política — não é nosso, não mexemos nem restauramos.
                return;
            }

            sys.SetValue(TaskMgrValue, 1, RegistryValueKind.DWord);

            using var marker = Registry.CurrentUser.CreateSubKey(MarkerKey);
            marker?.SetValue(MarkerValue, 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            Logger.LogException("LockdownPolicies.DisableTaskManager", ex);
        }
    }

    public static void RestoreTaskManager()
    {
        try
        {
            using var marker = Registry.CurrentUser.OpenSubKey(MarkerKey, writable: true);
            if (marker == null || marker.GetValue(MarkerValue) == null) return; // não fomos nós

            using (var sys = Registry.CurrentUser.OpenSubKey(SystemPolicyKey, writable: true))
            {
                sys?.DeleteValue(TaskMgrValue, throwOnMissingValue: false);
            }

            marker.DeleteValue(MarkerValue, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.LogException("LockdownPolicies.RestoreTaskManager", ex);
        }
    }
}
