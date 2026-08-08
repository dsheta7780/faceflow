using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using FaceFlow.Core.Imaging;

namespace FaceFlow.Core.Faces;

public sealed class FaceEngineOptions
{
    public int DetectionSize = 640;      // SCRFD input square
    public float DetectionThreshold = 0.50f;
    public float NmsThreshold = 0.40f;
    public int MinFacePixels = 28;       // faces smaller than this (in the decoded image) are ignored
    public bool PreferGpu = true;
}

/// <summary>
/// SCRFD detector + ArcFace embedder on ONNX Runtime.
/// InferenceSession.Run is thread-safe, so one engine instance serves all scan workers.
/// </summary>
public sealed class FaceEngine : IDisposable
{
    private readonly InferenceSession _det;
    private readonly InferenceSession _rec;
    private readonly string[] _detOutputs;
    private readonly string _detInput, _recInput;
    private readonly FaceEngineOptions _opt;
    private readonly int[] _strides = { 8, 16, 32 };
    private readonly int _numAnchors = 2;
    private readonly bool _detHasKps;
    private readonly ConcurrentDictionary<int, float[,]> _anchorCache = new();

    public bool UsingGpu { get; }
    public string DetectorModel { get; }
    public string RecognitionModel { get; }
    public int EmbeddingSize { get; }

    private FaceEngine(InferenceSession det, InferenceSession rec, FaceEngineOptions opt,
                       bool gpu, string detName, string recName)
    {
        _det = det; _rec = rec; _opt = opt; UsingGpu = gpu;
        DetectorModel = detName; RecognitionModel = recName;

        _detInput = _det.InputMetadata.Keys.First();
        _recInput = _rec.InputMetadata.Keys.First();
        _detOutputs = _det.OutputMetadata.Keys.ToArray();
        _detHasKps = _detOutputs.Length >= 9;

        var recOut = _rec.OutputMetadata.Values.First();
        EmbeddingSize = recOut.Dimensions.Length > 1 && recOut.Dimensions[^1] > 0 ? recOut.Dimensions[^1] : 512;

        if (!_detHasKps)
            throw new InvalidOperationException(
                "The detection model does not expose keypoint outputs. " +
                "FaceFlow needs an SCRFD *_bnkps / det_10g / det_500m model with 9 outputs.");
    }

    public static FaceEngine Create(FaceEngineOptions? options = null, string? modelsDir = null)
    {
        var opt = options ?? new FaceEngineOptions();
        var dir = modelsDir ?? AppPaths.ModelsDir;

        var detPath = FindModel(dir, "det_10g.onnx", "det_500m.onnx", "det_2.5g.onnx",
                                     "scrfd_10g_bnkps.onnx", "scrfd_2.5g_bnkps.onnx", "scrfd_500m_bnkps.onnx");
        var recPath = FindModel(dir, "w600k_r50.onnx", "w600k_mbf.onnx", "glintr100.onnx", "arcface.onnx");

        if (detPath is null || recPath is null)
            throw new FileNotFoundException(
                $"Face models not found in '{dir}'.\r\n" +
                "Run tools\\get-models.ps1 (or copy a detection model and a recognition model there) " +
                "and restart FaceFlow.");

        bool gpu = false;
        InferenceSession? det = null, rec = null;

        if (opt.PreferGpu)
        {
            try
            {
                var so = new SessionOptions();
                so.AppendExecutionProvider_CUDA(0);
                so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                det = new InferenceSession(detPath, so);
                rec = new InferenceSession(recPath, so);
                gpu = true;
                Log.Info("ONNX Runtime: CUDA execution provider active.");
            }
            catch (Exception ex)
            {
                Log.Warn("CUDA unavailable, falling back to CPU: " + ex.Message);
                det?.Dispose(); rec?.Dispose(); det = null; rec = null;
            }
        }

        if (det is null || rec is null)
        {
            var so = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2)
            };
            det = new InferenceSession(detPath, so);
            rec = new InferenceSession(recPath, so);
            Log.Info("ONNX Runtime: CPU execution provider active.");
        }

        return new FaceEngine(det!, rec!, opt, gpu,
                              Path.GetFileName(detPath), Path.GetFileName(recPath));
    }

    private static string? FindModel(string dir, params string[] names)
    {
        if (!Directory.Exists(dir)) return null;

        foreach (var n in names)
        {
            var direct = Path.Combine(dir, n);
            if (File.Exists(direct)) return direct;
        }

        // Some model packs extract into a subfolder; look one level deeper too.
        foreach (var n in names)
        {
            try
            {
                var nested = Directory.GetFiles(dir, n, SearchOption.AllDirectories);
                if (nested.Length > 0) return nested[0];
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not search '{dir}' for '{n}': {ex.Message}");
            }
        }
        return null;
    }

    // ------------------------------------------------------------- detection

    public List<DetectedFace> Detect(RgbImage img)
    {
        int size = _opt.DetectionSize;

        // Letterbox into a size x size square, anchored top-left (matches InsightFace).
        float scale = MathF.Min((float)size / img.Width, (float)size / img.Height);
        int newW = Math.Max(1, (int)MathF.Round(img.Width * scale));
        int newH = Math.Max(1, (int)MathF.Round(img.Height * scale));

        var input = new DenseTensor<float>(new[] { 1, 3, size, size });
        int plane = size * size;
        var buf = input.Buffer.Span;

        for (int y = 0; y < newH; y++)
        {
            float sy = (y + 0.5f) / scale - 0.5f;
            for (int x = 0; x < newW; x++)
            {
                float sx = (x + 0.5f) / scale - 0.5f;
                img.Sample(sx, sy, out var r, out var g, out var b);
                int o = y * size + x;
                buf[o]             = (r - 127.5f) / 128f;
                buf[plane + o]     = (g - 127.5f) / 128f;
                buf[2 * plane + o] = (b - 127.5f) / 128f;
            }
        }

        using var results = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(_detInput, input) });
        var outs = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (var r in results)
            outs[r.Name] = r.AsTensor<float>().ToArray();

        var faces = new List<DetectedFace>();

        for (int i = 0; i < _strides.Length; i++)
        {
            int stride = _strides[i];
            var scores = outs[_detOutputs[i]];
            var bboxes = outs[_detOutputs[i + 3]];
            var kps    = outs[_detOutputs[i + 6]];

            int fh = size / stride, fw = size / stride;
            var anchors = GetAnchors(stride, fh, fw);

            for (int k = 0; k < scores.Length; k++)
            {
                float s = scores[k];
                if (s < _opt.DetectionThreshold) continue;
                if (k >= anchors.GetLength(0)) break;

                float ax = anchors[k, 0], ay = anchors[k, 1];
                float l = bboxes[k * 4 + 0] * stride;
                float t = bboxes[k * 4 + 1] * stride;
                float rr = bboxes[k * 4 + 2] * stride;
                float bb = bboxes[k * 4 + 3] * stride;

                float x1 = (ax - l) / scale, y1 = (ay - t) / scale;
                float x2 = (ax + rr) / scale, y2 = (ay + bb) / scale;

                var f = new DetectedFace
                {
                    X = x1, Y = y1, W = x2 - x1, H = y2 - y1, Score = s
                };

                for (int p = 0; p < 5; p++)
                {
                    f.Landmarks[p * 2]     = (ax + kps[k * 10 + p * 2] * stride) / scale;
                    f.Landmarks[p * 2 + 1] = (ay + kps[k * 10 + p * 2 + 1] * stride) / scale;
                }
                faces.Add(f);
            }
        }

        var kept = GeometryUtil.Nms(faces, _opt.NmsThreshold);

        var final = new List<DetectedFace>(kept.Count);
        foreach (var f in kept)
        {
            if (f.W < _opt.MinFacePixels || f.H < _opt.MinFacePixels) continue;
            if (f.X + f.W < 0 || f.Y + f.H < 0 || f.X > img.Width || f.Y > img.Height) continue;
            float sizeFactor = Math.Clamp(MathF.Min(f.W, f.H) / 110f, 0.15f, 1f);
            f.Quality = f.Score * sizeFactor;
            final.Add(f);
        }
        return final;
    }

    private float[,] GetAnchors(int stride, int fh, int fw)
    {
        return _anchorCache.GetOrAdd(stride, _ =>
        {
            var a = new float[fh * fw * _numAnchors, 2];
            int idx = 0;
            for (int y = 0; y < fh; y++)
                for (int x = 0; x < fw; x++)
                    for (int n = 0; n < _numAnchors; n++)
                    {
                        a[idx, 0] = x * stride;
                        a[idx, 1] = y * stride;
                        idx++;
                    }
            return a;
        });
    }

    // ------------------------------------------------------------- embedding

    /// <summary>Align to the ArcFace 112x112 template and produce an L2-normalised embedding.</summary>
    public float[] Embed(RgbImage img, DetectedFace face)
    {
        const int S = 112;
        var m = GeometryUtil.SimilarityTransform(face.Landmarks, GeometryUtil.ArcFaceDst, 5);
        var inv = GeometryUtil.Invert(m);

        var input = new DenseTensor<float>(new[] { 1, 3, S, S });
        var buf = input.Buffer.Span;
        int plane = S * S;

        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float sx = inv[0] * x - inv[1] * y + inv[2];
                float sy = inv[1] * x + inv[0] * y + inv[3];
                img.Sample(sx, sy, out var r, out var g, out var b);
                int o = y * S + x;
                buf[o]             = (r - 127.5f) / 127.5f;
                buf[plane + o]     = (g - 127.5f) / 127.5f;
                buf[2 * plane + o] = (b - 127.5f) / 127.5f;
            }

        using var results = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(_recInput, input) });
        var v = results.First().AsTensor<float>().ToArray();

        double norm = 0;
        for (int i = 0; i < v.Length; i++) norm += v[i] * (double)v[i];
        norm = Math.Sqrt(norm);
        if (norm < 1e-9) norm = 1e-9;
        for (int i = 0; i < v.Length; i++) v[i] = (float)(v[i] / norm);
        return v;
    }

    public void Dispose()
    {
        _det.Dispose();
        _rec.Dispose();
    }
}
