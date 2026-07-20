using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace AnvilDepth.Services;

public sealed record SegmentationResult(float[] Mask, int Width, int Height);

/// <summary>
/// Runs a BiRefNet-style ONNX segmentation model (local, offline) to produce a foreground/
/// background probability mask (0..1, 1 = subject). This replaces the old "Remove BG" behavior
/// of just reading a PNG's existing alpha channel — that only worked if the source already had
/// transparency baked in. This actually looks at the image content, so it works on flat JPGs
/// and PNGs too.
///
/// Entirely optional: if Models\bg_remove.onnx isn't present, IsLoaded stays false and callers
/// (ImageProcessor's mask-based path) fall back to the original alpha-channel removal — Remove BG
/// still works, just less precisely, exactly as it did before this was added.
///
/// Model I/O is read dynamically rather than hardcoded, same approach as DepthEngine: different
/// BiRefNet ONNX exports (onnx-community's, third-party conversions, quantized variants) don't
/// all use the same input/output tensor names, and this hasn't been verified against a specific
/// downloaded file yet — treat the name-matching in ComputeMaskAsync as a reasonable default that
/// may need a one-line adjustment once tested against the actual model you use.
/// </summary>
public sealed class SegmentationEngine : IDisposable
{
    private const int DefaultNetSize = 1024; // BiRefNet's standard resolution
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private InferenceSession? _session;
    private int _netW = DefaultNetSize;
    private int _netH = DefaultNetSize;

    public bool IsLoaded => _session != null;

    /// <summary>Loads Models\bg_remove.onnx at startup. Missing file is not an error — it just
    /// means AI background removal isn't available and the alpha-channel fallback is used.</summary>
    public Task<string> InitializeAsync() => LoadModelAsync("bg_remove.onnx");

    public Task<string> LoadModelAsync(string modelFileName)
    {
        return Task.Run(() =>
        {
            string modelPath = Path.Combine(AppContext.BaseDirectory, "Models", modelFileName);
            if (!File.Exists(modelPath))
                return $"No AI background-removal model (Models\\{modelFileName}) — Remove BG will use the alpha-channel fallback.";

            var options = new SessionOptions();
            bool gpu = false;
            try
            {
                options.AppendExecutionProvider_DML();
                gpu = true;
            }
            catch
            {
                // No DX12-capable GPU — falls back to CPU automatically.
            }

            try
            {
                var newSession = new InferenceSession(modelPath, options);

                int netH = DefaultNetSize, netW = DefaultNetSize;
                var inputDims = newSession.InputMetadata.Values.First().Dimensions;
                if (inputDims.Length == 4)
                {
                    if (inputDims[2] > 0) netH = inputDims[2];
                    if (inputDims[3] > 0) netW = inputDims[3];
                }

                _session?.Dispose();
                _session = newSession;
                _netH = netH;
                _netW = netW;

                return gpu ? $"BG Removal AI Ready — {modelFileName} (GPU/DirectML)" : $"BG Removal AI Ready — {modelFileName} (CPU)";
            }
            catch (Exception ex)
            {
                return $"Failed to load {modelFileName}: {ex.Message}";
            }
        });
    }

    /// <summary>Runs segmentation on the source image, returns a foreground probability mask
    /// (0..1, 1 = subject) resized to the image's native resolution.</summary>
    public Task<SegmentationResult> ComputeMaskAsync(string path)
    {
        return Task.Run(() =>
        {
            if (_session == null)
                throw new InvalidOperationException("Background-removal model isn't loaded.");

            using var src = Cv2.ImRead(path, ImreadModes.Color);
            if (src.Empty())
                throw new InvalidOperationException($"Could not read image: {path}");

            int outW = src.Width, outH = src.Height;

            using var resized = new Mat();
            Cv2.Resize(src, resized, new OpenCvSharp.Size(_netW, _netH), 0, 0, InterpolationFlags.Cubic);
            using var rgb = new Mat();
            Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

            var input = new DenseTensor<float>(new[] { 1, 3, _netH, _netW });
            for (int y = 0; y < _netH; y++)
            {
                for (int x = 0; x < _netW; x++)
                {
                    var px = rgb.At<Vec3b>(y, x);
                    input[0, 0, y, x] = (px.Item0 / 255f - Mean[0]) / Std[0];
                    input[0, 1, y, x] = (px.Item1 / 255f - Mean[1]) / Std[1];
                    input[0, 2, y, x] = (px.Item2 / 255f - Mean[2]) / Std[2];
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_session!.InputMetadata.Keys.First(), input)
            };

            using var results = _session.Run(inputs);

            // BiRefNet's training graph has multiple decoder-stage side-outputs; ONNX exports
            // sometimes keep all of them. Prefer one whose name suggests it's the final mask;
            // otherwise take the last output (final decoder stage is conventionally listed last).
            var maskResult =
                results.FirstOrDefault(r =>
                    r.Name.Contains("mask", StringComparison.OrdinalIgnoreCase) ||
                    r.Name.Contains("output", StringComparison.OrdinalIgnoreCase) ||
                    r.Name.Contains("pred", StringComparison.OrdinalIgnoreCase))
                ?? results.Last();

            var outTensor = maskResult.AsTensor<float>();
            var dims = outTensor.Dimensions;
            int h = dims.Length >= 2 ? dims[dims.Length - 2] : outH;
            int w = dims.Length >= 2 ? dims[dims.Length - 1] : outW;
            var raw = outTensor.ToArray();

            // Some exports bake the final sigmoid into the graph (values already 0..1); others
            // emit raw logits. Detect which by range so either kind normalizes to a clean 0..1
            // probability mask instead of silently clipping a logit range to look like noise.
            float min = raw.Min(), max = raw.Max();
            bool looksLikeLogits = min < -0.01f || max > 1.01f;

            using var maskMat = new Mat(h, w, MatType.CV_32FC1);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float v = raw[y * w + x];
                    if (looksLikeLogits) v = 1f / (1f + MathF.Exp(-v));
                    maskMat.Set(y, x, Math.Clamp(v, 0f, 1f));
                }
            }

            using var maskFullRes = new Mat();
            Cv2.Resize(maskMat, maskFullRes, new OpenCvSharp.Size(outW, outH), 0, 0, InterpolationFlags.Cubic);

            var result = MatToFloatArray(maskFullRes, outW, outH);
            return new SegmentationResult(result, outW, outH);
        });
    }

    private static float[] MatToFloatArray(Mat mat, int w, int h)
    {
        bool cloned = !mat.IsContinuous();
        Mat contiguous = cloned ? mat.Clone() : mat;
        var result = new float[w * h];
        Marshal.Copy(contiguous.Data, result, 0, w * h);
        if (cloned) contiguous.Dispose();
        return result;
    }

    public void Dispose() => _session?.Dispose();
}
