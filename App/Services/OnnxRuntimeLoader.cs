using System.IO;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace SharkBlocker.Services;

/// <summary>
/// Carrega UMA vez a onnxruntime.dll DA PASTA DO PROGRAMA (por caminho completo), compartilhada pelos dois detectores
/// (<see cref="NsfwClassifier"/> e <see cref="NudeNetDetector"/>). Assim uma DLL de mesmo nome em outro lugar (ex.: do Windows)
/// não é usada por engano, e um erro de carregamento (ex.: falta o "Visual C++ Redistributable x64") aparece com o motivo.
/// Só o App usa; o Watchdog não compila este arquivo.
/// </summary>
internal static class OnnxRuntimeLoader
{
    private static readonly object Gate = new();
    private static bool _done;
    private static string? _error;

    /// <summary>Devolve null se a biblioteca nativa está carregada; senão o motivo (já registrado no log, 1 vez).</summary>
    public static string? EnsureLoaded()
    {
        lock (Gate)
        {
            if (_done) return _error;
            _done = true;

            var nativeDll = Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll");
            if (!File.Exists(nativeDll))
            {
                _error = "onnxruntime.dll não está em " + AppContext.BaseDirectory;
                Logger.Log("Detector de imagens: " + _error);
                return _error;
            }
            try
            {
                NativeLibrary.Load(nativeDll);
                NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
                    name.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase)
                    && NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory,
                        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll"), out var h)
                        ? h : IntPtr.Zero);
            }
            catch (Exception ex)
            {
                _error = "a biblioteca nativa do ONNX Runtime não carregou (falta o Visual C++ Redistributable x64?): " + ex.Message;
                Logger.LogException("OnnxRuntimeLoader", ex);
            }
            return _error;
        }
    }
}
