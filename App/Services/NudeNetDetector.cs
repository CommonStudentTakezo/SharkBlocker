using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SharkBlocker.Services;

/// <summary>
/// SEGUNDA OPINIÃO do detector de imagens: o NudeNet v3 (YOLOv8) acha PARTES DO CORPO (exposta/coberta) em vez de dar uma nota
/// para a imagem inteira. Serve para (a) vetar o "sexy" do <see cref="NsfwClassifier"/> quando a tela só mostra um rosto
/// (close do apresentador de xadrez) e (b) confirmar nudez de verdade (ver <see cref="ImageDecision.Refine"/>).
///
/// É OPCIONAL: sem <c>nsfw\nudenet.onnx</c> o app roda só com o classificador (estado "Sem modelo"). O arquivo NÃO vem no projeto:
/// <c>scripts\obter-modelo-nudenet.py</c> copia o <c>320n.onnx</c> do pacote pip <c>nudenet</c> (ver docs\DETECTOR-DE-IMAGENS-PC.md).
///
/// Contrato: entrada FLOAT32 [1,3,S,S] (NCHW, RGB, /255), S lido do modelo (320 no 320n; o 640m também serve); saída
/// FLOAT32 [1,22,N] ou [1,N,22] (4 de caixa + 18 classes). A imagem é colocada num quadrado preto (canto superior esquerdo, sem
/// distorcer a proporção) e reduzida, como o pacote nudenet faz. Nenhuma imagem é salva ou enviada.
/// Compartilha a onnxruntime.dll via <see cref="OnnxRuntimeLoader"/>. NÃO pode ser compilado pelo Watchdog.
/// </summary>
public static class NudeNetDetector
{
    public enum Status { NotChecked, Ready, NoModel, BadModel }

    public static volatile Status CurrentStatus = Status.NotChecked;
    public static volatile string Detail = "ainda não verificado";

    private static readonly object Gate = new();
    private static InferenceSession? _session;
    private static string _inputName = "";
    private static int _size = 320;
    private static bool _loaded;
    private static int _failures;

    public static event Action? StatusChanged;

    public static string ModelPath => Path.Combine(NsfwClassifier.ModelFolder, "nudenet.onnx");

    public static bool IsReady => CurrentStatus == Status.Ready;

    public static void Init()
    {
        InitCore();
        try { StatusChanged?.Invoke(); } catch { /* um ouvinte com erro nunca derruba o carregamento */ }
    }

    private static void InitCore()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;

            if (!File.Exists(ModelPath))
            {
                CurrentStatus = Status.NoModel;
                Detail = "opcional: coloque Assets\\nsfw\\nudenet.onnx (ver docs\\DETECTOR-DE-IMAGENS-PC.md)";
                return;
            }

            var nativeError = OnnxRuntimeLoader.EnsureLoaded();
            if (nativeError != null)
            {
                CurrentStatus = Status.BadModel;
                Detail = nativeError;
                return;
            }

            try
            {
                var session = new InferenceSession(ModelPath);
                var input = session.InputMetadata.First();
                var dims = input.Value.Dimensions;
                if (input.Value.ElementType != typeof(float) || dims.Length != 4 || dims[1] != 3)
                {
                    CurrentStatus = Status.BadModel;
                    Detail = $"entrada {input.Value.ElementType.Name} [{string.Join(",", dims)}] não suportada (esperado FLOAT32 [1,3,S,S])";
                    session.Dispose();
                    return;
                }

                // dims[0] pode ser -1 (lote dinâmico): ignorado. Dimensão dinâmica de imagem: usa 320.
                _size = dims[2] > 0 && dims[2] == dims[3] ? dims[2] : 320;

                _inputName = input.Key;
                _session = session;
                CurrentStatus = Status.Ready;
                Detail = $"entrada {_size}x{_size}";
                Logger.Log($"NudeNet (segunda opinião): modelo carregado ({Detail})");
            }
            catch (Exception ex)
            {
                CurrentStatus = Status.BadModel;
                Detail = "falha ao carregar: " + ex.GetType().Name + (ex.InnerException != null ? " <= " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : "");
                Logger.LogException("NudeNetDetector.Init", ex);
            }
        }
    }

    /// <summary>Resumo das partes do corpo achadas, ou null se o modelo não está pronto/falhou nesta captura.</summary>
    public static NudeNetResult? Detect(Bitmap source)
    {
        InferenceSession? session;
        lock (Gate) session = _session;
        if (CurrentStatus != Status.Ready || session == null) return null;

        try
        {
            var n = _size;
            var longest = Math.Max(source.Width, source.Height);
            if (longest <= 0) return null;
            var scale = (double)n / longest;
            var w = Math.Max(1, (int)Math.Round(source.Width * scale));
            var h = Math.Max(1, (int)Math.Round(source.Height * scale));

            // Quadrado preto n x n com a imagem no canto superior esquerdo (mesmo preenchimento do pacote nudenet).
            using var square = new Bitmap(n, n, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(square))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(source, new Rectangle(0, 0, w, h));
            }

            var data = square.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var bytes = new byte[Math.Abs(data.Stride) * n];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var stride = data.Stride;
            square.UnlockBits(data);

            var tensor = new DenseTensor<float>(new[] { 1, 3, n, n });
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var o = y * stride + x * 3; // Format24bppRgb = B, G, R
                    tensor[0, 0, y, x] = bytes[o + 2] / 255f;
                    tensor[0, 1, y, x] = bytes[o + 1] / 255f;
                    tensor[0, 2, y, x] = bytes[o] / 255f;
                }
            }

            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var outTensor = results.First().AsTensor<float>();
            var d = outTensor.Dimensions.ToArray();
            if (d.Length != 3 || d[0] != 1)
            {
                MarkBad($"saída [{string.Join(",", d)}] não suportada (esperado [1,22,N] ou [1,N,22])");
                return null;
            }

            var summary = NudeNetDecoder.Summarize(outTensor.ToArray(), d[1], d[2]);
            if (summary == null)
            {
                MarkBad($"saída [{string.Join(",", d)}] sem {NudeNetDecoder.Channels} canais (4 de caixa + {NudeNetDecoder.Classes} classes)");
                return null;
            }

            _failures = 0;
            return summary;
        }
        catch (Exception ex)
        {
            // Disjuntor: depois de 5 falhas seguidas, desliga a segunda opinião até reiniciar (o classificador segue sozinho).
            if (Interlocked.Increment(ref _failures) >= 5)
            {
                MarkBad("falhas seguidas ao analisar: " + ex.GetType().Name);
            }
            else
            {
                Logger.LogException("NudeNetDetector.Detect", ex);
            }
            return null;
        }
    }

    private static void MarkBad(string detail)
    {
        CurrentStatus = Status.BadModel;
        Detail = detail;
        Logger.Log("NudeNet (segunda opinião) desligado: " + detail);
        try { StatusChanged?.Invoke(); } catch { }
    }
}
