using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SharkBlocker.Services;

/// <summary>
/// Classificador de imagens adultas, 100% no PC (ONNX Runtime). Carrega Assets\nsfw\model.onnx (+ model.json opcional) da
/// pasta do programa. Sem o modelo o app compila e roda normalmente e o detector fica "Sem modelo".
///
/// Contrato do modelo: entrada FLOAT32 [1,H,H,3] (NHWC, RGB) ou [1,3,H,H] (NCHW); saída FLOAT32 [1,N] de probabilidades.
/// model.json (opcional; obrigatório se N != 2 e N != 5):
///   {"scale":0.00392157,"mean":0.0,"explicitIndices":[1,3],"suggestiveIndices":[4]}
///  - pixel_normalizado = pixel * scale + mean (padrão 1/255 e 0);
///  - explicitIndices = classes somadas na pontuação "explícita" (padrão [1,3] com 5 classes
///    [drawings, hentai, neutral, porn, sexy]: hentai + porn; [1] com 2 classes);
///  - suggestiveIndices = classes somadas na pontuação "sugestiva" (padrão [4] = sexy; vazio com 2 classes).
///    Elas ficam separadas porque "sexy" dispara com close de rosto e tom de pele: a decisão (ImageDecision) exige
///    mais do sugestivo do que do explícito;
///  - nsfwIndices (formato antigo, ainda aceito): tudo explícito, exceto o índice 4 de um modelo de 5 classes (vira sugestivo).
/// Nenhuma imagem é salva ou enviada; só as pontuações são usadas. Espelha o NsfwClassifier.kt do Android (que ainda usa a
/// pontuação única antiga).
/// NÃO pode ser compilado pelo Watchdog (usa WPF/WinForms): não o inclua em Watchdog.csproj.
/// </summary>
public static class NsfwClassifier
{
    public enum Status { NotChecked, Ready, NoModel, BadModel }

    public static volatile Status CurrentStatus = Status.NotChecked;
    public static volatile string Detail = "ainda não verificado";

    private static readonly object Gate = new();
    private static InferenceSession? _session;
    private static string _inputName = "";
    private static int _size = 224;
    private static bool _nchw;
    private static float _scale = 1f / 255f;
    private static float _mean;
    private static int[] _explicit = { 1, 3 };
    private static int[] _suggestive = { 4 };
    private static int _nOut = 5;
    private static bool _loaded;
    private static int _failures;

    public static string ModelFolder => Path.Combine(AppContext.BaseDirectory, "nsfw");

    /// <summary>Disparado (em thread de segundo plano) quando o estado muda; a interface usa para atualizar o texto.</summary>
    public static event Action? StatusChanged;

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

            var modelPath = Path.Combine(ModelFolder, "model.onnx");
            if (!File.Exists(modelPath))
            {
                CurrentStatus = Status.NoModel;
                Detail = "coloque Assets\\nsfw\\model.onnx (ver docs\\DETECTOR-DE-IMAGENS-PC.md)";
                return;
            }

            // Antes de tocar no ONNX Runtime: carrega a onnxruntime.dll da pasta do programa (compartilhada com o NudeNetDetector).
            var nativeError = OnnxRuntimeLoader.EnsureLoaded();
            if (nativeError != null)
            {
                CurrentStatus = Status.BadModel;
                Detail = nativeError;
                return;
            }

            try
            {
                var session = new InferenceSession(modelPath);
                var input = session.InputMetadata.First();
                var dims = input.Value.Dimensions;
                if (input.Value.ElementType != typeof(float) || dims.Length != 4)
                {
                    CurrentStatus = Status.BadModel;
                    Detail = $"entrada {input.Value.ElementType.Name} [{string.Join(",", dims)}] não suportada (use FLOAT32 de 4 dimensões)";
                    session.Dispose();
                    return;
                }

                // dims[0] pode ser -1 (lote dinâmico): ignorado, sempre usa 1.
                if (dims[3] == 3 && dims[1] == dims[2] && dims[1] > 0) { _nchw = false; _size = dims[1]; }
                else if (dims[1] == 3 && dims[2] == dims[3] && dims[2] > 0) { _nchw = true; _size = dims[2]; }
                else
                {
                    CurrentStatus = Status.BadModel;
                    Detail = $"entrada [{string.Join(",", dims)}] não é [1,H,H,3] nem [1,3,H,H]";
                    session.Dispose();
                    return;
                }

                var outDims = session.OutputMetadata.First().Value.Dimensions;
                _nOut = outDims.Length > 0 ? outDims[^1] : 0;

                var cfgPath = Path.Combine(ModelFolder, "model.json");
                if (File.Exists(cfgPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("scale", out var sc)) _scale = (float)sc.GetDouble();
                    if (root.TryGetProperty("mean", out var mn)) _mean = (float)mn.GetDouble();
                    var hasNew = false;
                    if (root.TryGetProperty("explicitIndices", out var expIdx) && expIdx.ValueKind == JsonValueKind.Array)
                    {
                        _explicit = expIdx.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                        hasNew = true;
                    }
                    if (root.TryGetProperty("suggestiveIndices", out var sugIdx) && sugIdx.ValueKind == JsonValueKind.Array)
                    {
                        _suggestive = sugIdx.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                        hasNew = true;
                    }
                    if (!hasNew && root.TryGetProperty("nsfwIndices", out var idx) && idx.ValueKind == JsonValueKind.Array)
                    {
                        // Formato antigo: tudo explícito; o índice 4 de um modelo de 5 classes (sexy) vira sugestivo.
                        var all = idx.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                        var sexyIsSuggestive = _nOut == 5;
                        _explicit = all.Where(i => !(sexyIsSuggestive && i == 4)).ToArray();
                        _suggestive = sexyIsSuggestive ? all.Where(i => i == 4).ToArray() : Array.Empty<int>();
                    }
                    else if (!hasNew && _nOut == 2)
                    {
                        _explicit = new[] { 1 };
                        _suggestive = Array.Empty<int>();
                    }
                }
                else if (_nOut == 2)
                {
                    _explicit = new[] { 1 };
                    _suggestive = Array.Empty<int>();
                }
                else if (_nOut != 5)
                {
                    CurrentStatus = Status.BadModel;
                    Detail = $"saída com {_nOut} classes exige model.json com nsfwIndices";
                    session.Dispose();
                    return;
                }

                _inputName = input.Key;
                _session = session;
                CurrentStatus = Status.Ready;
                Detail = $"entrada {_size}x{_size} ({(_nchw ? "NCHW" : "NHWC")}), saída {_nOut} classes, explicitIndices=[{string.Join(",", _explicit)}], suggestiveIndices=[{string.Join(",", _suggestive)}]";
                Logger.Log($"Detector de imagens: modelo carregado ({Detail})");
            }
            catch (Exception ex)
            {
                CurrentStatus = Status.BadModel;
                Detail = "falha ao carregar: " + ex.GetType().Name + (ex.InnerException != null ? " <= " + ex.InnerException.GetType().Name + ": " + ex.InnerException.Message : "");
                Logger.LogException("NsfwClassifier.Init", ex);
            }
        }
    }

    /// <summary>Pontuações explícita e sugestiva (cada uma de 0 a 1) mais a saída completa, ou null se o modelo não está pronto/falhou.</summary>
    public static ImageScores? Score(Bitmap source)
    {
        InferenceSession? session;
        lock (Gate) session = _session;
        if (CurrentStatus != Status.Ready || session == null) return null;

        try
        {
            var n = _size;
            using var scaled = new Bitmap(n, n, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(source, new Rectangle(0, 0, n, n));
            }

            var data = scaled.LockBits(new Rectangle(0, 0, n, n), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var bytes = new byte[Math.Abs(data.Stride) * n];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var stride = data.Stride;
            scaled.UnlockBits(data);

            var tensor = _nchw
                ? new DenseTensor<float>(new[] { 1, 3, n, n })
                : new DenseTensor<float>(new[] { 1, n, n, 3 });

            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    var o = y * stride + x * 3; // Format24bppRgb = B, G, R
                    var b = bytes[o] * _scale + _mean;
                    var gch = bytes[o + 1] * _scale + _mean;
                    var r = bytes[o + 2] * _scale + _mean;
                    if (_nchw) { tensor[0, 0, y, x] = r; tensor[0, 1, y, x] = gch; tensor[0, 2, y, x] = b; }
                    else { tensor[0, y, x, 0] = r; tensor[0, y, x, 1] = gch; tensor[0, y, x, 2] = b; }
                }
            }

            using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
            var output = results.First().AsEnumerable<float>().ToArray();
            if (output.Length == 0) return null;

            // Defesa: se a saída vier como "logits" (fora de 0..1), aplica softmax.
            if (output.Any(v => v < 0f || v > 1f))
            {
                var max = output.Max();
                var exp = output.Select(v => MathF.Exp(v - max)).ToArray();
                var sum = exp.Sum();
                output = exp.Select(v => v / sum).ToArray();
            }

            var explicitScore = 0f;
            foreach (var i in _explicit) if (i >= 0 && i < output.Length) explicitScore += output[i];
            var sexyScore = 0f;
            foreach (var i in _suggestive) if (i >= 0 && i < output.Length) sexyScore += output[i];
            _failures = 0;
            return new ImageScores(Math.Clamp(explicitScore, 0f, 1f), Math.Clamp(sexyScore, 0f, 1f), output);
        }
        catch (Exception ex)
        {
            // Disjuntor: depois de 5 falhas seguidas, desliga o detector até reiniciar (não enche o log nem gasta CPU).
            if (Interlocked.Increment(ref _failures) >= 5)
            {
                CurrentStatus = Status.BadModel;
                Detail = "falhas repetidas ao classificar (detector parado até reiniciar o app)";
                try { StatusChanged?.Invoke(); } catch { }
            }
            if (_failures <= 5) Logger.LogException("NsfwClassifier.Score", ex);
            return null;
        }
    }
}
