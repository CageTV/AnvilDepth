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

public sealed record DepthResult(float[] Depth, int Width, int Height);

/// <summary>
/// Runs Depth-Anything V2 (local ONNX, offline) to produce a relative depth map.
///
/// "HQ Tiled" is implemented literally: the model's native input is a fixed 518x518,
/// so a single global pass on a large texture atlas (e.g. 2048x2048) throws away most
/// detail. When HighQuality is on, the source is additionally split into overlapping
/// tiles that are each run through the model near-native-resolution, feathered back
/// together, and blended as a *local* detail layer on top of the coherent global pass
/// (so tile seams don't show up as large-scale depth drift).
/// </summary>
public sealed class DepthEngine : IDisposable
{
    private const int NetSize = 518;
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private InferenceSession? _session;

    public Task<string> InitializeAsync()
    {
        return Task.Run(() =>
        {
            string modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "model.onnx");
            if (!File.Exists(modelPath))
                return "AI model not found (Models\\model.onnx) — Relief mode still works. See README.";

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
                _session = new InferenceSession(modelPath, options);
                return gpu ? "AI Ready (GPU/DirectML)" : "AI Ready (CPU)";
            }
            catch (Exception ex)
            {
                return $"AI init failed: {ex.Message}";
            }
        });
    }

    public Task<DepthResult> EstimateDepthAsync(string path, bool highQuality)
    {
        return Task.Run(() =>
        {
            if (_session == null)
                throw new InvalidOperationException("AI model isn't loaded. Uncheck 'Use AI' for Relief mode, or add Models\\model.onnx.");

            using var src = Cv2.ImRead(path, ImreadModes.Color);
            if (src.Empty())
                throw new InvalidOperationException($"Could not read image: {path}");

            int outW = src.Width, outH = src.Height;

            using var globalDepth = RunSingleTile(src, new Rect(0, 0, src.Width, src.Height));
            using var globalFullRes = new Mat();
            Cv2.Resize(globalDepth, globalFullRes, new OpenCvSharp.Size(outW, outH), 0, 0, InterpolationFlags.Cubic);

            Mat finalDepth = globalFullRes;
            Mat? tiledDetail = null;

            if (highQuality)
            {
                tiledDetail = RunTiledDetail(src, outW, outH);
                // Inject only the *local* structure from the tiled pass (high-pass it first)
                // so per-tile brightness differences don't create visible seams.
                using var tiledLowPass = new Mat();
                Cv2.GaussianBlur(tiledDetail, tiledLowPass, new OpenCvSharp.Size(0, 0), 24.0);
                using var tiledHighPass = new Mat();
                Cv2.Subtract(tiledDetail, tiledLowPass, tiledHighPass);

                using var blended = new Mat();
                Cv2.AddWeighted(globalFullRes, 1.0, tiledHighPass, 0.6, 0, blended);
                Cv2.Min(blended, new Scalar(1.0), blended);
                Cv2.Max(blended, new Scalar(0.0), blended);
                finalDepth = blended.Clone();
            }

            var result = MatToFloatArray(finalDepth, outW, outH);
            tiledDetail?.Dispose();
            if (highQuality) finalDepth.Dispose();

            return new DepthResult(result, outW, outH);
        });
    }

    /// <summary>Runs one region of the source image through the model, returns a NetSize x NetSize depth Mat (0..1, white = near).</summary>
    private Mat RunSingleTile(Mat src, Rect region)
    {
        using var cropped = new Mat(src, region);
        using var resized = new Mat();
        Cv2.Resize(cropped, resized, new OpenCvSharp.Size(NetSize, NetSize), 0, 0, InterpolationFlags.Cubic);
        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        var input = new DenseTensor<float>(new[] { 1, 3, NetSize, NetSize });
        for (int y = 0; y < NetSize; y++)
        {
            for (int x = 0; x < NetSize; x++)
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
        var outTensor = results.First().AsTensor<float>();
        var dims = outTensor.Dimensions;
        int h = dims.Length == 4 ? dims[2] : dims[1];
        int w = dims.Length == 4 ? dims[3] : dims[2];
        var raw = outTensor.ToArray();

        float min = raw.Min(), max = raw.Max();
        float range = Math.Max(max - min, 1e-6f);

        var depth = new Mat(h, w, MatType.CV_32FC1);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Depth-Anything outputs disparity (higher = closer); invert to white = near.
                depth.Set(y, x, 1f - (raw[y * w + x] - min) / range);
            }
        }
        return depth;
    }

    private Mat RunTiledDetail(Mat src, int outW, int outH)
    {
        int tilesPerAxis = 3;
        int overlapPx = Math.Max(24, (int)(Math.Min(src.Width, src.Height) * 0.08));
        var xRanges = TileRanges(src.Width, tilesPerAxis, overlapPx);
        var yRanges = TileRanges(src.Height, tilesPerAxis, overlapPx);

        using var accum = new Mat(outH, outW, MatType.CV_32FC1, new Scalar(0));
        using var weightSum = new Mat(outH, outW, MatType.CV_32FC1, new Scalar(0));

        foreach (var (x0, x1) in xRanges)
        {
            foreach (var (y0, y1) in yRanges)
            {
                var region = new Rect(x0, y0, x1 - x0, y1 - y0);
                using var tileDepth = RunSingleTile(src, region);
                using var tileResized = new Mat();
                Cv2.Resize(tileDepth, tileResized, new OpenCvSharp.Size(region.Width, region.Height), 0, 0, InterpolationFlags.Cubic);
                using var weight = FeatherWindow(region.Width, region.Height, overlapPx);

                using var accumRoi = new Mat(accum, region);
                using var weightRoi = new Mat(weightSum, region);
                using var weighted = new Mat();
                Cv2.Multiply(tileResized, weight, weighted);
                Cv2.Add(accumRoi, weighted, accumRoi);
                Cv2.Add(weightRoi, weight, weightRoi);
            }
        }

        using var safeWeight = new Mat();
        Cv2.Max(weightSum, new Scalar(1e-4), safeWeight);
        var result = new Mat();
        Cv2.Divide(accum, safeWeight, result);
        return result;
    }

    private static List<(int start, int end)> TileRanges(int total, int count, int overlapPx)
    {
        var ranges = new List<(int, int)>();
        if (count <= 1 || total <= overlapPx * 2)
        {
            ranges.Add((0, total));
            return ranges;
        }

        int tileSize = (total + overlapPx * (count - 1)) / count;
        int step = tileSize - overlapPx;
        for (int i = 0; i < count; i++)
        {
            int start = Math.Min(i * step, Math.Max(0, total - tileSize));
            int end = Math.Min(start + tileSize, total);
            ranges.Add((start, end));
        }
        return ranges;
    }

    /// <summary>Linear feather ramp near tile edges so overlapping tiles blend smoothly instead of seaming.</summary>
    private static Mat FeatherWindow(int w, int h, int featherPx)
    {
        var window = new Mat(h, w, MatType.CV_32FC1);
        int fx = Math.Min(featherPx, w / 2);
        int fy = Math.Min(featherPx, h / 2);
        for (int y = 0; y < h; y++)
        {
            float wy = fy > 0 ? Math.Min(1f, Math.Min((y + 1f) / fy, (h - y) / (float)fy)) : 1f;
            for (int x = 0; x < w; x++)
            {
                float wx = fx > 0 ? Math.Min(1f, Math.Min((x + 1f) / fx, (w - x) / (float)fx)) : 1f;
                window.Set(y, x, Math.Max(0.05f, wx * wy));
            }
        }
        return window;
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
